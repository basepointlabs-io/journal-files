using System.Threading;
using System.Windows;

namespace RevitJournal.App;

/// <summary>
/// Starts the window and the tray agent. With --background (used by "Start with Windows") only the tray icon appears;
/// closing the window keeps watching Revit until Exit is chosen in the tray menu. Only one copy runs per user session:
/// starting it again brings the running copy to the front.
/// </summary>
public partial class App : Application
{
    // RJA_INSTANCE gives a build under test its own identity, so it can run next to an installed copy.
    private static readonly string Suffix = Environment.GetEnvironmentVariable("RJA_INSTANCE") is { Length: > 0 } s ? "." + s : "";
    private static readonly string InstanceName = "RevitJournalAnalyzer.Instance" + Suffix;
    private static readonly string ShowSignalName = "RevitJournalAnalyzer.Show" + Suffix;

    private TrayAgent? _agent;
    private Mutex? _instance;
    private EventWaitHandle? _showSignal;

    protected override void OnStartup(StartupEventArgs e)
    {
        base.OnStartup(e);

        _instance = new Mutex(initiallyOwned: true, InstanceName, out bool first);
        if (!first)
        {
            if (!e.Args.Contains("--background") && EventWaitHandle.TryOpenExisting(ShowSignalName, out var signal))
            {
                signal.Set();
                signal.Dispose();
            }
            Shutdown();
            return;
        }

        var window = new MainWindow();
        _agent = new TrayAgent(window.Analyzer);
        window.AttachAgent(_agent);
        _agent.ExitRequested += () =>
        {
            _agent.Dispose();
            Shutdown();
        };

        // A second launch signals this event instead of starting another copy.
        _showSignal = new EventWaitHandle(false, EventResetMode.AutoReset, ShowSignalName);
        var listener = new Thread(() =>
        {
            while (_showSignal.WaitOne())
                Dispatcher.Invoke(window.BringToFront);
        }) { IsBackground = true, Name = "ShowSignal" };
        listener.Start();

        if (Environment.GetEnvironmentVariable("RJA_SNAPSHOT") is { Length: > 0 } snapshots)
        {
            // Draw the screens to files and leave; nothing is shown on the desktop.
            _ = Dispatcher.InvokeAsync(async () =>
            {
                await window.SaveSnapshotsAsync(snapshots);
                _agent.Dispose();
                Shutdown();
            });
            return;
        }

        if (!e.Args.Contains("--background"))
            window.Show();
    }

    protected override void OnExit(ExitEventArgs e)
    {
        _agent?.Dispose();
        _instance?.Dispose();
        base.OnExit(e);
    }
}
