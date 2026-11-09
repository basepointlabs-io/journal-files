using RevitJournal.Core.Rules;

namespace RevitJournal.Core.Analysis;

/// <summary>Checks that look across several journals of the same computer.</summary>
public static class History
{
    public const int CrashThreshold = 3;
    public static readonly TimeSpan Window = TimeSpan.FromDays(7);

    /// <summary>metric-repeated-crashes: 3+ confirmed crashes within the 7 days before the newest session. Forced closes do not count.</summary>
    public static Finding? RepeatedCrashes(RuleSet rules, IEnumerable<AnalysisResult> results)
    {
        if (rules["metric-repeated-crashes"] is not { } rule)
            return null;

        var all = results.Where(r => r.Session.Start is not null).ToList();
        if (all.Count == 0)
            return null;
        DateTime newest = all.Max(r => r.Session.Start!.Value);
        var crashes = all
            .Where(r => r.EndKind == EndKind.Crash && newest - r.Session.Start!.Value <= Window)
            .OrderBy(r => r.Session.Start)
            .ToList();
        if (crashes.Count < CrashThreshold)
            return null;

        var finding = new Finding { Rule = rule, Severity = rule.Severity, HitCount = crashes.Count };
        finding.Facts.Add($"Revit crashed {crashes.Count} times (each confirmed by a dump, a crash record or a Windows event) between {Fmt.Stamp(crashes[0].Session.Start)} and {Fmt.Stamp(crashes[^1].Session.Start)}.");

        // A shared model or add-in among the crashes is the most useful hint.
        var models = crashes
            .Select(r => r.Session.Models.LastOrDefault()?.Path)
            .Where(p => p is not null)
            .GroupBy(p => Fmt.FileName(p!))
            .Where(g => g.Count() >= 2)
            .OrderByDescending(g => g.Count());
        foreach (var g in models.Take(3))
            finding.Facts.Add($"{g.Count()} of them had {g.Key} open.");

        var addIns = crashes
            .Select(r => r.Session.UserActions.Select(Metrics.AddInCommand).LastOrDefault(c => c is not null))
            .Where(c => c is not null)
            .GroupBy(c => c!)
            .Where(g => g.Count() >= 2)
            .OrderByDescending(g => g.Count());
        foreach (var g in addIns.Take(3))
            finding.Facts.Add($"In {g.Count()} of them the last add-in command used was {g.Key}.");

        return finding;
    }
}
