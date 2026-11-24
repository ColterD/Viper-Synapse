using System.Text;
using ViperSynapse.Core.Bindings;
using ViperSynapse.Core.Engine;
using ViperSynapse.Core.Devices;
using ViperSynapse.Core.Input;
using ViperSynapse.Core.Lighting;
using ViperSynapse.Core.Protocol;
using ViperSynapse.Hardware;
using ViperSynapse.Hardware.Linux;
using ViperSynapse.Service;

namespace ViperSynapse.DesktopSmoke;

sealed class EmulatedDevices : IDisposable
{
    readonly string _root;
    readonly DeviceManager _manager;
    readonly DpiFixture _dpi = new();
    NagaReader? _nagaReader;
    bool _includeNaga;

    public EmulatedDevices(string root)
    {
        _root = root;
        _manager = new DeviceManager(new SettingsStore(Path.Combine(root, "profiles")), new InputSink())
        {
            FindNativeNagaNode = node => node.Interface == 1 && node.ProductId == LegacyRemapControls.NagaChroma
                ? new(node.DevicePath, NagaReportKind.Dpi, NagaChromaButtons.VendorReportLength) : null,
            OpenNativeNagaButtons = (node, input, _) => _nagaReader = new NagaReader(node, input),
            StartInputSession = (_, engine, stick, _) => new DeviceSession(engine, stick),
        };
    }

    public string Profiles => Path.Combine(_root, "profiles", "devices");
    public void PressDpi(bool up) => (_nagaReader ?? throw new InvalidOperationException("No emulated Naga reader.")).Press(up);
    public void RejectDpiWrites(bool reject) => _dpi.Reject = reject;
    public void SimulateFirmwareDpi(int x, int y) => _dpi.Current = new(x, y);

    public DaemonBus CreateBus() => new(_manager, () => [], () => { })
    {
        RefreshDiscovery = () => Refresh(_includeNaga, forceControlRetry: true),
    };

    public void SetProfiles(string key, DeviceSettings profiles) => _manager.SetSettings(key, profiles);

    public void FocusGame(string executable) => _manager.Focused(new FocusedProgram(executable, null));

    public void Refresh(bool includeNaga, bool forceControlRetry = false)
    {
        _includeNaga = includeNaga;
        var units = new List<EmulatedUnit>
        {
            new(0x00e2, 3, "Basilisk wired", "cable", "WIR01"),
            new(0x00e3, 3, "Basilisk receiver", "receiver", "HYP02"),
            new(0x02eb, 0, "Tartarus Pro", "keypad", "STK04"),
        };
        if (includeNaga)
            units.Add(new(0x0053, 0, "Naga Chroma", "naga", "NGA03"));

        // Unknown Razer devices still get separate tabs and let the test overflow the visible strip.
        for (int i = 0; i < 6; i++)
            units.Add(new((ushort)(0xFA00 + i), 0, $"Emulated device with a long name {i}", $"other-{i}", $"EX{i}"));

        var nodes = units.Select(u => new HidrawNode(Path.Combine(_root, u.Port), 0x1532, u.Product,
            u.Interface, u.Name, $"usb-smoke-{u.Port}/input{u.Interface}")).ToArray();
        var serials = units.ToDictionary(u => Path.Combine(_root, u.Port), u => u.Serial);
        var inputs = nodes.Where(node => node.ProductId != 0x00e3).Select(node =>
            new InputNode(node.DevicePath + "-evdev", node.VendorId, node.ProductId, node.Name, node.PortPath + "/input0"));

        var hidraw = includeNaga ? nodes.Append(new HidrawNode(Path.Combine(_root, "naga-vendor"), 0x1532,
            LegacyRemapControls.NagaChroma, 1, "Naga Chroma", "usb-smoke-naga/input1")) : nodes;
        _manager.Refresh(hidraw, inputs, node => serials.ContainsKey(node.DevicePath),
            path => new RazerTransport(new FeatureDevice(path, serials[path], serials[path] == "NGA03" ? _dpi : null)) { AnswerDelay = TimeSpan.Zero },
            paths => paths.Any()
                ? new DiscoveredControls([KeyCodes.BtnLeft], "available")
                : new DiscoveredControls([], "unavailable"), forceControlRetry);
    }

    public void Dispose() => _manager.Dispose();

    sealed record EmulatedUnit(ushort Product, int Interface, string Name, string Port, string Serial);

    sealed class DpiFixture
    {
        public DpiStage Current { get; set; } = new(800, 800);
        public volatile bool Reject;
    }

    sealed class NagaReader(NagaReportNode node, Action<ushort, int> input) : IHidrawButtonReader
    {
        readonly NagaChromaButtons.Decoder _decoder = new(node, input);
        public void Start() { }
        public void Press(bool up)
        {
            var report = new byte[NagaChromaButtons.VendorReportLength];
            report[0] = 0x04;
            report[1] = up ? (byte)0x20 : (byte)0x21;
            _decoder.Read(report);
            report[1] = 0;
            _decoder.Read(report);
        }
        public void Dispose() { }
    }

    sealed class FeatureDevice(string path, string serial, DpiFixture? dpi) : IHidFeatureDevice
    {
        RazerReport? _pending;

        public string Path => path;

        public void SendFeature(ReadOnlySpan<byte> report) => _pending = RazerReport.Parse(report[1..]);

        public int ReceiveFeature(Span<byte> report)
        {
            var command = _pending ?? throw new InvalidOperationException("No emulated request is pending.");
            bool rejected = dpi?.Reject == true && (command.CommandClass, command.CommandId) == (0x04, 0x05);
            if (!rejected && dpi != null && (command.CommandClass, command.CommandId) == (0x04, 0x05))
            {
                var args = command.Arguments;
                dpi.Current = new((args[1] << 8) | args[2], (args[3] << 8) | args[4]);
            }
            byte[] data = (command.CommandClass, command.CommandId) switch
            {
                (0x00, 0x82) => Encoding.ASCII.GetBytes(serial),
                (0x00, 0x81) => [1, 2],
                (0x05, 0x81) => [5, 1, 2, 0, 0, 0, 0, 0, 0, 0],
                (0x05, 0x84) => [1],
                (0x04, 0x85) when dpi != null => [0, (byte)(dpi.Current.X >> 8), (byte)dpi.Current.X,
                    (byte)(dpi.Current.Y >> 8), (byte)dpi.Current.Y, 0, 0],
                _ => command.Arguments[..command.DataSize].ToArray(),
            };
            RazerReport.Create(command.TransactionId, command.CommandClass, command.CommandId, data).Bytes.CopyTo(report[1..]);
            report[1] = (byte)(rejected ? ReportStatus.Failure : ReportStatus.Successful);
            return report.Length;
        }

        public void Dispose() { }
    }

    sealed class InputSink : IInputSink
    {
        public void Key(ushort code, int value) { }
        public void Relative(ushort axis, int value) { }
        public void Sync() { }
    }
}
