using System.Text;
using ViperSynapse.Core.Bindings;
using ViperSynapse.Core.Engine;
using ViperSynapse.Core.Input;
using ViperSynapse.Core.Lighting;
using ViperSynapse.Core.Protocol;
using ViperSynapse.Hardware;
using ViperSynapse.Hardware.Linux;
using ViperSynapse.Service;

namespace ViperSynapse.Tests;

public sealed class DeviceLifecycleRecoveryTests : IDisposable
{
    readonly DirectoryInfo _root = Directory.CreateTempSubdirectory("viper-device-recovery-");

    [Fact]
    public void Missing_virtual_output_keeps_firmware_available_and_retry_enables_input()
    {
        var store = new SettingsStore(_root.FullName);
        var settings = store.LoadDevice("ffff");
        var keymap = settings.Active.Keymaps[0].With(KeyCodes.BtnLeft, new KeyboardBinding(46));
        var sink = new EventSink();
        int outputAttempts = 0;
        int grabs = 0;
        using var manager = new DeviceManager(store, () => ++outputAttempts == 1
            ? throw new UnauthorizedAccessException("test denial")
            : sink)
        {
            StartInputSession = (_, engine, _, onLost) =>
            {
                grabs++;
                var session = DeviceSession.Start([], engine, onNodeLost: onLost);
                engine.Key(KeyCodes.BtnLeft, 1);
                return session;
            },
        };
        var node = Node("mouse", 0xffff);
        Refresh(manager, [node], [Input(node)], _ => new ReplyDevice("MOUSE"));

        var device = Assert.Single(manager.Devices);
        Assert.True(device.ControlReady);
        Assert.False(device.VirtualOutputReady);
        Assert.Contains("/dev/uinput", device.InputIssue);
        Assert.False(device.Remapping);
        manager.SetSettings("ffff", settings with { Profiles = [settings.Active with { Keymaps = [keymap] }] });
        Assert.Equal(new KeyboardBinding(46), manager.GetSettings("ffff").ActiveKeymapOrFirst.Resolve(KeyCodes.BtnLeft, false));
        Assert.Equal(0, grabs);

        Assert.True(manager.RetryInputOutput());
        Assert.True(manager.RetryInputOutput());
        Assert.Equal(2, outputAttempts);
        Assert.Equal(1, grabs);
        Assert.True(Assert.Single(manager.Devices).Remapping);
        Assert.True(Assert.Single(manager.Devices).VirtualOutputReady);
        Assert.Null(Assert.Single(manager.Devices).InputIssue);
        Assert.Equal(["46:1"], sink.Events);
    }

    [Fact]
    public void A_late_or_replaced_legacy_control_node_is_reconciled_without_reopening_an_unchanged_one()
    {
        using var manager = new DeviceManager(new SettingsStore(_root.FullName), new EventSink());
        var node = Node("naga", 0x0053);
        var input = Input(node);
        var first = new ReplyDevice("FIRST");
        var replacement = new ReplyDevice("NEXT");
        int opens = 0;
        ReplyDevice Open(string path) => ++opens == 1 ? first : replacement;

        Refresh(manager, [], [input], Open);
        Assert.False(Assert.Single(manager.Devices).ControlReady);
        Refresh(manager, [node], [input], Open);
        Assert.Equal("FIRST", Assert.Single(manager.Devices).Serial);
        Refresh(manager, [node], [input], Open);
        Assert.Equal(1, opens);

        // Linux can reuse the name of a retired hidraw device.
        Refresh(manager, [node with { InstancePath = "second-kernel-instance" }], [input], Open);
        Assert.Equal("NEXT", Assert.Single(manager.Devices).Serial);
        Assert.True(first.Disposed);
        Refresh(manager, [], [input], Open);
        Assert.True(replacement.Disposed);
        Assert.False(Assert.Single(manager.Devices).ControlReady);
    }

