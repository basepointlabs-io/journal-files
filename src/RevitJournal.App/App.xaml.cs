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
    private const string InstanceName = "RevitJournalAnalyzer.Instance";
    private const string ShowSignalName = "RevitJournalAnalyzer.Show";

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
