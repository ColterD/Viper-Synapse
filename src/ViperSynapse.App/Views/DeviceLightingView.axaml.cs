using Avalonia.Controls;
using Avalonia.Interactivity;
using ViperSynapse.App.ViewModels;
using ViperSynapse.Core.Devices;

namespace ViperSynapse.App.Views;

public sealed partial class DeviceLightingView : UserControl
{
    public DeviceLightingView()
    {
        InitializeComponent();
        PaintCanvas.KeyPainted += row =>
        {
            if (DataContext is DeviceViewModel device)
                device.Lighting.PaintKey(row.Key);
        };
    }

    void PaintCell(object? sender, RoutedEventArgs e)
    {
        if (sender is Button { DataContext: LedCell cell } && DataContext is DeviceViewModel device)
            device.Lighting.PaintCell(cell);
    }

    void PaintKey(object? sender, RoutedEventArgs e)
    {
        if (sender is Button { DataContext: DeviceKey key } && DataContext is DeviceViewModel device)
            device.Lighting.PaintKey(key);
    }
}
