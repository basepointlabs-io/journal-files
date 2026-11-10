using RevitJournal.Core.Model;
using RevitJournal.Core.Parsing;
using RevitJournal.Core.Rules;

namespace RevitJournal.Core.Analysis;

/// <summary>Turns a parsed journal into findings and a plain-English summary.</summary>
public sealed class Analyzer(RuleSet rules)
{
    private static readonly HashSet<string> SessionEndRules = ["metric-journal-no-normal-exit", "metric-shutdown-incomplete", "metric-crash-dump-present"];

    private readonly RuleMatcher _matcher = new(rules);

    public static Analyzer CreateDefault() => new(RuleSet.LoadEmbedded());

    public RuleSet Rules { get; } = rules;

    /// <summary>The compiled text rules, shared with live parsers so they match lines as they arrive.</summary>
    public RuleMatcher Matcher => _matcher;

    /// <summary>
    /// Results of finished sessions kept on disk, so a journal is read once and not on every start. Null (the
    /// default) analyzes every time.
    /// </summary>
    public AnalysisCache? Cache { get; set; }

    /// <summary>Thread-safe. One streaming pass: the journal text is never held in memory as a whole.</summary>
    /// <param name="useCache">False reads the journal again even when a cached result exists (the result is stored anew).</param>
    public AnalysisResult AnalyzeFile(string path, bool useCache = true)
    {
        // A journal Revit is still writing changes every second: never cached.
        bool running = File.Exists(path) && JournalReader.IsBeingWritten(path);
        if (useCache && !running && Cache?.TryGet(path) is { } cached)
            return cached;

        var context = AnalysisContext.ForFile(path);
        var parser = new JournalParser(path, _matcher);
        foreach (string line in JournalReader.EnumerateLines(path))
            parser.Feed(line);
        AddWorkerLogHits(parser.Session, context);
        var result = Analyze(parser.Session, context);
        return Cache is not null && !context.IsBeingWritten ? Cache.Put(path, result) : result;
    }

    /// <summary>
    /// Runs the text rules over the worker logs of the session and adds the matches to the journal's own, marked with
    /// their source ("worker1"). Only rules are matched: session facts (syncs, memory, commands) come from the journal.
    /// </summary>
    public void AddWorkerLogHits(JournalSession session, AnalysisContext context)
    {
        if (session.RuleHits is not { } hits)
            return;
        foreach (string log in context.WorkerLogs)
        {
            // journal.0116.worker1.log -> "worker1"
            string name = Path.GetFileNameWithoutExtension(log);
            string source = name[(name.LastIndexOf('.') + 1)..];
            try
            {
                DateTime? time = null;
                int n = 0;
                foreach (string line in JournalReader.EnumerateLines(log))
                {
                    n++;
                    if (JournalTimestamp.TryParse(line, out var t))
                        time = t;
                    _matcher.MatchLine(line, n, time, hits, source);
                }
                session.WorkerLogLines += n;
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                // A worker log that cannot be read does not stop the analysis of the journal.
            }
        }
    }

    public AnalysisResult Analyze(JournalSession session, AnalysisContext context)
    {
        var status = context.IsBeingWritten ? SessionStatus.Running
            : session.EndedNormally ? SessionStatus.ClosedNormally
            : session.ExitRequestedAt is not null ? SessionStatus.ShutdownIncomplete
            : SessionStatus.EndedAbnormally;

        var byId = new Dictionary<string, Finding>(StringComparer.Ordinal);

        foreach (var (id, lines) in session.RuleHits ?? _matcher.Match(session))
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

        // A journal without shutdown lines is only a proven crash with a dump, a crash record or a Windows event.
        var (endKind, endEvidence) = status == SessionStatus.EndedAbnormally
            ? CrashEvidence.Find(session, context)
            : (EndKind.None, new List<string>());
        var endSeverity = endKind switch { EndKind.Crash => Severity.Critical, EndKind.Hang => Severity.High, _ => Severity.Medium };
        if (byId.TryGetValue("metric-journal-no-normal-exit", out var noExit))
        {
            noExit.Severity = endSeverity;
            noExit.Facts.AddRange(endEvidence);
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
                f.Severity = endSeverity;
                if (endKind == EndKind.Unconfirmed && id == "context-crash-while-editing")
                    f.Facts.Insert(0, "Not confirmed as a crash: this is only the last activity recorded before the journal ends.");
            }
        }
        AddOrphans(session, byId);
        if (byId.TryGetValue("metric-sync-failed", out var syncFailed))
            syncFailed.Steps.AddRange(SyncAdvice.Steps(session));

