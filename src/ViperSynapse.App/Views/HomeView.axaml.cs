using Avalonia.Controls;
using Avalonia.Interactivity;
using ViperSynapse.App.Services;
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
            _ = top.Launcher.LaunchUriAsync(ProjectLinks.GettingStarted);
    }

}
