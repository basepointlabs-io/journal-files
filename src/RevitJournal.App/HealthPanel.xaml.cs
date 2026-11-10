using System.Windows;
using System.Windows.Controls;

namespace RevitJournal.App;

public partial class HealthPanel : UserControl
{
    public HealthPanel() => InitializeComponent();

    /// <summary>The wide panel on the start screen shows every column of the Revit table; the tray flyout only the memory ones.</summary>
    public bool Wide
    {
        get => (bool)GetValue(WideProperty);
        set => SetValue(WideProperty, value);
    }

    public static readonly DependencyProperty WideProperty =
        DependencyProperty.Register(nameof(Wide), typeof(bool), typeof(HealthPanel), new PropertyMetadata(false));
}
