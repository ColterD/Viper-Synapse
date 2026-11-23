using System.Text;
using ViperSynapse.Core.Bindings;
using ViperSynapse.Core.Engine;
using ViperSynapse.Core.Input;
using ViperSynapse.Core.Protocol;
using ViperSynapse.Hardware;
using ViperSynapse.Hardware.Linux;
using ViperSynapse.Service;

namespace ViperSynapse.Tests;

public sealed class BasiliskConnectionTests : IDisposable
{
    const ushort Wired = 0x00e2, Receiver = 0x00e3, Bluetooth = 0x00e4;
    readonly string _root = Path.Combine(Path.GetTempPath(), "viper-basilisk-" + Guid.NewGuid().ToString("N"));

    [Fact]
    public void A_proven_pair_prefers_wired_then_falls_back_to_the_receiver_with_the_same_settings()
    {
        var store = new SettingsStore(_root);
        var settings = store.LoadDevice("00e3");
        var output = new EventSink();
        using var manager = new DeviceManager(store, output)
        {
            StartInputSession = (_, engine, _, onLost) =>
            {
                var session = DeviceSession.Start([], engine, onNodeLost: onLost);
                engine.Key(KeyCodes.BtnLeft, 1);
                return session;
            }
        };
        var keymap = settings.Active.Keymaps[0].With(KeyCodes.BtnLeft, new KeyboardBinding(46));
        store.SaveDevice("00e3", settings with { Profiles = [settings.Active with { Keymaps = [keymap] }] });
        var cable = Node("usb-cable", Wired);
        var dongle = Node("usb-receiver", Receiver);
        bool cableOnline = true;
        Refresh(manager, [dongle], [Input(dongle)], _ => new ReplyDevice("mouse-a"));
        Assert.Equal(["46:1"], output.Events);

        ReplyDevice Open(string path) => new("mouse-a", online: () => path != cable.DevicePath || cableOnline);
        Refresh(manager, [dongle, cable], [Input(dongle), Input(cable)], Open);
        var wired = Assert.Single(manager.Devices);
        Assert.Equal("00e2", wired.ProductId);
        Assert.Equal("00e3", wired.Key);
        Assert.Equal("wired", wired.Connection);
        Assert.Equal(["46:1", "46:0", "46:1"], output.Events);

        Refresh(manager, [dongle, cable], [Input(dongle), Input(cable)],
            path => new ReplyDevice(path == dongle.DevicePath ? null : "mouse-a"));
        Assert.Equal("wired", Assert.Single(manager.Devices).Connection);
        Assert.Equal(["46:1", "46:0", "46:1"], output.Events);

        cableOnline = false;
        Refresh(manager, [dongle, cable], [Input(dongle), Input(cable)], Open);
        var wireless = Assert.Single(manager.Devices);
        Assert.Equal("00e3", wireless.Key);
        Assert.Equal("hyperspeed", wireless.Connection);
        Assert.Equal(settings.ActiveProfile, manager.GetSettings(wireless.Key).ActiveProfile);
        Assert.Equal(["46:1", "46:0", "46:1", "46:0", "46:1"], output.Events);

        Refresh(manager, [dongle], [Input(dongle)], Open);
        Assert.Equal("hyperspeed", Assert.Single(manager.Devices).Connection);
        Assert.Equal(["46:1", "46:0", "46:1", "46:0", "46:1"], output.Events);
    }

    [Fact]
    public void Distinct_serials_remain_separate_and_a_sleeping_receiver_reprobes_on_the_same_port()
    {
        using var manager = new DeviceManager(new SettingsStore(_root), new EventSink());
        var cable = Node("usb-cable", Wired);
        var dongle = Node("usb-receiver", Receiver);
        bool receiverAwake = false;
        ReplyDevice Open(string path) => new(path == cable.DevicePath ? "mouse-a" : receiverAwake ? "mouse-b" : null);
        Refresh(manager, [cable, dongle], [], Open);
        Assert.Equal(2, manager.Devices.Count);
        Assert.Null(manager.Devices.Single(device => device.ProductId == "00e3").Serial);

        receiverAwake = true;
        Refresh(manager, [cable, dongle], [], Open);
        Assert.Equal(["mouse-a", "mouse-b"], manager.Devices.Select(device => device.Serial));
    }

    [Fact]
    public void Bluetooth_uses_remote_identity_and_does_not_send_vendor_commands()
    {
        using var manager = new DeviceManager(new SettingsStore(_root), new EventSink());
        var first = BluetoothNode("/dev/hidraw1", "01:02:03:04:05:06");
        var second = BluetoothNode("/dev/hidraw2", "01:02:03:04:05:07");
        var unknown = BluetoothNode("/dev/hidraw3", "01:02:03:04:05:08") with { ProductId = Receiver };
        var usb = Node("usb-unrelated", Bluetooth);
        Refresh(manager, [first, second, unknown, usb], [], path =>
            path == usb.DevicePath ? new ReplyDevice("other") : throw new InvalidOperationException("Bluetooth vendor access"));
        Assert.Equal(4, manager.Devices.Count);
        Assert.All(manager.Devices.Where(device => device.Connection == "bluetooth"),
            device =>
            {
                Assert.True(device.Known);
                Assert.Equal("00e3", device.Key);
                Assert.Null(device.ControlNode);
            });
        Assert.False(manager.Devices.Single(device => device.Port == usb.PortPath).Known);
        Assert.False(manager.Devices.Single(device => device.Port == unknown.PortPath).Known);
    }

