using System.Text.Json;
using ViperSynapse.App.Services;
using ViperSynapse.App.ViewModels;
using ViperSynapse.Core.Bindings;
using ViperSynapse.Core.Devices;
using ViperSynapse.Core.Engine;
using ViperSynapse.Core.Input;
using ViperSynapse.Core.Protocol;
using ViperSynapse.Hardware;
using ViperSynapse.Hardware.Linux;
using DeviceManager = ViperSynapse.Service.DeviceManager;
using SettingsStore = ViperSynapse.Service.SettingsStore;

namespace ViperSynapse.Tests;

public class NativeMacroIntegrationTests
{
    const string InputPath = "/dev/input/emulated-blackwidow";
    const string MacroPath = "/dev/hidraw-emulated-macro";
    const string ControlPath = "/dev/hidraw-emulated-control";
    const string ProductKey = "0221";
    const ushort Q = 16;
    const ushort Ctrl = KeyCodes.LeftCtrl;
    const ushort F1 = 59;

    [Theory]
    [InlineData(0x00)]
    [InlineData(0x03)]
    public Task Native_macros_are_editable_and_leave_ordinary_holds_alone(byte initialMode) =>
        HeadlessApp.Session.Dispatch(() =>
        {
            var root = Directory.CreateTempSubdirectory("viper-native-macros-");
            try
            {
                VerifyNativeMacros(root.FullName, initialMode);
            }
            finally
            {
                root.Delete(recursive: true);
            }
        }, CancellationToken.None);

    static void VerifyNativeMacros(string root, byte initialMode)
    {
        var store = new SettingsStore(root);
        var firmware = new ModeFirmware(initialMode);
        var output = new Output();
        DeviceSession? session = null;
        MacroReader? reader = null;
        ushort[] sources = Enumerable.Range(LegacyRemapControls.FirstMacroSource, 5)
            .Select(source => (ushort)source).ToArray();
        using var manager = new DeviceManager(store, output)
        {
            IsNativeMacroNode = node => node.DevicePath == MacroPath,
            OpenNativeMacroKeys = (path, onKey, _) =>
            {
                Assert.Equal(MacroPath, path);
                Assert.Null(reader);
                return reader = new MacroReader(onKey, () => Assert.Equal<byte>(0x03, firmware.Mode));
            },
            StartInputSession = (paths, engine, mode, _) =>
            {
                Assert.Equal(InputPath, Assert.Single(paths));
                Assert.Null(session);
                session = new DeviceSession(engine, mode);
                SendKey(session, Ctrl, 1);
                SendKey(session, Q, 1);
                return session;
            },
        };
        const ushort pid = LegacyRemapControls.BlackWidowChromaV2;
        var input = new InputNode(InputPath, 0x1532, pid, "BlackWidow Chroma V2", "usb-native-test/input0");
        var control = new HidrawNode(ControlPath, 0x1532, pid, 2, input.Name, "usb-native-test/input2");
        var macro = new HidrawNode(MacroPath, 0x1532, pid, 1, input.Name, "usb-native-test/input1");
        manager.Refresh([control, macro], [input], node => node.DevicePath == ControlPath,
            path =>
            {
                Assert.Equal(ControlPath, path);
                return new RazerTransport(firmware) { AnswerDelay = TimeSpan.Zero };
            }, _ => new DiscoveredControls([Q], "available"));

        Assert.Equal(new[] { Q }.Concat(sources), Assert.Single(manager.Devices).InputControls);
        Assert.False(Assert.Single(manager.Devices).Remapping);
        Assert.Null(session);
        Assert.Null(reader);
        Assert.Empty(firmware.ModeWrites);

        var info = Assert.Single(JsonSerializer.Deserialize(manager.DevicesJson(), ClientJson.Default.ListDeviceInfo)!);
        var main = new MainViewModel(new DaemonClient(), new AppPreferences(), false);
        var device = new DeviceViewModel(info, main);
        var rows = device.Rows.Where(row => row.Key.Group == "macro keys").ToArray();
        Assert.Equal(new[] { "M1", "M2", "M3", "M4", "M5" }, rows.Select(row => row.Key.Label));
        Assert.Equal(sources, rows.Select(row => row.Key.Source));
        Assert.All(rows, row => Assert.True(row.Bindable));

        var settings = store.LoadDevice(ProductKey);
        var keymap = settings.ActiveKeymapOrFirst;
        for (int i = 0; i < sources.Length; i++)
            keymap = keymap.With(sources[i], new KeyboardBinding((ushort)(F1 + i)));
        store.SaveDevice(ProductKey, settings with { Profiles = [settings.Active with { Keymaps = [keymap] }] });
        manager.Apply(ProductKey);

        var activeSession = Assert.IsType<DeviceSession>(session);
        var activeReader = Assert.IsType<MacroReader>(reader);
        Assert.True(activeSession.HasNativeMacroKeys);
        Assert.True(Assert.Single(manager.Devices).Remapping);
        Assert.Equal<byte>(0x03, firmware.Mode);
        Assert.Equal(initialMode == 0x00 ? new byte[] { 0x03 } : [], firmware.ModeWrites);

        Assert.Equal(new[] { (Ctrl, 1), (Q, 1), (F1, 1) }, output.Keys);
        var expected = new List<(ushort Code, int Value)> { (Ctrl, 1), (Q, 1) };
        for (int i = 0; i < sources.Length; i++)
        {
            byte usage = (byte)(0x20 + i);
            activeReader.Read(0x14, usage, 0xe0);
            activeReader.Read(usage);
            SendKey(activeSession, sources[i], 1);
            activeReader.Read();
            SendKey(activeSession, sources[i], 0);
            expected.Add(((ushort)(F1 + i), 1));
            expected.Add(((ushort)(F1 + i), 0));
        }
        Assert.Equal(expected, output.Keys);
        SendKey(activeSession, Q, 0);
        SendKey(activeSession, Ctrl, 0);
        expected.Add((Q, 0));
        expected.Add((Ctrl, 0));
        Assert.Equal(expected, output.Keys);

        store.SaveDevice(ProductKey, settings);
        manager.Apply(ProductKey);
        Assert.True(activeReader.Disposed);
        Assert.False(Assert.Single(manager.Devices).Remapping);
        Assert.Equal(initialMode, firmware.Mode);
        Assert.Equal(initialMode == 0x00 ? new byte[] { 0x03, 0x00 } : [], firmware.ModeWrites);
        Assert.Equal(expected, output.Keys);
    }

