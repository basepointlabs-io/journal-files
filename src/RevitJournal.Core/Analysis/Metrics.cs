using RevitJournal.Core.Model;

namespace RevitJournal.Core.Analysis;

/// <summary>
/// Computed checks for the rules in metrics.json (and metric rules elsewhere). Each returns the facts to show
/// when the check fires, or null. Thresholds follow the "metric" block of the rule.
/// Not implemented yet: metric-slow-model-open, metric-slow-local-file-creation, metric-long-unresponsive-gap,
/// metric-addin-command-before-crash, metric-repeated-crashes (folder level).
/// </summary>
internal static class Metrics
{
    public delegate List<string>? Check(JournalSession s, SessionStatus status, AnalysisContext ctx);

    public static readonly IReadOnlyDictionary<string, Check> All = new Dictionary<string, Check>
    {
        ["metric-journal-no-normal-exit"] = (s, status, _) => status == SessionStatus.EndedAbnormally
            ? [$"The journal stops at {Fmt.Time(s.LastTimestamp)} without Revit's normal shutdown lines."]
            : null,

        ["metric-shutdown-incomplete"] = (s, status, _) =>
        {
            if (status != SessionStatus.ShutdownIncomplete)
                return null;
            string last = s.Lines.LastOrDefault(l => !string.IsNullOrWhiteSpace(l))?.Trim() ?? "";
            return
            [
                $"Close was requested at {Fmt.Time(s.ExitRequestedAt)}; the journal ends at {Fmt.Time(s.LastTimestamp)}.",
                $"Last thing Revit wrote: {Fmt.Trim(last, 160)}",
            ];
        },

        ["metric-crash-dump-present"] = (_, _, ctx) => ctx.DumpFiles.Count > 0
            ? [.. ctx.DumpFiles.Select(f => $"Crash dump next to the journal: {Path.GetFileName(f)}")]
            : null,

        ["metric-ram-low-before-crash"] = (s, status, _) =>
        {
            if (status != SessionStatus.EndedAbnormally || s.Ram.Count == 0)
                return null;
            var last = s.Ram.LastOrDefault(r => r.Line > s.Lines.Count - 200);
            if (last is null || !IsLow(last.AvailableMb, s.TotalRamMb, 1024, 0.05))
                return null;
            return [$"Free RAM at the end of the journal: {Fmt.Mb(last.AvailableMb)}{OfTotal(s)} ({Fmt.Time(last.Time)})."];
        },

        ["metric-ram-available-low"] = (s, _, _) =>
        {
            if (s.Ram.Count == 0)
                return null;
            var min = s.Ram.MinBy(r => r.AvailableMb)!;
            if (!IsLow(min.AvailableMb, s.TotalRamMb, 1024, 0.10))
                return null;
            return [$"Lowest free RAM on the machine: {Fmt.Mb(min.AvailableMb)}{OfTotal(s)} at {Fmt.Time(min.Time)} (line {min.Line})."];
        },

        ["metric-revit-peak-ram-share-high"] = (s, _, _) =>
        {
            int? peak = PeakRevitRam(s);
            if (peak is null || s.TotalRamMb is not { } total || peak < 0.8 * total)
                return null;
            return [$"Revit used up to {Fmt.Mb(peak.Value)} of {Fmt.Mb(total)} RAM ({100.0 * peak.Value / total:F0}%)."];
        },

        ["metric-total-ram-below-recommended"] = (s, _, _) => s.TotalRamMb is { } total && total < 16384
            ? [$"This computer has {Fmt.Mb(total)} of RAM; Autodesk recommends at least 16 GB."]
            : null,

        ["metric-memory-growth-long-session"] = (s, _, _) =>
        {
            if (s.Duration is not { TotalHours: >= 8 } d)
                return null;
            var used = s.Ram.Where(r => r.RevitUsedMb is not null).ToList();
            if (used.Count < 2)
                return null;
            int growth = used[^1].RevitUsedMb!.Value - used[0].RevitUsedMb!.Value;
            return growth >= 8192
                ? [$"Session ran for {Fmt.Duration(d)} and Revit's memory grew by {Fmt.Mb(growth)}."]
                : null;
        },

        ["metric-gdi-resources-near-limit"] = (s, _, _) =>
        {
            if (s.Gdi.Count == 0)
                return null;
            var min = s.Gdi.MinBy(g => g.Available)!;
            return min.Available < 1000
                ? [$"Only {min.Available} of 10,000 Windows GDI handles were left at {Fmt.Time(min.Time)} (line {min.Line})."]
                : null;
        },

        ["metric-outdated-build"] = (s, _, _) =>
        {
            if (s.BuildDate is not { } bd || s.Start is not { } start)
                return null;
            int days = start.Date.Subtract(bd.ToDateTime(TimeOnly.MinValue)).Days;
            return days > 365
                ? [$"Revit {s.Release ?? ""} build {s.Build} is {days / 30} months old; newer updates are likely available."]
                : null;
        },

        ["metric-model-saved-in-different-build"] = (s, _, _) =>
        {
            int? year = RunningYear(s);
            var odd = s.Models.Where(m => m.SavedInYear == year && m.SavedInBuild is not null && s.Build is not null
                                          && !s.Build.StartsWith(m.SavedInBuild.Split('(')[0], StringComparison.Ordinal)).ToList();
            return odd.Count > 0
                ? [.. odd.Select(m => $"{Fmt.FileName(m.Path)} was last saved in build {m.SavedInBuild}; this session runs {s.Build}.")]
                : null;
        },

        ["metric-model-upgraded-on-open"] = (s, _, _) =>
        {
            int? year = RunningYear(s);
            var older = s.Models.Where(m => m.SavedInYear < year).ToList();
            return older.Count > 0
                ? [.. older.Select(m => $"{Fmt.FileName(m.Path)} was saved in Revit {m.SavedInYear} and is being upgraded to {year}.")]
                : null;
        },

        ["metric-slow-startup"] = (s, _, _) =>
        {
            var ready = s.UserActions.FirstOrDefault(a => a.CommandId is "ID_REVIT_MODEL_BROWSER_OPEN" or "ID_STARTUP_PAGE");
            if (ready?.Time is not { } t || s.Start is not { } start || (t - start).TotalSeconds < 120)
                return null;
            return [$"Revit needed {Fmt.Duration(t - start)} to start up."];
        },

        ["metric-long-sync"] = (s, _, _) => SyncsLongerThan(s, 300, 900),
        ["metric-very-long-sync"] = (s, _, _) => SyncsLongerThan(s, 900, double.MaxValue),

        ["metric-sync-aborted-or-unfinished"] = (s, status, _) =>
        {
            var facts = new List<string>();
            foreach (var sync in s.Syncs.Where(x => !x.Finished || x.GaveUp))
            {
                if (!sync.Finished && status == SessionStatus.Running)
                    facts.Add($"A Synchronize with Central started at {Fmt.Time(sync.Start)} is still running.");
                else if (!sync.Finished)
                    facts.Add($"Synchronize with Central started at {Fmt.Time(sync.Start)} never finished before the journal ended.");
                else
                    facts.Add($"Synchronize with Central at {Fmt.Time(sync.Start)} gave up waiting for the central model lock.");
            }
            return facts.Count > 0 ? facts : null;
        },

        ["metric-sync-many-reload-latest"] = (s, _, _) =>
        {
            var many = s.Syncs.Where(x => x.ReloadLatestCount >= 3).ToList();
            return many.Count > 0
                ? [.. many.Select(x => $"Sync at {Fmt.Time(x.Start)} had to reload others' changes {x.ReloadLatestCount} times.")]
                : null;
        },

        ["metric-sync-failed"] = (s, _, _) =>
        {
            var failed = s.Syncs.Where(x => x.Finished && x.Errors.Count > 0).ToList();
            if (failed.Count == 0)
                return null;
            int ok = s.Syncs.Count(x => x.Succeeded);
            var facts = new List<string>
            {
                $"{failed.Count} of {s.Syncs.Count} Synchronize with Central attempts failed ({ok} succeeded), " +
                $"from {Fmt.Time(failed[0].Start)} to {Fmt.Time(failed[^1].Start)}.",
            };
            if (ok == 0 && s.Models.Count > 0)
                facts.Add("No sync succeeded in this session: changes made since the model was opened are only in the local copy.");
            var firstError = failed[0].Errors[0];
            facts.Add($"First error (line {firstError.Line}): {Fmt.Trim(firstError.Text, 200)}");
            return facts;
        },

        ["metric-addin-failed-to-start"] = (s, _, _) =>
        {
            var failed = s.AddIns.Where(a => a.Failed).ToList();
            return failed.Count > 0
                ? [.. failed.Select(a => $"Add-in \"{a.Name}\" ({a.Vendor}) failed to start (line {a.Line}).")]
                : null;
        },

        ["metric-addin-inventory"] = (s, _, _) =>
        {
            var third = s.AddIns.Where(a => !a.IsAutodesk).Select(a => a.Name).Distinct().ToList();
            return third.Count >= 15
                ? [$"{third.Count} third-party add-ins are loaded: {string.Join(", ", third)}."]
                : null;
        },

        ["metric-journal-playback-session"] = (s, _, _) => s.IsPlayback
            ? ["This session was driven by a journal script (automation or batch tool), not by a person."]
            : null,
    };

    internal static int? PeakRevitRam(JournalSession s) =>
        s.Ram.Select(r => r.RevitPeakMb).Where(p => p is not null).Max();

    internal static int? RunningYear(JournalSession s) =>
        s.Release is { Length: >= 4 } r && int.TryParse(r[..4], out int y) ? y : null;

    private static bool IsLow(int availMb, int? totalMb, int absoluteMb, double fraction) =>
        availMb < absoluteMb || (totalMb is { } t && availMb < fraction * t);

    private static string OfTotal(JournalSession s) => s.TotalRamMb is { } t ? $" of {Fmt.Mb(t)}" : "";

    private static List<string>? SyncsLongerThan(JournalSession s, double minSeconds, double maxSeconds)
    {
        var slow = s.Syncs.Where(x => x.Duration?.TotalSeconds is { } d && d >= minSeconds && d < maxSeconds).ToList();
        return slow.Count > 0
            ? [.. slow.Select(x => $"Synchronize with Central at {Fmt.Time(x.Start)} took {Fmt.Duration(x.Duration!.Value)}.")]
            : null;
    }
}
