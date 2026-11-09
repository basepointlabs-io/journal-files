using System.Diagnostics;
using RevitJournal.Core.Analysis;
using RevitJournal.Core.Parsing;

namespace RevitJournal.Core.Live;

public enum LiveAlertKind { SyncFailed, SyncTakingLong, LowMemory, NotResponding, SeriousError, Crashed, DiskLow, ResourcesNearLimit }

/// <summary>Something the user should know right now. <see cref="SymptomId"/> says which answer page to open.</summary>
public sealed record LiveAlert(LiveAlertKind Kind, string Title, string Message, string JournalPath, string SymptomId, DateTime At);

/// <summary>Current state of one running Revit, for the status card.</summary>
/// <param name="ProcessId">The Revit.exe process that writes this journal, when it could be told apart.</param>
public sealed record LiveStatus(
    string Version, string JournalPath, string? Model, DateTime? LastActivity, bool? Responding,
    string Sync, int? FreeRamMb, int? TotalRamMb, string? Busy, int? ProcessId = null);

public sealed record RevitProcess(int Id, string Version, bool Responding, DateTime StartTime);

/// <summary>Running Revit.exe processes; replaced in tests.</summary>
public interface IRevitProcesses
{
    IReadOnlyList<RevitProcess> List();
}

public sealed class WindowsRevitProcesses : IRevitProcesses
{
    public IReadOnlyList<RevitProcess> List()
    {
        var result = new List<RevitProcess>();
        foreach (var p in Process.GetProcessesByName("Revit"))
        {
            try
            {
                // ...\Autodesk\Revit 2025\Revit.exe -> "2025"
                string dir = System.IO.Path.GetFileName(System.IO.Path.GetDirectoryName(p.MainModule?.FileName) ?? "");
                string version = dir.StartsWith("Revit ", StringComparison.OrdinalIgnoreCase) ? dir[6..] : "";
                result.Add(new RevitProcess(p.Id, version, p.Responding, p.StartTime));
            }
            catch (Exception ex) when (ex is InvalidOperationException or System.ComponentModel.Win32Exception)
            {
                // The process exited or is not ours to inspect.
            }
            finally
            {
                p.Dispose();
            }
        }
        return result;
    }
}

