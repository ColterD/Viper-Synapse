using System.Text;
using ViperSynapse.Core.Engine;
using ViperSynapse.Core.Input;
using ViperSynapse.Core.Protocol;
using ViperSynapse.Hardware;
using ViperSynapse.Hardware.Linux;
using ViperSynapse.Service;

namespace ViperSynapse.DesktopSmoke;

sealed class EmulatedDevices(string root) : IDisposable
{
    readonly DeviceManager _manager = new(new SettingsStore(Path.Combine(root, "profiles")), new InputSink());

    public string Profiles => Path.Combine(root, "profiles", "devices");

    public DaemonBus CreateBus() => new(_manager, () => [], () => { });

    public void Refresh(bool includeNaga)
    {
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

        var nodes = units.Select(u => new HidrawNode(Path.Combine(root, u.Port), 0x1532, u.Product,
            u.Interface, u.Name, $"usb-smoke-{u.Port}/input{u.Interface}")).ToArray();
        var serials = units.ToDictionary(u => Path.Combine(root, u.Port), u => u.Serial);
        var inputs = nodes.Where(node => node.ProductId != 0x00e3).Select(node =>
            new InputNode(node.DevicePath + "-evdev", node.VendorId, node.ProductId, node.Name, node.PortPath + "/input0"));

        _manager.Refresh(nodes, inputs, _ => true,
            path => new RazerTransport(new FeatureDevice(path, serials[path])) { AnswerDelay = TimeSpan.Zero },
            paths => paths.Any()
                ? new DiscoveredControls([KeyCodes.BtnLeft], "available")
                : new DiscoveredControls([], "unavailable"));
    }

    public void Dispose() => _manager.Dispose();

    sealed record EmulatedUnit(ushort Product, int Interface, string Name, string Port, string Serial);

    sealed class FeatureDevice(string path, string serial) : IHidFeatureDevice
    {
        RazerReport? _pending;

        public string Path => path;

        public void SendFeature(ReadOnlySpan<byte> report) => _pending = RazerReport.Parse(report[1..]);

        public int ReceiveFeature(Span<byte> report)
        {
            var command = _pending ?? throw new InvalidOperationException("No emulated request is pending.");
            byte[] data = (command.CommandClass, command.CommandId) switch
            {
                (0x00, 0x82) => Encoding.ASCII.GetBytes(serial),
                (0x00, 0x81) => [1, 2],
                (0x05, 0x81) => [5, 1, 2, 0, 0, 0, 0, 0, 0, 0],
                (0x05, 0x84) => [1],
                _ => command.Arguments[..command.DataSize].ToArray(),
            };
            RazerReport.Create(command.TransactionId, command.CommandClass, command.CommandId, data).Bytes.CopyTo(report[1..]);
            report[1] = (byte)ReportStatus.Successful;
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
