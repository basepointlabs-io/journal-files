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
        // The overview grows when a Revit or a problem appears: keep the bottom edge above the tray and let it grow upwards.
        SizeChanged += (_, _) =>
        {
            if (IsVisible && Left > -10000)
                PlaceNearTray();
        };
    }

    public void Update(HealthView view) => Panel.DataContext = view;

    /// <summary>Shows the flyout in the bottom right corner of the work area, where the tray is.</summary>
    public void ShowNearTray()
    {
        MaxHeight = Math.Min(760, SystemParameters.WorkArea.Height - 2 * Gap);
        Show();
        UpdateLayout();
        PlaceNearTray();
        Activate();
    }

    private const double Gap = 16;

    private void PlaceNearTray()
    {
        var area = SystemParameters.WorkArea;
        Left = area.Right - ActualWidth - Gap;
        Top = Math.Max(area.Top + Gap, area.Bottom - ActualHeight - Gap);
    }

    private void OnOpenFull(object sender, RoutedEventArgs e)
    {
        Hide();
        OpenFullRequested?.Invoke();
    }

    private void OnClose(object sender, RoutedEventArgs e) => Hide();

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
