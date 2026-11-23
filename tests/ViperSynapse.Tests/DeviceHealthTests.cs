using System.Text.Json;
using Avalonia.Controls;
using Avalonia.Threading;
using Avalonia.VisualTree;
using ViperSynapse.App.Services;
using ViperSynapse.App.ViewModels;
using ViperSynapse.App.Views;
using ViperSynapse.Core.Engine;
using ViperSynapse.Core.Input;
using ViperSynapse.Hardware;
using ViperSynapse.Hardware.Linux;
using ViperSynapse.Service;
using DeviceInfo = ViperSynapse.App.Services.DeviceInfo;

namespace ViperSynapse.Tests;

public sealed class DeviceHealthTests
{
    [Theory]
    [InlineData("wired", "Wired USB", "Available", "Control interface responded.", "Detected")]
    [InlineData("receiver", "HyperSpeed", "Available", "Control interface responded.", "Detected")]
    [InlineData("bluetooth", "Bluetooth", "Unavailable", "over Bluetooth", "Available")]
    [InlineData("missing-control", "Wired USB", "Unavailable", "No compatible control interface", "Detected")]
    [InlineData("missing-input", "Wired USB", "Available", "Control interface responded.", "Unavailable")]
    [InlineData("permission", "Wired USB", "Unavailable", "No permission", "Detected")]
    public Task Health_panel_reports_detected_transport_and_feature_access(string scenario, string connection,
        string firmwareStatus, string firmwareReason, string inputStatus) => HeadlessApp.Session.Dispatch(() =>
    {
        var directory = Directory.CreateTempSubdirectory("viper-device-health-");
        try
        {
            using var manager = new DeviceManager(new SettingsStore(directory.FullName), new Sink());
            bool bluetooth = scenario == "bluetooth";
            ushort product = scenario switch { "bluetooth" => 0x00e4, "receiver" => 0x00e3, _ => 0x00e2 };
            ushort bus = bluetooth ? HidrawNode.BluetoothBus : HidrawNode.UsbBus;
            const string peer = "01:02:03:04:05:06";
            var raw = new HidrawNode("/dev/emulated-control", 0x1532, product, scenario == "missing-control" ? 0 : 3,
                "Basilisk fixture", "usb-health/input3") { Bus = bus, Unique = peer };
            var input = new InputNode("/dev/input/emulated-event", 0x1532, product, raw.Name, "usb-health/input0")
                { Bus = bus, Unique = peer };
            var firmware = new BasiliskOnboardTests.Firmware();
            void Refresh(HidrawNode node, bool deny)
            {
                manager.Refresh([node], scenario == "missing-input" ? [] : [input], _ => true, _ =>
                    bluetooth || scenario == "missing-control" && node.Interface != 3
                        ? throw new InvalidOperationException("Unexpected access to an emulated control interface")
                        : deny ? throw new UnauthorizedAccessException()
                        : new RazerTransport(firmware) { AnswerDelay = TimeSpan.Zero },
                    paths => paths.Any() ? new DiscoveredControls([KeyCodes.BtnLeft], "available") : new DiscoveredControls([], "unavailable"));
            }

            DeviceInfo ReadDevice() =>
                Assert.Single(JsonSerializer.Deserialize(manager.DevicesJson(), ClientJson.Default.ListDeviceInfo)!);

            Refresh(raw, scenario == "permission");
            var main = new MainViewModel(new DaemonClient(), new AppPreferences(), false);
            var model = new DeviceViewModel(ReadDevice(), main);
            var view = new DeviceView { DataContext = model };
            var window = new Window { Width = 1080, Height = 680, Content = view };
            window.Show();
            try
            {
                var health = Assert.IsType<DeviceHealthView>(view.FindControl<DeviceHealthView>("HealthPage"));
                T Find<T>(string name) where T : Control => Assert.IsType<T>(health.FindControl<T>(name));
                var page = Find<ScrollViewer>("DeviceHealthPanel");
                Dispatcher.UIThread.RunJobs();
                Assert.False(health.IsEffectivelyVisible);
                int requestsBeforeHealth = firmware.Requests.Count;
                view.FindControl<ListBox>("DevicePages")!.SelectedItem = "HEALTH";
                Dispatcher.UIThread.RunJobs();
                Assert.True(page.IsEffectivelyVisible);
                Assert.Equal(requestsBeforeHealth, firmware.Requests.Count);
                Assert.Equal("Razer Basilisk V4 Pro", Find<TextBlock>("HealthModelName").Text);
                Assert.Equal(connection, Find<TextBlock>("HealthConnection").Text);

                var features = Find<ItemsControl>("HealthFeatures");
                Assert.True(features.Bounds.Width > 650);
                (string Status, string Reason, string Verification) Feature(string name)
                {
                    var row = Assert.Single(features.GetVisualDescendants().OfType<Grid>(), grid =>
                        grid.Children.OfType<TextBlock>().Any(text => text.Name == "HealthFeatureName" && text.Text == name));
                    var cells = row.Children.OfType<TextBlock>().ToList();
                    var status = Assert.Single(cells, text => text.Name == "HealthFeatureStatus");
                    Assert.Contains("deviceStatusText", status.Classes);
                    if (status.Text is "Available")
                        Assert.Contains("statusGood", status.Classes);
                    else if (status.Text is "Unavailable")
                        Assert.Contains("statusUnavailable", status.Classes);
                    return (status.Text!,
                        Assert.Single(cells, text => text.Name == "HealthFeatureReason").Text!,
                        Assert.Single(cells, text => text.Name == "HealthFeatureVerification").Text!);
                }
                var onboard = Feature("Onboard profiles and firmware buttons");
                Assert.Equal(firmwareStatus, onboard.Status);
                Assert.Contains(firmwareReason, onboard.Reason);
                Assert.Equal(firmwareStatus, Feature("Lighting").Status);
                Assert.Equal(firmwareStatus, Feature("DPI stages").Status);
                Assert.Equal(firmwareStatus, Feature("Firmware wheel settings").Status);
                Assert.Equal(inputStatus, Feature("Software remapping").Status);
                if (scenario == "receiver")
                {
                    Assert.StartsWith("Hardware: DPI table read.", Feature("DPI stages").Verification);
                    Assert.StartsWith("Emulated:", onboard.Verification);
                    Assert.Equal("Feature-specific verification is not recorded.", Feature("Lift-off calibration").Verification);
                }
                if (scenario == "wired")
                    Assert.StartsWith("Emulated:", Feature("DPI stages").Verification);
                if (bluetooth)
                    Assert.Equal("Unavailable on this connection.", onboard.Verification);
                if (scenario == "missing-input")
                    Assert.Contains("No Linux input interface", Feature("Software remapping").Reason);

                if (scenario is "missing-control" or "permission")
                {
                    int updates = 0;
                    manager.DevicesChanged += () =>
                    {
                        updates++;
                        model.Info = ReadDevice();
                    };
                    Refresh(raw with { Interface = 3 }, deny: false);
                    Dispatcher.UIThread.RunJobs();
                    Assert.True(updates > 0);
                    Assert.True(page.IsEffectivelyVisible);
                    Assert.Contains("Control interface responded.", Feature("Onboard profiles and firmware buttons").Reason);
                }
            }
            finally
            {
                window.Close();
            }
        }
        finally
        {
            directory.Delete(recursive: true);
        }
    }, CancellationToken.None);

