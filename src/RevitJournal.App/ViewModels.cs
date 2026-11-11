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
    public string Source => $"Source: {TextReport.ConfidenceLabel(f.Rule.Confidence)}";
    /// <summary>The articles the advice comes from; each opens in the browser.</summary>
    public IReadOnlyList<RuleSource> Sources => f.Rule.Sources;
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
            : $"{r.Revits.Count} Revit running. RAM, drawing resources and disk are fine.";

        long used = Math.Max(0, r.TotalRamMb - r.FreeRamMb);
        RamPercent = r.TotalRamMb > 0 ? 100.0 * used / r.TotalRamMb : 0;
        RamText = $"{Fmt.Mb((int)used)} of {Fmt.Mb((int)r.TotalRamMb)} RAM used";
        RamFreeText = $"{Fmt.Mb((int)r.FreeRamMb)} free";
        RamFreeValue = Fmt.Mb((int)r.FreeRamMb);
        RamKey = Key(r.Issues.Where(i => i.Key.StartsWith("ram", StringComparison.Ordinal)).Select(i => i.Level).DefaultIfEmpty(HealthLevel.Ok).Max());
        ForecastText = RamText + (r.RamRunsOutIn is { } eta && eta <= HealthMonitor.ForecastWatch
            ? $" · at this rate about {Math.Max(1, (int)Math.Round(eta.TotalMinutes))} min left" : "");
        RamChart = new MemoryChartData(r.RamHistory, r.TotalRamMb, (long)Math.Max(HealthMonitor.RamWatchMb, HealthMonitor.RamWatchShare * r.TotalRamMb));
        // The change over the window is said in words only when it is more than noise.
        if (r.RamHistory.Count > 1 && r.RamHistory[^1].Time - r.RamHistory[0].Time >= TimeSpan.FromMinutes(3)
            && Math.Abs(r.RamHistory[^1].FreeMb - r.RamHistory[0].FreeMb) is >= 512 and var change)
        {
            bool falling = r.RamHistory[^1].FreeMb < r.RamHistory[0].FreeMb;
            int minutes = (int)Math.Round((r.RamHistory[^1].Time - r.RamHistory[0].Time).TotalMinutes);
            RamTrendText = $"{(falling ? "fell" : "rose")} {Fmt.Mb((int)change)} in {minutes} min";
            RamTrendKey = falling ? "warning" : "pending";
        }

        // The three tiles: free RAM, free disk space, Revit open. Each figure has its label above and what it is measured against below.
        RamNote = $"of {Fmt.Mb((int)r.TotalRamMb)}"
                  + (RamTrendText.Length > 0 ? $" · {RamTrendText}" : "")
                  + (r.RamRunsOutIn is { } left && left <= HealthMonitor.ForecastWatch ? $" · about {Math.Max(1, (int)Math.Round(left.TotalMinutes))} min left at this rate" : "");

        var disk = r.Disks.OrderBy(d => d.FreeMb).FirstOrDefault();
        DiskLabel = disk is null ? "Free disk" : $"Free disk {disk.Name}";
        DiskValue = disk is null ? "–" : Fmt.Mb((int)disk.FreeMb);
        DiskNote = disk is null ? "" : disk.Level != HealthLevel.Ok ? "Autodesk requires at least 30 GB" : $"of {Fmt.Mb((int)disk.TotalMb)}";

        RevitCount = r.Revits.Count.ToString(CultureInfo.InvariantCulture);
        RevitNote = r.Revits.Count == 0 ? "Revit is not running" : $"using {Fmt.Mb((int)r.Revits.Sum(p => p.MemoryMb))} of RAM";
        DiskText = disk is null ? "" : $"{disk.Name} {Fmt.Mb((int)disk.FreeMb)} free";
        DiskKey = Key(disk?.Level ?? HealthLevel.Ok);
        DiskPercent = disk is { TotalMb: > 0 } ? 100.0 * (disk.TotalMb - disk.FreeMb) / disk.TotalMb : 0;

        Revits = [.. r.Revits.Select(p => new RevitHealthRow(p, Match(p, r.Revits, sessions), r.TotalRamMb))];
        HasRevits = Revits.Count > 0;
        TableNote = Revits.Any(x => x.HasModel)
            ? "* Autodesk: RAM ≈ 20 × the model file size. With linked models it is our estimate."
            : "";
        NoRevit = r.Revits.Count == 0;
        int openViews = sessions.Sum(s => s.OpenViews ?? 0);
        RevitShort = NoRevit ? "Revit is not running"
            : $"{r.Revits.Count} Revit" + (openViews > 0 ? $" · {openViews} views" : "") + $" · {Fmt.Mb((int)r.Revits.Sum(p => p.MemoryMb))} RAM";
        RevitSummary = Summary(r, sessions);
        HasRevitSummary = RevitSummary.Length > 0;
        Issues = [.. r.Issues.Take(3).Select(i => new HealthIssueRow(i.Key, Key(i.Level), i.Title, i.Advice))];
        HasIssues = Issues.Count > 0;
    }

    public string StatusKey { get; }
    public string StatusTitle { get; }
    public string StatusReason { get; }
    public double RamPercent { get; }
    public string RamText { get; }
    public string RamFreeText { get; }
    public string RamFreeValue { get; }
    public bool HasRamTrend => RamTrendText.Length > 0;
    /// <summary>"2 Revit · 12 views · 24.0 GB", or that none is running.</summary>
    public string RevitShort { get; }
    public string RamKey { get; }
    public string ForecastText { get; }
    public MemoryChartData RamChart { get; }
    public string RamTrendText { get; } = "";
    public string RamNote { get; }
    public string DiskLabel { get; }
    public string DiskValue { get; }
    public string DiskNote { get; }
    public string RevitCount { get; }
    public string RevitNote { get; }
    public string RamTrendKey { get; } = "pending";
    public string DiskText { get; }
    public string DiskKey { get; }
    public double DiskPercent { get; }
    public IReadOnlyList<RevitHealthRow> Revits { get; }
    public bool HasRevits { get; }
    /// <summary>Under the table: what "RAM needed" means and how much the computer has; empty while no model size is known.</summary>
    public string TableNote { get; }
    public bool HasTableNote => TableNote.Length > 0;
    public bool NoRevit { get; }
    /// <summary>Everything running together, shown when more than one Revit is open.</summary>
    public string RevitSummary { get; }
    public bool HasRevitSummary { get; }
    public IReadOnlyList<HealthIssueRow> Issues { get; }
    public bool HasIssues { get; }

    /// <summary>"3 Revit running (2024 ×2, 2025) · 5 models, 31 links, 9.4 GB on disk · 12 views · 41.0 GB of RAM".</summary>
    private static string Summary(HealthReport r, IReadOnlyList<LiveStatus> sessions)
    {
        if (r.Revits.Count < 2)
            return "";
        var versions = r.Revits.GroupBy(p => p.Version.Length > 0 ? p.Version : "?").OrderBy(g => g.Key)
            .Select(g => g.Count() > 1 ? $"{g.Key} ×{g.Count()}" : g.Key);
        var parts = new List<string> { $"{r.Revits.Count} Revit running ({string.Join(", ", versions)})" };

        var loads = sessions.Select(s => s.Load).OfType<ModelLoad>().ToList();
        int models = sessions.Sum(s => s.OpenProjects ?? (s.Load is null ? 0 : 1));
        if (models > 0)
        {
            int links = loads.Sum(l => l.LinkCount);
            int diskMb = loads.Sum(l => l.ModelMb + l.LinksMb);
            parts.Add($"{models} model{(models == 1 ? "" : "s")}" + (links > 0 ? $", {links} links" : "") + (diskMb > 0 ? $", {Fmt.Mb(diskMb)} on disk" : ""));
        }
        if (sessions.Sum(s => s.OpenViews ?? 0) is > 0 and var views)
            parts.Add($"{views} views");
        parts.Add($"{Fmt.Mb((int)r.Revits.Sum(p => p.MemoryMb))} of RAM");
        return string.Join(" · ", parts);
    }

    internal static string Key(HealthLevel level) => level switch { HealthLevel.Danger => "crash", HealthLevel.Watch => "warning", _ => "ok" };

    /// <summary>The journal session of a process: by process id when the monitor could tell, else the only one of that version.</summary>
    private static LiveStatus? Match(RevitHealth p, IReadOnlyList<RevitHealth> all, IReadOnlyList<LiveStatus> sessions) =>
        sessions.FirstOrDefault(s => s.ProcessId == p.ProcessId)
        ?? (all.Count(x => x.Version == p.Version) == 1 ? sessions.FirstOrDefault(s => s.Version == p.Version) : null);
}

