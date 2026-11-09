using RevitJournal.Core.Analysis;
using RevitJournal.Core.Rules;

namespace RevitJournal.Core.Symptoms;

/// <summary>The answer to "Revit crashed / won't sync / is slow": the relevant session and its top findings.</summary>
public sealed class Diagnosis
{
    public required Symptom Symptom { get; init; }
    public required string Headline { get; init; }

    /// <summary>The session the answer is based on; null when no journal fits the symptom.</summary>
    public AnalysisResult? Session { get; init; }
    public List<string> Facts { get; } = [];
    public List<Finding> Findings { get; } = [];

    public bool FoundSpecificCause => Findings.Count > 0;
}

/// <summary>Shown on the start screen before the user picks anything.</summary>
public sealed record Hint(Symptom Symptom, AnalysisResult Session, string Text);

public sealed class SymptomAdvisor(RuleSet rules, IReadOnlyList<Symptom> symptoms)
{
    public const int MaxFindings = 3;

    public IReadOnlyList<Symptom> Symptoms { get; } = symptoms;

    public static SymptomAdvisor CreateDefault(RuleSet rules) => new(rules, SymptomCatalog.LoadEmbedded());

    public Diagnosis Diagnose(Symptom symptom, IReadOnlyCollection<AnalysisResult> results, DateTime now)
    {
        // Ordered by when each session was last active, not when it started: sessions overlap.
        var newestFirst = results.Where(r => r.Session.LastTimestamp is not null).OrderByDescending(r => r.Session.LastTimestamp).ToList();
        return symptom.Journal switch
        {
            "last-abnormal-end" => Crashed(symptom, newestFirst, now),
            "last-sync" => WontSync(symptom, newestFirst, now),
            _ => Slow(symptom, newestFirst, now),
        };
    }

    /// <summary>Something worth pointing at right away: a crash or failed sync in the last 24 hours.</summary>
    public Hint? Suggest(IReadOnlyCollection<AnalysisResult> results, DateTime now)
    {
        var recent = results.Where(r => r.Session.LastTimestamp is { } t && now - t < TimeSpan.FromHours(24))
            .OrderByDescending(r => r.Session.LastTimestamp).ToList();

        // Only proven crashes and hangs: a forced close (Task Manager, Windows shutdown) is not news to the user.
        if (recent.FirstOrDefault(r => r.EndKind is EndKind.Crash or EndKind.Hang) is { } crash && Find("last-abnormal-end") is { } c)
            return new Hint(c, crash, crash.EndKind == EndKind.Crash
                ? $"Revit crashed {Fmt.When(crash.Session.LastTimestamp!.Value, now)}."
                : $"Revit stopped responding and was closed {Fmt.When(crash.Session.LastTimestamp!.Value, now)}.");

        if (recent.FirstOrDefault(r => r.Session.Syncs.Any(x => x.Finished && !x.Succeeded)) is { } sync && Find("last-sync") is { } s)
        {
            int failed = sync.Session.Syncs.Count(x => x.Finished && !x.Succeeded);
            return new Hint(s, sync, $"{failed} Synchronize with Central attempt{(failed == 1 ? "" : "s")} failed " +
                                     $"{Fmt.When(sync.Session.LastTimestamp!.Value, now)}.");
        }
        return null;
    }

    private Symptom? Find(string journal) => Symptoms.FirstOrDefault(s => s.Journal == journal);

