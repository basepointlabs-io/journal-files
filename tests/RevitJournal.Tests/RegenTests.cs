using RevitJournal.Core.Analysis;
using RevitJournal.Core.Parsing;
using RevitJournal.Core.Rules;

namespace RevitJournal.Tests;

/// <summary>A Reload Latest of a cloud model that stops at regeneration; the lines follow a real journal, ids are made up.</summary>
public class RegenTests
{
    private static readonly Analyzer Analyzer = Analyzer.CreateDefault();
    private static readonly AnalysisContext Closed = new(IsBeingWritten: false, DumpFiles: []);

    private const string ReloadThatFails = """
          ' 1:< SLOG $0a0a0a0a 2026-11-05 10:46:15.973 >RL
          ' 5:< in delayedPropagation(), Element -1 has an invalid partition.
          'C 05-Nov-2026 11:34:12.598;   5:< System exception in ADocument::regenerate
          'C 05-Nov-2026 11:34:12.598;   5:< executing step
          'C 05-Nov-2026 11:34:12.598;   5:<  element 12345678 of type IndependentTag
          ' 5:< ANTECEDENT:
          'inTransaction ADocumentSafeMerge__DelayedRegen:-1 Reload Latest
          'inTransaction RvtLinkTracking.cpp:388 Reload Linked Instances
          'regenerate
          'C 05-Nov-2026 11:34:12.646;  DBG_ERROR: System exception in ADocument::regenerate. See details in journal: line 7726 of F:\Ship\Source\Revit\RevitDB\Regen\ADocumentRegen.cpp.
          ' 3:< Error dialog data for transaction Reload Latest in Host document
          ' 3:< Error: A serious error has occurred. It is strongly recommended that you use Save As to save your work in a new file before continuing.
        """;

    private static AnalysisResult Analyze() =>
        Analyzer.Analyze(JournalParser.Parse("journal.0001.txt", Fixtures.Lines(Fixtures.Header, ReloadThatFails, Fixtures.NormalEnd)), Closed);

    [Fact]
    public void The_element_and_the_transaction_of_a_failed_regeneration_are_named()
    {
        var f = Analyze().Findings.Single(x => x.Rule.Id == Analyzer.RegenRule);

        Assert.Contains(f.Facts, x => x.StartsWith("Revit could not regenerate element 12345678 (IndependentTag) during Reload Latest > Reload Linked Instances at 11:34:12", StringComparison.Ordinal));
        Assert.Contains(f.Facts, x => x.Contains("Select by ID, paste: 12345678."));
        Assert.Contains(f.Facts, x => x.Contains("reloading linked models"));
        Assert.Equal(1, f.Facts.Count(x => x.StartsWith("Revit could not regenerate", StringComparison.Ordinal)));   // the DBG_ERROR copy is not a second failure
        Assert.Contains(f.Rule.Sources, s => s.Url.Contains("GUID-D01948FC", StringComparison.Ordinal));
    }

    [Fact]
    public void Invalid_partition_points_to_Autodesks_article_on_syncs_stuck_at_Reload_Latest()
    {
        var f = Analyze().Findings.Single(x => x.Rule.Id == "cloud-sync-invalid-partition");

        Assert.Equal(1, f.HitCount);
        Assert.Contains(f.Rule.Sources, s => s.Url.Contains("stops-at-the-Reload-Latest-stage", StringComparison.Ordinal));
        Assert.StartsWith("Open the model with all worksets closed", f.Rule.Fix.En[0], StringComparison.Ordinal);
    }
}

/// <summary>Rules keyed on a dialog's id or on Revit's own translations explain journals of a Revit in another language.</summary>
public class LanguageTests
{
    private static readonly Analyzer Analyzer = Analyzer.CreateDefault();
    private static readonly AnalysisContext Closed = new(IsBeingWritten: false, DumpFiles: []);

    private static AnalysisResult Analyze(string body) =>
        Analyzer.Analyze(JournalParser.Parse("journal.0001.txt", Fixtures.Lines(Fixtures.Header, body, Fixtures.NormalEnd)), Closed);

