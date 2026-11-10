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

    private const string BigModelWithLinks = """
        'C 05-Nov-2026 10:00:00.000;   0:< x
          ' 1:< RAM Statistics:    11185 /    32212      9769=InUse    21719=Peak
          ' 6:< Detect if missing elements for C:\\Cache\\P1\\aaaaaaaa-0000-0000-0000-000000000001.rvt: File Size: 1000000000, Element Count: 400000
          ' 6:< Detect if missing elements for C:\\Cache\\P1\\aaaaaaaa-0000-0000-0000-000000000001.rvt: File Size: 2147483648, Element Count: 436899
        ' 0:< ModelPath Created: Is server path = True, Region = "EMEA", Central server = "", Path = "CLD:\\\\EMEA\\{bbbbbbbb-0000-0000-0000-000000000000}Project\\{aaaaaaaa-0000-0000-0000-000000000001}Tower_ARC.rvt"
        ' 0:< ModelPath Created: Is server path = True, Region = "EMEA", Central server = "", Path = "CLD:\\\\EMEA\\{bbbbbbbb-0000-0000-0000-000000000000}Project\\{aaaaaaaa-0000-0000-0000-000000000002}Tower_MEP.rvt"
        '  3.123456       5:<<Load linked file: C:\\Cache\\P1\\LinkedModels\\aaaaaaaa-0000-0000-0000-000000000002.rvt
        '  1.123456       5:<<Load linked file: C:\\Cache\\P1\\LinkedModels\\aaaaaaaa-0000-0000-0000-000000000003.rvt
        '  3.123456       5:<<Load linked file: C:\\Cache\\P1\\LinkedModels\\aaaaaaaa-0000-0000-0000-000000000002.rvt
        """;

    [Fact]
    public void Model_size_is_compared_with_Autodesks_memory_guidance_and_links_are_sized_from_disk()
    {
        var onThisComputer = Closed with { FileSize = path => path.EndsWith("2.rvt", StringComparison.Ordinal) ? 800L * 1024 * 1024 : null };
        var session = JournalParser.Parse("journal.0001.txt", Fixtures.Lines(Fixtures.Header, BigModelWithLinks, Fixtures.NormalEnd));
        var f = Find(Analyzer.Analyze(session, onThisComputer), "metric-model-size-vs-ram");

        Assert.NotNull(f);
        Assert.Contains("Tower_ARC.rvt is 2.0 GB on disk (436,899 elements); this computer has 31.5 GB of RAM.", f.Facts[0]);   // latest figures, real name
        Assert.Contains("approximately 20 times the size of the compacted central file, here about 40.0 GB of RAM, more than this computer has", f.Facts[1]);
        Assert.Contains("loaded 2 linked model(s); 1 of them are on this computer and take 800 MB", f.Facts[2]);
        Assert.Contains("Tower_MEP.rvt 800 MB", f.Facts[2]);
        Assert.Contains("Estimate, not an Autodesk figure", f.Facts[3]);
        Assert.Contains("(2.8 GB together) gives about 55.6 GB of RAM, more than this computer has", f.Facts[3]);
        Assert.Contains(f.Steps, x => x.Contains("Tower_MEP.rvt") && x.EndsWith("[Autodesk: Memory Usage and Instability]"));
        Assert.All(f.Steps.Concat(f.Rule.Fix.En), x => Assert.Matches(@"\[Autodesk: .+\]$", x));

        // A journal from another computer: the model is still judged, the links are counted without sizes.
        var elsewhere = Find(Analyzer.Analyze(session, Closed), "metric-model-size-vs-ram");
        Assert.Contains("their sizes are not known here", elsewhere!.Facts[2]);
    }

    [Fact]
    public void Before_the_first_save_the_open_models_size_is_read_from_its_local_file()
    {
        // Revit logs "File Size" only at a save or sync; until then only the path of the local copy is known.
        var session = JournalParser.Parse("journal.0001.txt", Fixtures.Lines(Fixtures.Header, """
            ' 5:< SLOG  local="C:\Cache\P1\aaaaaaaa-0000-0000-0000-000000000001.rvt"
            ' 4:< SLOG  local="C:\Cache\P1\LinkedModels\aaaaaaaa-0000-0000-0000-000000000002.rvt"
            '  3.123456       5:<<Load linked file: C:\Cache\P1\LinkedModels\aaaaaaaa-0000-0000-0000-000000000002.rvt
            """));
        long? OnDisk(string path) => path.Contains("LinkedModels") ? 300L * 1024 * 1024 : 700L * 1024 * 1024;

        var load = ModelSizeAdvice.Load(session, OnDisk, ramMb: 12_000);

        Assert.Equal(new ModelLoad("aaaaaaaa-0000-0000-0000-000000000001.rvt", 700, null, 1, 1, 300, "about 19.5 GB of RAM", true), load);
        Assert.True(ModelSizeAdvice.Load(session, OnDisk, ramMb: 16_000)!.OverGuidance);    // the model alone (14,000 MB) fits, with its link it does not
        Assert.False(ModelSizeAdvice.Load(session, OnDisk, ramMb: 21_000)!.OverGuidance);
        Assert.Null(ModelSizeAdvice.Load(session, null, ramMb: 12_000));          // another computer's journal, nothing logged yet
    }

    [Fact]
    public void Open_views_and_models_are_the_counts_Revit_logged_last()
    {
        var s = JournalParser.Parse("journal.0001.txt", Fixtures.Lines(Fixtures.Header, """
                ' [Jrn.TabbedViewAnalytics] ProjectCount: 1
                ' [Jrn.TabbedViewAnalytics] ViewCount: 1
              ' [Jrn.TabbedViewAnalytics] ViewCount: 3
              ' [Jrn.TabbedViewAnalytics] Rvt.Attr.ActivateView: FromTab
              ' [Jrn.TabbedViewAnalytics] ViewCount: 2
            """));

        Assert.Equal(2, s.OpenViews);
        Assert.Equal(1, s.OpenProjects);
    }

    [Fact]
    public void Models_open_in_several_Revits_are_judged_together()
    {
        var small = new ModelLoad("A.rvt", 250, null, 0, 0, 0, "16 GB of RAM", false);
        var other = new ModelLoad("B.rvt", 280, null, 2, 2, 100, "16 GB of RAM", false);

        Assert.Null(ModelSizeAdvice.Combined([small], 16_000));                    // one model: its own row says it
        Assert.Null(ModelSizeAdvice.Combined([small, other], 16_000));             // 20 x 630 MB (models and links) fits 16 GB
        Assert.Equal((630, "about 12.3 GB of RAM"), ModelSizeAdvice.Combined([small, other], 8_000));   // each fits 8 GB, together they do not
    }

    [Fact]
    public void A_model_that_fits_Autodesks_guidance_is_not_reported()
    {
        var r = Analyze(Fixtures.Header, """
            'C 05-Nov-2026 10:00:00.000;   0:< x
              ' 1:< RAM Statistics:    11185 /    32212      9769=InUse    21719=Peak
              ' 6:< Detect if missing elements for C:\\Models\\Small.rvt: File Size: 524288000, Element Count: 90000
            """, Fixtures.NormalEnd);

        Assert.Null(Find(r, "metric-model-size-vs-ram"));
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
            """, Fixtures.CrashRecord);

        Assert.Null(History.RepeatedCrashes(Analyzer.Rules, [Crash(1), Crash(2)]));
        Assert.Null(History.RepeatedCrashes(Analyzer.Rules, [Crash(1), Crash(2), Crash(20)])); // 1 and 2 are outside the week before the 20th
        var f = History.RepeatedCrashes(Analyzer.Rules, [Crash(3), Crash(5), Crash(8)]);
        Assert.NotNull(f);
        Assert.Contains("3 times", f.Facts[0]);
    }
}