    private Diagnosis Crashed(Symptom symptom, List<AnalysisResult> sessions, DateTime now)
    {
        // The newest proven crash or hang; a session that merely lacks its shutdown lines only when there is nothing better.
        var crash = sessions.FirstOrDefault(r => r.EndKind is EndKind.Crash or EndKind.Hang)
                    ?? sessions.FirstOrDefault(r => r.Status == SessionStatus.EndedAbnormally);
        if (crash is null)
        {
            var closing = sessions.FirstOrDefault();
            string headline = closing?.Status == SessionStatus.ShutdownIncomplete
                ? $"No crash found. The last session stopped while Revit was closing ({Fmt.When(closing.Session.LastTimestamp!.Value, now)})."
                : "No crash found in the journals on this computer.";
            return new Diagnosis { Symptom = symptom, Headline = headline, Session = closing };
        }

        var d = new Diagnosis
        {
            Symptom = symptom,
            Session = crash,
            Headline = crash.EndKind switch
            {
                EndKind.Crash => $"{Version(crash)} crashed {Fmt.When(crash.Session.LastTimestamp!.Value, now)}",
                EndKind.Hang => $"{Version(crash)} stopped responding and was closed {Fmt.When(crash.Session.LastTimestamp!.Value, now)}",
                _ => $"No crash is recorded. {Version(crash)} closed without its shutdown lines {Fmt.When(crash.Session.LastTimestamp!.Value, now)}",
            },
        };
        d.Facts.AddRange(crash.EndEvidence);
        if (crash.Session.Models.LastOrDefault() is { } model)
            d.Facts.Add($"Open model: {Fmt.FileName(model.Path)}.");
        if (crash.Session.UserActions.LastOrDefault(IsUserCommand) is { } last)
            d.Facts.Add($"Last thing you did ({Fmt.Time(last.Time)}): {Describe(last)}.");
        if (crash.Session.Syncs.LastOrDefault(x => x.Succeeded) is { } synced)
            d.Facts.Add($"Last successful sync: {Fmt.Time(synced.End)}. Changes after that may be only in the local file.");
        else if (crash.Session.Syncs.Count == 0 && crash.Session.Models.Any(m => m.Worksharing is { Length: > 0 } w && w != "Not enabled"))
            d.Facts.Add("No Synchronize with Central in this session: check your local file for unsynchronized work.");

        // The same element in several crashes is the strongest hint there is.
        if (crash.Context is { } ctx)
        {
            foreach (long id in ctx.ElementIds)
            {
                var others = sessions.Where(r => !ReferenceEquals(r, crash) && r.Context?.ElementIds.Contains(id) == true).ToList();
                if (others.Count > 0)
                    d.Facts.Add($"Element {id} was also involved when Revit stopped {string.Join(", ", others.Select(o => Fmt.When(o.Session.LastTimestamp!.Value, now)))}. It is very likely the cause.");
            }
        }

        if (History.RepeatedCrashes(rules, sessions) is { } repeated)
            d.Findings.Add(repeated);
        AddFindings(d, crash, symptom);
        return d;
    }

    private Diagnosis WontSync(Symptom symptom, List<AnalysisResult> sessions, DateTime now)
    {
        // The session where a sync was attempted most recently.
        var session = sessions.Where(r => r.Session.Syncs.Count > 0).MaxBy(r => r.Session.Syncs[^1].Start);
        if (session is null)
            return new Diagnosis { Symptom = symptom, Headline = "No Synchronize with Central was found in the recent journals." };

        var syncs = session.Session.Syncs;
        int failed = syncs.Count(x => x.Finished && !x.Succeeded);
        var running = syncs.LastOrDefault(x => !x.Finished);
        string headline =
            running is not null && session.Status == SessionStatus.Running
                ? $"A sync has been running since {Fmt.Time(running.Start)} ({Fmt.Ago(now - running.Start)})"
            : failed == syncs.Count
                ? $"All {failed} sync attempt{(failed == 1 ? "" : "s")} failed {Fmt.When(syncs[^1].Start, now)}: your changes are only in your local copy"
            : failed > 0
                ? $"{failed} of {syncs.Count} sync attempts failed {Fmt.When(syncs[^1].Start, now)}"
            : $"Your last sync {Fmt.When(syncs[^1].End ?? syncs[^1].Start, now)} succeeded";

        var d = new Diagnosis { Symptom = symptom, Session = session, Headline = headline };
        if (session.Session.Models.FirstOrDefault() is { } model)
            d.Facts.Add($"Model: {Fmt.FileName(model.Path)}" + (model.Worksharing is { Length: > 0 } w ? $" ({w})." : "."));
        if (syncs.LastOrDefault(x => x.Succeeded) is { } ok)
            d.Facts.Add($"Last successful sync: {Fmt.Time(ok.End)}, took {Fmt.Duration(ok.Duration!.Value)}.");
        else if (failed > 0)
            d.Facts.Add("No sync succeeded in this session.");

        // Another Revit window syncing fine at the same time rules out the network and the cloud service.
        if (failed > 0 && syncs.FirstOrDefault(x => x.Finished && !x.Succeeded) is { } firstFail)
        {
            // Only journals from the same folder are the same computer; an opened file may come from someone else's PC.
            string? folder = Path.GetDirectoryName(session.Session.FilePath);
            var otherOk = sessions
                .Where(r => !ReferenceEquals(r, session)
                            && string.Equals(Path.GetDirectoryName(r.Session.FilePath), folder, StringComparison.OrdinalIgnoreCase))
                .SelectMany(r => r.Session.Syncs.Where(x => x.Succeeded && x.Start >= firstFail.Start.AddHours(-1) && x.Start <= syncs[^1].Start.AddHours(1))
                    .Select(x => (r, x)))
                .OrderBy(p => p.x.Start)
                .FirstOrDefault();
            if (otherOk.r is { } other)
                d.Facts.Add($"Another Revit window synchronized {(other.Session.Models.FirstOrDefault() is { } m ? Fmt.FileName(m.Path) : "a model")} " +
                            $"successfully at {Fmt.Time(otherOk.x.End)}: the network and the cloud service work, so the problem is inside the Revit window with this model. Restarting Revit is the first thing to try.");
        }
        AddFindings(d, session, symptom);
        return d;
    }

