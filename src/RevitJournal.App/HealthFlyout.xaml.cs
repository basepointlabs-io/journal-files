using System.Windows;

namespace RevitJournal.App;

/// <summary>Small window above the tray with the overview. It hides itself as soon as the user clicks elsewhere.</summary>
public partial class HealthFlyout : Window
{
    public event Action? OpenFullRequested;

    public HealthFlyout()
    {
        InitializeComponent();
        VersionText.Text = AppInfo.Version;
    }

    public void Update(HealthView view) => Panel.DataContext = view;

    /// <summary>Shows the flyout in the bottom right corner of the work area, where the tray is.</summary>
    public void ShowNearTray()
    {
        Show();
        UpdateLayout();
        var area = SystemParameters.WorkArea;
        Left = area.Right - ActualWidth - 12;
        Top = area.Bottom - ActualHeight - 12;
        Activate();
    }

    private void OnOpenFull(object sender, RoutedEventArgs e)
    {
        Hide();
        OpenFullRequested?.Invoke();
    }

    private void OnDeactivated(object? sender, EventArgs e) => Hide();

    protected override void OnClosing(System.ComponentModel.CancelEventArgs e)
    {
        // Alt+F4 only hides it; the app decides when it really ends.
        if (!Application.Current.Dispatcher.HasShutdownStarted)
        {
            e.Cancel = true;
            Hide();
        }
        base.OnClosing(e);
    }
}
