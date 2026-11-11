using System.Diagnostics;
using System.Runtime.InteropServices;
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
    private static readonly string InstanceName = "RevitDoctor.Instance" + Suffix;
    private static readonly string ShowSignalName = "RevitDoctor.Show" + Suffix;
    private static readonly string QuitSignalName = "RevitDoctor.Quit" + Suffix;

    private TrayAgent? _agent;
    private Mutex? _instance;
    private bool _owned;
    private EventWaitHandle? _showSignal, _quitSignal;

    protected override void OnStartup(StartupEventArgs e)
    {
        base.OnStartup(e);

        _instance = new Mutex(initiallyOwned: true, InstanceName, out bool first);
        _owned = first;
        if (!first && ReplaceOlderCopies())
            first = Acquire(_instance);
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

        // A newer version started while this one runs: it asks this copy to leave and takes over.
        _quitSignal = new EventWaitHandle(false, EventResetMode.AutoReset, QuitSignalName);
        new Thread(() =>
        {
            _quitSignal.WaitOne();
            Dispatcher.Invoke(() =>
            {
                _agent.Dispose();
                Shutdown();
            });
        }) { IsBackground = true, Name = "QuitSignal" }.Start();

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
        if (_owned)
            _instance?.ReleaseMutex();
        _instance?.Dispose();
        base.OnExit(e);
    }

    /// <summary>
    /// Closes the copies of an older version of this app that run for this user, e.g. a portable 0.5.0 left in the tray
    /// after 0.6.0 was installed: otherwise the new version would only bring the old window to the front. True when one
    /// was closed. A copy of the same or a newer version is left alone.
    /// </summary>
    private static bool ReplaceOlderCopies()
    {
        if (Suffix.Length > 0)
            return false;
        var mine = typeof(App).Assembly.GetName().Version ?? new Version(0, 0);
        using var me = Process.GetCurrentProcess();
        var older = Process.GetProcesses()
            .Where(p => p.Id != me.Id && p.SessionId == me.SessionId &&
                        (p.ProcessName.StartsWith("RevitDoctor", StringComparison.OrdinalIgnoreCase) ||
                         p.ProcessName.StartsWith("RevitJournalAnalyzer", StringComparison.OrdinalIgnoreCase)))
            .Where(p => VersionOf(p) is { } v && v < mine)
            .ToList();
        if (older.Count == 0)
            return false;

        // Copies from this version on leave by themselves; older ones do not know the signal and are ended.
        if (EventWaitHandle.TryOpenExisting(QuitSignalName, out var quit))
        {
            quit.Set();
            quit.Dispose();
        }
        foreach (var p in older)
        {
            try
            {
                if (!p.WaitForExit(3000))
                    p.Kill();
                p.WaitForExit(3000);
            }
            catch (Exception) { /* already gone or not ours to end */ }
            p.Dispose();
        }
        return true;
    }

    /// <summary>The file version of a running copy, null when it cannot be read.</summary>
    private static Version? VersionOf(Process p)
    {
        try
        {
            return ImagePath(p) is { } path && FileVersionInfo.GetVersionInfo(path) is { } info
                ? new Version(info.FileMajorPart, info.FileMinorPart, info.FileBuildPart, info.FilePrivatePart)
                : null;
        }
        catch (Exception) { return null; }
    }

    /// <summary>
    /// The executable of a process. Unlike Process.MainModule it also works for an x64 copy seen from the Arm64 build.
    /// </summary>
    private static string? ImagePath(Process p)
    {
        var handle = OpenProcess(0x1000 /* PROCESS_QUERY_LIMITED_INFORMATION */, false, p.Id);
        if (handle == IntPtr.Zero)
            return null;
        try
        {
            var buffer = new char[1024];
            int size = buffer.Length;
            return QueryFullProcessImageName(handle, 0, buffer, ref size) ? new string(buffer, 0, size) : null;
        }
        finally { CloseHandle(handle); }
    }

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern IntPtr OpenProcess(int access, bool inherit, int processId);

    [DllImport("kernel32.dll", SetLastError = true, CharSet = CharSet.Unicode)]
    private static extern bool QueryFullProcessImageName(IntPtr process, int flags, char[] name, ref int size);

    [DllImport("kernel32.dll")]
    private static extern bool CloseHandle(IntPtr handle);

    /// <summary>Takes the single-instance lock once the older copy has left; an ended copy leaves it abandoned.</summary>
    private bool Acquire(Mutex mutex)
    {
        try { _owned = mutex.WaitOne(5000); }
        catch (AbandonedMutexException) { _owned = true; }
        return _owned;
    }
}