    [Fact]
    public Task Two_units_share_a_profile_but_health_and_state_select_one_endpoint() => HeadlessApp.Session.Dispatch(() =>
    {
        var directory = Directory.CreateTempSubdirectory("viper-health-units-");
        try
        {
            using var manager = new DeviceManager(new SettingsStore(directory.FullName), new Sink());
            var wired = new HidrawNode("/dev/wired", 0x1532, 0x00e2, 3, "Basilisk", "usb-a/input3");
            var receiver = new HidrawNode("/dev/receiver", 0x1532, 0x00e3, 3, "Basilisk", "usb-b/input3");
            var first = new BasiliskOnboardTests.Firmware { Serial = "WIRED01" };
            var second = new BasiliskOnboardTests.Firmware { Serial = "RADIO02" };
            manager.Refresh([wired, receiver], [], _ => true,
                path => new RazerTransport(path == wired.DevicePath ? first : second) { AnswerDelay = TimeSpan.Zero });
            var units = JsonSerializer.Deserialize(manager.DevicesJson(), ClientJson.Default.ListDeviceInfo)!;
            Assert.Equal(2, units.Count);
            Assert.Equal(units[0].Key, units[1].Key);
            var main = new MainViewModel(new DaemonClient(), new AppPreferences(), false);
            var model = new DeviceViewModel(units[0], main) { SelectedPage = DeviceViewModel.HealthPage };
            model.SetAttachedUnits(units);
            var view = new DeviceView { DataContext = model };
            var window = new Window { Width = 1080, Height = 680, Content = view };
            window.Show();
            try
            {
                var chooser = Assert.IsType<ComboBox>(view.FindControl<ComboBox>("InspectedUnitBox"));
                Assert.Contains("WIRED01", Assert.IsType<DeviceUnitChoice>(chooser.SelectedItem).Label);
                Assert.Contains("all 2 connected units", view.FindControl<TextBlock>("DeviceUnitScope")!.Text!);
                chooser.SelectedIndex = 1;
                Dispatcher.UIThread.RunJobs();
                Assert.Equal("RADIO02", model.Info.Serial);
                Assert.Equal("HyperSpeed", view.FindControl<DeviceHealthView>("HealthPage")!.FindControl<TextBlock>("HealthConnection")!.Text);
                model.SetAttachedUnits(units.AsEnumerable().Reverse().ToArray());
                Dispatcher.UIThread.RunJobs();
                Assert.Contains("RADIO02", Assert.IsType<DeviceUnitChoice>(chooser.SelectedItem).Label);

                int firstRequests = first.Requests.Count, secondRequests = second.Requests.Count;
                Assert.Null(manager.ReadUnitState(model.Key, model.Info.Port, model.Info.ProductId, "WIRED01"));
                Assert.Equal(secondRequests, second.Requests.Count);
                Assert.NotNull(manager.ReadUnitState(model.Key, model.Info.Port, model.Info.ProductId, model.Info.Serial!));
                Assert.Equal(firstRequests, first.Requests.Count);
                Assert.True(second.Requests.Count > secondRequests);

                manager.Refresh([wired], [], _ => true, _ => throw new InvalidOperationException("The wired endpoint should stay open"));
                firstRequests = first.Requests.Count;
                Assert.Null(manager.ReadUnitState(model.Key, model.Info.Port, model.Info.ProductId, model.Info.Serial!));
                Assert.Equal(firstRequests, first.Requests.Count);
                model.SetAttachedUnits(JsonSerializer.Deserialize(manager.DevicesJson(), ClientJson.Default.ListDeviceInfo)!);
                Dispatcher.UIThread.RunJobs();
                Assert.Equal("WIRED01", model.Info.Serial);
                Assert.Contains("WIRED01", Assert.IsType<DeviceUnitChoice>(chooser.SelectedItem).Label);
                Assert.Equal("Wired USB", view.FindControl<DeviceHealthView>("HealthPage")!.FindControl<TextBlock>("HealthConnection")!.Text);
            }
            finally
            {
                window.Close();
            }
        }
        finally
        {
            directory.Delete(recursive: true);
        }
    }, CancellationToken.None);