    private Diagnosis Slow(Symptom symptom, List<AnalysisResult> sessions, DateTime now)
    {
        var session = sessions.FirstOrDefault(r => r.Status == SessionStatus.Running) ?? sessions.FirstOrDefault();
        if (session is null)
            return new Diagnosis { Symptom = symptom, Headline = "No Revit journals were found on this computer." };

        var s = session.Session;
        string headline = session.Status == SessionStatus.Running && s.LastTimestamp is { } t
            ? $"{Version(session)} is running. Last recorded activity: {Fmt.Time(t)} ({Fmt.Ago(now - t)})"
            : $"{Version(session)} is not running now. Showing the last session ({Fmt.When(s.LastTimestamp ?? s.Start!.Value, now)})";

        var d = new Diagnosis { Symptom = symptom, Session = session, Headline = headline };
        if (s.Syncs.LastOrDefault(x => !x.Finished) is { } sync && session.Status == SessionStatus.Running)
            d.Facts.Add($"Revit is busy synchronizing with central since {Fmt.Time(sync.Start)}.");
        if (s.Opens.LastOrDefault(x => !x.Finished) is { } open && session.Status == SessionStatus.Running)
            d.Facts.Add($"Revit is opening a model since {Fmt.Time(open.Start)}.");
        if (s.Ram.LastOrDefault() is { } ram)
            d.Facts.Add($"Free memory at {Fmt.Time(ram.Time)}: {Fmt.Mb(ram.AvailableMb)}"
                        + (s.TotalRamMb is { } total ? $" of {Fmt.Mb(total)}." : "."));
        if (s.Duration is { TotalHours: >= 4 } d1)
            d.Facts.Add($"This Revit session has been open for {Fmt.Duration(d1)}; a restart frees memory.");
        AddFindings(d, session, symptom);
        return d;
    }

    private static string Version(AnalysisResult r) => r.Session.Release is { } rel ? $"Revit {rel}" : "Revit";

    private static void AddFindings(Diagnosis d, AnalysisResult session, Symptom symptom)
    {
        foreach (var f in session.Findings.Where(f => f.Severity >= Severity.Low && symptom.IsRelevant(f.Rule)))
        {
            if (d.Findings.Count >= MaxFindings)
                break;
            d.Findings.Add(f);
        }
    }

    /// <summary>Commands and add-in commands; not UI bookkeeping like "TabActivated" or "ModelBrowserIsOpenChangedEvent".</summary>
    private static bool IsUserCommand(Model.UserAction a) =>
        (a.CommandId is not null && a.CommandId is not ("ID_REVIT_MODEL_BROWSER_OPEN" or "ID_DISPLAY_PROFILE_DIALOG" or "ID_CANCEL_EDITOR"))
        || Metrics.AddInCommand(a) is not null;

    private static string Describe(Model.UserAction a) =>
        Metrics.AddInCommand(a) is { } addIn ? $"add-in command {addIn}"
        : string.IsNullOrWhiteSpace(a.Text) ? a.CommandId! : $"{Fmt.Trim(a.Text.Trim('"', ' '), 90)} ({a.CommandId})";
}