/// <summary>
/// Watches the journals of every running Revit and raises alerts when something goes wrong, while it happens.
/// Call <see cref="Tick"/> every few seconds from one thread.
/// </summary>
public sealed class LiveMonitor(Analyzer analyzer, string? root = null, IRevitProcesses? processes = null, Func<DateTime>? clock = null,
    Func<IReadOnlyList<WindowsCrashEvent>>? crashEvents = null) : IDisposable
{
    public static readonly TimeSpan SyncLongAfter = TimeSpan.FromMinutes(10);
    public static readonly TimeSpan NotRespondingAfter = TimeSpan.FromMinutes(1);
    public static readonly TimeSpan CrashEventDelay = TimeSpan.FromSeconds(10);

    private readonly string _root = root ?? JournalLocator.DefaultRoot;
    private readonly IRevitProcesses _processes = processes ?? new WindowsRevitProcesses();
    private readonly Func<DateTime> _now = clock ?? (() => DateTime.Now);
    private readonly Func<IReadOnlyList<WindowsCrashEvent>> _crashEvents = crashEvents ?? (() => WindowsCrashLog.Recent(fresh: true));
    private readonly Dictionary<string, Tracked> _tracked = new(StringComparer.OrdinalIgnoreCase);

    private sealed class Tracked(LiveJournal journal, string version)
    {
        public LiveJournal Journal { get; } = journal;
        public string Version { get; } = version;
        public int FailedSyncsSeen;
        public int DialogsSeen;
        public int LongSyncAlertedFor = -1;   // StartLine of the sync already reported as long
        public DateTime? NotRespondingSince;
        public bool NotRespondingAlerted;
        public bool? Responding;
        public int? ProcessId;
        public int AnalyzedLines = -1;
        public bool? AnalyzedResponding;
        public AnalysisResult? Result;
        public DateTime? ReleasedAt;
    }

    public IReadOnlyList<LiveStatus> Status { get; private set; } = [];

    /// <summary>
    /// Journal path → up-to-date analysis of every followed session, plus (once) the final analysis of sessions that
    /// ended during the last <see cref="Tick"/>. Each result is built on a frozen copy, so other threads may read it.
    /// The app shows these instead of parsing a running journal again.
    /// </summary>
    public IReadOnlyDictionary<string, AnalysisResult> Results { get; private set; } = new Dictionary<string, AnalysisResult>();

    public List<LiveAlert> Tick()
    {
        var alerts = new List<LiveAlert>();
        DateTime now = _now();
        AttachRunningJournals();
        var procs = _processes.List();

        var results = new Dictionary<string, AnalysisResult>(StringComparer.OrdinalIgnoreCase);
        foreach (var (path, t) in _tracked.ToList())
        {
            t.Journal.Poll();
            if (JournalReader.IsBeingWritten(path))
            {
                CheckRunning(t, procs, now, alerts);
                // Analyze again only when something changed; the snapshot keeps readers off the live session.
                var s = t.Journal.Session;
                if (t.Result is null || s.LineCount != t.AnalyzedLines || t.Responding != t.AnalyzedResponding)
                {
                    t.Result = analyzer.Analyze(s.Snapshot(), new AnalysisContext(IsBeingWritten: true, DumpFiles: []));
                    t.AnalyzedLines = s.LineCount;
                    t.AnalyzedResponding = t.Responding;
                }
                results[path] = t.Result;
                continue;
            }
            // Revit released the journal: the session is over.
            t.Journal.Poll();
            // After a crash Windows needs a few seconds to write its event; wait for it before judging how it ended.
            t.ReleasedAt ??= now;
            var ended = t.Journal.Session;
            if (!ended.EndedNormally && ended.ExitRequestedAt is null && now - t.ReleasedAt < CrashEventDelay)
            {
                if (t.Result is not null)
                    results[path] = t.Result;
                continue;
            }
            var finalContext = AnalysisContext.ForFile(path) with { IsBeingWritten = false, IsLocal = true, CrashEvents = _crashEvents() };
            var finished = t.Journal.Session.Snapshot();
            analyzer.AddWorkerLogHits(finished, finalContext);
            var final = analyzer.Analyze(finished, finalContext);
            results[path] = final;
            if (EndedAbnormally(t, final) is { } crash)
                alerts.Add(crash);
            t.Journal.Dispose();
            _tracked.Remove(path);
        }

        Results = results;
        Status = [.. _tracked.Values.Where(t => t.ReleasedAt is null).Select(t => StatusOf(t, now))];
        return alerts;
    }

    /// <summary>Starts following journals that Revit currently has open. Past events are taken as known.</summary>
    private void AttachRunningJournals()
    {
        if (!Directory.Exists(_root))
            return;
        foreach (string versionDir in Directory.EnumerateDirectories(_root, "Autodesk Revit *"))
        {
            string folder = System.IO.Path.Combine(versionDir, "Journals");
            if (!Directory.Exists(folder))
                continue;
            string version = System.IO.Path.GetFileName(versionDir)["Autodesk Revit ".Length..];
            // Only recent journals can be open; several Revit windows of one version write separate journals.
            foreach (var j in JournalLocator.FindInFolder(folder, version)
                         .Where(j => _now() - j.LastWrite < TimeSpan.FromDays(2))
                         .OrderByDescending(j => j.LastWrite).Take(4))
            {
                if (_tracked.ContainsKey(j.Path) || !JournalReader.IsBeingWritten(j.Path))
                    continue;
                var live = new LiveJournal(j.Path, analyzer.Matcher);
                live.Poll();
                var s = live.Session;
                _tracked[j.Path] = new Tracked(live, version)
                {
                    FailedSyncsSeen = s.Syncs.Count(x => x.Finished && !x.Succeeded),
                    DialogsSeen = s.Dialogs.Count,
                    LongSyncAlertedFor = s.Syncs.LastOrDefault(x => !x.Finished)?.StartLine ?? -1,
                };
            }
        }
    }

    private void CheckRunning(Tracked t, IReadOnlyList<RevitProcess> procs, DateTime now, List<LiveAlert> alerts)
    {
        var s = t.Journal.Session;
        string revit = $"Revit {t.Version}";
        string path = t.Journal.Path;

        var failed = s.Syncs.Where(x => x.Finished && !x.Succeeded).ToList();
        if (failed.Count > t.FailedSyncsSeen)
        {
            t.FailedSyncsSeen = failed.Count;
            var lastOk = s.Syncs.LastOrDefault(x => x.Succeeded);
            string since = lastOk?.End is { } e ? $"since {Fmt.Time(e)}" : "since you opened the model";
            alerts.Add(new LiveAlert(LiveAlertKind.SyncFailed, "Synchronize with Central failed",
                $"{(failed.Count == 1 ? "Sync failed" : $"Sync failed {failed.Count} times")} in {ModelName(s) ?? "the model"}. Your changes {since} are only in your local copy: click Save now. Click here for the next steps.",
                path, "wont-sync", now));
        }

        if (s.Syncs.LastOrDefault(x => !x.Finished) is { } running && now - running.Start > SyncLongAfter && t.LongSyncAlertedFor != running.StartLine)
        {
            t.LongSyncAlertedFor = running.StartLine;
            alerts.Add(new LiveAlert(LiveAlertKind.SyncTakingLong, "Synchronize with Central is taking long",
                $"{revit} has been synchronizing {ModelName(s) ?? "the model"} for {Fmt.Duration(now - running.Start)}. It is still working; do not close Revit yet.",
                path, "slow", now));
        }


        foreach (var dialog in s.Dialogs.Skip(t.DialogsSeen))
        {
            if (dialog.Id.Contains("Serious_Error", StringComparison.OrdinalIgnoreCase) || dialog.Id.Contains("Fatal", StringComparison.OrdinalIgnoreCase))
                alerts.Add(new LiveAlert(LiveAlertKind.SeriousError, $"{revit} reported a serious error",
                    $"{dialog.Text ?? dialog.Id}. Click here to see what was happening and what to do.", path, "crashed", now));
        }
        t.DialogsSeen = s.Dialogs.Count;

        // "Not responding" comes from Windows, per process.
        var process = ProcessOf(t, procs);
        t.ProcessId = process?.Id;
        t.Responding = process?.Responding;
        if (t.Responding == false)
        {
            t.NotRespondingSince ??= now;
            if (!t.NotRespondingAlerted && now - t.NotRespondingSince >= NotRespondingAfter)
            {
                t.NotRespondingAlerted = true;
                alerts.Add(new LiveAlert(LiveAlertKind.NotResponding, $"{revit} is not responding",
                    $"For {Fmt.Duration(now - t.NotRespondingSince.Value)}. {BusyWith(s) ?? $"Last recorded activity: {Fmt.Time(s.LastTimestamp)}."} " +
                    "If it is syncing or opening a model, give it time before closing it.",
                    path, "slow", now));
            }
        }
        else
        {
            t.NotRespondingSince = null;
            t.NotRespondingAlerted = false;
        }
    }

    /// <summary>
    /// The Revit process that writes this journal: Revit starts the journal within seconds of starting itself, so with
    /// several windows of one version each journal belongs to the process whose start time is closest to its first line.
    /// </summary>
    private RevitProcess? ProcessOf(Tracked t, IReadOnlyList<RevitProcess> procs)
    {
        var sameVersion = procs.Where(p => p.Version == t.Version).ToList();
        var journals = _tracked.Values.Where(o => o.Version == t.Version && o.Journal.Session.Start is not null).ToList();
        if (sameVersion.Count <= 1 && journals.Count <= 1 || t.Journal.Session.Start is not { } start)
            return sameVersion.FirstOrDefault();

        static double Distance(RevitProcess p, DateTime journalStart) => Math.Abs((p.StartTime - journalStart).TotalSeconds);
        // Each process goes to the journal that started closest to it; this journal takes the nearest of its own.
        return sameVersion
            .Where(p => journals.MinBy(o => Distance(p, o.Journal.Session.Start!.Value)) == t)
            .MinBy(p => Distance(p, start));
    }

    private LiveAlert? EndedAbnormally(Tracked t, AnalysisResult result)
    {
        if (result.Status != SessionStatus.EndedAbnormally)
            return null;
        string what = result.Context?.Describe().FirstOrDefault(f => f.StartsWith("You were dragging", StringComparison.Ordinal))
                      ?? result.Context?.Describe().FirstOrDefault()
                      ?? "";
        string title = result.EndKind switch
        {
            EndKind.Crash => $"Revit {t.Version} crashed",
            EndKind.Hang => $"Revit {t.Version} stopped responding and was closed",
            _ => $"Revit {t.Version} closed unexpectedly",
        };
        return new LiveAlert(LiveAlertKind.Crashed, title,
            $"At {Fmt.Time(result.Session.LastTimestamp)}. {what} Click here to see what to do.".Replace("  ", " "),
            t.Journal.Path, "crashed", _now());
    }

    private LiveStatus StatusOf(Tracked t, DateTime now)
    {
        var s = t.Journal.Session;
        var lastSync = s.Syncs.LastOrDefault();
        string sync = lastSync is null ? "No sync yet"
            : !lastSync.Finished ? $"Syncing since {Fmt.Time(lastSync.Start)}"
            : lastSync.Succeeded ? $"Last sync OK at {Fmt.Time(lastSync.End)}"
            : $"Last sync FAILED at {Fmt.Time(lastSync.Start)}";
        return new LiveStatus(t.Version, t.Journal.Path, ModelName(s), s.LastTimestamp, t.Responding, sync,
            s.Ram.LastOrDefault()?.AvailableMb, s.TotalRamMb, BusyWith(s), t.ProcessId);
    }

    private static string? BusyWith(Model.JournalSession s) =>
        s.Syncs.LastOrDefault(x => !x.Finished) is { } sync ? $"Synchronizing since {Fmt.Time(sync.Start)}."
        : s.Opens.LastOrDefault(x => !x.Finished) is { } open ? $"Opening a model since {Fmt.Time(open.Start)}."
        : null;

    /// <summary>The model in the active view, else the last opened one; null on the Revit home screen.</summary>
    private static string? ModelName(Model.JournalSession s)
    {
        var view = s.Interactions.LastOrDefault(i => i.Kind == Model.InteractionKind.View);
        if (view?.Detail is { } doc)
            return s.DocumentName(doc);
        return s.Models.LastOrDefault() is { } m ? Fmt.FileName(m.Path) : null;
    }

    public void Dispose()
    {
        foreach (var t in _tracked.Values)
            t.Journal.Dispose();
        _tracked.Clear();
    }
}
