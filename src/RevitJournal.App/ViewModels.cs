using System.ComponentModel;
using System.Globalization;
using System.Runtime.CompilerServices;
using RevitJournal.Core;
using RevitJournal.Core.Analysis;
using RevitJournal.Core.Live;
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
        // Red only for a proven crash; a session that merely lacks its shutdown lines is a warning.
        SessionStatus.EndedAbnormally => Result.EndKind == EndKind.Crash ? "crash" : "abnormal",
        _ => "pending",
    };

    public string StatusText => StatusKey switch
    {
        "running" => "Running",
        "ok" => "Closed normally",
        "warning" => "Problems found",
        "exit" => "Did not finish closing",
        "crash" => "Crashed",
        "abnormal" => Result?.EndKind == EndKind.Hang ? "Stopped responding and was closed" : "Closed without shutdown record",
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
    private static readonly string[] Precedence = ["crash", "error", "abnormal", "warning", "running", "exit", "pending", "ok"];

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
        int abnormal = keys.Count(k => k == "abnormal");
        int problems = keys.Count(k => k == "warning");
        int running = keys.Count(k => k == "running");
        var parts = new List<string> { Items.Count == 1 ? "1 session" : $"{Items.Count} sessions" };
        if (crashes > 0)
            parts.Add($"{crashes} crashed");
        if (abnormal > 0)
            parts.Add($"{abnormal} closed without shutdown");
        if (problems > 0)
            parts.Add($"{problems} with problems");
        if (running > 0)
            parts.Add($"{running} running");
        if (crashes + abnormal + problems + running == 0 && !keys.Contains("pending"))
            parts.Add("all OK");
        Summary = string.Join(" · ", parts);
    }
}

/// <summary>One running Revit on the start screen, refreshed by the tray agent every few seconds.</summary>
public sealed class LiveCardView(Core.Live.LiveStatus s, DateTime now)
{
    public string JournalPath => s.JournalPath;
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
        f.Evidence.Select(e => $"{e.Where,7}  {Fmt.Time(e.Time)}  {Fmt.Trim(e.Text, 220)}"));
    /// <summary>How far to trust this finding, shown in the card header.</summary>
    public string Confidence => TextReport.ConfidenceChip(f.Rule);
    public string Source => $"Source: {TextReport.ConfidenceLabel(f.Rule.Confidence)}"
                            + (f.Rule.Sources.Count > 0 ? $" — {f.Rule.Sources[0].Title}" : "");
    public string? SourceUrl => f.Rule.Sources.FirstOrDefault()?.Url;
}

/// <summary>The overview of the computer and the running Revit sessions, as shown in the tray flyout and on the start screen.</summary>
public sealed class HealthView
{
    public HealthView(HealthReport r, IReadOnlyList<LiveStatus> sessions)
    {
        StatusKey = Key(r.Level);
        StatusTitle = r.Level switch
        {
            HealthLevel.Danger => "Act now",
            HealthLevel.Watch => "Keep an eye on it",
            _ => "All good",
        };
        StatusReason = r.Issues.Count > 0 ? r.Issues[0].Title
            : r.Revits.Count == 0 ? "Revit is not running. The computer has enough resources."
            : $"{r.Revits.Count} Revit running. Memory, drawing resources and disk are fine.";

        long used = Math.Max(0, r.TotalRamMb - r.FreeRamMb);
        RamPercent = r.TotalRamMb > 0 ? 100.0 * used / r.TotalRamMb : 0;
        RamText = $"{Fmt.Mb((int)used)} of {Fmt.Mb((int)r.TotalRamMb)} used";
        RamFreeText = $"{Fmt.Mb((int)r.FreeRamMb)} free";
        RamKey = Key(r.Issues.Where(i => i.Key.StartsWith("ram", StringComparison.Ordinal)).Select(i => i.Level).DefaultIfEmpty(HealthLevel.Ok).Max());
        ForecastText = r.RamRunsOutIn is { } eta && eta <= HealthMonitor.ForecastWatch
            ? $"Falling: runs out in about {Math.Max(1, (int)Math.Round(eta.TotalMinutes))} min"
            : r.RamHistory.Count > 1 ? $"Free memory, last {Math.Max(1, (int)Math.Round((r.RamHistory[^1].Time - r.RamHistory[0].Time).TotalMinutes))} min" : "";
        Sparkline = Points(r.RamHistory, r.TotalRamMb);

        CpuPercent = r.CpuPercent;
        CpuText = $"{r.CpuPercent:0} %";
        var disk = r.Disks.OrderBy(d => d.FreeMb).FirstOrDefault();
        DiskText = disk is null ? "" : $"{disk.Name} {Fmt.Mb((int)disk.FreeMb)} free";
        DiskKey = Key(disk?.Level ?? HealthLevel.Ok);
        DiskPercent = disk is { TotalMb: > 0 } ? 100.0 * (disk.TotalMb - disk.FreeMb) / disk.TotalMb : 0;

        Revits = [.. r.Revits.Select(p => new RevitHealthRow(p, Match(p, r.Revits, sessions)))];
        NoRevit = r.Revits.Count == 0;
        Issues = [.. r.Issues.Take(3).Select(i => new HealthIssueRow(Key(i.Level), i.Title, i.Advice))];
        HasIssues = Issues.Count > 0;
    }

