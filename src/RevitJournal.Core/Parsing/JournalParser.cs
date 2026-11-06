using System.Globalization;
using System.Text.RegularExpressions;
using RevitJournal.Core.Model;

namespace RevitJournal.Core.Parsing;

/// <summary>
/// Single pass over a journal that extracts session facts. Rule matching happens separately in
/// <see cref="Rules.RuleMatcher"/>; this class only understands the journal's structure.
/// See knowledge-base/notes/journal-format.md for the line formats.
/// </summary>
public static partial class JournalParser
{
    [GeneratedRegex(@"^' Release: (\S+)")] private static partial Regex ReleaseRegex();
    [GeneratedRegex(@"^' Build: ((\d{4})(\d{2})(\d{2})_\d{4}\S*)")] private static partial Regex BuildRegex();
    [GeneratedRegex(@"RAM: Avail (?:[+-]\d+ -> )?(\d+) MB, Used (?:[+-]\d+ -> )?(\d+) MB(?:, Peak (?:[+-]\d+ -> )?(\d+))?")]
    private static partial Regex DeltaRamRegex();
    [GeneratedRegex(@"RAM Statistics:\s+(\d+)\s*/\s*(\d+)\s+(\d+)=InUse\s+(\d+)=Peak")] private static partial Regex RamStatsRegex();
    [GeneratedRegex(@"GUI Resource Usage GDI: Avail (\d+), Used (\d+)")] private static partial Regex GdiRegex();
    [GeneratedRegex(@"^\s*'\s+([\d.]+)!+\s*\d+:!!!BIG_GAP (.+?)\s*$")] private static partial Regex BigGapRegex();
    [GeneratedRegex(@"Jrn\.Command\s+""([^""]*)""\s*,\s*""(.*?)\s*,\s*(ID_[A-Z0-9_]+)\s*""")] private static partial Regex CommandRegex();
    [GeneratedRegex(@"Jrn\.(RibbonEvent|AddInEvent)\s+(.+)$")] private static partial Regex UiEventRegex();
    [GeneratedRegex(@"TaskDialog ""(.*)""\s*$")] private static partial Regex TaskDialogTextRegex();
    [GeneratedRegex(@"^\s*'Id : (\S+)")] private static partial Regex DialogIdRegex();
    [GeneratedRegex(@"SLOG \$(\w+) (\d{4}-\d{2}-\d{2} \d{2}:\d{2}:\d{2}\.\d{3}) ([<>.])STC(\S*)")] private static partial Regex SlogRegex();
    [GeneratedRegex(@"(API_SUCCESS|API_ERROR) \{ Starting External (?:DB )?Application: (.+?), Class: .+?, Vendor : ([^(,]*)\(([^)]*)\)")]
    private static partial Regex AddInRegex();
    [GeneratedRegex(@"API External Application added: (.+?), Class: .+?, VendorId: ([^,]*)")] private static partial Regex AddInLegacyRegex();
    [GeneratedRegex(@"Rvt\.Attr\.(\w+): (.*?)(?= Rvt\.Attr\.|\s*$)")] private static partial Regex FileInfoAttrRegex();
    [GeneratedRegex(@"Autodesk Revit (\d{4}) \(Build: ([^)]+)\)")] private static partial Regex SavedInRegex();
    [GeneratedRegex(@"User's Name ([^,]+), User's Machine Name ([^,]+),")] private static partial Regex UserTokenRegex();
    [GeneratedRegex(@"(?:System|Autodesk|Microsoft)\.[\w.]*Exception\b|Exception occurred|ExceptionCode=")] private static partial Regex ExceptionRegex();

    private static readonly string[] FailureDialogMarkers = ["Could_Not", "Fail", "Error", "Cannot", "Unable"];

    public static JournalSession Parse(string path) => Parse(path, JournalReader.ReadLines(path));

