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

    public Severity? WorstSeverity => Findings.Count == 0 ? null : Findings.Max(f => f.Severity);
}

/// <summary>Facts about the journal that live outside its text (file system state).</summary>
public sealed record AnalysisContext(bool IsBeingWritten, IReadOnlyList<string> DumpFiles)
{
    public static AnalysisContext ForFile(string journalPath)
    {
        string dir = Path.GetDirectoryName(journalPath) ?? ".";
        string stem = Path.GetFileNameWithoutExtension(journalPath); // journal.0117
        var dumps = Directory.Exists(dir)
            ? Directory.EnumerateFiles(dir, stem + "*.dmp").ToList()
            : [];
        return new AnalysisContext(File.Exists(journalPath) && Parsing.JournalReader.IsBeingWritten(journalPath), dumps);
    }
}
