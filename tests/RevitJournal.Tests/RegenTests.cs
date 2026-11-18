using RevitJournal.Core.Analysis;
using RevitJournal.Core.Parsing;

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
