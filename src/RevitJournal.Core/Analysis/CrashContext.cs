using RevitJournal.Core.Model;

namespace RevitJournal.Core.Analysis;

/// <summary>
/// What the user was doing in the last moments before a journal ends or an error appears: model, view, selection,
/// the elements under the cursor and what was being dragged. Turns that into concrete, element-level steps.
/// </summary>
public sealed class CrashContext
{
    /// <summary>Interactions further back than this before the end are not "what you were doing".</summary>
    private static readonly TimeSpan Window = TimeSpan.FromMinutes(5);

    public string? Model { get; private init; }
    public string? View { get; private init; }
    public string? Selection { get; private init; }
    public string? Tab { get; private init; }
    public string? Drag { get; private init; }
    public long? ElementId { get; private init; }
    public long? SnapTargetId { get; private init; }
    public bool InSystem { get; private init; }
    public UserAction? LastCommand { get; private init; }
    public DateTime? At { get; private init; }

    public IEnumerable<long> ElementIds => new[] { ElementId, SnapTargetId }.OfType<long>().Distinct();
    public bool HasElement => ElementId is not null;

    /// <summary>Builds the context at the end of the session (or before <paramref name="endLine"/>); null when nothing useful was recorded.</summary>
    public static CrashContext? Build(JournalSession s, int? endLine = null)
    {
        int end = endLine ?? int.MaxValue;
        var before = s.Interactions.Where(i => i.Line <= end).ToList();
        DateTime? at = s.LastTimestamp;
        bool Recent(Interaction i) => at is null || i.Time is null || at - i.Time <= Window;

        var view = before.LastOrDefault(i => i.Kind == InteractionKind.View);
        var recent = before.Where(Recent).ToList();
        var pick = recent.LastOrDefault(i => i.Kind == InteractionKind.Pick && !i.IsSnap);
        var snap = recent.LastOrDefault(i => i.Kind == InteractionKind.Pick && i.IsSnap);
        var drag = recent.LastOrDefault(i => i.Kind == InteractionKind.Drag);
        var selection = recent.LastOrDefault(i => i.Kind == InteractionKind.Selection && i.Text != "<empty>");
        var tabs = recent.Where(i => i.Kind == InteractionKind.Tab).ToList();

        // A snap only matters when it came after the element was grabbed (the release of the drag).
        long? snapId = snap is not null && (drag is null || snap.Line > drag.Line) ? snap.Ids[0] : null;
        // The element being edited is the one whose click produced the current selection; later clicks on a grip
        // list whatever lies under the cursor first (often a neighbouring element).
        var selectingPick = selection is null ? null
            : recent.LastOrDefault(i => i.Kind == InteractionKind.Pick && !i.IsSnap && i.Line < selection.Line);
        long? id = (selectingPick ?? pick)?.Ids[0];
        if (id == snapId)
            snapId = null;

        if (view is null && id is null && drag is null && selection is null)
            return null;

        return new CrashContext
        {
            Model = view is not null ? s.DocumentName(view.Detail) : s.Models.LastOrDefault() is { } m ? Fmt.FileName(m.Path) : null,
            View = view?.Text,
            Selection = selection?.Text,
            Tab = tabs.LastOrDefault(t => t.Text.Contains('|'))?.Text,
            Drag = drag?.Text,
            ElementId = id,
            SnapTargetId = snapId,
            InSystem = tabs.Any(t => IsSystemTab(t.Text)),
            LastCommand = s.UserActions.LastOrDefault(a => a.Line <= end && (a.CommandId is not null || Metrics.AddInCommand(a) is not null)),
            At = at,
        };
    }