    public string StatusKey { get; }
    public string StatusTitle { get; }
    public string StatusReason { get; }
    public double RamPercent { get; }
    public string RamText { get; }
    public string RamFreeText { get; }
    public string RamKey { get; }
    public string ForecastText { get; }
    public System.Windows.Media.PointCollection Sparkline { get; }
    public double CpuPercent { get; }
    public string CpuText { get; }
    public string DiskText { get; }
    public string DiskKey { get; }
    public double DiskPercent { get; }
    public IReadOnlyList<RevitHealthRow> Revits { get; }
    public bool NoRevit { get; }
    public IReadOnlyList<HealthIssueRow> Issues { get; }
    public bool HasIssues { get; }

    internal static string Key(HealthLevel level) => level switch { HealthLevel.Danger => "crash", HealthLevel.Watch => "warning", _ => "ok" };

    /// <summary>The journal session of a process: by process id when the monitor could tell, else the only one of that version.</summary>
    private static LiveStatus? Match(RevitHealth p, IReadOnlyList<RevitHealth> all, IReadOnlyList<LiveStatus> sessions) =>
        sessions.FirstOrDefault(s => s.ProcessId == p.ProcessId)
        ?? (all.Count(x => x.Version == p.Version) == 1 ? sessions.FirstOrDefault(s => s.Version == p.Version) : null);

    /// <summary>Free memory over time as points in a 300 x 40 box (0 at the bottom, the machine's RAM at the top).</summary>
    private static System.Windows.Media.PointCollection Points(IReadOnlyList<RamPoint> history, long totalMb)
    {
        var points = new System.Windows.Media.PointCollection();
        if (history.Count < 2 || totalMb <= 0)
            return points;
        double span = Math.Max(1, (history[^1].Time - history[0].Time).TotalSeconds);
        foreach (var p in history)
            points.Add(new System.Windows.Point(300 * (p.Time - history[0].Time).TotalSeconds / span, 40 - 40.0 * p.FreeMb / totalMb));
        points.Freeze();
        return points;
    }
}

public sealed class RevitHealthRow(RevitHealth p, LiveStatus? session)
{
    public string StatusKey => !p.Responding ? HungKey : HealthView.Key(p.Level);
    public string Title => (p.Version.Length > 0 ? $"Revit {p.Version}" : "Revit") + (session?.Model is { Length: > 0 } m ? $" · {m}" : "");
    public string State => !p.Responding ? (p.CpuPercent >= HealthMonitor.BusyCpuPercent ? "Busy, not answering clicks" : "Not responding")
        : session?.Busy is { } busy ? busy.TrimEnd('.') : p.CpuPercent >= HealthMonitor.BusyCpuPercent ? "Working" : "Idle";
    // Red only for a Revit that is stuck; a busy one is a warning.
    private string HungKey => p.CpuPercent >= HealthMonitor.BusyCpuPercent ? "warning" : "crash";
    public string Numbers => $"{Fmt.Mb((int)p.MemoryMb)} RAM · CPU {p.CpuPercent:0} %";
    public string Sync => session?.Sync ?? "";
    public bool HasSync => session is not null;
    public string SyncKey => session?.Sync.Contains("FAILED", StringComparison.Ordinal) == true ? "crash" : "pending";
    public double GdiPercent => 100.0 * Math.Max(p.GdiObjects, p.UserObjects) / HealthMonitor.GuiLimit;
    public string GdiText => $"{Math.Max(p.GdiObjects, p.UserObjects).ToString("N0", CultureInfo.InvariantCulture)} of 10,000 drawing resources";
    public string GdiKey => HealthView.Key(Math.Max(p.GdiObjects, p.UserObjects) >= HealthMonitor.GuiDanger ? HealthLevel.Danger
        : Math.Max(p.GdiObjects, p.UserObjects) >= HealthMonitor.GuiWatch ? HealthLevel.Watch : HealthLevel.Ok);
}

public sealed record HealthIssueRow(string LevelKey, string Title, string Advice);
