using RevitJournal.Core.Model;
using RevitJournal.Core.Rules;

namespace RevitJournal.Core.Analysis;

public enum SessionStatus
{
    /// <summary>Revit still has the journal open: the session is running (or Revit is hung).</summary>
    Running,
    ClosedNormally,
    /// <summary>The user closed Revit, but the journal stops before Revit finished shutting down.</summary>
    ShutdownIncomplete,
    /// <summary>The journal stops without the normal shutdown lines: crash, hang + End Task, power loss.</summary>
    EndedAbnormally,
}

/// <summary>A rule that fired, with the journal lines or computed facts that triggered it.</summary>
public sealed class Finding
{
    public const int MaxEvidence = 5;

    public required Rule Rule { get; init; }
    public Severity Severity { get; set; }
    public int HitCount { get; set; }
    public List<LineRef> Evidence { get; } = [];
    public List<string> Facts { get; } = [];

    /// <summary>Steps written for this exact case (element ids, model, view); shown before the rule's general fix.</summary>
    public List<string> Steps { get; } = [];

    /// <summary>Rules from other sources that matched the same text; shown together with this one.</summary>
    public List<Rule> AlsoMatched { get; } = [];

    internal void AddEvidence(IEnumerable<LineRef> lines)
    {
        foreach (var l in lines)
        {
            HitCount++;
            if (Evidence.Count < MaxEvidence)
                Evidence.Add(l);
        }
    }
}

public sealed class AnalysisResult
{
    public required JournalSession Session { get; init; }
    public required SessionStatus Status { get; init; }
    public required string Headline { get; init; }
    public required IReadOnlyList<string> Summary { get; init; }
    public required IReadOnlyList<Finding> Findings { get; init; }
    public required IReadOnlyList<UserAction> LastActions { get; init; }

    /// <summary>What the user was doing when the session ended abnormally; null otherwise.</summary>
    public CrashContext? Context { get; init; }

    /// <summary>For an abnormal end: whether a crash or a hang is proven, or neither.</summary>
    public EndKind EndKind { get; init; }

    /// <summary>The proof behind <see cref="EndKind"/> (dump file, journal crash record, Windows event), or why there is none.</summary>
    public IReadOnlyList<string> EndEvidence { get; init; } = [];

    public Severity? WorstSeverity => Findings.Count == 0 ? null : Findings.Max(f => f.Severity);
}

/// <summary>Facts about the journal that live outside its text (file system state, the Windows event log).</summary>
public sealed record AnalysisContext(bool IsBeingWritten, IReadOnlyList<string> DumpFiles)
{
    /// <summary>Revit.exe crash and hang events Windows recorded on this computer.</summary>
    public IReadOnlyList<WindowsCrashEvent> CrashEvents { get; init; } = [];

    /// <summary>The journal lies in this computer's own Revit journal folder, not a copy sent from elsewhere.</summary>
    public bool IsLocal { get; init; } = true;

    /// <summary>
    /// journal.NNNN.workerK.log files next to the journal: the logs of RevitWorker.exe, the helper process Revit
    /// starts for background work. Same line format as the journal.
    /// </summary>
    public IReadOnlyList<string> WorkerLogs { get; init; } = [];

    /// <summary>How the journal with the previous number in the same folder ended; null when there is none or it is still open.</summary>
    public PreviousSession? Previous { get; init; }

    /// <summary>Free space on the journal's drive at analysis time; only for a journal in this computer's own journal folder.</summary>
    public DiskSpace? Disk { get; init; }

    public static AnalysisContext ForFile(string journalPath)
    {
        string dir = Path.GetDirectoryName(journalPath) ?? ".";
        string stem = Path.GetFileNameWithoutExtension(journalPath); // journal.0117
        var dumps = Directory.Exists(dir)
            ? Directory.EnumerateFiles(dir, stem + "*.dmp").ToList()
            : [];
        bool local = Path.GetFullPath(journalPath).StartsWith(JournalLocator.DefaultRoot, StringComparison.OrdinalIgnoreCase);
        var workers = Directory.Exists(dir)
            ? Directory.EnumerateFiles(dir, stem + ".worker*.log").Order(StringComparer.OrdinalIgnoreCase).ToList()
            : [];
        return new AnalysisContext(File.Exists(journalPath) && Parsing.JournalReader.IsBeingWritten(journalPath), dumps)
        {
            WorkerLogs = workers,
            Previous = PreviousSession.Of(journalPath),
            Disk = local ? DiskSpace.Of(journalPath) : null,
            IsLocal = local,
            // Events of this computer say nothing about a journal that came from another one.
            CrashEvents = local ? WindowsCrashLog.Recent() : [],
        };
    }
}

/// <summary>The end of the session recorded just before this one (journal.NNNN-1.txt).</summary>
public sealed record PreviousSession(string Name, DateTime End, bool EndedNormally)
{
    public static PreviousSession? Of(string journalPath)
    {
        // journal.0047.txt -> journal.0046.txt
        string[] parts = Path.GetFileName(journalPath).Split('.');
        if (parts.Length != 3 || !int.TryParse(parts[1], out int number) || number <= 0)
            return null;
        string name = $"{parts[0]}.{(number - 1).ToString(new string('0', parts[1].Length), System.Globalization.CultureInfo.InvariantCulture)}.{parts[2]}";
        string path = Path.Combine(Path.GetDirectoryName(journalPath) ?? ".", name);
        try
        {
            if (!File.Exists(path) || Parsing.JournalReader.IsBeingWritten(path))
                return null;
            DateTime? end = null;
            bool normal = false;
            foreach (string line in Parsing.JournalReader.ReadTail(path))
            {
                if (!Parsing.JournalTimestamp.TryParse(line, out var t))
                    continue;
                end = t;
                normal |= line.Contains("finished recording journal file", StringComparison.Ordinal);
            }
            return end is { } e ? new PreviousSession(name, e, normal) : null;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return null;
        }
    }
}

/// <summary>Free space of a drive ("C:") at one moment.</summary>
public sealed record DiskSpace(string Name, long FreeMb, DateTime At)
{
    public static DiskSpace? Of(string path)
    {
        try
        {
            if (Path.GetPathRoot(Path.GetFullPath(path)) is not { Length: > 0 } root)
                return null;
            var drive = new DriveInfo(root);
            return drive.IsReady ? new DiskSpace(root.TrimEnd('\\', '/'), drive.AvailableFreeSpace / (1024 * 1024), DateTime.Now) : null;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ArgumentException)
        {
            return null;
        }
    }
}
