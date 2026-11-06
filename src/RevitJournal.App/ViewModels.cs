using System.ComponentModel;
using System.Globalization;
using System.Runtime.CompilerServices;
using RevitJournal.Core;
using RevitJournal.Core.Analysis;
using RevitJournal.Core.Reporting;
using RevitJournal.Core.Rules;
using RevitJournal.Core.Symptoms;

namespace RevitJournal.App;

public abstract class Observable : INotifyPropertyChanged
{
    public event PropertyChangedEventHandler? PropertyChanged;

    protected void Set<T>(ref T field, T value, [CallerMemberName] string? name = null)
    {
        if (EqualityComparer<T>.Default.Equals(field, value))
            return;
        field = value;
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(name));
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(string.Empty)); // refresh derived properties
    }
}

/// <summary>One row in the session list.</summary>
public sealed class JournalItem(JournalFile file) : Observable
{
    private AnalysisResult? _result;
    private string? _error;

    public JournalFile File { get; private set; } = file;

    public AnalysisResult? Result { get => _result; set { Set(ref _result, value); Day?.Recompute(); } }
    public string? Error { get => _error; set { Set(ref _error, value); Day?.Recompute(); } }

    /// <summary>The day this session belongs to (by the journal's last write time). Used for list grouping.</summary>
    public DayGroup? Day { get; set; }
    public DateTime SortKey => File.LastWrite;

    public string Title => string.IsNullOrEmpty(File.RevitVersion) ? File.Name : $"Revit {File.RevitVersion} · {File.Name}";
    public string When => File.LastWrite.ToString("HH:mm", CultureInfo.InvariantCulture);

    public string StatusKey => Error is not null ? "error" : Result?.Status switch
    {
        SessionStatus.Running => "running",
        SessionStatus.ClosedNormally => Result.WorstSeverity >= Severity.High ? "warning" : "ok",
        SessionStatus.ShutdownIncomplete => Result.WorstSeverity >= Severity.High ? "warning" : "exit",
        SessionStatus.EndedAbnormally => "crash",
        _ => "pending",
    };

    public string StatusText => StatusKey switch
    {
        "running" => "Running",
        "ok" => "Closed normally",
        "warning" => "Problems found",
        "exit" => "Did not finish closing",
        "crash" => "Ended abnormally",
        "error" => "Could not read",
        _ => "Analyzing…",
    };

    public string Headline => Error ?? Result?.Headline ?? "";

    public void Refresh(JournalFile file) => File = file;
}

/// <summary>
/// A day in the session list. One shared instance per date, so the list can group items by reference
/// and the header updates as the day's journals finish analyzing.
/// </summary>
public sealed class DayGroup(DateOnly date) : Observable
{
    // Worst first: the day takes the status of its most serious session.
    private static readonly string[] Precedence = ["crash", "error", "warning", "running", "exit", "pending", "ok"];

    private string _statusKey = "pending";
    private string _summary = "";
    private bool _isExpanded;

    /// <summary>Days start collapsed; the user opens the ones they need.</summary>
    public bool IsExpanded { get => _isExpanded; set => Set(ref _isExpanded, value); }

    public DateOnly Date { get; } = date;
    public List<JournalItem> Items { get; } = [];

    public string Label
    {
        get
        {
            var today = DateOnly.FromDateTime(DateTime.Today);
            string day = Date.ToString("ddd dd MMM yyyy", CultureInfo.InvariantCulture);
            return Date == today ? $"Today · {day}" : Date == today.AddDays(-1) ? $"Yesterday · {day}" : day;
        }
    }

    public string StatusKey { get => _statusKey; private set => Set(ref _statusKey, value); }
    public string Summary { get => _summary; private set => Set(ref _summary, value); }

    /// <summary>✓ for a day without problems, a dot otherwise.</summary>
    public string Icon => StatusKey is "ok" or "exit" ? "✓" : "●";

    public void Recompute()
    {
        var keys = Items.Select(i => i.StatusKey).ToList();
        StatusKey = Precedence.FirstOrDefault(keys.Contains) ?? "pending";

        int crashes = keys.Count(k => k is "crash" or "error");
        int problems = keys.Count(k => k == "warning");
        int running = keys.Count(k => k == "running");
        var parts = new List<string> { Items.Count == 1 ? "1 session" : $"{Items.Count} sessions" };
        if (crashes > 0)
            parts.Add($"{crashes} ended abnormally");
        if (problems > 0)
            parts.Add($"{problems} with problems");
        if (running > 0)
            parts.Add($"{running} running");
        if (crashes + problems + running == 0 && !keys.Contains("pending"))
            parts.Add("all OK");
        Summary = string.Join(" · ", parts);
    }
}

