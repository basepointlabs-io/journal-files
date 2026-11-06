using RevitJournal.Core.Analysis;
using RevitJournal.Core.Parsing;
using RevitJournal.Core.Symptoms;

namespace RevitJournal.Tests;

public class AdvisorTests
{
    private static readonly Analyzer Analyzer = Analyzer.CreateDefault();
    private static readonly SymptomAdvisor Advisor = SymptomAdvisor.CreateDefault(Analyzer.Rules);
    private static readonly DateTime Now = new(2026, 11, 6, 9, 0, 0);

    private static Symptom S(string id) => Advisor.Symptoms.Single(s => s.Id == id);

    private static AnalysisResult Session(string name, bool running, params string[] blocks) =>
        Analyzer.Analyze(JournalParser.Parse(name, Fixtures.Lines(blocks)), new AnalysisContext(running, []));

    [Fact]
    public void Catalog_has_the_three_start_symptoms_with_checklists()
    {
        Assert.Equal(["crashed", "wont-sync", "slow"], Advisor.Symptoms.Select(s => s.Id));
        Assert.All(Advisor.Symptoms, s => Assert.NotEmpty(s.Checklist.En));
    }

    [Fact]
    public void Sync_answer_uses_the_session_with_the_latest_sync_not_the_latest_start()
    {
        // Started first, but its (failed) sync is the most recent one — like two Revit windows side by side.
        var failing = Session("journal.0116.txt", false, Fixtures.Header, Fixtures.FailedCloudSync, """
              ' 1:< SLOG $dddd0004 2026-11-05 16:15:00.000 >STC
              ' 1:<   System.IO.FileNotFoundException: Could not load file or assembly 'X'.
              ' 1:< SLOG $dddd0004 2026-11-05 16:15:01.000 <STC
            """);
        var fine = Session("journal.0117.txt", false, Fixtures.Header.Replace("09:24:38.631", "09:42:54.810"), Fixtures.GoodSync);

        var d = Advisor.Diagnose(S("wont-sync"), [fine, failing], Now);

        Assert.Same(failing, d.Session);
        Assert.StartsWith("All 2 sync attempts failed", d.Headline);
        Assert.Contains(d.Findings, f => f.Rule.Id == "metric-sync-failed");
    }

    [Fact]
    public void Another_window_syncing_fine_counts_only_on_the_same_computer()
    {
        AnalysisResult Failing(string folder) => Session(Path.Combine(folder, "journal.0116.txt"), false, Fixtures.Header, Fixtures.FailedCloudSync);
        AnalysisResult Fine(string folder) => Session(Path.Combine(folder, "journal.0117.txt"), false,
            Fixtures.Header.Replace("09:24:38.631", "09:42:54.810"), Fixtures.GoodSync.Replace("11:4", "11:1"));

        var samePc = Advisor.Diagnose(S("wont-sync"), [Failing("pc1"), Fine("pc1")], Now);
        Assert.Contains(samePc.Facts, f => f.StartsWith("Another Revit window synchronized"));

        // A journal someone sent from another computer must not be compared with this computer's sessions.
        var otherPc = Advisor.Diagnose(S("wont-sync"), [Failing("from-colleague"), Fine("pc1")], Now);
        Assert.DoesNotContain(otherPc.Facts, f => f.StartsWith("Another Revit window synchronized"));
    }

    [Fact]
    public void Crash_answer_explains_the_last_abnormal_session_and_hint_points_to_it()
    {
        var crash = Session("journal.0001.txt", false, Fixtures.Header, """
              'E 05-Nov-2026 10:00:00.000;   0:<
              Jrn.Command "Ribbon"  , "Move selected objects , ID_EDIT_MOVE"
            """);
        var closed = Session("journal.0002.txt", false, Fixtures.Header, Fixtures.NormalEnd);

        var d = Advisor.Diagnose(S("crashed"), [closed, crash], Now);
        Assert.Same(crash, d.Session);
        Assert.StartsWith("Revit 2025.4.2 stopped unexpectedly yesterday at 10:00", d.Headline);
        Assert.Contains(d.Facts, f => f.Contains("ID_EDIT_MOVE"));

        var hint = Advisor.Suggest([closed, crash], Now);
        Assert.Equal("crashed", hint?.Symptom.Id);
    }

    [Fact]
    public void Slow_answer_prefers_the_running_session()
    {
        var older = Session("journal.0001.txt", false, Fixtures.Header, Fixtures.NormalEnd);
        var running = Session("journal.0002.txt", true, Fixtures.Header, "  ' 1:< SLOG $eeee0005 2026-11-05 08:50:00.000 >STC ");

        var d = Advisor.Diagnose(S("slow"), [older, running], Now);

        Assert.Same(running, d.Session);
        Assert.Contains("is running", d.Headline);
        Assert.Contains(d.Facts, f => f.Contains("busy synchronizing"));
    }

    [Fact]
    public void Without_a_matching_journal_the_answer_falls_back_to_the_checklist()
    {
        var closed = Session("journal.0001.txt", false, Fixtures.Header, Fixtures.NormalEnd);

        var d = Advisor.Diagnose(S("wont-sync"), [closed], Now);

        Assert.False(d.FoundSpecificCause);
        Assert.Null(d.Session);
    }
}