    [Fact]
    public void A_dialog_is_recognised_by_its_id_whatever_the_language_of_its_text()
    {
        // German Revit: the text is translated, the id under it is not.
        var german = Analyze("""
            ' 0:< TaskDialog "Die Daten in Datei Tower.rvt sind beschädigt und müssen manuell wiederhergestellt werden."
            'Id : TaskDialog_Revit_File_Corrupted
            'CommonButtons : Close
            """);
        Assert.Equal(1, german.Findings.Single(f => f.Rule.Id == "data-in-file-corrupt").HitCount);

        // English Revit: text and id are the same dialog, counted once.
        var english = Analyze("""
            ' 0:< TaskDialog "Data in file Tower.rvt is corrupt and needs to be manually recovered."
            'Id : TaskDialog_Revit_File_Corrupted
            'CommonButtons : Close
            """);
        Assert.Equal(1, english.Findings.Single(f => f.Rule.Id == "data-in-file-corrupt").HitCount);
    }

    [Fact]
    public void The_serious_error_dialog_is_recognised_in_its_current_wording()
    {
        var r = Analyze("""
            ' 0:< TaskDialog "Revit encountered a serious error. The current action could not be completed."
            'Id : TaskDialog_Serious_Error_Occurred
            """);
        Assert.Contains(r.Findings, f => f.Rule.Id == "serious-error-has-occurred");
    }

    [Fact]
    public void A_message_without_an_id_is_recognised_in_Revits_own_translation()
    {
        var r = Analyze("""
            ' 9:< Error posted:
            ' 9:< Verknüpfungsexemplar benötigt Koordinationsprüfung
            ' 0:< TaskDialog "Nicht genügend Speicher."
            'CommonButtons : Close
            """);
        Assert.Contains(r.Findings, f => f.Rule.Id == "link-needs-coordination-review");
        Assert.Contains(r.Findings, f => f.Rule.Id == "forum-out-of-memory");      // "Out of memory." in German
    }
}

/// <summary>Rules drafted from Autodesk support articles that quote journal lines; the lines follow a real night sync that failed.</summary>
public class AutodeskJournalLineTests
{
    private static readonly Analyzer Analyzer = Analyzer.CreateDefault();
    private static readonly AnalysisContext Closed = new(IsBeingWritten: false, DumpFiles: []);

    [Fact]
    public void A_sync_that_runs_out_of_memory_and_reads_corrupt_data_is_explained_by_Autodesk_articles()
    {
        var r = Analyzer.Analyze(JournalParser.Parse("journal.0001.txt", Fixtures.Lines(Fixtures.Header, """
              ' 1:< SLOG $0a0a0a0a 2026-11-05 03:10:51.846 >STC:RL:Read
              ' 0:< A MemoryException is raised on line 200 of F:\Ship\Source\Foundation\Utility\Arr.cpp
              'C 05-Nov-2026 03:11:10.906;  DBG_INFO: An ArchiveException 105 corruptData is raised at line 50 of F:\Ship\Source\Foundation\Utility\Tasks\ExceptionCache.cpp: ExceptionCache::propagate
              ' 3:< TaskDialog "Data in file Tower.rvt is corrupt and needs to be manually recovered."
              'Id : TaskDialog_Revit_File_Corrupted
            """, Fixtures.NormalEnd)), Closed);

        foreach (string id in new[] { "memory-exception-arr", "archive-exception-corrupt-data", "data-in-file-corrupt" })
        {
            var f = r.Findings.Single(x => x.Rule.Id == id);
            Assert.All(f.Rule.Sources, s => Assert.Contains("autodesk.com", s.Url, StringComparison.Ordinal));
        }
    }
}

/// <summary>A scheduled IFC export that crashes Revit; the lines follow real journals of a German Revit, ids made up.</summary>
public class IfcExportTests
{
    private static readonly Analyzer Analyzer = Analyzer.CreateDefault();

