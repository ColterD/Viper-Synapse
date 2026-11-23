using ViperSynapse.Core.Bindings;
using ViperSynapse.Core.Engine;
using ViperSynapse.Core.Input;
using ViperSynapse.Hardware;
using ViperSynapse.Hardware.Linux;
using ViperSynapse.Service;
using static ViperSynapse.Core.Devices.LegacyRemapControls;

namespace ViperSynapse.Tests;

public class NativeNagaIntegrationTests
{
    const string InputPath = "/dev/input/emulated-naga";
    const string MousePath = "/dev/hidraw-emulated-naga-mouse";
    const string VendorPath = "/dev/hidraw-emulated-naga-vendor";
    const string ProductKey = "0053";
    const ushort ThumbOne = 2;
    const ushort F1 = 59, F2 = 60, F3 = 61, F4 = 62;

    [Fact]
    public void Native_reports_remap_all_four_buttons_without_disturbing_holds_or_forwarding_mirrors()
    {
        using var device = new EmulatedNaga();
        device.Refresh(MouseNode(), VendorNode());

        Assert.Equal<ushort>([ThumbOne, KeyCodes.BtnLeft, NagaTiltRight, NagaTiltLeft, NagaDpiUp, NagaDpiDown],
            device.Info.InputControls!);
        Assert.Null(device.Info.ControlNode);
        Assert.False(device.Info.Remapping);
        Assert.Empty(device.Readers);

        device.Bind((NagaTiltLeft, F1), (NagaTiltRight, F2), (NagaDpiUp, F3), (NagaDpiDown, F4));
        var mouse = device.Readers[MousePath];
        var vendor = device.Readers[VendorPath];
        Assert.True(device.Info.Remapping);

        device.Send(ThumbOne, 1);
        device.Send(KeyCodes.BtnLeft, 1);
        mouse.MouseReport(0x21);
        device.Send(NagaTiltRight, 1); // The generic HID mouse may number the tilt aliases the other way around.
        vendor.VendorReport(0x22, 0x20);
        mouse.MouseReport(0x01);
        device.Send(NagaTiltRight, 0);
        Assert.Equal<(ushort, int)>([(ThumbOne, 1), (KeyCodes.BtnLeft, 1), (F1, 1), (F3, 1)], device.Output.Keys);

        vendor.VendorReport(0x20);
        vendor.VendorReport();
        mouse.MouseReport(0x41);
        vendor.VendorReport(0x23, 0x21);
        vendor.VendorReport();
        mouse.MouseReport(0x01);
        device.Send(KeyCodes.BtnLeft, 0);
        device.Send(ThumbOne, 0);

        Assert.Equal<(ushort, int)>([
            (ThumbOne, 1), (KeyCodes.BtnLeft, 1), (F1, 1), (F3, 1), (F1, 0), (F3, 0),
            (F2, 1), (F4, 1), (F4, 0), (F2, 0), (KeyCodes.BtnLeft, 0), (ThumbOne, 0),
        ], device.Output.Keys);

        device.Bind();
        Assert.False(device.Info.Remapping);
        Assert.True(mouse.Disposed);
        Assert.True(vendor.Disposed);
    }

    [Fact]
    public void Ambiguous_vendor_endpoint_leaves_tilt_available_without_advertising_dpi()
    {
        using var device = new EmulatedNaga();
        device.Refresh(MouseNode(), VendorNode(), VendorNode(VendorPath + "-duplicate"));

        Assert.Equal<ushort>([ThumbOne, KeyCodes.BtnLeft, NagaTiltRight, NagaTiltLeft], device.Info.InputControls!);
        device.Bind((NagaDpiUp, F3));
        Assert.False(device.Info.Remapping);
        Assert.Empty(device.Readers);

        device.Bind((NagaTiltLeft, F1), (NagaDpiUp, F3));
        var mouse = Assert.Single(device.Readers).Value;
        mouse.MouseReport(0x20);
        mouse.MouseReport(0x00);
        Assert.Equal<(ushort, int)>([(F1, 1), (F1, 0)], device.Output.Keys);
    }

