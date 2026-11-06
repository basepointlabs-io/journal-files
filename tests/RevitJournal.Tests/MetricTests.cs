using RevitJournal.Core.Analysis;
using RevitJournal.Core.Parsing;

namespace RevitJournal.Tests;

public class MetricTests
{
    private static readonly Analyzer Analyzer = Analyzer.CreateDefault();
    private static readonly AnalysisContext Closed = new(IsBeingWritten: false, DumpFiles: []);

    private static AnalysisResult Analyze(params string[] blocks) =>
        Analyzer.Analyze(JournalParser.Parse("journal.0001.txt", Fixtures.Lines(blocks)), Closed);

    private static Finding? Find(AnalysisResult r, string id) => r.Findings.FirstOrDefault(f => f.Rule.Id == id);

    [Fact]
    public void Slow_model_open_is_timed_from_slog()
    {
        var r = Analyze(Fixtures.Header, """
              ' 1:< SLOG $05033e0a 2026-11-05 10:00:00.000 >Open
              ' 1:< SLOG $05033e0a 2026-11-05 10:00:01.000 >Open:Local
              ' 1:< SLOG $05033e0a 2026-11-05 10:07:59.000 <Open:Local
              ' 1:< SLOG $05033e0a 2026-11-05 10:08:00.000 <Open
            """, Fixtures.NormalEnd);

        var f = Find(r, "metric-slow-model-open");
        Assert.NotNull(f);
        Assert.Contains("8 min 0 s", f.Facts[0]);
    }

    [Fact]
    public void Slow_local_copy_is_timed_from_create_new_local()
    {
        var r = Analyze(Fixtures.Header, """
            'C 05-Nov-2026 10:00:00.000;   0:< x
            ' 0:< createNewLocal: starting to create a new local file from "J:\Projects\Tower.rvt".
            'C 05-Nov-2026 10:06:00.000;   0:< x
            ' 0:< createNewLocal: finished creating the new local file from "J:\Projects\Tower.rvt".
            """, Fixtures.NormalEnd);

        Assert.Contains("Tower.rvt took 6 min 0 s", Find(r, "metric-slow-local-file-creation")!.Facts[0]);
    }

    [Fact]
    public void Long_operation_uses_revit_timings_not_idle_pauses()
    {
        var busy = Analyze(Fixtures.Header, """
            'C 05-Nov-2026 09:50:00.000;   0:< x 
            ' 652.792112   1:<<API External Command Time/ALL_GAPS   2.6/TurnOff_GAPs 26%
            """);
        Assert.Contains("10 min 52 s", Find(busy, "metric-long-unresponsive-gap")!.Facts[0]);

        var idle = Analyze(Fixtures.Header, """
            'C 05-Nov-2026 09:25:00.000;   0:< start 
            'C 05-Nov-2026 18:50:00.000;   0:< back after a long break 
            """);
        Assert.Null(Find(idle, "metric-long-unresponsive-gap"));
    }

    [Fact]
    public void Add_in_command_just_before_abnormal_end_is_reported()
    {
        var r = Analyze(Fixtures.Header, """
              'E 05-Nov-2026 12:39:40.000;   0:<
              Jrn.RibbonEvent "Execute external command:CustomCtrl_%Vendor%Export:Vendor.Plugins.Export"
            'C 05-Nov-2026 12:40:30.000;   0:< still exporting
            """);

        Assert.Equal(SessionStatus.EndedAbnormally, r.Status);
        Assert.Contains("Vendor.Plugins.Export", Find(r, "metric-addin-command-before-crash")!.Facts[0]);
    }

    [Fact]
    public void Repeated_crashes_need_three_abnormal_ends_within_a_week()
    {
        AnalysisResult Crash(int day) => Analyze($"""
            ' Release: 2025.4.2
            'C {day:00}-Nov-2026 09:00:00.000;  started recording journal file
            """);

        Assert.Null(History.RepeatedCrashes(Analyzer.Rules, [Crash(1), Crash(2)]));
        Assert.Null(History.RepeatedCrashes(Analyzer.Rules, [Crash(1), Crash(2), Crash(20)])); // 1 and 2 are outside the week before the 20th
        var f = History.RepeatedCrashes(Analyzer.Rules, [Crash(3), Crash(5), Crash(8)]);
        Assert.NotNull(f);
        Assert.Contains("3 times", f.Facts[0]);
    }
}
