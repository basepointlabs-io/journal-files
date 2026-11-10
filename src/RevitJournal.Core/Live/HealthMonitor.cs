using RevitJournal.Core.Analysis;

namespace RevitJournal.Core.Live;

public enum HealthLevel { Ok, Watch, Danger }

/// <summary>Something that will cause trouble if it continues, with what to do about it.</summary>
/// <param name="Key">Stable id of the problem ("ram-low", "gdi:1234"), used to avoid repeating a notification.</param>
public sealed record HealthIssue(string Key, HealthLevel Level, string Title, string Advice);

public sealed record RevitHealth(int ProcessId, string Version, DateTime StartTime, bool Responding, long MemoryMb,
    double CpuPercent, int GdiObjects, int UserObjects, HealthLevel Level);

public sealed record DiskHealth(string Name, long FreeMb, long TotalMb, HealthLevel Level);

public sealed record RamPoint(DateTime Time, long FreeMb);

/// <summary>The computer's state right now, judged: overall level, the reasons, and a forecast for memory.</summary>
/// <param name="RamRunsOutIn">When free memory reaches the danger line if it keeps falling as in the last minutes; null when it is not falling or there is still plenty.</param>
public sealed record HealthReport(DateTime Time, HealthLevel Level, long TotalRamMb, long FreeRamMb, long CommitLimitMb, long CommitFreeMb,
    double CpuPercent, IReadOnlyList<RevitHealth> Revits, IReadOnlyList<DiskHealth> Disks, IReadOnlyList<HealthIssue> Issues,
    TimeSpan? RamRunsOutIn, IReadOnlyList<RamPoint> RamHistory);

/// <summary>
/// Turns resource readings into a judgement and warns before something runs out. Only exhaustion can be foreseen
/// this way (memory, drawing resources, disk); a crash from a bug or a damaged element gives no such warning.
/// Call <see cref="Tick"/> every few seconds from one thread.
/// </summary>
public sealed class HealthMonitor(ISystemProbe probe)
{
    // Free RAM, as a share of the machine's RAM or in absolute terms, whichever is hit first.
    public const double RamWatchShare = 0.12, RamDangerShare = 0.05;
    public const long RamWatchMb = 2048, RamDangerMb = 1024;
    // A forecast only means something once memory is actually short: opening a model takes gigabytes in a few
    // minutes and then stops, and a straight line through that predicts an end that never comes.
    public const double ForecastBelowShare = 0.25;
    public const long ForecastBelowMb = 4096;
    // Windows gives one process at most 10,000 GDI and 10,000 USER objects; Revit stops drawing when it gets there.
    public const int GuiLimit = 10_000, GuiWatch = 7_000, GuiDanger = 9_000;
    // Autodesk: "Minimum 30 GB free space is required in the hard drive." Below 5 GB the journal, temporary files and caches may not fit.
    public const long DiskWatchMb = 30 * 1024, DiskDangerMb = 5 * 1024;
    public const double CommitWatchShare = 0.20, CommitDangerShare = 0.10;
    /// <summary>A Revit above this share of the whole processor is doing something, even when its window does not respond.</summary>
    public const double BusyCpuPercent = 3;

    public static readonly TimeSpan HistoryLength = TimeSpan.FromMinutes(30);
    /// <summary>The forecast looks at the last 10 minutes and needs at least 3 of them.</summary>
    public static readonly TimeSpan ForecastWindow = TimeSpan.FromMinutes(10);
    public static readonly TimeSpan ForecastMinimum = TimeSpan.FromMinutes(3);
    public static readonly TimeSpan ForecastWatch = TimeSpan.FromMinutes(30);
    public static readonly TimeSpan ForecastDanger = TimeSpan.FromMinutes(10);
    /// <summary>A fall smaller than this over the window is noise, not a trend.</summary>
    public const long ForecastMinimumDropMb = 400;
    public static readonly TimeSpan AlertRepeat = TimeSpan.FromMinutes(30);

    private readonly List<RamPoint> _history = [];
    private readonly Dictionary<string, DateTime> _alerted = [];
    private SystemSample? _previous;

    public HealthReport? Latest { get; private set; }

    /// <summary>Notifications raised by the last <see cref="Tick"/>: a problem that became serious, at most once per half hour each.</summary>
    public IReadOnlyList<LiveAlert> NewAlerts { get; private set; } = [];