    [Fact]
    public Task A_late_read_from_an_old_unit_cannot_replace_the_selected_units_live_state() =>
        HeadlessApp.Session.Dispatch<bool>(async () =>
        {
            var main = new MainViewModel(new DaemonClient(), new AppPreferences(), false);
            var previous = new DeviceInfo("00e3", "00e3", "Razer Basilisk V4 Pro", "mouse", "usb-first",
                true, true, "FIRST", "v1.00", "/dev/emulated-first", [], false, null, Connection: "hyperspeed");
            var current = previous with { Port = "usb-second", ControlNode = "/dev/emulated-second", Serial = "SECOND" };
            var device = new DeviceViewModel(previous, main);
            var laterReply = new TaskCompletionSource<ViperSynapse.App.Services.DeviceState?>(TaskCreationOptions.RunContinuationsAsynchronously);
            var started = new TaskCompletionSource<DeviceInfo>(TaskCreationOptions.RunContinuationsAsynchronously);
            var oldRead = device.ReadDeviceAsync(info =>
            {
                started.SetResult(info);
                return laterReply.Task;
            });
            Assert.Equal("FIRST", (await started.Task).Serial);
            Assert.True(device.SetAttachedUnits([current]));
            DeviceInfo? target = null;
            await device.ReadDeviceAsync(info =>
            {
                target = info;
                return Task.FromResult<ViperSynapse.App.Services.DeviceState?>(new(null, 0, 0, 81, false));
            });
            Assert.Equal("SECOND", target?.Serial);
            Assert.Equal(81, device.BatteryPercent);
            laterReply.SetResult(new(null, 0, 0, 30, true));
            await oldRead;
            Assert.Equal(81, device.BatteryPercent);
            Assert.False(device.Charging);
            return true;
        }, CancellationToken.None);