    private const string ExportThatCrashes = """
          ' 1:< ANTECEDENT:
          'inTransaction APITransactionIdentity_1:-1 Export IFC
          '  C 08-Jun-2026 22:28:04.187;   1:< IFC error: Exporting element "MW",1234567 - Autodesk.Revit.Exceptions.InternalException: A managed exception was thrown by Revit
          '  1:< Error posted:
          '  1:< Unerwartete Fehler beim Exportieren dieser Elemente in die IFC-Datei.
        ' 1:< Pass 0: TessellateSolidOrShell failed in IFC export for reason: "Faceting failed." in element "MW" with id 1234570
        ' 0:< Exception occurred
        'C 08-Jun-2026 22:29:08.373;   0:< ExceptionCode=0xe0434352 ExceptionFlags=0x00000081 ExceptionAddress=00007FF8211D770A
        """;

    [Fact]
    public void A_crash_during_IFC_export_is_the_headline_with_the_elements_that_failed()
    {
        var session = JournalParser.Parse("journal.0001.txt", Fixtures.Lines(Fixtures.Header, ExportThatCrashes));
        var r = Analyzer.Analyze(session, new AnalysisContext(IsBeingWritten: false, DumpFiles: ["journal.0001.0001.dmp"]));

        var f = r.Findings.First();
        Assert.Equal(Analyzer.IfcRule, f.Rule.Id);
        Assert.Equal(Severity.Critical, f.Severity);
        Assert.Equal("IFC export failed or crashed Revit", r.Headline);
        Assert.StartsWith("Revit crashed while an IFC export was running", f.Facts[0], StringComparison.Ordinal);
        Assert.Contains(f.Facts, x => x.Contains("\"MW\" 1234567 (InternalException)") && x.Contains("\"MW\" 1234570 (Faceting failed)"));
        Assert.Contains(f.Facts, x => x.EndsWith("Select by ID, paste: 1234567; 1234570", StringComparison.Ordinal));
    }

    [Fact]
    public void Elements_that_fail_in_an_export_that_finishes_are_reported_without_a_crash()
    {
        var session = JournalParser.Parse("journal.0001.txt", Fixtures.Lines(Fixtures.Header,
            "' 1:< Pass 0: TessellateSolidOrShell failed in IFC export for reason: \"Faceting failed.\" in element \"Duct\" with id 42",
            Fixtures.NormalEnd));
        var f = Analyzer.Analyze(session, new AnalysisContext(IsBeingWritten: false, DumpFiles: [])).Findings.Single(x => x.Rule.Id == Analyzer.IfcRule);

        Assert.Equal(Severity.High, f.Severity);
        Assert.DoesNotContain(f.Facts, x => x.Contains("crashed", StringComparison.Ordinal));
    }
}

/// <summary>A cloud model open that never reaches the model; the lines follow a real journal, ids made up.</summary>
public class CloudOpenTests
{
    private static readonly Analyzer Analyzer = Analyzer.CreateDefault();
    private static readonly AnalysisContext Closed = new(IsBeingWritten: false, DumpFiles: []);

    private const string ModelPath = """
        ' 1:< ModelPath Created: Is server path = True, Region = "EMEA", Central server = "", Path = "CLD:\\EMEA\{11111111-1111-1111-1111-111111111111}Project\{22222222-2222-2222-2222-222222222222}Tower_ELT.rvt"
        """;

    [Fact]
    public void A_cloud_open_that_never_reaches_the_model_is_reported_with_its_name()
    {
        var session = JournalParser.Parse("journal.0001.txt", Fixtures.Lines(Fixtures.Header, ModelPath, """
            'C 23-Nov-2026 09:57:08.205;   0:< Open Cloud Model Method: open, Region: EMEA, Model Guid: 22222222-2222-2222-2222-222222222222, Project Guid: 11111111-1111-1111-1111-111111111111.
            ' 0:< 23-Nov-2026 09:57:09:056; {"http_method":"GET","http_url":"https://model.skyscraper.eu.autodesk.com/ModelService2015/api/projects/x/models/y","duration":204,"http_response_code":200}
            'C 23-Nov-2026 10:11:23.354;   0:< License Idle: Enter
            """));
        var f = Analyzer.Analyze(session, Closed).Findings.Single(x => x.Rule.Id == Analyzer.CloudOpenRule);

        Assert.StartsWith("Revit started opening Tower_ELT.rvt (region EMEA) at 09:57:08 but never got as far as reading it", f.Facts[0], StringComparison.Ordinal);
        Assert.Contains(f.Rule.Sources, s => s.Url.Contains("internet-connection", StringComparison.Ordinal));
    }

