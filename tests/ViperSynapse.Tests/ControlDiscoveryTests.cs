using ViperSynapse.Core.Bindings;
using ViperSynapse.Hardware.Linux;
using ViperSynapse.App.Services;
using ViperSynapse.App.ViewModels;

namespace ViperSynapse.Tests;

public class ControlDiscoveryTests
{
    [Fact]
    public void Advertised_controls_are_filtered_deduplicated_and_include_normal_wheels()
    {
        var nodes = new[]
        {
            new InputCapabilities([30, 48, 30, 0, 0x110], [8, 11]),
            new InputCapabilities([30, 0x130], [6]), // gamepad node is not grabbed
            new InputCapabilities([48], [12]), // high-resolution wheel without its ordinary twin
        };
        var controls = InputControlDiscovery.Select(nodes);
        Assert.Equal<ushort>([30, 48, 0x110, SourceKeys.WheelUp, SourceKeys.WheelDown], controls);
    }

    [Fact]
    public void Missing_input_nodes_are_distinct_from_an_empty_device()
    {
        var result = InputControlDiscovery.Read([]);
        Assert.Equal("unavailable", result.Status);
        Assert.Empty(result.Controls);
    }

    [Fact]
    public void Disconnected_nodes_are_reported_unavailable()
    {
        var result = InputControlDiscovery.Read(["/definitely-not-a-real-viper-input-device"]);
        Assert.Equal("unavailable", result.Status);
        Assert.Empty(result.Controls);
    }

    [Theory]
    [InlineData(1)]
    [InlineData(13)]
    public void Capability_permission_errors_remain_distinguishable(int errno) =>
        Assert.IsType<UnauthorizedAccessException>(EvdevDevice.CapabilityError("/dev/input/event-test", errno));

    [Fact]
    public void Read_distinguishes_permission_errors_empty_results_and_partial_discovery()
    {
        var denied = InputControlDiscovery.Read(["denied"], _ => throw new UnauthorizedAccessException());
        var empty = InputControlDiscovery.Read(["empty"], _ => new InputCapabilities([], []));
        var partial = InputControlDiscovery.Read(["working", "lost"], path => path == "lost"
            ? throw new IOException() : new InputCapabilities([30], []));
        Assert.Equal("permission", denied.Status);
        Assert.Equal("available", empty.Status);
        Assert.Empty(empty.Controls);
        Assert.Equal("partial", partial.Status);
        Assert.Equal<ushort>([30], partial.Controls);
    }

    [Fact]
    public void Shortcut_matching_requires_the_discovered_keys_on_unknown_or_uncurated_devices()
    {
        Assert.True(ViperSynapse.Service.DeviceManager.ShortcutMatchesControls(null, [30, 48], "available", [30, 48]));
        Assert.False(ViperSynapse.Service.DeviceManager.ShortcutMatchesControls(null, [30], "available", [30, 48]));
        Assert.False(ViperSynapse.Service.DeviceManager.ShortcutMatchesControls(null, [30, 48], "partial", [30, 48]));
        Assert.False(ViperSynapse.Service.DeviceManager.ShortcutMatchesControls(null, [30, SourceKeys.WheelUp], "available", [30, SourceKeys.WheelUp]));
        var curated = ViperSynapse.Core.Devices.DeviceCatalogue.Default.Find(0x02eb)!;
        var sources = curated.Keys.Where(k => k.Source != 0 && !SourceKeys.IsWheel(k.Source)).Take(2).Select(k => k.Source).ToArray();
        Assert.Equal(2, sources.Length);
        Assert.True(ViperSynapse.Service.DeviceManager.ShortcutMatchesControls(curated, [], "unavailable", sources));
    }

