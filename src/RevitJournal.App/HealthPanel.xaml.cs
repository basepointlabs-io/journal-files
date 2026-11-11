using System.Windows;
using System.Windows.Controls;

namespace RevitJournal.App;

public partial class HealthPanel : UserControl
{
    public HealthPanel() => InitializeComponent();

    /// <summary>An incident on the Autodesk Health Dashboard opens in the browser.</summary>
    private void OnOpenLink(object sender, System.Windows.Navigation.RequestNavigateEventArgs e)
    {
        try { System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo(e.Uri.AbsoluteUri) { UseShellExecute = true }); }
        catch (System.ComponentModel.Win32Exception) { /* no browser */ }
        e.Handled = true;
    }

    /// <summary>The wide panel on the start screen shows every column of the Revit table; the tray flyout only the memory ones.</summary>
    public bool Wide
    {
        get => (bool)GetValue(WideProperty);
        set => SetValue(WideProperty, value);
    }

    public static readonly DependencyProperty WideProperty =
        DependencyProperty.Register(nameof(Wide), typeof(bool), typeof(HealthPanel), new PropertyMetadata(false));
}
