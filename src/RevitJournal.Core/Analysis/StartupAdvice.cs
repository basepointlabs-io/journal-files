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
        if (endKind == EndKind.Crash)
            f.Severity = Rules.Severity.Critical;
        else if (endKind == EndKind.Unconfirmed && ctx.IsLocal && !blockedRestart && ctx.Disk is not { FreeMb: < DiskAdvice.AlmostFullMb })
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
                      "This start came right after it; Autodesk's steps for a Revit that does not start again are below.");
        return facts;
    }

    public static List<string> NotCompletedSteps(JournalSession s, AnalysisContext ctx)
    {
        var steps = new List<string>();
        if (SilentExitBefore(s, ctx) is not null || s.LicenseReadyAt is null)
        {
            steps.Add(AutodeskArticle.StoppedWorking.Cite(
                $"Restart the computer. Then, before starting Revit, check that the folder {SessionFolder(s)} is empty: with no Revit open there should be nothing in it. If files are there, move them to another location (for example the Desktop) and start Revit."));
            steps.Add(AutodeskArticle.NoResponseAtLaunch.Cite(
                "Check the licensing services: type services.msc in Windows Search and verify that Autodesk Desktop Licensing Service, FLEXnet Licensing Service and FLEXnet Licensing Service 64 are listed as running. If they are disabled, Revit cannot start."));
        }
        if (s.LicenseReadyAt is not null)
            steps.Add(DisableAddInsStep(s, s.AddIns.LastOrDefault(a => !a.IsAutodesk) is { } addIn
                ? $"Revit stopped while loading add-ins; the last third-party one that started was \"{addIn.Name}\". Disable the add-ins and start Revit again"
                : "Revit stopped while loading add-ins. Disable the add-ins and start Revit again"));
        steps.Add(EventViewerStep(s));
        steps.Add(AutodeskArticle.SplashScreen.Cite("If Revit still does not start: install missing Revit updates, then reinstall the Autodesk Desktop Licensing Service" +
                                                    (Metrics.RunningYear(s) is >= 2024 or null ? " and the Autodesk Identity Manager component." : " and the Autodesk Single Sign-On component.")));
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
        var active = LastActiveAddIns(s);
        return
        [
            EventViewerStep(s),
            DisableAddInsStep(s, active.Count > 0
                ? $"Disable the add-ins and start Revit again; the ones active when Revit closed were {string.Join(", ", active)}"
                : "Disable the add-ins and start Revit again"),
            AutodeskArticle.ClosedUnexpectedly.Cite("Make sure Revit has the latest update installed, and install pending Windows updates."),
            AutodeskArticle.SplashScreen.Cite("Security software can conflict with Revit: ask IT to test with it disabled, then start Revit again."),
            AutodeskArticle.StoppedWorking.Cite(
                $"If Revit does not start again afterwards: restart the computer and check that the folder {SessionFolder(s)} is empty before starting Revit; move any files in it to another location."),
        ];
    }

    /// <summary>Where Revit keeps the queues its helper process uses; leftovers of a session that vanished stay there.</summary>
    private static string SessionFolder(JournalSession s) => Metrics.RunningYear(s) switch
    {
        >= 2024 => @"C:\ProgramData\boost_interprocess",
        null => @"C:\ProgramData\boost_interprocess (Revit 2024 and later) or C:\ProgramData\RevitInterProcess (2023 and earlier)",
        _ => @"C:\ProgramData\RevitInterProcess",
    };

    /// <summary>Autodesk's way to disable add-ins differs by version: the Add-Ins Manager from 2025.3, moving files before.</summary>
    private static string DisableAddInsStep(JournalSession s, string what)
    {
        int? year = Metrics.RunningYear(s);
        string how = year >= 2026 || (year == 2025 && s.Release is { } r && Version.TryParse(r, out var v) && v >= new Version(2025, 3))
            ? "open the Add-ins Manager, click Disable all Add-ins (with cloud models: clear the check mark at the top of the column, then check Autodesk Collaborate again), click OK and restart Revit"
            : $"close Revit and temporarily move the add-in files out of C:\\ProgramData\\Autodesk\\Revit\\Addins\\{year?.ToString(System.Globalization.CultureInfo.InvariantCulture) ?? "[Version]"}; if you work with cloud models keep Autodesk.Collaborate.addin, ExportViewSelectorApp.addin and the ExportViewSelectorAddin folder there";
        return AutodeskArticle.DisableAddIns.Cite($"{what}: {how}. Put them back one at a time to find the one responsible.");
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

    private static string EventViewerStep(JournalSession s) => AutodeskArticle.EventViewer.Cite(
        $"Look for what ended Revit: Windows Event Viewer > Custom Views > Administrative Events, entries for Revit.exe at {Fmt.Time(s.LastTimestamp)}. The entry names the faulting module.");
}