    /// <summary>Plain-English description of the moment, one fact per line.</summary>
    public List<string> Describe()
    {
        var facts = new List<string>();
        if (Model is not null || View is not null)
            facts.Add($"You were in {Quote(Model) ?? "the model"}" + (View is not null ? $", view {Quote(View)}." : "."));
        if (Selection is not null)
            facts.Add($"Selected: {Selection}.");
        if (Drag is not null && ElementId is not null)
            facts.Add($"You were dragging {DragName(Drag)} of element {ElementId}"
                      + (SnapTargetId is not null ? $" and released it on element {SnapTargetId}." : "."));
        else if (Drag is not null)
            facts.Add($"You were dragging {DragName(Drag)}.");
        else if (ElementId is not null)
            facts.Add($"The last element you clicked was {ElementId}" + (SnapTargetId is not null ? $", snapping to element {SnapTargetId}." : "."));
        if (LastCommand is not null)
            facts.Add($"Last command: {Fmt.Trim(LastCommand.Text.Trim('"', ' '), 80)}" + (LastCommand.CommandId is { } cid ? $" ({cid})" : "") + $" at {Fmt.Time(LastCommand.Time)}.");
        return facts;
    }

    /// <summary>Concrete steps for this exact situation, in order.</summary>
    public List<string> Steps()
    {
        var steps = new List<string>();
        if (Model is not null && View is not null)
            steps.Add($"Open {Model} and go to the view \"{View}\" in the Project Browser.");
        if (ElementIds.Any())
            steps.Add($"Find the element{(ElementIds.Count() > 1 ? "s" : "")}: Manage tab > Inquiry > Select by ID, enter {string.Join("; ", ElementIds)} and click Show.");

        switch (Drag)
        {
            case "RbsSingleCurveEndControl":
                steps.Add(SnapTargetId is not null
                    ? $"Do not drag the end of {Name(ElementId)} onto element {SnapTargetId} again: Revit stopped while doing exactly that. Instead delete {Name(ElementId)} and draw it again from the connector of the element you want to join, or join them with Modify > Trim/Extend to Corner."
                    : $"Do not repeat the same drag on {Name(ElementId)}. Delete the segment and draw it again from the connector, or use Modify > Trim/Extend.");
                break;
            case "WallEndControl":
                steps.Add($"Do not drag the end of wall {ElementId} again. Use Modify > Trim/Extend, or change the wall's length and joins in Properties.");
                break;
            case "BoxBaseControl":
                steps.Add("Do not drag the section box handle again. Turn the section box off and on in View Properties > Extents, or set it with \"Selection Box\" (BX) from selected elements.");
                break;
            case { } other:
                steps.Add($"Revit stopped while you were dragging {DragName(other)}. Avoid repeating the same drag; change the element in Properties or redraw it instead.");
                break;
            case null when ElementId is not null:
                steps.Add($"Check element {ElementId} first: select it, use Temporary Hide/Isolate > Isolate Element and repeat your last action slowly. If Revit stops again, delete the element and draw it again.");
                break;
        }

        if (InSystem && ElementId is not null)
            steps.Add($"Element {ElementId} is part of a {(IsPipe(Tab) ? "piping" : "duct")} system, and Revit recalculates the whole system after each edit. " +
                      "Check the system size in the System Browser (F9); if it is very large, split it into smaller systems " +
                      "(disconnect a branch, then Modify | Duct/Piping Systems > Divide System).");

        if (HasElement)
            steps.Add($"If Revit stops again on the same element, open {Model ?? "the model"} with Audit, then delete and redraw element {ElementId}.");
        return steps;
    }

    private string Name(long? id) => id is null ? "the element" : $"{Singular(Tab)} {id}";

    private static string Singular(string? tab)
    {
        string t = tab ?? "";
        return t.Contains("Duct", StringComparison.OrdinalIgnoreCase) || t.Contains("Luftkan", StringComparison.OrdinalIgnoreCase) ? "duct"
            : IsPipe(t) ? "pipe"
            : t.Contains("Cable Tray", StringComparison.OrdinalIgnoreCase) ? "cable tray"
            : t.Contains("Conduit", StringComparison.OrdinalIgnoreCase) ? "conduit"
            : t.Contains("Wall", StringComparison.OrdinalIgnoreCase) || t.Contains("Wände", StringComparison.OrdinalIgnoreCase) ? "wall"
            : "element";
    }

    private static bool IsPipe(string? tab) =>
        tab is not null && (tab.Contains("Pip", StringComparison.OrdinalIgnoreCase) || tab.Contains("Rohr", StringComparison.OrdinalIgnoreCase));

    private static bool IsSystemTab(string tab) =>
        tab is "Duct Systems" or "Piping Systems" or "Luftkanalsysteme" or "Rohrsysteme" || tab.EndsWith("Systems", StringComparison.Ordinal);

    private static string DragName(string control) => control switch
    {
        "RbsSingleCurveEndControl" => "the end grip of a duct/pipe (MEP segment)",
        "WallEndControl" => "the end of a wall",
        "BoxBaseControl" => "a section box handle",
        "FamilyConnectorControl" => "a family connector",
        _ => $"a control ({control})",
    };

    private static string? Quote(string? s) => s is null ? null : $"\"{s}\"";
}
