using Avalonia.Controls;
using Avalonia.Interactivity;
using ViperSynapse.App.ViewModels;

namespace ViperSynapse.App.Views;

public sealed partial class HomeView : UserControl
{
    public HomeView() => InitializeComponent();

    HomeViewModel? Home => DataContext as HomeViewModel;

    void OpenDevice(object? sender, RoutedEventArgs e)
    {
        if (sender is Button { DataContext: DeviceViewModel device })
            Home?.Open(device);
    }

    void OpenDeviceSetup(object? sender, RoutedEventArgs e)
    {
        if (TopLevel.GetTopLevel(this) is { } top)
            _ = top.Launcher.LaunchUriAsync(new Uri("https://github.com/ColterD/Viper-Synapse/blob/main/docs/getting-started.md"));
    }

    void OpenLinkedGames(object? sender, RoutedEventArgs e) => Home?.OpenLinkedGames();

    void OpenMacros(object? sender, RoutedEventArgs e) => Home?.OpenMacros();

    void OpenSettings(object? sender, RoutedEventArgs e) => Home?.OpenSettings();
}