    [Fact]
    public void Input_watcher_observes_permission_changes()
    {
        if (!OperatingSystem.IsLinux())
            return;
        string dir = Path.Combine(Path.GetTempPath(), "viper-input-watcher-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(dir);
        string path = Path.Combine(dir, "event-test");
        try
        {
            File.WriteAllText(path, "");
            using var changed = new ManualResetEventSlim();
            using var watcher = ViperSynapse.Service.Program.WatchInputNodes(dir, (_, _) => changed.Set());
            File.SetUnixFileMode(path, UnixFileMode.UserRead | UnixFileMode.UserWrite);
            Assert.True(changed.Wait(TimeSpan.FromSeconds(3), TestContext.Current.CancellationToken), "Changing input-node permissions should trigger a rescan.");
        }
        finally { Directory.Delete(dir, true); }
    }

    [Fact]
    public void Input_watcher_still_observes_created_and_deleted_nodes()
    {
        if (!OperatingSystem.IsLinux())
            return;
        string dir = Path.Combine(Path.GetTempPath(), "viper-input-hotplug-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(dir);
        string path = Path.Combine(dir, "event-test");
        try
        {
            using var created = new ManualResetEventSlim();
            using var deleted = new ManualResetEventSlim();
            using var watcher = ViperSynapse.Service.Program.WatchInputNodes(dir, (_, e) =>
            {
                if (e.ChangeType == WatcherChangeTypes.Created) created.Set();
                if (e.ChangeType == WatcherChangeTypes.Deleted) deleted.Set();
            });
            File.WriteAllText(path, "");
            Assert.True(created.Wait(TimeSpan.FromSeconds(3), TestContext.Current.CancellationToken));
            File.Delete(path);
            Assert.True(deleted.Wait(TimeSpan.FromSeconds(3), TestContext.Current.CancellationToken));
        }
        finally { Directory.Delete(dir, true); }
    }

    [Fact]
    public void Service_control_data_round_trips_and_older_device_data_is_accepted()
    {
        var service = new ViperSynapse.Service.DeviceInfo("ffff", "ffff", "Unknown", "unknown", "usb-test", false, false,
            null, null, null, [], false, null, [30, SourceKeys.WheelUp], "available");
        var options = new System.Text.Json.JsonSerializerOptions { PropertyNamingPolicy = System.Text.Json.JsonNamingPolicy.CamelCase };
        string json = System.Text.Json.JsonSerializer.Serialize(service, options);
        var client = System.Text.Json.JsonSerializer.Deserialize<DeviceInfo>(json, options)!;
        Assert.Equal<ushort>([30, SourceKeys.WheelUp], client.InputControls!);
        Assert.Equal("available", client.InputControlsStatus);
        var old = System.Text.Json.JsonSerializer.Deserialize<DeviceInfo>("{\"key\":\"x\",\"productId\":\"ffff\",\"name\":\"Old\",\"type\":\"unknown\",\"port\":\"usb\",\"inputNodes\":[]}", options)!;
        Assert.Null(old.InputControls);
        Assert.Null(old.InputControlsStatus);
    }

    [Fact]
    public Task Generic_rows_update_when_discovered_controls_change() => HeadlessApp.Session.Dispatch(() =>
    {
        var main = new MainViewModel(new DaemonClient(), new AppPreferences(), false);
        var info = new DeviceInfo("ffff", "ffff", "Unknown", "unknown", "usb-test", false, false,
            null, null, null, [], false, null, [30], "available");
        var device = new DeviceViewModel(info, main);
        Assert.True(device.UsingGenericControls);
        Assert.Equal("KEY_A", Assert.Single(device.Rows).Key.Label);
        Assert.Equal("", device.Rows[0].Number);
        Assert.False(device.ShowDrawing);
        device.Info = info with { InputControls = [48, SourceKeys.WheelUp] };
        Assert.Equal<ushort>([48, SourceKeys.WheelUp], device.Rows.Select(r => r.Key.Source));
        Assert.Equal("KEY_B", device.Rows[0].Key.Label);
        Assert.All(device.Rows, row => Assert.True(row.Bindable));
    }, CancellationToken.None);

    [Fact]
    public Task Partial_discovery_shows_but_disables_bindings_until_access_is_restored() => HeadlessApp.Session.Dispatch(() =>
    {
        var main = new MainViewModel(new DaemonClient(), new AppPreferences(), false);
        var info = new DeviceInfo("ffff", "ffff", "Unknown", "unknown", "usb-test", false, false,
            null, null, null, [], false, null, [30], "partial");
        var device = new DeviceViewModel(info, main);
        Assert.False(Assert.Single(device.Rows).Bindable);
        Assert.Contains("unavailable", device.GenericControlsStatus, StringComparison.OrdinalIgnoreCase);
        device.Info = info with { InputControlsStatus = "available" };
        Assert.True(Assert.Single(device.Rows).Bindable);
    }, CancellationToken.None);

    [Fact]
    public Task Shortcut_choices_exclude_wheels_and_refresh_when_device_controls_change() => HeadlessApp.Session.Dispatch(() =>
    {
        var main = new MainViewModel(new DaemonClient(), new AppPreferences(), false);
        var info = new DeviceInfo("ffff", "ffff", "Unknown", "unknown", "usb-test", false, false,
            null, null, null, [], false, null, [30, SourceKeys.WheelUp], "available");
        var device = new DeviceViewModel(info, main);
        main.Tabs.Add(device);
        var shortcuts = new ShortcutsViewModel(main) { EditDevice = device };
        int changes = 0;
        shortcuts.PropertyChanged += (_, e) => { if (e.PropertyName == nameof(ShortcutsViewModel.KeyChoices)) changes++; };
        Assert.Equal<ushort>([30], shortcuts.KeyChoices.Select(k => k.Source));
        device.Info = info with { InputControls = [48, SourceKeys.WheelDown] };
        Assert.Equal<ushort>([48], shortcuts.KeyChoices.Select(k => k.Source));
        Assert.True(changes > 0);
        shortcuts.Key1 = Assert.Single(shortcuts.KeyChoices);
        main.Tabs.Remove(device);
        shortcuts.DevicesUpdated();
        Assert.Null(shortcuts.EditDevice);
        Assert.Null(shortcuts.Key1);
    }, CancellationToken.None);

    [Fact]
    public Task A_curated_layout_is_not_replaced_by_discovered_controls() => HeadlessApp.Session.Dispatch(() =>
    {
        var main = new MainViewModel(new DaemonClient(), new AppPreferences(), false);
        var info = new DeviceInfo("02eb", "02eb", "Test", "keypad", "usb-test", true, true,
            null, null, null, [], false, null, [30], "available");
        var device = new DeviceViewModel(info, main);
        Assert.False(device.UsingGenericControls);
        Assert.True(device.Rows.Count > 1);
        Assert.DoesNotContain(device.Rows, r => r.Key.Group == "linux input");
    }, CancellationToken.None);
}