    [Fact]
    public void A_cloud_open_that_reaches_the_worksharing_log_is_fine()
    {
        var session = JournalParser.Parse("journal.0001.txt", Fixtures.Lines(Fixtures.Header, ModelPath, """
            'C 29-Oct-2026 11:38:35.642;   0:< Open Cloud Model Method: open, Region: EMEA, Model Guid: 22222222-2222-2222-2222-222222222222, Project Guid: 11111111-1111-1111-1111-111111111111
            ' 5:< SLOG $05033e0a 2026-10-29 11:38:37.781 >Open
            """, Fixtures.NormalEnd));
        Assert.DoesNotContain(Analyzer.Analyze(session, Closed).Findings, x => x.Rule.Id == Analyzer.CloudOpenRule);
    }

    [Fact]
    public void A_cloud_open_the_user_cancelled_is_not_a_connection_problem()
    {
        var session = JournalParser.Parse("journal.0001.txt", Fixtures.Lines(Fixtures.Header, ModelPath, """
            'C 23-Nov-2026 15:52:20.154;   0:< Open Cloud Model Method: open, Region: EMEA, Model Guid: 22222222-2222-2222-2222-222222222222, Project Guid: 11111111-1111-1111-1111-111111111111.
            ' 1:< TaskDialog "You have made changes that have not been synchronized. What would you like to do?"
            'Id : TaskDialog_Unsubmitted_Changes
            'C 23-Nov-2026 15:52:28.780;   0:< OperationCanceledException "Open canceled by user."
            """, Fixtures.NormalEnd));
        Assert.DoesNotContain(Analyzer.Analyze(session, Closed).Findings, x => x.Rule.Id == Analyzer.CloudOpenRule);
    }
}

/// <summary>A journal whose last lines are a MemoryException; the lines follow a real journal.</summary>
public class MemoryEndTests
{
    private static readonly Analyzer Analyzer = Analyzer.CreateDefault();
    private static readonly AnalysisContext Copied = new(IsBeingWritten: false, DumpFiles: []);

    private const string OutOfMemory = """
        'C 23-Nov-2026 12:06:07.438;   0:< DEBUG: RIA: {"Message":"Ending HttpMessageHandler cleanup cycle"}
          ' 4:< ::14187:: Delta VM: Avail -647 -> 134075840 MB, Used +647 -> 65472 MB, Peak +631 -> 65480 MB; RAM: Avail -586 -> 10034 MB, Used +622 -> 14548 MB
          ' 4:< A MemoryException is raised on line 238 of F:\Ship24.3.6\2024_px64\Source\Foundation\Utility\Arr.cpp
          ' 5:< A MemoryException is raised on line 238 of F:\Ship24.3.6\2024_px64\Source\Foundation\Utility\Arr.cpp
        """;

    [Fact]
    public void A_journal_that_ends_with_a_MemoryException_is_a_crash()
    {
        var r = Analyzer.Analyze(JournalParser.Parse("journal.0001.txt", Fixtures.Lines(Fixtures.Header, OutOfMemory)), Copied);

        Assert.Equal(EndKind.Crash, r.EndKind);
        Assert.Contains(r.Summary, x => x.StartsWith("Revit crashed", StringComparison.Ordinal));
    }

    [Fact]
    public void A_MemoryException_Revit_survived_is_not_a_crash()
    {
        var r = Analyzer.Analyze(JournalParser.Parse("journal.0001.txt", Fixtures.Lines(Fixtures.Header, OutOfMemory, """
            'C 23-Nov-2026 12:07:00.000;   0:< License Idle: Enter
            ' 0:< ::14188:: Delta VM: Avail 134075840 MB, Used 65472 MB; RAM: Avail 10034 MB, Used 14548 MB
            ' 0:< GUI Resource Usage GDI: Avail 6538, Used 3462, User: Used 978
            'C 23-Nov-2026 12:07:10.000;   0:< License Idle: Exit
            'C 23-Nov-2026 12:07:20.000;   0:< License Idle: Enter
            'C 23-Nov-2026 12:07:30.000;   0:< License Idle: Exit
            """)), Copied);

        Assert.NotEqual(EndKind.Crash, r.EndKind);
    }
}

