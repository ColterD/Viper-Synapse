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
    [InlineData("wired", "Wired USB", "Unconfirmed", "Hardware verification", "Detected")]
    [InlineData("receiver", "HyperSpeed", "Available", "Control interface responded.", "Detected")]
    [InlineData("bluetooth", "Bluetooth", "Unavailable", "over Bluetooth", "Available")]
    [InlineData("missing-control", "Wired USB", "Unavailable", "No compatible control interface", "Detected")]
    [InlineData("missing-input", "Wired USB", "Unconfirmed", "Hardware verification", "Unavailable")]
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
                T Find<T>(string name) where T : Control => Assert.IsType<T>(view.FindControl<T>(name));
                var page = Find<ScrollViewer>("DeviceHealthPanel");
                Assert.False(page.IsEffectivelyVisible);
                int requestsBeforeHealth = firmware.Requests.Count;
                Find<ListBox>("DevicePages").SelectedItem = "HEALTH";
                Dispatcher.UIThread.RunJobs();
                Assert.True(page.IsEffectivelyVisible);
                Assert.Equal(requestsBeforeHealth, firmware.Requests.Count);
                Assert.Equal("Razer Basilisk V4 Pro", Find<TextBlock>("HealthModelName").Text);
                Assert.Equal(connection, Find<TextBlock>("HealthConnection").Text);

                var features = Find<ItemsControl>("HealthFeatures");
                Assert.True(features.Bounds.Width > 650);
                (string Status, string Reason) Feature(string name)
                {
                    var row = Assert.Single(features.GetVisualDescendants().OfType<Grid>(), grid =>
                        grid.Children.OfType<TextBlock>().Any(text => text.Name == "HealthFeatureName" && text.Text == name));
                    var cells = row.Children.OfType<TextBlock>().ToList();
                    return (Assert.Single(cells, text => text.Name == "HealthFeatureStatus").Text!,
                        Assert.Single(cells, text => text.Name == "HealthFeatureReason").Text!);
                }
                var onboard = Feature("Onboard profiles and firmware buttons");
                Assert.Equal(firmwareStatus, onboard.Status);
                Assert.Contains(firmwareReason, onboard.Reason);
                Assert.Equal(firmwareStatus, Feature("Lighting").Status);
                Assert.Equal(firmwareStatus, Feature("DPI stages").Status);
                Assert.Equal(firmwareStatus, Feature("Firmware wheel settings").Status);
                Assert.Equal(inputStatus, Feature("Software remapping").Status);
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
