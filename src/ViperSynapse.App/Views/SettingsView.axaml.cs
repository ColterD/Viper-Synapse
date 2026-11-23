using Avalonia.Controls;
using Avalonia.Interactivity;
using ViperSynapse.App.Services;

namespace ViperSynapse.App.Views;

public sealed partial class SettingsView : UserControl
{
    public SettingsView() => InitializeComponent();

    void OpenReleases(object? sender, RoutedEventArgs e) => Open(ProjectLinks.Releases);

    void OpenProject(object? sender, RoutedEventArgs e) => Open(ProjectLinks.Repository);

    void OpenNagaDpiComparison(object? sender, RoutedEventArgs e)
    {
        if (TopLevel.GetTopLevel(this) is Window owner)
            _ = new NagaDpiWindowComparisonView().ShowDialog(owner);
    }

    void Open(Uri url)
    {
        if (TopLevel.GetTopLevel(this) is { } top)
            _ = top.Launcher.LaunchUriAsync(url);
    }
}