    public static JournalSession Parse(string path, IReadOnlyList<string> lines)
    {
        var s = new JournalSession(path, lines);
        DateTime? ts = null;
        string? pendingDialogText = null;
        var openSyncs = new Dictionary<string, SyncAttempt>();
        var seenModels = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        for (int i = 0; i < lines.Count; i++)
        {
            string line = lines[i];
            int n = i + 1;

            if (JournalTimestamp.TryParse(line, out var t))
            {
                ts = t;
                s.Start ??= t;
                s.LastTimestamp = t;
                if (line.Contains("finished recording journal file", StringComparison.Ordinal))
                    s.EndedNormally = true;
                if (line.Contains("started journal file playback", StringComparison.Ordinal))
                    s.IsPlayback = true;
            }

            if (n <= 20)
            {
                if (ReleaseRegex().Match(line) is { Success: true } rel)
                    s.Release = rel.Groups[1].Value;
                if (BuildRegex().Match(line) is { Success: true } b)
                {
                    s.Build = b.Groups[1].Value;
                    if (DateOnly.TryParseExact(b.Groups[2].Value + b.Groups[3].Value + b.Groups[4].Value, "yyyyMMdd",
                            CultureInfo.InvariantCulture, DateTimeStyles.None, out var bd))
                        s.BuildDate = bd;
                }
            }

            // Cheap prefilters keep the regexes off the ~90% of lines that are mouse moves and memory deltas.
            if (line.Contains("RAM: Avail", StringComparison.Ordinal) || line.Contains("RAM Statistics", StringComparison.Ordinal))
            {
                if (RamStatsRegex().Match(line) is { Success: true } rs)
                    s.Ram.Add(new RamSample(n, ts, Int(rs, 1), Int(rs, 2), Int(rs, 3), Int(rs, 4)));
                else if (DeltaRamRegex().Match(line) is { Success: true } dr)
                    s.Ram.Add(new RamSample(n, ts, Int(dr, 1), null, Int(dr, 2), dr.Groups[3].Success ? Int(dr, 3) : null));
            }
            else if (line.Contains("GUI Resource", StringComparison.Ordinal) && GdiRegex().Match(line) is { Success: true } g)
            {
                s.Gdi.Add(new GdiSample(n, ts, Int(g, 1), Int(g, 2)));
            }
            else if (line.Contains("BIG_GAP", StringComparison.Ordinal) && BigGapRegex().Match(line) is { Success: true } gap
                     && double.TryParse(gap.Groups[1].Value, CultureInfo.InvariantCulture, out double secs))
            {
                s.SlowOperations.Add(new SlowOperation(n, ts, secs, gap.Groups[2].Value));
            }
            else if (line.TrimStart().StartsWith("Jrn.", StringComparison.Ordinal))
            {
                string logical = JoinContinuation(lines, ref i);
                if (CommandRegex().Match(logical) is { Success: true } cmd)
                {
                    s.UserActions.Add(new UserAction(n, ts, "Command", cmd.Groups[2].Value.Trim(), cmd.Groups[3].Value));
                    if (cmd.Groups[3].Value == "ID_APP_EXIT")
                        s.ExitRequestedAt ??= ts;
                }
                else if (UiEventRegex().Match(logical) is { Success: true } ui)
                    s.UserActions.Add(new UserAction(n, ts, ui.Groups[1].Value, ui.Groups[2].Value.Trim(), null));
                continue; // a joined line has no other content to inspect
            }

            if (line.Contains("[Jrn.CloseRevitMainFrame]", StringComparison.Ordinal))
                s.ExitRequestedAt ??= ts;

            if (line.Contains("TaskDialog \"", StringComparison.Ordinal) && TaskDialogTextRegex().Match(line) is { Success: true } td)
                pendingDialogText = td.Groups[1].Value;
            else if (DialogIdRegex().Match(line) is { Success: true } did)
            {
                var dialog = new DialogShown(n, ts, did.Groups[1].Value, pendingDialogText);
                s.Dialogs.Add(dialog);
                pendingDialogText = null;
                if (FailureDialogMarkers.Any(m => dialog.Id.Contains(m, StringComparison.OrdinalIgnoreCase)))
                    foreach (var sync in openSyncs.Values)
                        sync.Errors.Add(new LineRef(n, ts, $"Dialog {dialog.Id}: {dialog.Text}"));
            }

            if (line.Contains("SLOG", StringComparison.Ordinal) && SlogRegex().Match(line) is { Success: true } slog)
                HandleSlog(s, openSyncs, slog, n, line);

            if (line.Contains("Exception", StringComparison.Ordinal) && ExceptionRegex().IsMatch(line))
            {
                var ex = new LineRef(n, ts, line.Trim());
                s.Exceptions.Add(ex);
                foreach (var sync in openSyncs.Values)
                    sync.Errors.Add(ex);
            }

            if (line.Contains("Starting External", StringComparison.Ordinal) && AddInRegex().Match(line) is { Success: true } a)
                s.AddIns.Add(new AddIn(n, a.Groups[2].Value.Trim(), a.Groups[3].Value.Trim(), a.Groups[4].Value.Trim(), a.Groups[1].Value == "API_ERROR"));
            else if (line.Contains("API External Application added", StringComparison.Ordinal) && AddInLegacyRegex().Match(line) is { Success: true } la)
                s.AddIns.Add(new AddIn(n, la.Groups[1].Value.Trim(), la.Groups[2].Value.Trim(), la.Groups[2].Value.Trim(), false));

            if (line.Contains("[Jrn.BasicFileInfo]", StringComparison.Ordinal))
                AddModel(s, seenModels, line, n);

            if (s.UserName is null && line.Contains("User's Name", StringComparison.Ordinal) && UserTokenRegex().Match(line) is { Success: true } u)
            {
                s.UserName = u.Groups[1].Value.Trim();
                s.MachineName = u.Groups[2].Value.Trim();
            }
        }

        return s;
    }

