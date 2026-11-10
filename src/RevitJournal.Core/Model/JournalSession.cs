namespace RevitJournal.Core.Model;

/// <summary>Everything the parser extracted from one journal file.</summary>
public sealed class JournalSession(string filePath, IReadOnlyList<string> lines)
{
    public string FilePath { get; } = filePath;

    /// <summary>How many of the last lines are kept as text. Journals are tens of MB; the analysis only needs the end.</summary>
    public const int TailSize = 200;

    /// <summary>The whole text, only when the session was parsed from lines already in memory (tests); otherwise empty.</summary>
    public IReadOnlyList<string> Lines { get; } = lines;

    /// <summary>Number of lines parsed so far.</summary>
    public int LineCount { get; internal set; }

    /// <summary>Lines read from the session's worker logs (0 when there are none).</summary>
    public int WorkerLogLines { get; internal set; }

    internal Queue<string> TailQueue { get; private set; } = new();

    /// <summary>The last <see cref="TailSize"/> lines; Tail[^1] is line number <see cref="LineCount"/>.</summary>
    public IReadOnlyList<string> Tail => [.. TailQueue];

    /// <summary>Text-rule matches collected while parsing; null when the analyzer should match <see cref="Lines"/> itself.</summary>
    public Dictionary<string, List<LineRef>>? RuleHits { get; internal set; }

    /// <summary>Journal lines that record a crash Revit handled itself ("ExceptionCode=", unrecoverable error, "program terminated").</summary>
    public List<LineRef> FatalMarkers { get; } = [];

    /// <summary>
    /// A copy that no longer changes, for handing a live session to another thread. Sync attempts and timed operations
    /// are cloned because the parser completes them later; everything else is immutable records in copied lists.
    /// </summary>
    public JournalSession Snapshot()
    {
        var c = new JournalSession(FilePath, [])
        {
            LineCount = LineCount, WorkerLogLines = WorkerLogLines, TailQueue = new Queue<string>(TailQueue),
            Release = Release, Build = Build, BuildDate = BuildDate, UserName = UserName, MachineName = MachineName,
            Start = Start, LastTimestamp = LastTimestamp, EndedNormally = EndedNormally, ExitRequestedAt = ExitRequestedAt,
            IsPlayback = IsPlayback, LicenseReadyAt = LicenseReadyAt, StartupCompletedAt = StartupCompletedAt,
            OpenViews = OpenViews, OpenProjects = OpenProjects,
            RuleHits = RuleHits?.ToDictionary(kv => kv.Key, kv => kv.Value.ToList(), StringComparer.Ordinal),
        };
        c.Ram.AddRange(Ram); c.Gdi.AddRange(Gdi); c.SlowOperations.AddRange(SlowOperations); c.UserActions.AddRange(UserActions);
        c.Dialogs.AddRange(Dialogs); c.LongOperations.AddRange(LongOperations); c.AddIns.AddRange(AddIns); c.Models.AddRange(Models);
        c.Exceptions.AddRange(Exceptions); c.Interactions.AddRange(Interactions); c.PostedMessages.AddRange(PostedMessages);
        c.Orphans.AddRange(Orphans); c.FatalMarkers.AddRange(FatalMarkers); c.AddInActivity.AddRange(AddInActivity);
        c.ModelFiles.AddRange(ModelFiles);
        c.Syncs.AddRange(Syncs.Select(s => s.Clone()));
        c.Opens.AddRange(Opens.Select(o => o.Clone()));
        c.LocalCopies.AddRange(LocalCopies.Select(o => o.Clone()));
        foreach (var (k, v) in DocumentNames)
            c.DocumentNames[k] = v;
        return c;
    }

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

    /// <summary>"License initialization complete": the first start-up step that needs the licensing service is done.</summary>
    public DateTime? LicenseReadyAt { get; set; }
    /// <summary>"&lt;-desktop InitManagedInstance": all add-ins are loaded and Revit is ready for the user.</summary>
    public DateTime? StartupCompletedAt { get; set; }

    /// <summary>View tabs open right now, as Revit last logged it ("[Jrn.TabbedViewAnalytics] ViewCount: N"); null before the first view.</summary>
    public int? OpenViews { get; set; }
    /// <summary>Models and families open right now ("[Jrn.TabbedViewAnalytics] ProjectCount: N").</summary>
    public int? OpenProjects { get; set; }

    /// <summary>How many of the last add-in events are kept.</summary>
    public const int AddInActivitySize = 30;
    /// <summary>The last add-ins that did something (registered or dropped an event, ran an external event), oldest first.</summary>
    public List<AddInActivity> AddInActivity { get; } = [];

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
    /// <summary>The .rvt files behind the session: the model being edited (with the size Revit logged) and every linked file it loaded.</summary>
    public List<ModelFile> ModelFiles { get; } = [];

    public Dictionary<string, string> DocumentNames { get; } = new(StringComparer.OrdinalIgnoreCase);

    public string DocumentName(string? token) =>
        token is null ? "" : DocumentNames.TryGetValue(token, out var name) ? name : token;

    public TimeSpan? Duration => Start is { } s && LastTimestamp is { } e ? e - s : null;

    /// <summary>Total physical RAM of the machine in MB, from 'RAM Statistics' lines.</summary>
    public int? TotalRamMb => Ram.Where(r => r.TotalMb is not null).Select(r => r.TotalMb).Max();
}

/// <summary>A pointer back to a line in the journal (1-based), with the time of the block it belongs to.</summary>
/// <param name="Source">Null for the journal itself; "worker1" for a line of journal.NNNN.worker1.log.</param>
public sealed record LineRef(int Line, DateTime? Time, string Text, string? Source = null)
{
    /// <summary>"44184", or "worker1:211" for a line of a worker log.</summary>
    public string Where => Source is null ? Line.ToString(System.Globalization.CultureInfo.InvariantCulture) : $"{Source}:{Line}";
}

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

    public SyncAttempt Clone()
    {
        var c = new SyncAttempt { StartLine = StartLine, Start = Start, EndLine = EndLine, End = End, ReloadLatestCount = ReloadLatestCount, GaveUp = GaveUp };
        c.Errors.AddRange(Errors);
        return c;
    }
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

    public TimedOperation Clone() => new() { StartLine = StartLine, Start = Start, Name = Name, EndLine = EndLine, End = End };
}

public sealed record AddIn(int Line, string Name, string VendorId, string Vendor, bool Failed)
{
    public bool IsAutodesk => VendorId.Equals("ADSK", StringComparison.OrdinalIgnoreCase)
                              || Vendor.Contains("Autodesk", StringComparison.OrdinalIgnoreCase);
}

/// <summary>An add-in was active at this point: Revit logged an event registration or an external event for it.</summary>
public sealed record AddInActivity(int Line, DateTime? Time, string Name);

/// <summary>
/// A model file on disk. For the edited model Revit logs size and element count ("File Size: ..., Element Count: ...");
/// for a link only the path it was loaded from ("Load linked file: ..."), so the size has to come from the disk.
/// </summary>
public sealed record ModelFile(int Line, string Path, long? Bytes, int? Elements, bool IsLink);

public sealed record ModelInfo(int Line, string Path, string? Worksharing, int? SavedInYear, string? SavedInBuild);