    static void SendKey(DeviceSession session, ushort source, int value)
    {
        session.Dispatch(InputPath, new InputEvent { Type = EventTypes.Key, Code = source, Value = value });
        session.Dispatch(InputPath, new InputEvent { Type = EventTypes.Syn, Code = EventTypes.SynReport });
    }

    sealed class MacroReader(Action<ushort, int> onKey, Action verifyMode) : IHidrawButtonReader
    {
        readonly BlackWidowMacroKeys.Decoder _decoder = new(onKey);
        public bool Disposed { get; private set; }

        public void Start()
        {
            verifyMode();
            Read(0x20);
        }

        public void Read(params byte[] usages)
        {
            var report = new byte[16];
            report[0] = 0x04;
            usages.CopyTo(report, 1);
            _decoder.Read(report);
        }

        public void Dispose() => Disposed = true;
    }

    sealed class ModeFirmware(byte initialMode) : IHidFeatureDevice
    {
        RazerReport? _request;
        public string Path => ControlPath;
        public byte Mode { get; private set; } = initialMode;
        public List<byte> ModeWrites { get; } = [];

        public void SendFeature(ReadOnlySpan<byte> report) => _request = RazerReport.Parse(report[1..]);

        public int ReceiveFeature(Span<byte> report)
        {
            var request = _request!;
            byte[]? arguments = (request.CommandClass, request.CommandId) switch
            {
                (0x00, 0x82) => "BW-EMULATED"u8.ToArray(),
                (0x00, 0x81) => [1, 0],
                (0x00, 0x84) => ReadMode(request),
                (0x00, 0x04) => WriteMode(request),
                _ => null,
            };
            var reply = RazerReport.Create(request.TransactionId, request.CommandClass, request.CommandId, arguments ?? [])
                .Bytes.ToArray();
            reply[0] = (byte)(arguments != null ? ReportStatus.Successful : ReportStatus.NotSupported);
            reply.CopyTo(report[1..]);
            return report.Length;
        }

        byte[] ReadMode(RazerReport request)
        {
            Assert.Equal<byte>(0xff, request.TransactionId);
            return [Mode, 0];
        }

        byte[] WriteMode(RazerReport request)
        {
            Assert.Equal<byte>(0xff, request.TransactionId);
            Assert.Equal<byte>(2, request.DataSize);
            Assert.Equal<byte>(0, request.Arguments[1]);
            Mode = request.Arguments[0];
            ModeWrites.Add(Mode);
            return [Mode, 0];
        }

        public void Dispose() { }
    }

    sealed class Output : IInputSink
    {
        public List<(ushort Code, int Value)> Keys { get; } = [];
        public void Key(ushort code, int value) => Keys.Add((code, value));
        public void Relative(ushort axis, int value) { }
        public void Sync() { }
    }
}
