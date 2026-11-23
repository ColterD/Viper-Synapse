using Avalonia.Controls;
using Avalonia.Interactivity;

namespace ViperSynapse.App.Views;

public sealed partial class SettingsView : UserControl
{
    public SettingsView() => InitializeComponent();

    void OpenReleases(object? sender, RoutedEventArgs e) => Open("https://github.com/codingncaffeine/Viper-Synapse/releases");

    void OpenProject(object? sender, RoutedEventArgs e) => Open("https://github.com/codingncaffeine/Viper-Synapse");

    void OpenNagaDpiComparison(object? sender, RoutedEventArgs e)
    {
        if (TopLevel.GetTopLevel(this) is Window owner)
            _ = new NagaDpiWindowComparisonView().ShowDialog(owner);
    }

    void Open(string url)
    {
        if (TopLevel.GetTopLevel(this) is { } top)
            _ = top.Launcher.LaunchUriAsync(new Uri(url));
    }
}
