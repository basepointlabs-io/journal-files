using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using RevitJournal.Core.Model;
using RevitJournal.Core.Rules;

namespace RevitJournal.Core.Analysis;

/// <summary>
/// Keeps the analysis of finished sessions on disk, one small file per journal, so starting the app does not read
/// every journal again. An entry is valid while the journal has the same size and modification time and the analyzer
/// (code and rule base) is the same build. The files stay in the user's own profile, like the journals themselves.
/// <para>
/// What is stored is a digest: the findings and summary as shown, and of the session only what later views need
/// (syncs, models, a thinned memory curve, the last user actions). <see cref="Put"/> returns the result rebuilt from
/// that digest, so a fresh analysis and a cached one are the same object shape and hold the same, small amount of memory.
/// </para>
/// </summary>
public sealed class AnalysisCache(string directory, RuleSet rules)
{
    /// <summary>Changes with every build of this assembly, which embeds the rule base: old entries are then ignored.</summary>
    private static readonly string Stamp = typeof(AnalysisCache).Assembly.ManifestModule.ModuleVersionId.ToString("N");

    private const int MaxRamSamples = 300;
    private const int MaxUserActions = 200;

    private int _hits;

    /// <summary>How many results were served from disk (for tests and the status line).</summary>
    public int Hits => _hits;

    public AnalysisResult? TryGet(string journalPath)
    {
        try
        {
            var info = new FileInfo(journalPath);
            string file = EntryPath(journalPath);
            if (!info.Exists || !File.Exists(file))
                return null;
            using var stream = File.OpenRead(file);
            var entry = JsonSerializer.Deserialize<Entry>(stream);
            if (entry is null || entry.Stamp != Stamp || entry.Size != info.Length || entry.Modified != info.LastWriteTimeUtc.Ticks
                || !string.Equals(entry.Path, info.FullName, StringComparison.OrdinalIgnoreCase))
                return null;
            var result = ToResult(entry.Analysis, info.FullName);
            if (result is not null)
                Interlocked.Increment(ref _hits);
            return result;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or JsonException or NotSupportedException)
        {
            return null; // a damaged or locked entry only costs one fresh analysis
        }
    }

    /// <summary>Stores the result and returns it in its compact form. On any storage problem the compact form is still returned.</summary>
    public AnalysisResult Put(string journalPath, AnalysisResult result)
    {
        var digest = ToDigest(result);
        var info = new FileInfo(journalPath);
        try
        {
            Directory.CreateDirectory(directory);
            string file = EntryPath(journalPath);
            string temp = file + "." + Environment.ProcessId + ".tmp";
            using (var stream = File.Create(temp))
                JsonSerializer.Serialize(stream, new Entry(Stamp, info.FullName, info.Length, info.LastWriteTimeUtc.Ticks, digest));
            File.Move(temp, file, overwrite: true); // readers never see a half-written entry
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            // The cache is an optimization; without it the app simply analyzes again next time.
        }
        return ToResult(digest, info.FullName) ?? result;
    }

    /// <summary>Deletes entries of journals that no longer exist and entries of other builds. Returns how many were removed.</summary>
    public int Prune()
    {
        int removed = 0;
        if (!Directory.Exists(directory))
            return 0;
        foreach (string file in Directory.EnumerateFiles(directory, "*.json"))
        {
            try
            {
                Entry? entry;
                using (var stream = File.OpenRead(file))
                    entry = JsonSerializer.Deserialize<Entry>(stream);
                if (entry is not null && entry.Stamp == Stamp && File.Exists(entry.Path))
                    continue;
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or JsonException or NotSupportedException)
            {
                // unreadable: remove below
            }
            try
            {
                File.Delete(file);
                removed++;
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
            }
        }
        return removed;
    }

    private string EntryPath(string journalPath)
    {
        byte[] hash = SHA256.HashData(Encoding.UTF8.GetBytes(Path.GetFullPath(journalPath).ToLowerInvariant()));
        return Path.Combine(directory, Convert.ToHexString(hash, 0, 12).ToLowerInvariant() + ".json");
    }

    // ---- digest -------------------------------------------------------------------------------------------

    private sealed record Entry(string Stamp, string Path, long Size, long Modified, Digest Analysis);

    private sealed record Digest(
        SessionStatus Status, EndKind EndKind, string Headline, List<string> Summary, List<string> EndEvidence,
        List<FindingDigest> Findings, List<UserAction> LastActions, CrashContext? Context, SessionDigest Session);

    private sealed record FindingDigest(
        string RuleId, Severity Severity, int HitCount, List<LineRef> Evidence, List<string> Facts, List<string> Steps, List<string> AlsoMatched);

    private sealed record SessionDigest(
        string? Release, string? Build, DateOnly? BuildDate, DateTime? Start, DateTime? LastTimestamp, bool EndedNormally,
        DateTime? ExitRequestedAt, bool IsPlayback, int LineCount, int WorkerLogLines,
        List<RamSample> Ram, List<SyncDigest> Syncs, List<OperationDigest> Opens, List<ModelInfo> Models,
        List<UserAction> UserActions, List<AddIn> AddIns);

