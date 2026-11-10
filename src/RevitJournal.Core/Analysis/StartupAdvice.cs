using RevitJournal.Core.Model;

namespace RevitJournal.Core.Analysis;

/// <summary>
/// Sessions that end around start-up, where Revit vanishes without an error message: it never finished starting, or
/// it closed by itself seconds after it had. The journal names the stage and the add-ins that were active.
/// </summary>
public static class StartupAdvice
{
    public const string NotCompletedRule = "observed-startup-not-completed";
    public const string ClosedAfterRule = "observed-closed-right-after-startup";

    /// <summary>A start this soon after a session that ended without shutdown lines is treated as a restart attempt.</summary>
    public static readonly TimeSpan RestartWindow = TimeSpan.FromMinutes(30);
    /// <summary>"Right after start-up": the journal stops within this time of the end of start-up.</summary>
    public static readonly TimeSpan AfterStartup = TimeSpan.FromSeconds(60);
    /// <summary>Below this much free space on the journal's drive, the disk is the first suspect.</summary>
    public const long LowDiskMb = 5 * 1024;
    private static readonly TimeSpan LastSeconds = TimeSpan.FromSeconds(10);

    /// <summary>The journal ends before Revit was ready, and the user had not asked for anything yet.</summary>
    public static bool NotCompleted(JournalSession s, SessionStatus status) =>
        status == SessionStatus.EndedAbnormally && s.StartupCompletedAt is null && s.Start is not null && !s.IsPlayback
        && s.Models.Count == 0 && s.Opens.Count == 0 && !s.UserActions.Any(a => a.CommandId is not null)
        && !s.Interactions.Any(i => i.Kind != InteractionKind.Tab);

    /// <summary>Start-up finished, and the journal stops moments later with no model opened.</summary>
    public static bool ClosedRightAfter(JournalSession s, SessionStatus status) =>
        status == SessionStatus.EndedAbnormally && s.StartupCompletedAt is { } ready && s.LastTimestamp is { } end
        && end - ready <= AfterStartup && s.Models.Count == 0 && s.Opens.Count == 0;

    /// <summary>The session before this one ended without shutdown lines shortly before this start.</summary>
    public static PreviousSession? SilentExitBefore(JournalSession s, AnalysisContext ctx) =>
        ctx.Previous is { EndedNormally: false } p && s.Start is { } start && p.End <= start && start - p.End <= RestartWindow ? p : null;

    /// <summary>
    /// On the computer the journal comes from, Windows records every crash and hang of Revit.exe. Without such a
    /// record Revit was closed from outside, which is rarely a fault of Revit, unless the start was blocked by a
    /// session that had just vanished.
    /// </summary>
    public static void Weigh(Finding f, JournalSession s, AnalysisContext ctx, EndKind endKind)
    {
        bool blockedRestart = f.Rule.Id == NotCompletedRule && s.LicenseReadyAt is null && SilentExitBefore(s, ctx) is not null;
        if (ctx.Disk is { } disk && disk.FreeMb < LowDiskMb)
        {
            // A full disk explains both symptoms at once, so it goes first and is never played down.
            f.Facts.Insert(0, $"Drive {disk.Name} had only {Fmt.Mb((int)Math.Min(disk.FreeMb, int.MaxValue))} free when this journal was analyzed ({Fmt.Stamp(disk.At)}). " +
                              "Without free disk space Revit cannot write its journal, temporary files and caches: it can close without a message and fail to start.");
            f.Steps.Insert(0, $"Free space on drive {disk.Name} first: empty %TEMP%, delete old local model copies and the collaboration cache you no longer need, then start Revit again. Keep at least 10 GB free.");
            if (f.Severity < Rules.Severity.High)
                f.Severity = Rules.Severity.High;
            return;
        }
        if (endKind == EndKind.Crash)
            f.Severity = Rules.Severity.Critical;
        else if (endKind == EndKind.Unconfirmed && ctx.IsLocal && !blockedRestart)
        {
            f.Severity = Rules.Severity.Medium;
            f.Facts.Add("Windows recorded no crash or hang of Revit.exe for this session, so it was most likely closed from outside: Task Manager, a script or security software.");
        }
    }

    public static List<string> NotCompletedFacts(JournalSession s, AnalysisContext ctx)
    {
        var facts = new List<string>();
        string after = s.Duration is not { } d ? "during start-up"
            : d.TotalSeconds < 1 ? "within a second of being started"
            : $"{Fmt.Duration(d)} after it was started";
        if (s.LicenseReadyAt is null)
            facts.Add($"Revit stopped {after}, in its first start-up step, before licensing was initialized. The journal has only {s.LineCount} lines.");
        else if (s.AddIns.LastOrDefault() is { } last)
            facts.Add($"Revit stopped {after}, while loading add-ins: {s.AddIns.Count} had started, the last one was \"{last.Name}\"" +
                      (last.IsAutodesk ? " (Autodesk)." : $" ({last.Vendor})."));
        else
            facts.Add($"Revit stopped {after}, after licensing was initialized and before the first add-in was loaded.");
        facts.Add("Revit writes its journal in blocks, so it may have got slightly further than the last line shows.");

        if (SilentExitBefore(s, ctx) is { } p)
            facts.Add($"The session before it ({p.Name}) ended without a shutdown record at {Fmt.Time(p.End)}, {Fmt.Duration(s.Start!.Value - p.End)} before this start. " +
                      (s.LicenseReadyAt is null
                          ? "Processes that session left behind are the most likely reason Revit does not start again."
                          : "Processes that session left behind may still be in the way."));
        return facts;
    }

