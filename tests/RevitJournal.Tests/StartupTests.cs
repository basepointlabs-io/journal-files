using RevitJournal.Core.Analysis;
using RevitJournal.Core.Parsing;
using RevitJournal.Core.Rules;

namespace RevitJournal.Tests;

public class StartupTests
{
    private static readonly Analyzer Analyzer = Analyzer.CreateDefault();
    private static readonly AnalysisContext Closed = new(IsBeingWritten: false, DumpFiles: []);

    private const string NativeInit = """
        'C 05-Nov-2026 09:24:38.650;  ->desktop InitApplication
        'C 05-Nov-2026 09:24:38.680;  <-desktop InitApplication
        'C 05-Nov-2026 09:24:38.681;  ->desktop InitNativeInstance
        ' 0:< Actual language code=DEU
        """;

    private const string LoadingAddIns = """
        'C 05-Nov-2026 09:24:55.000;   0:< License initialization complete
        ' 0:< API_SUCCESS { Starting External Application: Coordination Model Interface, Class: A.B, Vendor : ADSK(https://autodesk.com), Assembly: a.dll,  Assembly Version: 24.3.60.12 }
        'C 05-Nov-2026 09:25:10.000;   0:< x
        ' 0:< API_SUCCESS { Starting External Application: Duct Helper, Class: D.H, Vendor : ACME(Acme Tools, www.example.com), Assembly: d.dll,  Assembly Version: 1.0.0.0 }
        ' 0:< API_SUCCESS { Starting External Application: Cloud Linker, Class: C.L, Vendor : ACME(Acme Tools, www.example.com), Assembly: c.dll,  Assembly Version: 2.0.0.0 }
        """;

    private const string StartupDone = """
        'C 05-Nov-2026 09:27:00.000;  <-desktop InitManagedInstance
        'C 05-Nov-2026 09:27:00.050;   0:< appPriv idle
          'E 05-Nov-2026 09:27:00.300;   0:<
          ' [Jrn.ExternalEventExecution] Rvt.Attr.AddInId: 11111111-2222-3333-4444-555555555555 Rvt.Attr.AddInName: Duct Helper Rvt.Attr.AddInVersion: 1.0.0.0 Rvt.Attr.ExternalEventName: e
        ' 1:< API_SUCCESS { Unregistering Idling event by application Coordination Model Interface (9817e4a5-0333-4323-b69e-781db5f70005). }
        ' 1:< API_SUCCESS { Unregistering Idling event by application Cloud Linker (86807e17-a85b-4107-a4b4-a4736e5c3394). }
        'C 05-Nov-2026 09:27:01.100;   0:< x
        """;

    private static AnalysisResult Analyze(AnalysisContext ctx, params string[] blocks) =>
        Analyzer.Analyze(JournalParser.Parse("journal.0002.txt", Fixtures.Lines(blocks)), ctx);

    private static Finding? Find(AnalysisResult r, string id) => r.Findings.FirstOrDefault(f => f.Rule.Id == id);

    [Fact]
    public void Start_that_stops_before_licensing_after_a_silent_exit_points_at_leftover_processes()
    {
        var ctx = Closed with { Previous = new PreviousSession("journal.0001.txt", new DateTime(2026, 11, 5, 9, 23, 40), EndedNormally: false) };
        var r = Analyze(ctx, Fixtures.Header, NativeInit);

        var f = Find(r, StartupAdvice.NotCompletedRule);
        Assert.NotNull(f);
        Assert.Equal("Revit stopped while it was starting", r.Headline);
        Assert.Contains(f.Facts, x => x.Contains("before licensing was initialized"));
        Assert.Contains(f.Facts, x => x.Contains("journal.0001.txt") && x.Contains("09:23:40"));
        Assert.Contains(f.Steps, x => x.Contains("RevitWorker.exe"));
    }

    [Fact]
    public void Previous_session_that_closed_normally_or_long_ago_is_not_blamed()
    {
        var normal = Closed with { Previous = new PreviousSession("journal.0001.txt", new DateTime(2026, 11, 5, 9, 23, 40), EndedNormally: true) };
        var old = Closed with { Previous = new PreviousSession("journal.0001.txt", new DateTime(2026, 11, 4, 18, 0, 0), EndedNormally: false) };

        foreach (var ctx in new[] { normal, old })
        {
            var f = Find(Analyze(ctx, Fixtures.Header, NativeInit, LoadingAddIns), StartupAdvice.NotCompletedRule);
            Assert.NotNull(f);
            Assert.DoesNotContain(f.Facts, x => x.Contains("journal.0001.txt"));
        }
    }

    [Fact]
    public void Start_that_stops_while_loading_add_ins_names_the_last_one()
    {
        var f = Find(Analyze(Closed, Fixtures.Header, NativeInit, LoadingAddIns), StartupAdvice.NotCompletedRule);

        Assert.NotNull(f);
        Assert.Contains(f.Facts, x => x.Contains("3 had started") && x.Contains("\"Cloud Linker\" (Acme Tools"));
        Assert.Contains(f.Steps, x => x.Contains("Disable \"Cloud Linker\""));
    }

    [Fact]
    public void Silent_end_right_after_startup_lists_the_third_party_add_ins_that_were_active()
    {
        var r = Analyze(Closed with { IsLocal = false }, Fixtures.Header, NativeInit, LoadingAddIns, StartupDone);

        Assert.Null(Find(r, StartupAdvice.NotCompletedRule));
        var f = Find(r, StartupAdvice.ClosedAfterRule);
        Assert.NotNull(f);
        Assert.Equal(Severity.High, f.Severity);
        Assert.Equal("Revit closed by itself right after starting", r.Headline);
        Assert.Contains(f.Facts, x => x.Contains("stops 1 s later"));
        Assert.Contains(f.Facts, x => x.Contains("Cloud Linker, Duct Helper") && !x.Contains("Coordination Model"));
        Assert.Contains(f.Steps, x => x.Contains("Event Viewer") && x.Contains("09:27:01"));
    }

