namespace RevitJournal.Core.Model;

/// <summary>Everything the parser extracted from one journal file.</summary>
public sealed class JournalSession(string filePath, IReadOnlyList<string> lines)
{
    public string FilePath { get; } = filePath;

    /// <summary>The raw lines; empty after <see cref="ReleaseLines"/>.</summary>
    public IReadOnlyList<string> Lines { get; private set; } = lines;
    public int LineCount { get; } = lines.Count;

    /// <summary>Journals are tens of MB; drop the text once analysis has extracted what the report needs.</summary>
    public void ReleaseLines() => Lines = [];

    public string? Release { get; set; }
    public string? Build { get; set; }
    public DateOnly? BuildDate { get; set; }
    public string? UserName { get; set; }
    public string? MachineName { get; set; }

    public DateTime? Start { get; set; }
    public DateTime? LastTimestamp { get; set; }
    public bool EndedNormally { get; set; }
    /// <summary>The user asked Revit to quit (ID_APP_EXIT or closing the main window).</summary>
    public DateTime? ExitRequestedAt { get; set; }
    public bool IsPlayback { get; set; }

    public List<RamSample> Ram { get; } = [];
    public List<GdiSample> Gdi { get; } = [];
    public List<SlowOperation> SlowOperations { get; } = [];
    public List<UserAction> UserActions { get; } = [];
    public List<DialogShown> Dialogs { get; } = [];
    public List<SyncAttempt> Syncs { get; } = [];
    /// <summary>Model opens timed by SLOG '>Open' / '<Open' events.</summary>
    public List<TimedOperation> Opens { get; } = [];
    /// <summary>Local copies of a central model ('createNewLocal: starting' / 'finished').</summary>
    public List<TimedOperation> LocalCopies { get; } = [];
    /// <summary>Top-level operations Revit timed itself ("' 352.79  1:&lt;&lt;API External Command Time"), 60 s or longer.</summary>
    public List<SlowOperation> LongOperations { get; } = [];
    public List<AddIn> AddIns { get; } = [];
    public List<ModelInfo> Models { get; } = [];
    public List<LineRef> Exceptions { get; } = [];

    /// <summary>What the user was looking at and touching: views, selections, element picks, drags, contextual tabs.</summary>
    public List<Interaction> Interactions { get; } = [];
    /// <summary>Warnings and errors Revit posted ("Error posted:" and error-dialog text).</summary>
    public List<LineRef> PostedMessages { get; } = [];
    /// <summary>Tags/families whose host element was deleted in a linked model.</summary>
    public List<OrphanedHosted> Orphans { get; } = [];
    /// <summary>Cloud models appear in view directives as "[&lt;guid&gt;.rvt]"; this maps the guid file to the model name.</summary>
    public Dictionary<string, string> DocumentNames { get; } = new(StringComparer.OrdinalIgnoreCase);

    public string DocumentName(string? token) =>
        token is null ? "" : DocumentNames.TryGetValue(token, out var name) ? name : token;

    public TimeSpan? Duration => Start is { } s && LastTimestamp is { } e ? e - s : null;

    /// <summary>Total physical RAM of the machine in MB, from 'RAM Statistics' lines.</summary>
    public int? TotalRamMb => Ram.Where(r => r.TotalMb is not null).Select(r => r.TotalMb).Max();
}

/// <summary>A pointer back to a line in the journal (1-based), with the time of the block it belongs to.</summary>
public sealed record LineRef(int Line, DateTime? Time, string Text);

/// <summary>Machine-wide free RAM and Revit's own usage at one point in time.</summary>
public sealed record RamSample(int Line, DateTime? Time, int AvailableMb, int? TotalMb, int? RevitUsedMb, int? RevitPeakMb);

public sealed record GdiSample(int Line, DateTime? Time, int Available, int Used);

/// <summary>A BIG_GAP entry: Revit itself flagged an operation as slow.</summary>
public sealed record SlowOperation(int Line, DateTime? Time, double Seconds, string Operation);

/// <summary>Something the user did: a command, ribbon click, add-in UI event.</summary>
public sealed record UserAction(int Line, DateTime? Time, string Kind, string Text, string? CommandId);

public sealed record DialogShown(int Line, DateTime? Time, string Id, string? Text);

public sealed class SyncAttempt
{
    public required int StartLine { get; init; }
    public required DateTime Start { get; init; }
    public int? EndLine { get; set; }
    public DateTime? End { get; set; }
    public int ReloadLatestCount { get; set; }
    public bool GaveUp { get; set; }
    public List<LineRef> Errors { get; } = [];

    public bool Finished => End is not null;
    public bool Succeeded => Finished && !GaveUp && Errors.Count == 0;
    public TimeSpan? Duration => End - Start;
}

public enum InteractionKind
{
    /// <summary>Active view changed or was redrawn: Text = view title, Detail = document token.</summary>
    View,
    /// <summary>"SEL RESULT: ..." — Text = what is selected ("workset : Category : Family : Type" or "N items").</summary>
    Selection,
    /// <summary>Elements under the cursor on a click ("Candidates ..."); Ids[0] is the one Revit picked.</summary>
    Pick,
    /// <summary>A grip or control the user dragged (Text = control name, e.g. RbsSingleCurveEndControl).</summary>
    Drag,
    /// <summary>A contextual ribbon tab was built (Text = tab name, e.g. "Modify | Ducts").</summary>
    Tab,
}

public sealed record Interaction(int Line, DateTime? Time, InteractionKind Kind, string Text, IReadOnlyList<long> Ids, string? Detail = null)
{
    public bool IsSnap => Detail == "snap";
}

public sealed record OrphanedHosted(int Line, long ElementId, long LinkId);

/// <summary>An operation with a start and (if it completed) an end in the journal.</summary>
public sealed class TimedOperation
{
    public required int StartLine { get; init; }
    public required DateTime Start { get; init; }
    public string? Name { get; set; }
    public int? EndLine { get; set; }
    public DateTime? End { get; set; }

    public bool Finished => End is not null;
    public TimeSpan? Duration => End - Start;
}

public sealed record AddIn(int Line, string Name, string VendorId, string Vendor, bool Failed)
{
    public bool IsAutodesk => VendorId.Equals("ADSK", StringComparison.OrdinalIgnoreCase)
                              || Vendor.Contains("Autodesk", StringComparison.OrdinalIgnoreCase);
}

public sealed record ModelInfo(int Line, string Path, string? Worksharing, int? SavedInYear, string? SavedInBuild);