public sealed class RevitHealthRow(RevitHealth p, LiveStatus? session, long totalRamMb)
{
    public string StatusKey => !p.Responding ? HungKey : HealthView.Key(p.Level);
    public string Title => (p.Version.Length > 0 ? $"Revit {p.Version}" : "Revit") + (session?.Model is { Length: > 0 } m ? $" · {m}" : "");
    public string State => !p.Responding ? (p.CpuPercent >= HealthMonitor.BusyCpuPercent ? "Busy, not answering clicks" : "Not responding")
        : session?.Busy is { } busy ? busy.TrimEnd('.') : p.CpuPercent >= HealthMonitor.BusyCpuPercent ? "Working" : "Idle";
    // Red only for a Revit that is stuck; a busy one is a warning.
    private string HungKey => p.CpuPercent >= HealthMonitor.BusyCpuPercent ? "warning" : "crash";
    public string Numbers => $"Uses {Fmt.Mb((int)p.MemoryMb)} of RAM"
                             + (session?.OpenViews is > 0 and var views ? $" · {views} view{(views == 1 ? "" : "s")} open" : "");

    // The open model against Autodesk's memory guidance; known once Revit has written the model's local file.
    private Core.Analysis.ModelLoad? Load => session?.Load;
    public bool HasModel => Load is not null;
    public string ModelLine => Load is not { } m ? "" :
        $"Model {Fmt.Mb(m.ModelMb)}" + (m.Elements is { } e ? $" · {e.ToString("N0", CultureInfo.InvariantCulture)} elements" : "") +
        (m.LinkCount == 0 ? "" : m.LinksKnown == 0 ? $" · {m.LinkCount} links" : $" · {m.LinkCount} links {Fmt.Mb(m.LinksMb)}");
    public string GuidanceLine => Load is not { } m ? "" : m.OverGuidance
        ? $"{Needs(m)}; this computer has {Fmt.Mb((int)totalRamMb)}"
        : $"Within Autodesk's memory guidance for this computer ({Fmt.Mb((int)totalRamMb)})";
    public string GuidanceKey => Load?.OverGuidance == true ? "warning" : "pending";