    public static List<string> NotCompletedSteps(JournalSession s, AnalysisContext ctx)
    {
        var steps = new List<string>();
        if (SilentExitBefore(s, ctx) is not null || s.LicenseReadyAt is null)
        {
            steps.Add("Do not restart the computer yet. Open Task Manager (Ctrl+Shift+Esc) > Details and look for Revit.exe, RevitWorker.exe and CefSharp.BrowserSubprocess.exe left from the earlier session. Note which ones are there.");
            steps.Add("End each of them (right-click > End task), then start Revit again. If it starts, the leftover processes were blocking it and a restart of the computer is not needed.");
            steps.Add("If Revit still stops at the same point, ask IT to restart the Autodesk Desktop Licensing Service (services.msc), then start Revit again.");
        }
        if (s.LicenseReadyAt is not null && s.AddIns.LastOrDefault(a => !a.IsAutodesk) is { } addIn)
            steps.Add($"Revit stopped while loading add-ins. Disable \"{addIn.Name}\" first (the last third-party add-in that started) and start Revit again; then the ones that load after it.");
        steps.Add(EventViewerStep(s));
        return steps;
    }

    public static List<string> ClosedAfterFacts(JournalSession s)
    {
        var facts = new List<string>
        {
            $"Start-up finished at {Fmt.Time(s.StartupCompletedAt)}" + (s.StartupCompletedAt - s.Start is { } took ? $", {Fmt.Duration(took)} after Revit was started" : "") +
            $". The journal stops {Fmt.Duration(s.LastTimestamp!.Value - s.StartupCompletedAt!.Value)} later, before any model was opened, and no error was recorded.",
        };
        var active = LastActiveAddIns(s);
        if (active.Count > 0)
            facts.Add($"Third-party add-ins active in the last seconds: {string.Join(", ", active)}. That is a coincidence in time, not proof, but they are the first to check.");
        return facts;
    }

    public static List<string> ClosedAfterSteps(JournalSession s)
    {
        var steps = new List<string> { EventViewerStep(s) };
        var active = LastActiveAddIns(s);
        if (active.Count > 0)
            steps.Add($"Disable these add-ins first and start Revit again: {string.Join(", ", active)}. Move their .addin files out of C:\\ProgramData\\Autodesk\\Revit\\Addins\\<year> and %AppData%\\Autodesk\\Revit\\Addins\\<year>; put them back one at a time to find the one that closes Revit.");
        else
            steps.Add("Start Revit without third-party add-ins: move the .addin files out of C:\\ProgramData\\Autodesk\\Revit\\Addins\\<year> and %AppData%\\Autodesk\\Revit\\Addins\\<year>, then put them back one at a time.");
        steps.Add("If Windows recorded no error for Revit.exe at that time, something outside Revit ended the process: ask IT to check the antivirus / endpoint protection log for the same minute.");
        steps.Add("If Revit does not start again afterwards, end leftover Revit.exe, RevitWorker.exe and CefSharp.BrowserSubprocess.exe in Task Manager > Details instead of restarting the computer.");
        return steps;
    }

    /// <summary>Third-party add-ins Revit logged in the last seconds of the journal, most recent first.</summary>
    public static List<string> LastActiveAddIns(JournalSession s)
    {
        if (s.LastTimestamp is not { } end)
            return [];
        var autodesk = s.AddIns.Where(a => a.IsAutodesk).Select(a => a.Name).ToHashSet(StringComparer.OrdinalIgnoreCase);
        return [.. s.AddInActivity
            .Where(a => a.Time is { } t && end - t <= LastSeconds)
            .Select(a => a.Name)
            .Reverse()
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .Where(n => !autodesk.Contains(n) && !n.StartsWith("Autodesk", StringComparison.OrdinalIgnoreCase))
            .Take(6)];
    }

    private static string EventViewerStep(JournalSession s) =>
        $"Find out what ended Revit: Event Viewer > Windows Logs > Application, entries at {Fmt.Time(s.LastTimestamp)} with source \"Application Error\" (1000) or \".NET Runtime\" (1026) for Revit.exe. " +
        "\"Faulting module name\" is the component that failed. When this journal is analyzed on the computer it came from, that event is shown in this report.";
}