    [Fact]
    public void Bluetooth_replays_only_discovered_inputs_and_releases_held_output_on_disconnect()
    {
        var store = new SettingsStore(_root);
        var settings = store.LoadDevice("00e3");
        var keymap = settings.Active.Keymaps[0].With(KeyCodes.BtnLeft, new KeyboardBinding(46));
        store.SaveDevice("00e3", settings with { Profiles = [settings.Active with { Keymaps = [keymap] }] });
        var output = new EventSink();
        using var manager = new DeviceManager(store, output)
        {
            StartInputSession = (paths, engine, _, onLost) =>
            {
                Assert.Equal("/dev/input/event0", Assert.Single(paths));
                var session = DeviceSession.Start([], engine, onNodeLost: onLost);
                engine.Key(KeyCodes.BtnLeft, 1);
                return session;
            }
        };
        var raw = BluetoothNode("/dev/hidraw1", "01:02:03:04:05:06");
        var input = new InputNode("/dev/input/event0", 0x1532, Bluetooth, "Basilisk", "aa:aa:aa:aa:aa:aa")
        {
            Bus = HidrawNode.BluetoothBus,
            Unique = "01-02-03-04-05-06",
        };
        manager.Refresh([raw], [input], _ => true, _ => throw new InvalidOperationException("Bluetooth vendor access"),
            paths => new DiscoveredControls([KeyCodes.BtnLeft], paths.Any() ? "available" : "unavailable"));

        var device = Assert.Single(manager.Devices);
        Assert.Equal("bluetooth", device.Connection);
        Assert.Equal(new ushort[] { KeyCodes.BtnLeft }, device.InputControls);
        Assert.True(device.Remapping);
        Assert.Equal(["46:1"], output.Events);

        manager.Refresh([], [], _ => false, _ => throw new InvalidOperationException("Unexpected vendor access"));
        Assert.Empty(manager.Devices);
        Assert.Equal(["46:1", "46:0"], output.Events);
    }

    [Fact]
    public void Refresh_publishes_the_slot_selected_on_the_mouse()
    {
        using var manager = new DeviceManager(new SettingsStore(_root), new EventSink());
        var cable = Node("usb-cable", Wired);
        byte slot = 1;
        var firmware = new ReplyDevice("mouse-a", () => slot);
        Refresh(manager, [cable], [], _ => firmware);
        Assert.Equal((byte)1, Assert.Single(manager.Devices).OnboardProfile);
        int slotReads = firmware.SlotReads;

        int updates = 0;
        manager.DevicesChanged += () => updates++;
        slot = 2;
        Refresh(manager, [cable], [], _ => throw new InvalidOperationException("The same USB endpoint should stay open"));
        Assert.Equal((byte)2, Assert.Single(manager.Devices).OnboardProfile);
        Assert.Equal(slotReads + 1, firmware.SlotReads);
        Assert.Equal(1, updates);
    }

    static HidrawNode Node(string port, ushort pid) =>
        new("/dev/" + port, 0x1532, pid, 3, "Basilisk", port + "/input3") { Bus = HidrawNode.UsbBus };

    static InputNode Input(HidrawNode node) => new(node.DevicePath + "-evdev", node.VendorId, node.ProductId,
        node.Name, node.PortPath + "/input0") { Bus = node.Bus };

    static HidrawNode BluetoothNode(string path, string uniq) => HidrawNode.FromUevent(path,
        "HID_ID=0005:00001532:000000E4\nHID_NAME=Basilisk\nHID_PHYS=aa:aa:aa:aa:aa:aa\nHID_UNIQ=" + uniq)!;

    static void Refresh(DeviceManager manager, IReadOnlyList<HidrawNode> nodes, IReadOnlyList<InputNode> inputs,
        Func<string, ReplyDevice> open) => manager.Refresh(nodes, inputs, _ => true,
            path => new RazerTransport(open(path)) { AnswerDelay = TimeSpan.Zero });

    sealed class ReplyDevice(string? serial, Func<byte>? slot = null, Func<bool>? online = null) : IHidFeatureDevice
    {
        RazerReport? _request;
        public string Path => "fixture";
        public int SlotReads { get; private set; }
        public void SendFeature(ReadOnlySpan<byte> report)
        {
            _request = RazerReport.Parse(report[1..]);
            if ((_request.CommandClass, _request.CommandId) == (0x05, 0x84))
                SlotReads++;
        }
        public int ReceiveFeature(Span<byte> report)
        {
            var request = _request!;
            byte[] arguments = request.CommandId switch
            {
                0x82 when serial != null => Encoding.ASCII.GetBytes(serial),
                0x81 => [1, 0],
                0x84 when request.CommandClass == 5 => [slot?.Invoke() ?? 1],
                _ => [],
            };
            var reply = RazerReport.Create(request.TransactionId, request.CommandClass, request.CommandId, arguments).Bytes.ToArray();
            reply[0] = (byte)(serial == null || online?.Invoke() == false ? ReportStatus.NotSupported : ReportStatus.Successful);
            reply.CopyTo(report[1..]);
            return report.Length;
        }
        public void Dispose() { }
    }

    sealed class EventSink : IInputSink
    {
        public List<string> Events { get; } = [];
        public void Key(ushort code, int value) => Events.Add($"{code}:{value}");
        public void Relative(ushort axis, int value) { }
        public void Sync() { }
    }

    public void Dispose()
    {
        if (Directory.Exists(_root))
            Directory.Delete(_root, recursive: true);
    }
}
