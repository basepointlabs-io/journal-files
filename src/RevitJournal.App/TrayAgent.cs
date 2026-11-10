using System.IO;
using System.Windows.Threading;
using RevitJournal.Core.Analysis;
using RevitJournal.Core.Live;
using Drawing = System.Drawing;
using Forms = System.Windows.Forms;

namespace RevitJournal.App;

/// <summary>
/// The background part: a tray icon that watches running Revit sessions and the computer's resources every few
/// seconds. The icon carries a colored dot for the overall state, a left click asks for the compact overview, and a
/// Windows notification appears when something goes wrong or is about to.
/// </summary>
public sealed class TrayAgent : IDisposable
{
    private readonly Forms.NotifyIcon _icon;
    private readonly LiveMonitor _monitor;
    private readonly HealthMonitor _health = new(new WindowsSystemProbe());
    private readonly DispatcherTimer _timer = new() { Interval = TimeSpan.FromSeconds(5) };
    private readonly Forms.ToolStripMenuItem _autostart;
    private readonly Dictionary<HealthLevel, Drawing.Icon> _icons = [];
    private HealthLevel? _shownLevel;
    private LiveAlert? _shownAlert;
    private bool _busy;

    /// <summary>The full window was asked for (tray menu, or a notification without a specific answer).</summary>
    public event Action? OpenRequested;
    /// <summary>The compact overview was asked for (left click on the icon).</summary>
    public event Action? OverviewRequested;
    public event Action<LiveAlert>? AlertClicked;
    public event Action<IReadOnlyList<LiveStatus>>? StatusChanged;
    /// <summary>Fresh analyses of running sessions (and, once, of sessions that just ended), built by the monitor.</summary>
    public event Action<IReadOnlyDictionary<string, AnalysisResult>>? ResultsChanged;
    /// <summary>The computer's state, judged, together with the running Revit sessions it belongs to.</summary>
    public event Action<HealthReport, IReadOnlyList<LiveStatus>>? HealthChanged;
    /// <summary>Every alert, including those not shown as the notification of the round.</summary>
    public event Action<IReadOnlyList<LiveAlert>>? AlertsRaised;
    public event Action? ExitRequested;

    public TrayAgent(Analyzer analyzer)
    {
        _monitor = new LiveMonitor(analyzer);

        _autostart = new Forms.ToolStripMenuItem("Start with Windows", null, (_, _) => ToggleAutostart())
        {
            Checked = Settings.StartsWithWindows,
        };
        var menu = new Forms.ContextMenuStrip();
        menu.Items.Add("Overview", null, (_, _) => OverviewRequested?.Invoke());
        menu.Items.Add("Open full analysis", null, (_, _) => OpenRequested?.Invoke());
        menu.Items.Add(_autostart);
        menu.Items.Add(new Forms.ToolStripSeparator());
        menu.Items.Add("Exit", null, (_, _) => ExitRequested?.Invoke());

        _icon = new Forms.NotifyIcon
        {
            Icon = IconFor(HealthLevel.Ok),
            Text = $"Revit Doctor {AppInfo.Version}",
            ContextMenuStrip = menu,
            Visible = true,
        };
        _icon.MouseClick += (_, e) =>
        {
            if (e.Button == Forms.MouseButtons.Left)
                OverviewRequested?.Invoke();
        };
        _icon.MouseDoubleClick += (_, e) =>
        {
            if (e.Button == Forms.MouseButtons.Left)
                OpenRequested?.Invoke();
        };
        _icon.BalloonTipClicked += (_, _) =>
        {
            if (_shownAlert is { } alert)
                AlertClicked?.Invoke(alert);
            else
                OpenRequested?.Invoke();
        };

        _timer.Tick += async (_, _) => await TickAsync();
        _timer.Start();
        // The first reading right away, so the overview is not empty for five seconds.
        _ = TickAsync();
    }

    public void ShowInfo(string title, string text)
    {
        _shownAlert = null;
        _icon.ShowBalloonTip(5000, title, text, Forms.ToolTipIcon.Info);
    }

