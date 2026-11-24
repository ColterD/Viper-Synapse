using System.ComponentModel;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.Threading;
using Avalonia.VisualTree;
using ViperSynapse.App.Services;
using ViperSynapse.App.ViewModels;

namespace ViperSynapse.App.Views;

public sealed partial class HomeView : UserControl
{
    ShortcutsViewModel? _shortcuts;
    HomeViewModel? _observedHome;
    Button? _shortcutOrigin;
    Guid? _shortcutOriginId;
    long _shortcutFocusRequest;

    public HomeView()
    {
        InitializeComponent();
        AttachedToVisualTree += (_, _) => ObserveHome(Home);
        DetachedFromVisualTree += (_, _) => ObserveHome(null);
        DataContextChanged += (_, _) =>
        {
            if (TopLevel.GetTopLevel(this) != null)
                ObserveHome(Home);
        };
    }

    HomeViewModel? Home => DataContext as HomeViewModel;

    void ObserveHome(HomeViewModel? home)
    {
        if (ReferenceEquals(_observedHome, home))
            return;
        if (_shortcuts != null)
            _shortcuts.PropertyChanged -= ShortcutChanged;
        if (_observedHome != null)
            _observedHome.PropertyChanged -= HomeChanged;
        _observedHome = home;
        _shortcuts = home?.Shortcuts;
        _shortcutOrigin = null;
        _shortcutOriginId = _shortcuts?.EditingShortcutId;
        ++_shortcutFocusRequest;
        if (home != null)
            home.PropertyChanged += HomeChanged;
        if (_shortcuts != null)
            _shortcuts.PropertyChanged += ShortcutChanged;
    }

    void HomeChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName == nameof(HomeViewModel.ShowShortcutsPage))
            ++_shortcutFocusRequest;
    }

    void EditShortcut(object? sender, RoutedEventArgs e)
    {
        if (_shortcuts is not { } shortcuts || sender is not Button origin)
            return;
        _shortcutOrigin = origin;
        _shortcutOriginId = (origin.DataContext as ShortcutRow)?.Shortcut.Id;
        if (origin.DataContext is ShortcutRow row)
            shortcuts.ChangeCommand.Execute(row);
        else
            shortcuts.AddCommand.Execute(null);
    }

    void ShortcutChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName != nameof(ShortcutsViewModel.Editing))
            return;
        long request = ++_shortcutFocusRequest;
        if (_shortcuts is not { Editing: false } shortcuts)
        {
            _shortcutOriginId = _shortcuts?.EditingShortcutId;
            return;
        }
        var origin = _shortcutOrigin;
        var id = _shortcutOriginId;
        _shortcutOrigin = null;
        _shortcutOriginId = null;
        Dispatcher.UIThread.Post(() =>
        {
            if (request != _shortcutFocusRequest || shortcuts.Editing || Home is not { ShowShortcutsPage: true })
                return;
            var target = id.HasValue
                ? this.GetVisualDescendants().OfType<Button>().FirstOrDefault(button =>
                    button.Classes.Contains("shortcutEdit") && button.DataContext is ShortcutRow row && row.Shortcut.Id == id &&
                    button.IsEffectivelyVisible && button.IsEffectivelyEnabled)
                : origin is { IsEffectivelyVisible: true, IsEffectivelyEnabled: true } && TopLevel.GetTopLevel(origin) != null ? origin : null;
            target ??= AddShortcutButton;
            target.BringIntoView();
            target.Focus(NavigationMethod.Tab);
        }, DispatcherPriority.Loaded);
    }

    void OpenDevice(object? sender, RoutedEventArgs e)
    {
        if (sender is Button { DataContext: DeviceViewModel device })
            Home?.Open(device);
    }

    void OpenHealth(object? sender, RoutedEventArgs e)
    {
        if (sender is Button { Tag: DeviceViewModel device, DataContext: DashboardUnitAttention attention })
            Home?.OpenHealth(device, attention.Unit);
    }

    void OpenDeviceSetup(object? sender, RoutedEventArgs e)
    {
        if (TopLevel.GetTopLevel(this) is { } top)
            _ = top.Launcher.LaunchUriAsync(ProjectLinks.GettingStarted);
    }
}
