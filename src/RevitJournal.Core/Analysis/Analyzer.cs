using RevitJournal.Core.Model;
using RevitJournal.Core.Parsing;
using RevitJournal.Core.Rules;

namespace RevitJournal.Core.Analysis;

/// <summary>Turns a parsed journal into findings and a plain-English summary.</summary>
public sealed class Analyzer(RuleSet rules)
{
    private static readonly HashSet<string> SessionEndRules = ["metric-journal-no-normal-exit", "metric-shutdown-incomplete"];

    private readonly RuleMatcher _matcher = new(rules);

    public static Analyzer CreateDefault() => new(RuleSet.LoadEmbedded());

    public RuleSet Rules { get; } = rules;

    /// <summary>Thread-safe; the journal text is released afterwards to keep memory low.</summary>
    public AnalysisResult AnalyzeFile(string path)
    {
        var context = AnalysisContext.ForFile(path);
        var result = Analyze(JournalParser.Parse(path), context);
        result.Session.ReleaseLines();
        return result;
    }

    public AnalysisResult Analyze(JournalSession session, AnalysisContext context)
    {
        var status = context.IsBeingWritten ? SessionStatus.Running
            : session.EndedNormally ? SessionStatus.ClosedNormally
            : session.ExitRequestedAt is not null ? SessionStatus.ShutdownIncomplete
            : SessionStatus.EndedAbnormally;

        var byId = new Dictionary<string, Finding>(StringComparer.Ordinal);

        foreach (var (id, lines) in _matcher.Match(session))
        {
            var finding = GetOrAdd(byId, id);
            finding?.AddEvidence(lines);
        }

        foreach (var (id, check) in Metrics.All)
        {
            if (check(session, status, context) is { } facts && GetOrAdd(byId, id) is { } finding)
            {
                finding.Facts.AddRange(facts);
                if (finding.HitCount == 0)
                    finding.HitCount = 1;
            }
        }

        var crash = status == SessionStatus.EndedAbnormally ? CrashContext.Build(session) : null;
        if (crash is not null)
        {
            // With an element or a drag we can say exactly what to do; otherwise the context still tells where it happened.
            string id = crash.HasElement || crash.Drag is not null ? "context-crash-while-editing" : "metric-journal-no-normal-exit";
            if (GetOrAdd(byId, id) is { } f)
            {
                f.Facts.InsertRange(0, crash.Describe());
                f.Steps.AddRange(crash.Steps());
                f.HitCount = Math.Max(f.HitCount, 1);
            }
        }
        AddOrphans(session, byId);
        if (byId.TryGetValue("metric-sync-failed", out var syncFailed))
            syncFailed.Steps.AddRange(SyncAdvice.Steps(session));

        var findings = MergeRelated(byId);
        var lastActions = session.UserActions.TakeLast(10).ToList();

        return new AnalysisResult
        {
            Session = session,
            Status = status,
            Headline = Headline(status, findings),
            Summary = Summarize(session, status),
            Findings = findings,
            LastActions = lastActions,
            Context = crash,
        };
    }

    /// <summary>Tags and families whose host in a linked model was deleted, grouped by link, with their element ids.</summary>
    private void AddOrphans(JournalSession session, Dictionary<string, Finding> byId)
    {
        if (session.Orphans.Count == 0 || GetOrAdd(byId, "observed-orphaned-hosted-on-link") is not { } f)
            return;
        f.Steps.Add("Collaborate tab > Reconcile Hosting lists all orphaned tags and elements: right-click one and choose Pick Host, or delete it.");
        foreach (var link in session.Orphans.GroupBy(o => o.LinkId))
        {
            var ids = link.Select(o => o.ElementId).Distinct().ToList();
            f.HitCount += ids.Count;
            f.Facts.Add($"{ids.Count} tag(s)/families lost their host in the link with id {link.Key}.");
            const int perStep = 40;
            for (int i = 0; i < ids.Count; i += perStep)
            {
                var chunk = ids.Skip(i).Take(perStep);
                f.Steps.Add($"To select them directly: Manage tab > Inquiry > Select by ID, paste: {string.Join("; ", chunk)}"
                            + (ids.Count > perStep ? $"  (part {i / perStep + 1} of {(ids.Count + perStep - 1) / perStep})" : ""));
            }
        }
        f.Steps.Add("With a tag or element selected: click Pick New Host on the ribbon and pick the new host in the link, or delete it if it is no longer needed. Pin elements first so they do not move.");
        f.Steps.Add("If many elements are affected after a link update, ask the author of the linked model whether those elements were deleted on purpose before you delete the tags.");
    }

    private Finding? GetOrAdd(Dictionary<string, Finding> byId, string id)
    {
        if (byId.TryGetValue(id, out var f))
            return f;
        if (Rules[id] is not { } rule)
            return null; // a metric without a rule entry is not reported
        return byId[id] = new Finding { Rule = rule, Severity = rule.Severity };
    }

