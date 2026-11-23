using ViperSynapse.App.Services;
using ViperSynapse.App.ViewModels;
using ViperSynapse.App.Views;
using ViperSynapse.Core.Bindings;
using Avalonia.Controls;
using Avalonia.Threading;
using Avalonia.VisualTree;

namespace ViperSynapse.Tests;

public class ShortcutSafetyTests
{
    [Fact]
    public Task Shortcut_view_clears_all_combo_selections_when_a_key_disappears() => HeadlessApp.Session.Dispatch(() =>
    {
        var main = new MainViewModel(new DaemonClient(), new AppPreferences(), false);
        var info = new DeviceInfo("ffff", "ffff", "Unknown", "unknown", "usb-test", false, false,
            null, null, null, [], false, null, [30, 48, 46], "available");
        var device = new DeviceViewModel(info, main);
        main.Tabs.Add(device);
        main.Home.ShowShortcutsCommand.Execute(null);
        var shortcuts = main.Home.Shortcuts;
        var original = GlobalShortcut.Create([30, 48, 46], new KeyboardBinding(15));
        shortcuts.ChangeCommand.Execute(new ShortcutRow(original, "", "", ""));
        var window = new Window { Width = 1440, Height = 1400, Content = new HomeView { DataContext = main.Home } };
        window.Show();
        Dispatcher.UIThread.RunJobs();
        var boxes = window.GetVisualDescendants().OfType<ComboBox>()
            .Where(box => box.PlaceholderText is "First key" or "Second key" or "Third key (optional)").ToList();
        Assert.Equal(3, boxes.Count);
        Assert.All(boxes, box => Assert.NotNull(box.SelectedItem));

        device.Info = info with { InputControls = [30, 48] };
        Dispatcher.UIThread.RunJobs();

        Assert.All(boxes, box => Assert.Null(box.SelectedItem));
        Assert.True(shortcuts.Editing);
        Assert.Contains("select", shortcuts.Problem!, StringComparison.OrdinalIgnoreCase);
        window.Close();
    }, CancellationToken.None);

    [Fact]
    public Task Existing_shortcut_selects_a_device_with_available_matching_keys() => HeadlessApp.Session.Dispatch(() =>
    {
        var main = new MainViewModel(new DaemonClient(), new AppPreferences(), false);
        var partial = new DeviceInfo("fffe", "fffe", "Partial", "unknown", "usb-partial", false, false,
            null, null, null, [], false, null, [30, 48], "partial");
        var available = new DeviceInfo("ffff", "ffff", "Available", "unknown", "usb-available", false, false,
            null, null, null, [], false, null, [30, 48], "available");
        var first = new DeviceViewModel(partial, main);
        var second = new DeviceViewModel(available, main);
        main.Tabs.Add(first);
        main.Tabs.Add(second);
        var shortcuts = new ShortcutsViewModel(main);
        var original = GlobalShortcut.Create([30, 48], new KeyboardBinding(15));

        shortcuts.ChangeCommand.Execute(new ShortcutRow(original, "", "", ""));

        Assert.Same(second, shortcuts.EditDevice);
        Assert.Equal<ushort>([30, 48], new[] { shortcuts.Key1!, shortcuts.Key2! }.Select(k => k.Source));
        Assert.Null(shortcuts.Problem);
    }, CancellationToken.None);

    [Fact]
    public Task Existing_shortcut_cannot_silently_lose_a_key_when_capabilities_change() => HeadlessApp.Session.Dispatch(() =>
    {
        var main = new MainViewModel(new DaemonClient(), new AppPreferences(), false);
        var info = new DeviceInfo("ffff", "ffff", "Unknown", "unknown", "usb-test", false, false,
            null, null, null, [], false, null, [30, 48, 46], "available");
        var device = new DeviceViewModel(info, main);
        main.Tabs.Add(device);
        var shortcuts = new ShortcutsViewModel(main);
        var original = GlobalShortcut.Create([30, 48, 46], new KeyboardBinding(15));
        shortcuts.ChangeCommand.Execute(new ShortcutRow(original, "", "", ""));
        Assert.Equal<ushort>([30, 48, 46], new[] { shortcuts.Key1!, shortcuts.Key2!, shortcuts.Key3! }.Select(k => k.Source));

        device.Info = info with { InputControls = [30, 48] };
        shortcuts.Editor!.SaveCommand.Execute(null);

        Assert.True(shortcuts.Editing);
        Assert.NotNull(shortcuts.Problem);
        Assert.Null(shortcuts.Key1);
        Assert.Null(shortcuts.Key2);
        Assert.Null(shortcuts.Key3);
    }, CancellationToken.None);

    [Fact]
    public Task Missing_saved_keys_require_an_explicit_new_selection() => HeadlessApp.Session.Dispatch(() =>
    {
        var main = new MainViewModel(new DaemonClient(), new AppPreferences(), false);
        var info = new DeviceInfo("ffff", "ffff", "Unknown", "unknown", "usb-test", false, false,
            null, null, null, [], false, null, [30, 48], "available");
        var device = new DeviceViewModel(info, main);
        main.Tabs.Add(device);
        var shortcuts = new ShortcutsViewModel(main);
        var original = GlobalShortcut.Create([30, 48, 46], new KeyboardBinding(15));
        shortcuts.ChangeCommand.Execute(new ShortcutRow(original, "", "", ""));

        Assert.Null(shortcuts.Key1);
        Assert.Null(shortcuts.Key2);
        Assert.Null(shortcuts.Key3);
        Assert.Contains("select", shortcuts.Problem!, StringComparison.OrdinalIgnoreCase);
    }, CancellationToken.None);
}