        if (byId.TryGetValue(StartupAdvice.NotCompletedRule, out var notStarted))
        {
            notStarted.Steps.AddRange(StartupAdvice.NotCompletedSteps(session, context));
            StartupAdvice.Weigh(notStarted, session, context, endKind);
        }
        if (byId.TryGetValue(StartupAdvice.ClosedAfterRule, out var closedAfter))
        {
            closedAfter.Steps.AddRange(StartupAdvice.ClosedAfterSteps(session));
            StartupAdvice.Weigh(closedAfter, session, context, endKind);
        }

        var findings = MergeRelated(byId);
        var lastActions = session.UserActions.TakeLast(10).ToList();

        return new AnalysisResult
        {
            Session = session,
            Status = status,
            Headline = Headline(status, endKind, findings),
            Summary = Summarize(session, status, endKind, context),
            Findings = findings,
            LastActions = lastActions,
            Context = crash,
            EndKind = endKind,
            EndEvidence = endEvidence,
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
            .ThenByDescending(f => f.Rule.Id is StartupAdvice.NotCompletedRule or StartupAdvice.ClosedAfterRule) // why Revit is gone comes first
            .ThenByDescending(f => f.Rule.Confidence)
            .ThenByDescending(f => f.HitCount)];
    }

    private static string Headline(SessionStatus status, EndKind endKind, List<Finding> findings)
    {
        // A specific problem makes a better headline than "how the session ended".
        var top = findings.FirstOrDefault(f => f.Severity >= Severity.High && !SessionEndRules.Contains(f.Rule.Id));
        if (top is not null)
            return top.Rule.Title.En;
        return status switch
        {
            SessionStatus.Running => "Revit is running; no serious problems recorded so far",
            SessionStatus.ClosedNormally => "Revit closed normally; no serious problems found",
            SessionStatus.ShutdownIncomplete => "Revit did not finish shutting down after you closed it",
            _ => endKind switch
            {
                EndKind.Crash => "Revit crashed",
                EndKind.Hang => "Revit stopped responding and was closed",
                _ => "Revit closed without a shutdown record (no crash found)",
            },
        };
    }

    private static List<string> Summarize(JournalSession s, SessionStatus status, EndKind endKind, AnalysisContext context)
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
            _ => endKind switch
            {
                EndKind.Crash => $"Revit crashed: the journal stops at {Fmt.Time(s.LastTimestamp)}.",
                EndKind.Hang => $"Revit stopped responding and was closed: the journal stops at {Fmt.Time(s.LastTimestamp)}.",
                _ when !context.IsLocal => $"The journal stops at {Fmt.Time(s.LastTimestamp)} without shutdown lines and without a crash record. If the file was copied while Revit was running, nothing went wrong at that moment.",
                _ => $"Revit closed without its shutdown lines at {Fmt.Time(s.LastTimestamp)}, with no crash record: most likely it was closed forcibly (Task Manager, Windows shutdown, power loss).",
            },
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

        if (s.WorkerLogLines > 0)
            lines.Add($"The worker log (RevitWorker) of this session was checked as well: {s.WorkerLogLines.ToString("N0", System.Globalization.CultureInfo.InvariantCulture)} lines.");

        return lines;
    }
}