    [Fact]
    public void A_denied_control_path_backs_off_then_explicit_retry_recovers_it()
    {
        using var manager = new DeviceManager(new SettingsStore(_root.FullName), new EventSink());
        var node = Node("naga", 0x0053);
        int attempts = 0;
        RazerTransport Open(string path)
        {
            if (++attempts == 1)
                throw new UnauthorizedAccessException("test denial");
            return Transport(new ReplyDevice("NAGA"));
        }
        manager.Refresh([node], [Input(node)], _ => true, Open, ReadControls);
        Assert.Contains("permission", Assert.Single(manager.Devices).ControlIssue);
        manager.Refresh([node], [Input(node)], _ => true, Open, ReadControls);
        Assert.Equal(1, attempts);

        manager.Refresh([node], [Input(node)], _ => true, Open, ReadControls, forceControlRetry: true);
        Assert.Equal("NAGA", Assert.Single(manager.Devices).Serial);
    }

    [Fact]
    public void Periodic_recovery_polls_the_receiver_and_leaves_other_endpoints_alone()
    {
        using var manager = new DeviceManager(new SettingsStore(_root.FullName), new EventSink());
        var wiredNode = Node("wired", 0x00e2, 3);
        var receiverNode = Node("receiver", 0x00e3, 3);
        var legacyNode = Node("legacy", 0x0053);
        var wired = new ReplyDevice("WIRED");
        var receiver = new ReplyDevice("RECEIVER");
        var legacy = new ReplyDevice("LEGACY");
        Refresh(manager, [wiredNode, legacyNode], [], path => path == wiredNode.DevicePath ? wired : legacy);
        manager.RefreshReceivers(() => throw new InvalidOperationException("Wired-only should not enumerate."),
            _ => true, _ => throw new InvalidOperationException("Wired-only should not probe."));

        HidrawNode[] nodes = [wiredNode, receiverNode, legacyNode];
        Refresh(manager, nodes, [], path => path == receiverNode.DevicePath
            ? receiver : throw new InvalidOperationException("Unchanged endpoints should not reopen."));
        int wiredReports = wired.Sent;
        int receiverReports = receiver.Sent;
        int legacyReports = legacy.Sent;
        manager.RefreshReceivers(() => nodes, _ => true,
            _ => throw new InvalidOperationException("A responsive receiver should not reopen."));

        Assert.Equal(wiredReports, wired.Sent);
        Assert.Equal(receiverReports + 1, receiver.Sent);
        Assert.Equal(legacyReports, legacy.Sent);
    }

    [Fact]
    public void A_sleeping_receiver_wakes_on_a_periodic_tick_without_an_input_event()
    {
        using var manager = new DeviceManager(new SettingsStore(_root.FullName), new EventSink());
        var receiver = Node("receiver", 0x00e3, 3);
        manager.Refresh([receiver], [], _ => true, _ => throw new IOException("test sleeping receiver"), ReadControls);
        Assert.False(Assert.Single(manager.Devices).ControlReady);

        manager.RefreshReceivers(() => [receiver], _ => true, _ => Transport(new ReplyDevice("RECEIVER")));

        var device = Assert.Single(manager.Devices);
        Assert.True(device.ControlReady);
        Assert.Equal("RECEIVER", device.Serial);
        Assert.Equal("hyperspeed", device.Connection);
    }

    [Fact]
    public void Stationary_stick_readers_expire_without_input_and_have_one_owner()
    {
        using var lease = new StickWatch();
        using var closed = new ManualResetEventSlim();
        long generation = lease.Renew(200, needReader: true)!.Value;
        Assert.Null(lease.Renew(200, needReader: true));
        lease.Complete(generation, new CallbackDisposable(closed.Set));

        Assert.True(closed.Wait(TimeSpan.FromSeconds(3), TestContext.Current.CancellationToken));
        Assert.False(lease.ShouldReport(generation));
    }

