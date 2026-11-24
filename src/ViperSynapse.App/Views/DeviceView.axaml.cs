using System.ComponentModel;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.Threading;
using Avalonia.VisualTree;
using ViperSynapse.App.ViewModels;

namespace ViperSynapse.App.Views;

public sealed partial class DeviceView : UserControl
{
    DeviceViewModel? _device;
    MainWindow? _window;
    ushort? _editedSource;
    ushort? _restoreSource;

    public DeviceView()
    {
        InitializeComponent();
        AttachedToVisualTree += (_, _) =>
        {
            _window = TopLevel.GetTopLevel(this) as MainWindow;
            ObserveDevice(DataContext as DeviceViewModel);
        };
        DataContextChanged += (_, _) =>
        {
            if (_window != null)
                ObserveDevice(DataContext as DeviceViewModel);
        };
        DetachedFromVisualTree += (_, _) =>
        {
            var window = _window;
            var removed = _editedSource != null ? _device : null;
            _window = null;
            ObserveDevice(null);
            if (removed != null && window != null)
                Dispatcher.UIThread.Post(() =>
                {
                    if (window.DataContext is MainViewModel main && ReferenceEquals(main.SelectedTab, main.Home) &&
                        !main.Home.Devices.Contains(removed))
                        window.GetVisualDescendants().OfType<TextBlock>().FirstOrDefault(c => c.Name == "DevicesHeading" && c.IsEffectivelyVisible)
                            ?.Focus(NavigationMethod.Tab);
                }, DispatcherPriority.Loaded);
        };
    }

    void ObserveDevice(DeviceViewModel? device)
    {
        if (ReferenceEquals(_device, device))
            return;
        if (_device != null)
            _device.PropertyChanged -= DeviceChanged;
        _device = device;
        _editedSource = device?.Editor?.Row.Key.Source;
        _restoreSource = null;
        if (device != null)
            device.PropertyChanged += DeviceChanged;
    }

    void DeviceChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (_device is not { } device)
            return;
        if (e.PropertyName == nameof(DeviceViewModel.Editor))
        {
            _restoreSource = device.Editor == null ? _editedSource ?? _restoreSource : null;
            _editedSource = device.Editor?.Row.Key.Source;
        }
        else if (e.PropertyName != nameof(DeviceViewModel.ReloadingSave))
            return;
        if (_restoreSource == null)
            return;

        Dispatcher.UIThread.Post(() =>
        {
            if (_window == null || !ReferenceEquals(device, _device) || device.Editor != null || !IsEffectivelyVisible ||
                device.ReloadingSave || _restoreSource is not { } source)
                return;
            _restoreSource = null;
            var row = this.GetVisualDescendants().OfType<Button>().FirstOrDefault(b => b.Classes.Contains("keyCell") &&
                b.IsEffectivelyVisible && b.IsEffectivelyEnabled && b.DataContext is KeyRowViewModel key && key.Key.Source == source);
            if (row == null)
                KeyListToggle.Focus(NavigationMethod.Tab);
            else
            {
                row.BringIntoView();
                row.Focus(NavigationMethod.Tab);
            }
        }, DispatcherPriority.Loaded);
    }

    void ShowHealth(object? sender, RoutedEventArgs e)
    {
        if (DataContext is DeviceViewModel device)
            device.SelectedPage = DeviceViewModel.HealthPage;
    }

    void OpenBinding(object? sender, RoutedEventArgs e)
    {
        if (DataContext is DeviceViewModel device && sender is Button { DataContext: KeyRowViewModel { Bindable: true } row })
            device.SelectKeyCommand.Execute(row);
    }

    void ShowLayout(object? sender, RoutedEventArgs e)
    {
        if (DataContext is DeviceViewModel device)
            device.ShowPhoto = false;
    }

    void ActivateStage(object? sender, RoutedEventArgs e)
    {
        if (sender is Button { DataContext: StageRow row } && DataContext is DeviceViewModel device)
            _ = device.ActivateDpiStageAsync(row);
    }
}
