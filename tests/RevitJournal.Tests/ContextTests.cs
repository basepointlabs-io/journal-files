using RevitJournal.Core.Analysis;
using RevitJournal.Core.Parsing;

namespace RevitJournal.Tests;

/// <summary>Element-level context: what the user was editing when Revit stopped. Ids and names are made up.</summary>
public class ContextTests
{
    private static readonly Analyzer Analyzer = Analyzer.CreateDefault();
    private static readonly AnalysisContext Closed = new(IsBeingWritten: false, DumpFiles: []);

    private const string DuctDragThenStop = """
        ' 0:< ModelPath Created: Is server path = True, Region = "EMEA", Central server = "", Path = "CLD:\\EMEA\{11111111-2222-3333-4444-555555555555}\{aaaaaaaa-bbbb-cccc-dddd-eeeeeeeeeeee}Tower_MEP.rvt"
          'E 05-Nov-2026 12:45:30.000;   0:<
          Jrn.Directive  _
                  "WindowSize"  , "[aaaaaaaa-bbbb-cccc-dddd-eeeeeeeeeeee.rvt]" , "Floor Plan: Level 1 Ducts"  _
                  , 1407, 802
          Jrn.LButtonDown    1 ,    374 ,    379
          ' 0:< Candidates (curIdx = 0): 4203586 (+442.25722, +293.83140, +71.440289)
          Jrn.Data  _
                  "Selection action"  , "REPLACE" ,  _
                   "SEL RESULT: Mechanical : Ducts : Rectangular Duct : Supply 300x200"
        'CT [Modify | Ducts]:
        'CT [Duct Systems]:
          Jrn.LButtonDown    1 ,    373 ,    405
          ' 0:< Candidates (curIdx = 0): 9999999-0 (+442.25722, +292.70013, +71.440289) 4203586 (+442.25722, +292.70013, +71.440289)
          Jrn.Data  _
                  "Control"  , "RbsSingleCurveEndControl" , 1.00000000000000
          'E 05-Nov-2026 12:45:45.405;   0:<
          Jrn.LButtonUp    0 ,    374 ,    355
          ' 0:< Candidates (curIdx = 0): Combo[Snap(4203581-6L [4203581]),DirSnap(-1V)] DirSnap(-1V) NoSnap
        """;

    [Fact]
    public void Crash_while_dragging_a_duct_end_names_the_duct_the_target_the_view_and_the_model()
    {
        var r = Analyzer.Analyze(JournalParser.Parse("j", Fixtures.Lines(Fixtures.Header, DuctDragThenStop, Fixtures.CrashRecord)), Closed);

        var ctx = r.Context!;
        Assert.Equal("Tower_MEP.rvt", ctx.Model);
        Assert.Equal("Floor Plan: Level 1 Ducts", ctx.View);
        Assert.Equal(4203586, ctx.ElementId);       // the selected duct, not the first candidate under the grip
        Assert.Equal(4203581, ctx.SnapTargetId);
        Assert.True(ctx.InSystem);

        var f = r.Findings[0];
        Assert.Equal("context-crash-while-editing", f.Rule.Id);
        Assert.Contains(f.Steps, s => s.Contains("Select by ID, enter 4203586; 4203581"));
        Assert.Contains(f.Steps, s => s.Contains("Do not drag the end of duct 4203586 onto element 4203581"));
        Assert.Contains(f.Steps, s => s.Contains("Divide System"));
    }

    [Fact]
    public void Orphaned_tags_are_listed_per_link_with_select_by_id()
    {
        var r = Analyzer.Analyze(JournalParser.Parse("j", Fixtures.Lines(Fixtures.Header, """
            ' 8:< A hosting element no longer exists in the link. Dependent family or tag 5000001 on link 4000001.
            ' 8:< A hosting element no longer exists in the link. Dependent family or tag 5000002 on link 4000001.
            ' 8:< A hosting element no longer exists in the link. Dependent family or tag 5000001 on link 4000001.
            """, Fixtures.NormalEnd)), Closed);

        var f = r.Findings.Single(x => x.Rule.Id == "observed-orphaned-hosted-on-link");
        Assert.Contains("2 tag(s)/families lost their host in the link with id 4000001.", f.Facts);
        Assert.Contains(f.Steps, s => s.EndsWith("paste: 5000001; 5000002"));
        Assert.Contains(f.Steps, s => s.Contains("Reconcile Hosting"));
    }

    [Fact]
    public void Failed_sync_gets_save_first_steps_with_the_model_name()
    {
        string model = """
            ' [Jrn.BasicFileInfo] Rvt.Attr.Worksharing: Cloud Local Rvt.Attr.Username:  Rvt.Attr.CentralModelPath: Autodesk Docs://Project/Tower_MEP.rvt Rvt.Attr.RevitBuildVersion: Autodesk Revit 2025 (Build: 20250515_1515(x64))
            """;
        var r = Analyzer.Analyze(JournalParser.Parse("j", Fixtures.Lines(Fixtures.Header, model, Fixtures.FailedCloudSync)), Closed);

        var steps = r.Findings.Single(x => x.Rule.Id == "metric-sync-failed").Steps;
        Assert.StartsWith("In the Revit window with Tower_MEP.rvt, click Save now", steps[0]);
        Assert.Contains(steps, s => s.Contains("from Autodesk Docs"));
        Assert.Contains(steps, s => s.StartsWith("Do not use Detach"));
    }

    [Fact]
    public void Posted_revit_warning_gets_its_own_rule()
    {
        var r = Analyzer.Analyze(JournalParser.Parse("j", Fixtures.Lines(Fixtures.Header, """
            ' 2:< Error posted:
            ' 2:< Elements have duplicate "Mark" values.
            """, Fixtures.NormalEnd)), Closed);

        Assert.Contains(r.Findings, f => f.Rule.Id == "warning-duplicate-mark");
        Assert.Contains("Elements have duplicate \"Mark\" values.", r.Session.PostedMessages.Select(m => m.Text));
    }
}