    private sealed record SyncDigest(int StartLine, DateTime Start, int? EndLine, DateTime? End, int ReloadLatestCount, bool GaveUp, List<LineRef> Errors);

    private sealed record OperationDigest(int StartLine, DateTime Start, string? Name, int? EndLine, DateTime? End);

    private static Digest ToDigest(AnalysisResult r)
    {
        var s = r.Session;
        return new Digest(r.Status, r.EndKind, r.Headline, [.. r.Summary], [.. r.EndEvidence],
            [.. r.Findings.Select(f => new FindingDigest(f.Rule.Id, f.Severity, f.HitCount, [.. f.Evidence], [.. f.Facts], [.. f.Steps],
                [.. f.AlsoMatched.Select(a => a.Id)]))],
            [.. r.LastActions], r.Context,
            new SessionDigest(s.Release, s.Build, s.BuildDate, s.Start, s.LastTimestamp, s.EndedNormally, s.ExitRequestedAt, s.IsPlayback,
                s.LineCount, s.WorkerLogLines, Thin(s.Ram),
                [.. s.Syncs.Select(x => new SyncDigest(x.StartLine, x.Start, x.EndLine, x.End, x.ReloadLatestCount, x.GaveUp, [.. x.Errors.Take(3)]))],
                [.. s.Opens.Select(x => new OperationDigest(x.StartLine, x.Start, x.Name, x.EndLine, x.End))],
                [.. s.Models], [.. s.UserActions.TakeLast(MaxUserActions)], [.. s.AddIns]));
    }

    private AnalysisResult? ToResult(Digest d, string journalPath)
    {
        var findings = new List<Finding>(d.Findings.Count);
        foreach (var f in d.Findings)
        {
            if (rules[f.RuleId] is not { } rule)
                return null; // written with a different rule base
            var finding = new Finding { Rule = rule, Severity = f.Severity, HitCount = f.HitCount };
            finding.Evidence.AddRange(f.Evidence);
            finding.Facts.AddRange(f.Facts);
            finding.Steps.AddRange(f.Steps);
            finding.AlsoMatched.AddRange(f.AlsoMatched.Select(id => rules[id]).OfType<Rule>());
            findings.Add(finding);
        }

        var ds = d.Session;
        var s = new JournalSession(journalPath, [])
        {
            Release = ds.Release, Build = ds.Build, BuildDate = ds.BuildDate, Start = ds.Start, LastTimestamp = ds.LastTimestamp,
            EndedNormally = ds.EndedNormally, ExitRequestedAt = ds.ExitRequestedAt, IsPlayback = ds.IsPlayback,
            LineCount = ds.LineCount, WorkerLogLines = ds.WorkerLogLines,
        };
        s.Ram.AddRange(ds.Ram);
        s.Syncs.AddRange(ds.Syncs.Select(x =>
        {
            var sync = new SyncAttempt { StartLine = x.StartLine, Start = x.Start, EndLine = x.EndLine, End = x.End, ReloadLatestCount = x.ReloadLatestCount, GaveUp = x.GaveUp };
            sync.Errors.AddRange(x.Errors);
            return sync;
        }));
        s.Opens.AddRange(ds.Opens.Select(x => new TimedOperation { StartLine = x.StartLine, Start = x.Start, Name = x.Name, EndLine = x.EndLine, End = x.End }));
        s.Models.AddRange(ds.Models);
        s.UserActions.AddRange(ds.UserActions);
        s.AddIns.AddRange(ds.AddIns);

        return new AnalysisResult
        {
            Session = s, Status = d.Status, Headline = d.Headline, Summary = d.Summary, Findings = findings,
            LastActions = d.LastActions, Context = d.Context, EndKind = d.EndKind, EndEvidence = d.EndEvidence,
        };
    }

    /// <summary>
    /// At most <see cref="MaxRamSamples"/> evenly spaced samples, always including the ones the views read: the lowest
    /// free memory, Revit's peak, a sample that knows the machine's total RAM, and the first and last.
    /// </summary>
    private static List<RamSample> Thin(List<RamSample> ram)
    {
        if (ram.Count <= MaxRamSamples)
            return [.. ram];
        var keep = new SortedSet<int> { 0, ram.Count - 1 };
        for (int i = 0; i < MaxRamSamples; i++)
            keep.Add((int)((long)i * (ram.Count - 1) / (MaxRamSamples - 1)));
        keep.Add(IndexOfBest(ram, (a, b) => a.AvailableMb < b.AvailableMb));
        keep.Add(IndexOfBest(ram, (a, b) => (a.RevitPeakMb ?? -1) > (b.RevitPeakMb ?? -1)));
        keep.Add(IndexOfBest(ram, (a, b) => (a.TotalMb ?? -1) > (b.TotalMb ?? -1)));
        return [.. keep.Select(i => ram[i])];
    }

    private static int IndexOfBest(List<RamSample> ram, Func<RamSample, RamSample, bool> better)
    {
        int best = 0;
        for (int i = 1; i < ram.Count; i++)
        {
            if (better(ram[i], ram[best]))
                best = i;
        }
        return best;
    }
}