public class AddInStartTests
{
    private static readonly Analyzer Analyzer = Analyzer.CreateDefault();
    private static readonly AnalysisContext Closed = new(IsBeingWritten: false, DumpFiles: []);

    [Fact]
    public void An_add_in_built_for_an_older_NET_that_fails_to_start_says_so_with_Autodesks_article()
    {
        var session = JournalParser.Parse("journal.0001.txt", Fixtures.Lines(Fixtures.Header, """
            ' 0:< API_ERROR { : Assembly version conflict in some references in Good.dll assembly
            'Addin's module System.Runtime of version 8.0.0.0 conflicts with same preloaded module of version 10.0.0.0 }
            ' 0:< API_SUCCESS { Starting External Application: Good, Class: Good.App, Vendor : GOOD(Good Ltd), Assembly: C:\Program Files\Good\Good.dll,  Assembly Version: 1.0.0.0 }
            ' 0:< API_ERROR { : Assembly version conflict in some references in Hangers.Commands.dll assembly
            'Addin's module System.Runtime of version 8.0.0.0 conflicts with same preloaded module of version 10.0.0.0
            'Addin's module RevitAPI of version 25.0.0.0 conflicts with same preloaded module of version 25.5.0.0 }
            ' 0:< API_ERROR { Starting External Application: SHPanel, Class: Hangers.Commands.SHPanel, Vendor : HNGR(Hangers Group, www.example.com), Assembly: C:\Program Files\Hangers\R2025\Hangers.Commands.dll,  Assembly Version: 2026.5.0.3 }
            ' 0:< API_ERROR { Starting External Application: Other, Class: Other.App, Vendor : OTHR(Other Ltd), Assembly: C:\Program Files\Other\Other.dll,  Assembly Version: 1.0.0.0 }
            """));
        var f = Analyzer.Analyze(session, Closed).Findings.Single(x => x.Rule.Id == "metric-addin-failed-to-start");

        Assert.Equal(2, f.Facts.Count);
        Assert.EndsWith("It was built for .NET 8; this Revit runs on .NET 10.", f.Facts[0], StringComparison.Ordinal);
        Assert.StartsWith("Add-in \"SHPanel\" (Hangers Group, www.example.com) failed to start", f.Facts[0], StringComparison.Ordinal);
        Assert.DoesNotContain(".NET", f.Facts[1], StringComparison.Ordinal);                 // no conflict list of its own
        Assert.Contains(f.Rule.Sources, s => s.Url.Contains("NET-10-transition", StringComparison.Ordinal));
    }

    [Fact]
    public void A_slow_start_names_the_add_in_the_timestamps_prove_slow()
    {
        var session = JournalParser.Parse("journal.0001.txt", Fixtures.Lines(Fixtures.Header, """
            'E 05-Nov-2026 09:25:00.000;   0:<
            ' 0:< API_SUCCESS { Starting External Application: Quick, Class: Quick.App, Vendor : QUIK(Quick Ltd), Assembly: C:\Quick\Quick.dll,  Assembly Version: 1.0.0.0 }
            'C 05-Nov-2026 09:25:50.000;   0:< Notify Active View.
            ' 0:< API_SUCCESS { Starting External Application: Loader, Class: Loader.App, Vendor : LOAD(Loader Ltd), Assembly: C:\Loader\Loader.dll,  Assembly Version: 1.0.0.0 }
            ' 0:< API_SUCCESS { Starting External Application: Unknown, Class: Unknown.App, Vendor : UNKN(Unknown Ltd), Assembly: C:\Unknown\Unknown.dll,  Assembly Version: 1.0.0.0 }
            'E 05-Nov-2026 09:27:30.000;   0:<
            ' 0:< API_SUCCESS { Starting External Application: Last, Class: Last.App, Vendor : LAST(Last Ltd), Assembly: C:\Last\Last.dll,  Assembly Version: 1.0.0.0 }
            'E 05-Nov-2026 09:27:40.000;
            Jrn.Command "Internal" , "Display Profile Dialog , ID_REVIT_MODEL_BROWSER_OPEN"
            """));
        var f = Analyzer.Analyze(session, Closed).Findings.Single(x => x.Rule.Id == "metric-slow-startup");

        // Loader: 50 s after Quick, proven. Unknown and Last share 100 s no timestamp divides: neither is named.
        Assert.Equal("Add-ins that took longest to load: Loader about 50 s.", f.Facts[1]);
        Assert.Contains(f.Rule.Sources, s => s.Url.Contains("Revit-is-slow-on-startup", StringComparison.Ordinal));
    }