/// <summary>One running Revit on the start screen, refreshed by the tray agent every few seconds.</summary>
public sealed class LiveCardView(Core.Live.LiveStatus s, DateTime now)
{
    public string Title => s.Responding == false ? $"Revit {s.Version} is not responding" : $"Revit {s.Version} is running";
    public string StatusKey => s.Responding == false || s.Sync.Contains("FAILED", StringComparison.Ordinal) ? "crash"
        : s.Busy is not null ? "running" : "ok";
    public string Details => string.Join("   ·   ", new[]
    {
        s.Model,
        s.Busy ?? (s.LastActivity is { } t ? $"last activity {Fmt.Ago(now - t)}" : null),
        s.Sync,
        s.FreeRamMb is { } free ? $"free RAM {Fmt.Mb(free)}" + (s.TotalRamMb is { } total ? $" of {Fmt.Mb(total)}" : "") : null,
    }.Where(x => !string.IsNullOrEmpty(x)));
}

/// <summary>A big button on the start screen.</summary>
public sealed class SymptomCard(Symptom symptom)
{
    public Symptom Symptom => symptom;
    public string Icon => symptom.Icon;
    public string Title => symptom.Title.En;
    public string Subtitle => symptom.Subtitle.En;
}

/// <summary>Option in the Revit version picker; Version null means all versions.</summary>
public sealed record VersionOption(string? Version, string Label)
{
    public override string ToString() => Label;
}

/// <summary>The answer page for a symptom.</summary>
public sealed class DiagnosisView(Diagnosis d)
{
    public Diagnosis Diagnosis => d;
    public string Icon => d.Symptom.Icon;
    public string SymptomTitle => d.Symptom.Title.En;
    public string Headline => d.Headline;
    public string Source => d.Session is { } r
        ? $"Based on {System.IO.Path.GetFileName(r.Session.FilePath)} · session {Fmt.Stamp(r.Session.Start)} – {Fmt.Time(r.Session.LastTimestamp)}"
        : "";
    public bool HasSource => d.Session is not null;
    public IReadOnlyList<string> Facts => d.Facts;
    public bool HasFacts => d.Facts.Count > 0;
    public IReadOnlyList<FindingView> Findings { get; } = [.. d.Findings.Select((f, i) => new FindingView(f, i == 0))];
    public bool HasFindings => d.Findings.Count > 0;
    public string NoCauseText => "The journal does not point to a specific cause. These steps fix most cases:";
    public bool ShowNoCause => d.Findings.Count == 0;
    public string ChecklistTitle => d.Findings.Count > 0 ? "IF THAT DOESN'T HELP" : "WHAT TO DO";
    public IReadOnlyList<string> Checklist { get; } = [.. d.Symptom.Checklist.En.Select((s, i) => $"{i + 1}.  {s}")];
}

/// <summary>The report pane for the selected journal.</summary>
public sealed class ReportView(AnalysisResult r)
{
    public AnalysisResult Result => r;
    public string Headline => r.Headline;
    public string FileName => System.IO.Path.GetFileName(r.Session.FilePath);
    public string FilePath => r.Session.FilePath;
    public IReadOnlyList<string> Summary => r.Summary;
    public IReadOnlyList<FindingView> Findings { get; } = [.. r.Findings.Select((f, i) => new FindingView(f, i == 0))];
    public bool HasFindings => r.Findings.Count > 0;
    public bool NoFindings => r.Findings.Count == 0;
    public string FindingsTitle => $"FINDINGS ({r.Findings.Count})";

    public bool ShowLastActions => r.Status != SessionStatus.ClosedNormally && r.LastActions.Count > 0;
    public IReadOnlyList<string> LastActions { get; } =
        [.. r.LastActions.Select(a => $"{Fmt.Time(a.Time)}   {a.CommandId ?? a.Kind}   {Fmt.Trim(a.Text, 110)}")];
}

public sealed class FindingView(Finding f, bool expanded)
{
    public Severity Severity => f.Severity;
    public string SeverityText => f.Severity.ToString().ToUpperInvariant();
    public string Title => f.Rule.Title.En;
    public string Cause => f.Rule.Cause.En;
    public bool IsExpanded { get; set; } = expanded;
    public IReadOnlyList<string> Facts => f.Facts;
    public bool HasFacts => f.Facts.Count > 0;
    /// <summary>Steps written for this case (element ids, model names), when the analysis could produce them.</summary>
    public IReadOnlyList<string> CaseSteps { get; } = [.. f.Steps.Select((s, i) => $"{i + 1}.  {s}")];
    public bool HasCaseSteps => f.Steps.Count > 0;
    public string StepsTitle => f.Steps.Count > 0 ? "General advice" : "What to do";
    public IReadOnlyList<string> Steps { get; } = [.. f.Rule.Fix.En.Select((s, i) => $"{i + 1}.  {s}")];
    public bool HasEvidence => f.Evidence.Count > 0;
    public string EvidenceTitle => $"Journal lines ({f.HitCount} match{(f.HitCount == 1 ? "" : "es")})";
    public string Evidence => string.Join(Environment.NewLine,
        f.Evidence.Select(e => $"{e.Line,7}  {Fmt.Time(e.Time)}  {Fmt.Trim(e.Text, 220)}"));
    public string Source => $"Source: {TextReport.ConfidenceLabel(f.Rule.Confidence)}"
                            + (f.Rule.Sources.Count > 0 ? $" — {f.Rule.Sources[0].Title}" : "");
    public string? SourceUrl => f.Rule.Sources.FirstOrDefault()?.Url;
}
