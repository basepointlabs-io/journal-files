using System.IO;
using System.Windows.Threading;
using RevitJournal.Core.Analysis;
using RevitJournal.Core.Live;
using Drawing = System.Drawing;
using Forms = System.Windows.Forms;

namespace RevitJournal.App;

/// <summary>
/// The background part: a tray icon that watches running Revit sessions every few seconds and shows a Windows
/// notification when something goes wrong. Clicking the notification opens the matching answer page.
/// </summary>
public sealed class TrayAgent : IDisposable
{
    private readonly Forms.NotifyIcon _icon;
    private readonly LiveMonitor _monitor;
    private readonly DispatcherTimer _timer = new() { Interval = TimeSpan.FromSeconds(5) };
    private readonly Forms.ToolStripMenuItem _autostart;
    private LiveAlert? _shownAlert;
    private bool _busy;

    public event Action? OpenRequested;
    public event Action<LiveAlert>? AlertClicked;
    public event Action<IReadOnlyList<LiveStatus>>? StatusChanged;
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
        menu.Items.Add("Open Revit Journal Analyzer", null, (_, _) => OpenRequested?.Invoke());
        menu.Items.Add(_autostart);
        menu.Items.Add(new Forms.ToolStripSeparator());
        menu.Items.Add("Exit", null, (_, _) => ExitRequested?.Invoke());

        _icon = new Forms.NotifyIcon
        {
            Icon = MakeIcon(),
            Text = $"Revit Journal Analyzer {AppInfo.Version} — watching Revit",
            ContextMenuStrip = menu,
            Visible = true,
        };
        _icon.MouseClick += (_, e) =>
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
            var alerts = await Task.Run(_monitor.Tick);
            StatusChanged?.Invoke(_monitor.Status);
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
        LiveAlertKind.Crashed => 5,
        LiveAlertKind.SyncFailed => 4,
        LiveAlertKind.SeriousError => 3,
        LiveAlertKind.NotResponding => 2,
        LiveAlertKind.LowMemory => 1,
        _ => 0,
    };

    private void ToggleAutostart()
    {
        Settings.StartsWithWindows = !Settings.StartsWithWindows;
        _autostart.Checked = Settings.StartsWithWindows;
    }

    /// <summary>The app's own icon (Icons\app.ico, the "Rj" tile), so the tray matches the title bar and the exe.</summary>
    private static Drawing.Icon MakeIcon()
    {
        var info = System.Windows.Application.GetResourceStream(new Uri("pack://application:,,,/Icons/app.ico"));
        using var stream = info.Stream;
        return new Drawing.Icon(stream, Forms.SystemInformation.SmallIconSize);
    }

    public void Dispose()
    {
        _timer.Stop();
        _icon.Visible = false;
        _icon.Dispose();
        _monitor.Dispose();
    }
}