    [Fact]
    public void Data_that_needs_recovery_and_geometry_a_DWG_import_lost_are_explained()
    {
        var session = JournalParser.Parse("journal.0001.txt", Fixtures.Lines(Fixtures.Header, """
            'C 18-Nov-2026 03:18:07.046;   3:< Load exception "CArchiveException", code=105: Some data in file an unnamed file needs to be recovered. In the Open dialog, select the Audit option. Then open and re-save the model.
            ' 9:< Revit cannot import some of the geometry.
            """));
        var ids = Analyzer.Analyze(session, Closed).Findings.Select(x => x.Rule.Id).ToList();

        Assert.Contains("model-data-needs-recovery-audit", ids);
        Assert.Contains("dwg-cannot-import-some-geometry", ids);
    }

    [Fact]
    public void A_link_without_phases_is_the_cause_and_its_cannot_connect_status_is_not_a_network_problem()
    {
        var session = JournalParser.Parse("journal.0001.txt", Fixtures.Lines(Fixtures.Header, """
            'C 05-Nov-2026 10:00:00.000;  File: Open
            'inTransaction RvtLinkTracking.cpp:388 Reload Linked Instances
            'C 05-Nov-2026 10:05:10.000;   0:< SkyscraperClientHost.LogAnalyticsRequest: AnalyticsProperties:
            'context_call: Links and UI
            'status: ERROR : Cannot connect to central model
            'C 05-Nov-2026 10:05:10.010;   0:< openDocumentFileForUI: The id does not represent a valid phase.
            'C 05-Nov-2026 10:06:00.000;   2:< SkyscraperClientHost.LogAnalyticsRequest: AnalyticsProperties:
            'context_call: GUI:Dialog_Revit_Partitions
            'status: ERROR : Cannot connect to central model
            """, Fixtures.NormalEnd));
        var findings = Analyzer.Analyze(session, Closed).Findings;
        var f = findings.Single(x => x.Rule.Id == "link-missing-project-phases");

        Assert.DoesNotContain(findings, x => x.Rule.Id == "forum-cannot-connect-central-cloud");
        Assert.Contains(f.Facts, x => x.Contains("Cannot connect to central model", StringComparison.Ordinal));
        Assert.Contains(f.Rule.Sources, s => s.Url.Contains("Revit-model-crashes-with-multiple-errors", StringComparison.Ordinal));
    }

    [Fact]
    public void Cannot_connect_far_from_a_phase_error_still_counts()
    {
        var session = JournalParser.Parse("journal.0001.txt", Fixtures.Lines(Fixtures.Header, """
            'C 05-Nov-2026 10:00:00.000;   0:< SkyscraperClientHost.LogAnalyticsRequest: AnalyticsProperties:
            'status: ERROR : Cannot connect to central model
            'C 05-Nov-2026 15:00:00.000;  DBG_WARN: The id does not represent a valid phase.
            """, Fixtures.NormalEnd));
        var ids = Analyzer.Analyze(session, Closed).Findings.Select(x => x.Rule.Id).ToList();

        Assert.Contains("forum-cannot-connect-central-cloud", ids);
        Assert.Contains("link-missing-project-phases", ids);
    }
}