    /// <param name="models">What each running Revit has open, from its journal; judged together against the computer's memory.</param>
    public HealthReport Tick(IReadOnlyCollection<ModelLoad>? models = null)
    {
        var now = probe.Sample();
        var previous = _previous;
        _previous = now;

        _history.Add(new RamPoint(now.Time, now.FreeRamMb));
        _history.RemoveAll(p => now.Time - p.Time > HistoryLength);

        double seconds = previous is null ? 0 : (now.Time - previous.Time).TotalSeconds;
        double cpu = 0;
        if (previous is not null && now.CpuTotalSeconds > previous.CpuTotalSeconds)
            cpu = 100 * (1 - (now.CpuIdleSeconds - previous.CpuIdleSeconds) / (now.CpuTotalSeconds - previous.CpuTotalSeconds));

        var issues = new List<HealthIssue>();
        var revits = new List<RevitHealth>();
        foreach (var r in now.Revits.OrderBy(r => r.StartTime))
        {
            double share = 0;
            if (seconds > 0 && previous?.Revits.FirstOrDefault(x => x.Id == r.Id) is { } before)
                share = 100 * (r.CpuSeconds - before.CpuSeconds) / (seconds * Math.Max(1, now.ProcessorCount));
            var level = HealthLevel.Ok;
            string name = r.Version.Length > 0 ? $"Revit {r.Version}" : "Revit";

            foreach (var (count, what, key) in new[] { (r.GdiObjects, "drawing resources (GDI objects)", "gdi"), (r.UserObjects, "window resources (USER objects)", "user") })
            {
                if (count < GuiWatch)
                    continue;
                var l = count >= GuiDanger ? HealthLevel.Danger : HealthLevel.Watch;
                level = Max(level, l);
                issues.Add(new HealthIssue($"{key}:{r.Id}", l,
                    $"{name} has used {N(count)} of {N(GuiLimit)} {what}",
                    "At the limit Revit stops drawing its windows and can crash. Close inactive views (View tab > Close Inactive Views) and extra windows in that Revit, then restart it when convenient."));
            }
            if (!r.Responding)
            {
                level = Max(level, HealthLevel.Watch);
                // Windows says "not responding" for a Revit that is busy and for one that is stuck; the processor tells them apart.
                issues.Add(share >= BusyCpuPercent
                    ? new HealthIssue($"hung:{r.Id}", HealthLevel.Watch, $"{name} is busy and does not answer clicks",
                        "It is working (it is using the processor): synchronizing, saving, exporting or opening a model. Give it time; closing it now loses unsaved work.")
                    : new HealthIssue($"hung:{r.Id}", HealthLevel.Watch, $"{name} is not responding and is not using the processor",
                        "It may be waiting for the network or a file, or be stuck. If nothing changes for several minutes it will probably not recover; closing it loses unsaved work."));
            }
            revits.Add(new RevitHealth(r.Id, r.Version, r.StartTime, r.Responding, r.MemoryMb, Math.Clamp(share, 0, 100), r.GdiObjects, r.UserObjects, level));
        }

        // ---- memory now
        if (now.TotalRamMb > 0)
        {
            if (now.FreeRamMb < Math.Max(RamDangerMb, RamDangerShare * now.TotalRamMb))
                issues.Add(new HealthIssue("ram-low", HealthLevel.Danger,
                    $"Memory is almost full: {Fmt.Mb((int)now.FreeRamMb)} of {Fmt.Mb((int)now.TotalRamMb)} free",
                    "Save now. Close the Revit window you are not working in and other heavy programs; Revit can close unexpectedly when memory runs out."));
            else if (now.FreeRamMb < Math.Max(RamWatchMb, RamWatchShare * now.TotalRamMb))
                issues.Add(new HealthIssue("ram-low", HealthLevel.Watch,
                    $"Memory is getting low: {Fmt.Mb((int)now.FreeRamMb)} of {Fmt.Mb((int)now.TotalRamMb)} free",
                    "Close views and programs you do not need. Restarting Revit frees the memory it has collected."));
        }

        // ---- memory soon
        bool memoryShort = now.TotalRamMb > 0 && now.FreeRamMb < Math.Max(ForecastBelowMb, ForecastBelowShare * now.TotalRamMb);
        TimeSpan? runsOut = now.Revits.Count > 0 && memoryShort ? Forecast(now) : null;
        if (runsOut is { } eta && eta <= ForecastWatch && !issues.Any(i => i is { Key: "ram-low", Level: HealthLevel.Danger }))
            issues.Add(new HealthIssue("ram-forecast", eta <= ForecastDanger ? HealthLevel.Danger : HealthLevel.Watch,
                $"Memory is short and still falling: {Fmt.Mb((int)now.FreeRamMb)} free, about {Math.Max(1, (int)Math.Round(eta.TotalMinutes))} min left at this rate",
                "Save and synchronize now. Close the Revit window and views you do not need; Revit gives memory back only when restarted."));

        if (now.CommitLimitMb > 0 && now.CommitFreeMb < CommitWatchShare * now.CommitLimitMb)
        {
            bool danger = now.CommitFreeMb < CommitDangerShare * now.CommitLimitMb;
            issues.Add(new HealthIssue("commit-low", danger ? HealthLevel.Danger : HealthLevel.Watch,
                $"Windows is running out of virtual memory: {Fmt.Mb((int)now.CommitFreeMb)} of {Fmt.Mb((int)now.CommitLimitMb)} left",
                "Save now and close programs. If this happens often, ask IT to let Windows manage the page file size or to add RAM."));
        }

        long revitMemory = now.Revits.Sum(r => r.MemoryMb);
        if (now.Revits.Count >= 2 && now.TotalRamMb > 0 && revitMemory > 0.7 * now.TotalRamMb)
            issues.Add(new HealthIssue("many-revits", HealthLevel.Watch,
                $"{now.Revits.Count} Revit windows use {Fmt.Mb((int)revitMemory)} of {Fmt.Mb((int)now.TotalRamMb)} RAM",
                "Close the Revit you are not working in: each one keeps its models in memory."));

        if (models is not null && ModelSizeAdvice.Combined(models, now.TotalRamMb) is var (totalModelsMb, neededRam))
            issues.Add(new HealthIssue("models-large", HealthLevel.Watch,
                $"{models.Count} open models are {Fmt.Mb(totalModelsMb)} on disk together",
                AutodeskArticle.MemoryInstability.Cite(
                    $"Autodesk lists a single model of that size under {neededRam.Split(" (")[0]}; this computer has {Fmt.Mb((int)now.TotalRamMb)}. Reduce the number of loaded links, limit the worksets and views you open, and restart Revit regularly.")));

        var disks = new List<DiskHealth>();
        foreach (var d in now.Disks)
        {
            var level = d.FreeMb < DiskDangerMb ? HealthLevel.Danger : d.FreeMb < DiskWatchMb ? HealthLevel.Watch : HealthLevel.Ok;
            disks.Add(new DiskHealth(d.Name, d.FreeMb, d.TotalMb, level));
            if (level != HealthLevel.Ok)
                issues.Add(new HealthIssue($"disk:{d.Name}", level, $"Drive {d.Name} has only {Fmt.Mb((int)d.FreeMb)} free",
                    level == HealthLevel.Danger
                        ? "Free up space now: Revit needs room for temporary files, saves and the cloud model cache, and opening, saving and synchronizing fail when the drive is full. Autodesk requires at least 30 GB free."
                        : "Autodesk requires at least 30 GB of free disk space for Revit. Free up space: Windows Disk Cleanup, and the cloud model cache you no longer need."));
        }

        issues = [.. issues.OrderByDescending(i => i.Level)];
        var report = new HealthReport(now.Time, issues.Count == 0 ? HealthLevel.Ok : issues[0].Level,
            now.TotalRamMb, now.FreeRamMb, now.CommitLimitMb, now.CommitFreeMb, Math.Clamp(cpu, 0, 100),
            revits, disks, issues, runsOut, [.. _history]);

        NewAlerts = Alerts(report);
        Latest = report;
        return report;
    }

    /// <summary>
    /// Least-squares line through free memory over the forecast window. Returns the time until it reaches the danger
    /// line, or null when memory is not falling by more than noise.
    /// </summary>
    private TimeSpan? Forecast(SystemSample now)
    {
        var window = _history.Where(p => now.Time - p.Time <= ForecastWindow).ToList();
        if (window.Count < 5 || now.Time - window[0].Time < ForecastMinimum)
            return null;

        double meanT = window.Average(p => (p.Time - window[0].Time).TotalSeconds);
        double meanY = window.Average(p => (double)p.FreeMb);
        double covariance = 0, variance = 0;
        foreach (var p in window)
        {
            double t = (p.Time - window[0].Time).TotalSeconds - meanT;
            covariance += t * (p.FreeMb - meanY);
            variance += t * t;
        }
        if (variance <= 0)
            return null;
        double slope = covariance / variance; // MB per second
        double span = (window[^1].Time - window[0].Time).TotalSeconds;
        if (slope >= 0 || -slope * span < ForecastMinimumDropMb)
            return null;

        double dangerLine = Math.Max(RamDangerMb, RamDangerShare * now.TotalRamMb);
        double left = now.FreeRamMb - dangerLine;
        return left <= 0 ? TimeSpan.Zero : TimeSpan.FromSeconds(left / -slope);
    }

    /// <summary>Serious problems become notifications, each at most once per <see cref="AlertRepeat"/>.</summary>
    private List<LiveAlert> Alerts(HealthReport report)
    {
        var alerts = new List<LiveAlert>();
        foreach (var issue in report.Issues.Where(i => i.Level == HealthLevel.Danger))
        {
            if (_alerted.TryGetValue(issue.Key, out var last) && report.Time - last < AlertRepeat)
                continue;
            _alerted[issue.Key] = report.Time;
            var kind = issue.Key.StartsWith("ram", StringComparison.Ordinal) || issue.Key == "commit-low" ? LiveAlertKind.LowMemory
                : issue.Key.StartsWith("disk", StringComparison.Ordinal) ? LiveAlertKind.DiskLow
                : LiveAlertKind.ResourcesNearLimit;
            alerts.Add(new LiveAlert(kind, issue.Title, issue.Advice, "", "", report.Time));
        }
        return alerts;
    }

    private static HealthLevel Max(HealthLevel a, HealthLevel b) => a > b ? a : b;

    private static string N(int n) => n.ToString("N0", System.Globalization.CultureInfo.InvariantCulture);
}