    /// <summary>Autodesk's 20 x rule for the model alone; with links it is our estimate and says so.</summary>
    private static string Needs(Core.Analysis.ModelLoad m) => m.LinksKnown > 0
        ? $"Estimate with links (Autodesk's 20× rule): {m.NeededRam}"
        : $"Autodesk: {m.NeededRam} for this model";

    // The columns of the table of running Revits.
    // The model on the first line (it may wrap), the Revit version and what it is doing under it.
    public string Name => session?.Model is { Length: > 0 } m ? m : "No model open";
    public string Under => (p.Version.Length > 0 ? $"Revit {p.Version}" : "Revit") + " · " + char.ToLowerInvariant(State[0]) + State[1..];
    public string RamUsed => Fmt.Mb((int)p.MemoryMb);
    public string RamNeeded => Load is { } m ? "≈ " + Fmt.Mb((int)Math.Min(Core.Analysis.ModelSizeAdvice.Required(m.ModelMb + m.LinksMb), int.MaxValue)) : "–";
    public string RamNeededKey => Load?.OverGuidance == true ? "warning" : "ok";
    public string ModelSize => Load is { } m ? Fmt.Mb(m.ModelMb) : "–";
    public string Links => Load is not { } m || m.LinkCount == 0 ? "–"
        : $"{m.LinkCount} file{(m.LinkCount == 1 ? "" : "s")}" + (m.LinksKnown > 0 ? $", {Fmt.Mb(m.LinksMb)}" : "");
    public string Views => session?.OpenViews is > 0 and var v ? v.ToString(CultureInfo.InvariantCulture) : "–";

    /// <summary>"Revit uses 18.0 GB of RAM · 9 views · model 1.3 GB, 13 links": the numbers of this Revit on one line.</summary>
    public string Detail => string.Join(" · ", new[]
    {
        $"Revit uses {Fmt.Mb((int)p.MemoryMb)} of RAM",
        session?.OpenViews is > 0 and var views ? $"{views} view{(views == 1 ? "" : "s")}" : null,
        Load is { } m ? $"model {Fmt.Mb(m.ModelMb)}" + (m.LinkCount > 0 ? $", {m.LinkCount} links" : "") : null,
    }.Where(x => x is not null));

    /// <summary>The one thing about this Revit that needs attention, most serious first; empty when all is well.</summary>
    public string Attention =>
        HasSync && Sync.Contains("FAILED", StringComparison.Ordinal) ? Sync
        : GdiKey != "ok" ? GdiText
        : "";
    public bool HasAttention => Attention.Length > 0;
    public string AttentionKey => HasSync && Sync.Contains("FAILED", StringComparison.Ordinal) ? "crash" : GdiKey == "crash" ? "crash" : "warning";
    public string Sync => session?.Sync ?? "";
    public bool HasSync => session is not null;
    public string SyncKey => session?.Sync.Contains("FAILED", StringComparison.Ordinal) == true ? "crash" : "pending";
    public double GdiPercent => 100.0 * Math.Max(p.GdiObjects, p.UserObjects) / HealthMonitor.GuiLimit;
    public string GdiText => $"{Math.Max(p.GdiObjects, p.UserObjects).ToString("N0", CultureInfo.InvariantCulture)} of 10,000 drawing resources";
    public string GdiKey => HealthView.Key(Math.Max(p.GdiObjects, p.UserObjects) >= HealthMonitor.GuiDanger ? HealthLevel.Danger
        : Math.Max(p.GdiObjects, p.UserObjects) >= HealthMonitor.GuiWatch ? HealthLevel.Watch : HealthLevel.Ok);
}

/// <summary>
/// One thing to do, shown folded to its title. The overview is rebuilt every few seconds, so which ones the user
/// unfolded is kept by the issue's key (its title carries live figures and changes).
/// </summary>
public sealed record HealthIssueRow(string Key, string LevelKey, string Title, string Advice)
{
    private static readonly HashSet<string> Unfolded = [];

    public bool IsExpanded
    {
        get => Unfolded.Contains(Key);
        set { if (value) Unfolded.Add(Key); else Unfolded.Remove(Key); }
    }
}