    private static void HandleSlog(JournalSession s, Dictionary<string, SyncAttempt> open, Match slog, int n, string line)
    {
        string id = slog.Groups[1].Value;
        string kind = slog.Groups[3].Value;
        string sub = slog.Groups[4].Value;
        if (!DateTime.TryParseExact(slog.Groups[2].Value, "yyyy-MM-dd HH:mm:ss.fff", CultureInfo.InvariantCulture,
                DateTimeStyles.AssumeLocal, out var time))
            return;

        bool topLevel = sub.Length == 0;
        if (kind == ">" && topLevel)
        {
            var attempt = new SyncAttempt { StartLine = n, Start = time };
            open[id] = attempt;
            s.Syncs.Add(attempt);
        }
        else if (open.TryGetValue(id, out var attempt))
        {
            if (kind == "<" && topLevel)
            {
                attempt.End = time;
                attempt.EndLine = n;
                open.Remove(id);
            }
            else if (kind == ">" && sub == ":RL:Read")
                attempt.ReloadLatestCount++;
            else if (line.Contains("gaveUp", StringComparison.Ordinal))
                attempt.GaveUp = true;
        }
    }

    private static void AddModel(JournalSession s, HashSet<string> seen, string line, int n)
    {
        var attrs = FileInfoAttrRegex().Matches(line).ToDictionary(m => m.Groups[1].Value, m => m.Groups[2].Value.Trim());
        string? path = attrs.GetValueOrDefault("CentralModelPath") is { Length: > 0 } central ? central : attrs.GetValueOrDefault("LastSavePath");
        if (string.IsNullOrWhiteSpace(path) || !seen.Add(path))
            return;

        int? year = null;
        string? build = null;
        if (attrs.GetValueOrDefault("RevitBuildVersion") is { } v && SavedInRegex().Match(v) is { Success: true } sv)
        {
            year = int.Parse(sv.Groups[1].Value, CultureInfo.InvariantCulture);
            build = sv.Groups[2].Value;
        }
        s.Models.Add(new ModelInfo(n, path, attrs.GetValueOrDefault("Worksharing"), year, build));
    }

    /// <summary>Joins VBScript continuation lines ("... _") into one logical line and advances the index past them.</summary>
    private static string JoinContinuation(IReadOnlyList<string> lines, ref int i)
    {
        string current = lines[i];
        if (!current.TrimEnd().EndsWith(" _", StringComparison.Ordinal))
            return current;

        var sb = new System.Text.StringBuilder(current.TrimEnd()[..^1]);
        while (i + 1 < lines.Count)
        {
            string next = lines[++i].Trim();
            bool more = next.EndsWith(" _", StringComparison.Ordinal) || next == "_";
            sb.Append(' ').Append(more ? next[..^1].TrimEnd() : next);
            if (!more)
                break;
        }
        return sb.ToString();
    }

    private static int Int(Match m, int group) => int.Parse(m.Groups[group].ValueSpan, CultureInfo.InvariantCulture);
}