    [Fact]
    public async Task A_stick_reader_opening_during_unplug_is_retired_without_publishing()
    {
        using var started = new ManualResetEventSlim();
        using var unblock = new ManualResetEventSlim();
        using var closed = new ManualResetEventSlim();
        int opens = 0;
        using var manager = new DeviceManager(new SettingsStore(_root.FullName), new EventSink())
        {
            OpenStickMonitor = (_, _) =>
            {
                Interlocked.Increment(ref opens);
                started.Set();
                Assert.True(unblock.Wait(TimeSpan.FromSeconds(3), TestContext.Current.CancellationToken));
                return new CallbackDisposable(closed.Set);
            },
        };
        var keypad = Node("keypad", 0x02eb);
        Refresh(manager, [], [Input(keypad)], _ => throw new InvalidOperationException("No control interface."));
        var first = Task.Run(() => manager.WatchStick("02eb"), TestContext.Current.CancellationToken);
        try
        {
            Assert.True(started.Wait(TimeSpan.FromSeconds(3), TestContext.Current.CancellationToken));
            manager.WatchStick("02eb");
            Refresh(manager, [], [], _ => throw new InvalidOperationException("No control interface."));
        }
        finally
        {
            unblock.Set();
            await first.WaitAsync(TimeSpan.FromSeconds(3), TestContext.Current.CancellationToken);
        }
        Assert.Equal(1, opens);
        Assert.True(closed.IsSet);
        Assert.Empty(manager.Devices);
    }

    [Fact]
    public void Software_lighting_reports_a_transient_failure_and_retries_the_frame()
    {
        using var failed = new ManualResetEventSlim();
        using var recovered = new ManualResetEventSlim();
        var device = new ReplyDevice("LIGHTING") { FailFirstSend = true };
        using var transport = Transport(device);
        using var lighting = new SoftwareLighting(transport, 0x1f, null);
        lighting.ProblemChanged += () =>
        {
            if (lighting.Problem != null)
                failed.Set();
            else
                recovered.Set();
        };

        lighting.Show(new Dictionary<string, ZoneEffect> { ["logo"] = ZoneEffect.Default }, "mouse");

        Assert.True(failed.Wait(TimeSpan.FromSeconds(3), TestContext.Current.CancellationToken));
        Assert.True(recovered.Wait(TimeSpan.FromSeconds(3), TestContext.Current.CancellationToken));
        Assert.True(device.Sent >= 2);
        Assert.Null(lighting.Problem);
    }

    static HidrawNode Node(string port, ushort product, int @interface = 0) =>
        new("/dev/" + port, 0x1532, product, @interface, port, $"usb-{port}/input{@interface}");

    static InputNode Input(HidrawNode node) =>
        new(node.DevicePath + "-evdev", node.VendorId, node.ProductId, node.Name, node.PortPath + "/input0");

    static DiscoveredControls ReadControls(IEnumerable<string> nodes) =>
        new([KeyCodes.BtnLeft], nodes.Any() ? "available" : "unavailable");

    static RazerTransport Transport(ReplyDevice device) => new(device) { AnswerDelay = TimeSpan.Zero };

    static void Refresh(DeviceManager manager, IReadOnlyList<HidrawNode> nodes, IReadOnlyList<InputNode> inputs,
        Func<string, ReplyDevice> open) => manager.Refresh(nodes, inputs, _ => true, path => Transport(open(path)), ReadControls);

    sealed class ReplyDevice(string serial) : IHidFeatureDevice
    {
        RazerReport? _request;
        int _sent;
        public bool FailFirstSend { get; init; }
        public int Sent => Volatile.Read(ref _sent);
        public bool Disposed { get; private set; }
        public string Path => "fixture";

        public void SendFeature(ReadOnlySpan<byte> report)
        {
            if (Interlocked.Increment(ref _sent) == 1 && FailFirstSend)
                throw new IOException("test transport interruption");
            _request = RazerReport.Parse(report[1..]);
        }

        public int ReceiveFeature(Span<byte> report)
        {
            var request = _request!;
            byte[] arguments = request.CommandId switch
            {
                0x82 => Encoding.ASCII.GetBytes(serial),
                0x84 when request.CommandClass == 5 => [1],
                _ => [],
            };
            var reply = RazerReport.Create(request.TransactionId, request.CommandClass, request.CommandId, arguments).Bytes.ToArray();
            reply[0] = (byte)ReportStatus.Successful;
            reply.CopyTo(report[1..]);
            return report.Length;
        }

        public void Dispose() => Disposed = true;
    }

    sealed class CallbackDisposable(Action callback) : IDisposable
    {
        public void Dispose() => callback();
    }

    sealed class EventSink : IInputSink
    {
        public List<string> Events { get; } = [];
        public void Key(ushort code, int value) => Events.Add($"{code}:{value}");
        public void Relative(ushort axis, int value) { }
        public void Sync() { }
    }

    public void Dispose() => _root.Delete(recursive: true);
}