    [Fact]
    public async Task Losing_the_vendor_reader_releases_its_output_and_retires_the_session()
    {
        using var device = new EmulatedNaga();
        device.Refresh(MouseNode(), VendorNode());
        device.Bind((NagaDpiUp, F3));
        var vendor = Assert.Single(device.Readers).Value;
        Assert.False(device.Session!.HasNativeNagaTilt);

        vendor.VendorReport(0x22, 0x21, 0x20);
        Assert.Equal<(ushort, int)>([(F3, 1)], device.Output.Keys);

        var retired = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        device.Manager.DevicesChanged += () => retired.TrySetResult();
        vendor.Lose();
        await retired.Task.WaitAsync(TimeSpan.FromSeconds(5), TestContext.Current.CancellationToken);

        Assert.Equal<(ushort, int)>([(F3, 1), (F3, 0)], device.Output.Keys);
        Assert.True(vendor.Disposed);
        Assert.False(device.Info.Remapping);
        Assert.Contains(VendorPath, device.Info.Problem!);
    }

    static HidrawNode MouseNode() => new(MousePath, 0x1532, NagaChroma, 0, "Naga Chroma", "usb-native-naga/input0");
    static HidrawNode VendorNode(string path = VendorPath) => new(path, 0x1532, NagaChroma, 1, "Naga Chroma", "usb-native-naga/input1");

    sealed class EmulatedNaga : IDisposable
    {
        readonly DirectoryInfo _root = Directory.CreateTempSubdirectory("viper-native-naga-");
        readonly SettingsStore _store;
        public DeviceManager Manager { get; }
        public DeviceInfo Info => Assert.Single(Manager.Devices);
        public Output Output { get; } = new();
        public DeviceSession? Session { get; private set; }
        public Dictionary<string, ReportReader> Readers { get; } = [];

        public EmulatedNaga()
        {
            _store = new SettingsStore(_root.FullName);
            Manager = new DeviceManager(_store, Output)
            {
                FindNativeNagaNode = node => node.Interface switch
                {
                    0 => new(node.DevicePath, NagaReportKind.Tilt, 8),
                    1 => new(node.DevicePath, NagaReportKind.Dpi, 16),
                    _ => null,
                },
                OpenNativeNagaButtons = (node, onKey, onLost) =>
                {
                    var reader = new ReportReader(node, onKey, onLost);
                    Readers.Add(node.Path, reader);
                    return reader;
                },
                StartInputSession = (paths, engine, mode, _) =>
                {
                    Assert.Equal(InputPath, Assert.Single(paths));
                    return Session = new DeviceSession(engine, mode);
                },
            };
        }

        public void Refresh(params HidrawNode[] nodes) => Manager.Refresh(nodes,
            [new InputNode(InputPath, 0x1532, NagaChroma, "Naga Chroma", "usb-native-naga/input0")],
            _ => false, _ => throw new InvalidOperationException("Native Naga bindings require no feature transport."),
            _ => new DiscoveredControls([ThumbOne, KeyCodes.BtnLeft], "available"));

        public void Bind(params (ushort Source, ushort Target)[] bindings)
        {
            var settings = _store.LoadDevice(ProductKey);
            var keymap = Keymap.Empty(settings.ActiveKeymapOrFirst.Name);
            foreach (var (source, target) in bindings)
                keymap = keymap.With(source, new KeyboardBinding(target));
            _store.SaveDevice(ProductKey, settings with { Profiles = [settings.Active with { Keymaps = [keymap] }] });
            Manager.Apply(ProductKey);
        }

        public void Send(ushort source, int value) => Session!.Dispatch(InputPath,
            new InputEvent { Type = EventTypes.Key, Code = source, Value = value });

        public void Dispose()
        {
            try { Manager.Dispose(); }
            finally { _root.Delete(recursive: true); }
        }
    }

    sealed class ReportReader(NagaReportNode node, Action<ushort, int> onKey, Action<Exception> onLost) : IHidrawButtonReader
    {
        readonly NagaChromaButtons.Decoder _decoder = new(node, onKey);
        bool _started;
        public bool Disposed { get; private set; }

        public void Start() => _started = true;
        public void Dispose() => Disposed = true;
        public void Lose() => onLost(new IOException("Emulated hidraw disconnect."));

        public void MouseReport(byte buttons) => Read([buttons, 0, 0, 0, 0, 0, 0, 0]);

        public void VendorReport(params byte[] usages)
        {
            var report = new byte[16];
            report[0] = 0x04;
            usages.CopyTo(report, 1);
            Read(report);
        }

        void Read(ReadOnlySpan<byte> report)
        {
            Assert.True(_started);
            Assert.False(Disposed);
            _decoder.Read(report);
        }
    }

    sealed class Output : IInputSink
    {
        public List<(ushort Code, int Value)> Keys { get; } = [];
        public void Key(ushort code, int value) => Keys.Add((code, value));
        public void Relative(ushort axis, int value) { }
        public void Sync() { }
    }
}
