using System.ComponentModel;
using System.Runtime.CompilerServices;
using RevitJournal.Core;
using RevitJournal.Core.Analysis;
using RevitJournal.Core.Reporting;
using RevitJournal.Core.Rules;

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

    public AnalysisResult? Result { get => _result; set => Set(ref _result, value); }
    public string? Error { get => _error; set => Set(ref _error, value); }

    public string Title => string.IsNullOrEmpty(File.RevitVersion) ? File.Name : $"Revit {File.RevitVersion} · {File.Name}";
    public string When => File.LastWrite.ToString("ddd dd MMM, HH:mm");

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
    public IReadOnlyList<string> Steps { get; } = [.. f.Rule.Fix.En.Select((s, i) => $"{i + 1}.  {s}")];
    public bool HasEvidence => f.Evidence.Count > 0;
    public string EvidenceTitle => $"Journal lines ({f.HitCount} match{(f.HitCount == 1 ? "" : "es")})";
    public string Evidence => string.Join(Environment.NewLine,
        f.Evidence.Select(e => $"{e.Line,7}  {Fmt.Time(e.Time)}  {Fmt.Trim(e.Text, 220)}"));
    public string Source => $"Source: {TextReport.ConfidenceLabel(f.Rule.Confidence)}"
                            + (f.Rule.Sources.Count > 0 ? $" — {f.Rule.Sources[0].Title}" : "");
    public string? SourceUrl => f.Rule.Sources.FirstOrDefault()?.Url;
}