    [Fact]
    public void On_its_own_computer_a_silent_end_without_a_Windows_crash_record_is_a_forced_close()
    {
        var r = Analyze(Closed, Fixtures.Header, NativeInit, LoadingAddIns, StartupDone);

        var f = Find(r, StartupAdvice.ClosedAfterRule);
        Assert.Equal(Severity.Medium, f?.Severity);
        Assert.Contains(f!.Facts, x => x.Contains("closed from outside"));
        Assert.NotEqual(f.Rule.Title.En, r.Headline);

        var crashed = Analyze(Closed, Fixtures.Header, NativeInit, LoadingAddIns, StartupDone, Fixtures.CrashRecord);
        Assert.Equal(Severity.Critical, Find(crashed, StartupAdvice.ClosedAfterRule)?.Severity);
    }

    [Fact]
    public void A_nearly_full_disk_is_the_first_suspect_and_the_first_step()
    {
        var ctx = Closed with { Disk = new DiskSpace("C:", 300, new DateTime(2026, 11, 5, 10, 0, 0)) };
        var f = Find(Analyze(ctx, Fixtures.Header, NativeInit, LoadingAddIns, StartupDone), StartupAdvice.ClosedAfterRule);

        Assert.Equal(Severity.High, f?.Severity);
        Assert.Contains("Drive C: had only 300 MB free", f!.Facts[0]);
        Assert.StartsWith("Free space on drive C:", f.Steps[0]);

        var roomy = Closed with { Disk = new DiskSpace("C:", 80_000, DateTime.Now) };
        Assert.DoesNotContain(Find(Analyze(roomy, Fixtures.Header, NativeInit), StartupAdvice.NotCompletedRule)!.Facts, x => x.Contains("Drive C:"));
    }

    [Fact]
    public void Running_or_normally_closed_sessions_and_sessions_with_work_are_not_startup_failures()
    {
        string work = """
            'C 05-Nov-2026 09:27:30.000;   0:< x
              Jrn.Command "Ribbon"  , "Create a wall , ID_OBJECTS_WALL"
            'C 05-Nov-2026 09:40:00.000;   0:< x
            """;
        AnalysisResult[] results =
        [
            Analyze(Closed with { IsBeingWritten = true }, Fixtures.Header, NativeInit),
            Analyze(Closed, Fixtures.Header, NativeInit, LoadingAddIns, StartupDone, Fixtures.NormalEnd),
            Analyze(Closed, Fixtures.Header, NativeInit, LoadingAddIns, StartupDone, work),
            Analyze(Closed, Fixtures.Header, work), // an old journal without the start-up marker
        ];

        Assert.All(results, r =>
        {
            Assert.Null(Find(r, StartupAdvice.NotCompletedRule));
            Assert.Null(Find(r, StartupAdvice.ClosedAfterRule));
        });
    }

    [Fact]
    public void Too_many_ribbon_tabs_is_counted_once_per_dialog()
    {
        var r = Analyze(Closed, Fixtures.Header, """
            ' 0:< TaskDialog "Too many tabs have been created by the API. Instead, you may add your Ribbon Panels to the Add-Ins tab."
            'CommonButtons : Close
            'DefaultButton : Close
              'H 05-Nov-2026 09:26:00.000;   0:<
              Jrn.Data  _
                      "TaskDialogResult"  , "Too many tabs have been created by the API. Instead, you may add your Ribbon Panels to the Add-Ins tab." ,  _
                       "Close"  _
                      , "IDCLOSE"
            ' 0:< TaskDialog "Too many tabs have been created by the API. Instead, you may add your Ribbon Panels to the Add-Ins tab."
            """, Fixtures.NormalEnd);

        Assert.Equal(2, Find(r, "observed-too-many-ribbon-tabs")?.HitCount);
    }

    [Fact]
    public void Slow_startup_is_measured_to_the_end_of_startup()
    {
        var f = Find(Analyze(Closed, Fixtures.Header, NativeInit, LoadingAddIns, StartupDone, Fixtures.NormalEnd), "metric-slow-startup");

        Assert.NotNull(f);
        Assert.Contains("2 min 21 s", f.Facts[0]);
    }

    [Fact]
    public void Previous_session_is_read_from_the_end_of_the_journal_before()
    {
        string dir = Path.Combine(Path.GetTempPath(), "rja-startup-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(dir);
        try
        {
            File.WriteAllLines(Path.Combine(dir, "journal.0009.txt"), Fixtures.Lines(Fixtures.Header, NativeInit));
            File.WriteAllLines(Path.Combine(dir, "journal.0010.txt"), Fixtures.Lines(Fixtures.Header, Fixtures.NormalEnd));

            var silent = PreviousSession.Of(Path.Combine(dir, "journal.0010.txt"));
            var normal = PreviousSession.Of(Path.Combine(dir, "journal.0011.txt"));

            Assert.Equal(new PreviousSession("journal.0009.txt", new DateTime(2026, 11, 5, 9, 24, 38, 681), false), silent);
            Assert.True(normal?.EndedNormally);
            Assert.Null(PreviousSession.Of(Path.Combine(dir, "journal.0009.txt")));
        }
        finally
        {
            Directory.Delete(dir, recursive: true);
        }
    }
}