    private async Task TickAsync()
    {
        if (_busy)
            return; // the first attach to a big journal can take a few seconds
        _busy = true;
        try
        {
            var (alerts, report) = await Task.Run(() =>
            {
                var journalAlerts = _monitor.Tick();
                var health = _health.Tick([.. _monitor.Status.Select(s => s.Load).OfType<ModelLoad>()]);
                journalAlerts.AddRange(_health.NewAlerts);
                DiskHistory.Default.Record(health.Time, health.Disks.Select(d => new DiskSample(d.Name, d.FreeMb, d.TotalMb)));
                return (journalAlerts, health);
            });
            StatusChanged?.Invoke(_monitor.Status);
            ResultsChanged?.Invoke(_monitor.Results);
            HealthChanged?.Invoke(report, _monitor.Status);
            ShowLevel(report);
            if (alerts.Count > 0)
                AlertsRaised?.Invoke(alerts);
            // Windows shows one notification at a time: show the most serious of this round.
            if (alerts.OrderByDescending(a => Priority(a.Kind)).FirstOrDefault() is { } alert)
            {
                _shownAlert = alert;
                _icon.ShowBalloonTip(15000, alert.Title, alert.Message,
                    alert.Kind is LiveAlertKind.SyncTakingLong ? Forms.ToolTipIcon.Info : Forms.ToolTipIcon.Warning);
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            // A journal was rotated or locked; try again on the next tick.
        }
        finally
        {
            _busy = false;
        }
    }

    private static int Priority(LiveAlertKind k) => k switch
    {
        LiveAlertKind.Crashed => 6,
        LiveAlertKind.SyncFailed => 5,
        LiveAlertKind.SeriousError => 4,
        LiveAlertKind.LowMemory => 3,
        LiveAlertKind.ResourcesNearLimit or LiveAlertKind.DiskLow => 2,
        LiveAlertKind.NotResponding => 1,
        _ => 0,
    };

    /// <summary>The icon's dot and tooltip say how things are without opening anything.</summary>
    private void ShowLevel(HealthReport report)
    {
        if (_shownLevel != report.Level)
        {
            _shownLevel = report.Level;
            _icon.Icon = IconFor(report.Level);
        }
        string state = report.Level switch
        {
            HealthLevel.Danger => "Act now",
            HealthLevel.Watch => "Keep an eye on it",
            _ => "All good",
        };
        string revit = report.Revits.Count == 0 ? "Revit is not running" : $"{report.Revits.Count} Revit running";
        // NotifyIcon.Text is limited to 127 characters.
        string text = $"Revit Doctor — {state}\n{revit} · {Fmt.Mb((int)report.FreeRamMb)} RAM free";
        _icon.Text = text.Length > 127 ? text[..127] : text;
    }

    private void ToggleAutostart()
    {
        Settings.StartsWithWindows = !Settings.StartsWithWindows;
        _autostart.Checked = Settings.StartsWithWindows;
    }

    /// <summary>
    /// The app's own icon (Icons\app.ico, the "Rj" tile) with a dot in the corner: green, amber or red. The plain
    /// icon would look the same whether everything is fine or memory is about to run out.
    /// </summary>
    private Drawing.Icon IconFor(HealthLevel level)
    {
        if (_icons.TryGetValue(level, out var cached))
            return cached;

        var info = System.Windows.Application.GetResourceStream(new Uri("pack://application:,,,/Icons/app.ico"));
        using var stream = info.Stream;
        using var source = new Drawing.Icon(stream, new Drawing.Size(32, 32));
        using var bitmap = source.ToBitmap();
        using (var g = Drawing.Graphics.FromImage(bitmap))
        {
            g.SmoothingMode = Drawing.Drawing2D.SmoothingMode.AntiAlias;
            var color = level switch
            {
                HealthLevel.Danger => Drawing.Color.FromArgb(0xCF, 0x22, 0x2E),
                HealthLevel.Watch => Drawing.Color.FromArgb(0xE3, 0x9A, 0x00),
                _ => Drawing.Color.FromArgb(0x1A, 0x7F, 0x37),
            };
            using var ring = new Drawing.SolidBrush(Drawing.Color.White);
            using var dot = new Drawing.SolidBrush(color);
            g.FillEllipse(ring, 16, 16, 16, 16);
            g.FillEllipse(dot, 18, 18, 12, 12);
        }
        return _icons[level] = Drawing.Icon.FromHandle(bitmap.GetHicon());
    }

    public void Dispose()
    {
        _timer.Stop();
        _icon.Visible = false;
        _icon.Dispose();
        _monitor.Dispose();
    }
}