    [Fact]
    public Task An_older_device_refresh_does_not_replace_newer_health() => HeadlessApp.Session.Dispatch<bool>(async () =>
    {
        var main = new MainViewModel(new DaemonClient(), new AppPreferences(), false);
        var previous = new DeviceInfo("00e3", "00e3", "Razer Basilisk V4 Pro", "mouse", "usb-health",
            true, true, "FIXTURE", "v1.00", "/dev/emulated", [], false, null, Connection: "hyperspeed", ControlReady: false);
        var device = new DeviceViewModel(previous, main) { SelectedPage = DeviceViewModel.HealthPage };
        main.Tabs.Insert(1, device);
        var olderReply = new TaskCompletionSource<IReadOnlyList<DeviceInfo>>(TaskCreationOptions.RunContinuationsAsynchronously);
        var olderRefresh = main.RefreshDevicesAsync(() => olderReply.Task);

        var current = previous with { ControlReady = true };
        await main.RefreshDevicesAsync(() => Task.FromResult<IReadOnlyList<DeviceInfo>>([current]));
        olderReply.SetResult([previous]);
        await olderRefresh;

        Assert.Same(current, device.Info);
        Assert.Equal("Available", device.Health.ControlStatus);
        return true;
    }, CancellationToken.None);

    [Fact]
    public void Older_device_snapshots_do_not_claim_the_control_interface_responded()
    {
        const string json = """
            [{"key":"00e3","productId":"00e2","name":"Razer Basilisk V4 Pro","type":"mouse","port":"usb-health",
              "known":true,"verified":false,"controlNode":"/dev/emulated","inputNodes":[],"connection":"wired"}]
            """;
        var info = Assert.Single(JsonSerializer.Deserialize(json, ClientJson.Default.ListDeviceInfo)!);
        var health = DeviceHealth.From(info, Core.Devices.DeviceCatalogue.Default.Find(0x00e2), genericControls: false);
        Assert.Equal("Wired USB", health.Connection);
        Assert.Equal("Unconfirmed", health.ControlStatus);
        Assert.Equal("Unconfirmed", Assert.Single(health.Features, feature => feature.Name == "Lighting").Status);
    }

    sealed class Sink : IInputSink
    {
        public void Key(ushort code, int value) { }
        public void Relative(ushort axis, int value) { }
        public void Sync() { }
    }
}
