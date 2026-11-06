using RevitJournal.Core.Analysis;
using RevitJournal.Core.Parsing;
using RevitJournal.Core.Rules;

namespace RevitJournal.Tests;

public class AnalyzerTests
{
    private static readonly Analyzer Analyzer = Analyzer.CreateDefault();
    private static readonly AnalysisContext Closed = new(IsBeingWritten: false, DumpFiles: []);

    private static AnalysisResult Analyze(AnalysisContext ctx, params string[] blocks) =>
        Analyzer.Analyze(JournalParser.Parse("journal.0001.txt", Fixtures.Lines(blocks)), ctx);

    [Fact]
    public void Embedded_rule_base_loads_with_unique_ids_and_valid_regexes()
    {
        var rules = RuleSet.LoadEmbedded();
        Assert.True(rules.Rules.Count > 100);
        Assert.Equal(rules.Rules.Count, rules.Rules.Select(r => r.Id).Distinct().Count());
        _ = new RuleMatcher(rules); // compiles every regex
        Assert.All(rules.Rules, r => Assert.NotEmpty(r.Fix.En));
    }

    [Fact]
    public void Failed_cloud_sync_is_the_headline()
    {
        var r = Analyze(Closed, Fixtures.Header, Fixtures.FailedCloudSync);

        Assert.Equal(SessionStatus.EndedAbnormally, r.Status);
        Assert.Equal(Severity.Critical, r.Findings[0].Severity);
        Assert.NotEqual("metric-journal-no-normal-exit", r.Findings[0].Rule.Id);
        Assert.Contains(r.Findings, f => f.Rule.Id == "metric-sync-failed");
        Assert.Contains(r.Summary, l => l.Contains("1 attempts, 0 succeeded, 1 failed"));
    }

    [Fact]
    public void Same_line_explained_by_two_sources_is_reported_once()
    {
        var r = Analyze(Closed, Fixtures.Header, Fixtures.FailedCloudSync);

        var crypto = r.Findings.Single(f => f.Rule.Id == "observed-cloud-sync-crypto-assembly-missing");
        Assert.Contains(crypto.AlsoMatched, a => a.Id == "forum-addin-assembly-load");
        Assert.DoesNotContain(r.Findings, f => f.Rule.Id == "forum-addin-assembly-load");
    }

    [Fact]
    public void Routine_lines_do_not_trigger_error_rules()
    {
        var r = Analyze(Closed, Fixtures.Header, Fixtures.RoutineLinesThatLookLikeErrors, Fixtures.NormalEnd);

        Assert.Equal(SessionStatus.ClosedNormally, r.Status);
        Assert.DoesNotContain(r.Findings, f => f.Rule.Category is "crash" or "file-corruption");
    }

    [Fact]
    public void Exit_without_finish_is_not_reported_as_a_crash()
    {
        var r = Analyze(Closed, Fixtures.Header, Fixtures.ExitRequested);

        Assert.Equal(SessionStatus.ShutdownIncomplete, r.Status);
        Assert.Contains(r.Findings, f => f.Rule.Id == "metric-shutdown-incomplete");
        Assert.DoesNotContain(r.Findings, f => f.Rule.Id == "metric-journal-no-normal-exit");
    }

    [Fact]
    public void Open_journal_is_running_and_unfinished_sync_is_in_progress()
    {
        string[] syncStarted = ["  ' 1:< SLOG $cccc0003 2026-11-05 12:00:00.000 >STC "];
        var r = Analyzer.Analyze(
            JournalParser.Parse("j", [.. Fixtures.Lines(Fixtures.Header), .. syncStarted]),
            new AnalysisContext(IsBeingWritten: true, DumpFiles: []));

        Assert.Equal(SessionStatus.Running, r.Status);
        var f = r.Findings.Single(x => x.Rule.Id == "metric-sync-aborted-or-unfinished");
        Assert.Contains(f.Facts, x => x.Contains("still running"));
    }
}