    /// <summary>
    /// The same journal line is often explained by several sources (Autodesk Help + forums). Show the most
    /// trustworthy one and attach the others to it instead of listing duplicates.
    /// </summary>
    private static List<Finding> MergeRelated(Dictionary<string, Finding> byId)
    {
        var ranked = byId.Values
            .OrderByDescending(f => f.Rule.Confidence)
            .ThenByDescending(f => f.Severity)
            .ToList();
        var consumed = new HashSet<string>(StringComparer.Ordinal);
        var result = new List<Finding>();

        foreach (var f in ranked)
        {
            if (!consumed.Add(f.Rule.Id))
                continue;
            foreach (string relatedId in f.Rule.Related)
            {
                if (byId.TryGetValue(relatedId, out var other) && consumed.Add(relatedId))
                {
                    f.AlsoMatched.Add(other.Rule);
                    if (other.Severity > f.Severity)
                        f.Severity = other.Severity;
                    f.Facts.AddRange(other.Facts);
                }
            }
            result.Add(f);
        }

        // Within a severity, specific problems come before the generic "how the session ended" findings,
        // which the summary already states.
        return [.. result
            .OrderByDescending(f => f.Severity)
            .ThenBy(f => SessionEndRules.Contains(f.Rule.Id))
            .ThenByDescending(f => f.Rule.Confidence)
            .ThenByDescending(f => f.HitCount)];
    }

    private static string Headline(SessionStatus status, List<Finding> findings)
    {
        var top = findings.FirstOrDefault(f => f.Severity >= Severity.High);
        if (top is not null)
            return top.Rule.Title.En;
        return status switch
        {
            SessionStatus.Running => "Revit is running; no serious problems recorded so far",
            SessionStatus.ClosedNormally => "Revit closed normally; no serious problems found",
            SessionStatus.ShutdownIncomplete => "Revit did not finish shutting down after you closed it",
            _ => "Revit did not close normally",
        };
    }

    private static List<string> Summarize(JournalSession s, SessionStatus status)
    {
        var lines = new List<string>();

        string version = s.Release is null ? "Revit" : $"Revit {s.Release}";
        string when = s.Start is { } start
            ? $"{Fmt.Stamp(start)} – {Fmt.Time(s.LastTimestamp)}" + (s.Duration is { } d ? $" ({Fmt.Duration(d)})" : "")
            : "unknown time";
        lines.Add($"{version} session {when}.");

        lines.Add(status switch
        {
            SessionStatus.Running => $"Revit still has this journal open: the session is running or Revit is not responding. Last recorded activity: {Fmt.Time(s.LastTimestamp)}.",
            SessionStatus.ClosedNormally => "Revit was closed normally.",
            SessionStatus.ShutdownIncomplete => $"You closed Revit at {Fmt.Time(s.ExitRequestedAt)}, but it stopped before finishing its shutdown (the journal ends at {Fmt.Time(s.LastTimestamp)}).",
            _ => $"Revit did not close normally: the journal stops at {Fmt.Time(s.LastTimestamp)} (crash, freeze or forced close).",
        });

        if (s.Models.Count > 0)
            lines.Add($"Models opened: {string.Join(", ", s.Models.Select(m => Fmt.FileName(m.Path)))}.");

        if (s.Syncs.Count > 0)
        {
            int ok = s.Syncs.Count(x => x.Succeeded);
            int failed = s.Syncs.Count(x => x.Finished && !x.Succeeded);
            int open = s.Syncs.Count(x => !x.Finished);
            lines.Add($"Synchronize with Central: {s.Syncs.Count} attempts, {ok} succeeded"
                      + (failed > 0 ? $", {failed} failed" : "")
                      + (open > 0 ? $", {open} not finished" : "") + ".");
        }

        if (s.Ram.Count > 0)
        {
            var min = s.Ram.MinBy(r => r.AvailableMb)!;
            string total = s.TotalRamMb is { } t ? $"Computer RAM {Fmt.Mb(t)}; " : "";
            string peak = Metrics.PeakRevitRam(s) is { } p ? $"; Revit peak {Fmt.Mb(p)}" : "";
            lines.Add($"{total}lowest free {Fmt.Mb(min.AvailableMb)} at {Fmt.Time(min.Time)}{peak}.");
        }

        if (s.AddIns.Count > 0)
        {
            int third = s.AddIns.Count(a => !a.IsAutodesk);
            int failedAddIns = s.AddIns.Count(a => a.Failed);
            lines.Add($"Add-ins started: {s.AddIns.Count} ({third} third-party" + (failedAddIns > 0 ? $", {failedAddIns} failed" : "") + ").");
        }

        return lines;
    }
}
