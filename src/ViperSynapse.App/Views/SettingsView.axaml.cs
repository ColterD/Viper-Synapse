using Avalonia.Controls;
using Avalonia.Interactivity;

namespace ViperSynapse.App.Views;

public sealed partial class SettingsView : UserControl
{
    public SettingsView() => InitializeComponent();

    void OpenNagaDpiComparison(object? sender, RoutedEventArgs e)
    {
        if (TopLevel.GetTopLevel(this) is Window owner)
            _ = new NagaDpiWindowComparisonView().ShowDialog(owner);
    }
}
