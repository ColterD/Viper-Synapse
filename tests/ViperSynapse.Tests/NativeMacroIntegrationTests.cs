using System.Text.Json;
using Microsoft.Extensions.Time.Testing;
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
        HeadlessApp.Session.Dispatch<bool>(async () =>
        {
            var root = Directory.CreateTempSubdirectory("viper-native-macros-");
            try
            {
                await VerifyNativeMacrosAsync(root.FullName, initialMode);
            }
            finally
            {
                root.Delete(recursive: true);
            }
            return true;
        }, CancellationToken.None);

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Evdev_macro_fallback_only_acts_on_the_ordinary_release_it_receives(bool earlyRelease)
    {
        var root = Directory.CreateTempSubdirectory("viper-macro-evdev-");
        try
        {
            var output = new Output();
            var store = new SettingsStore(root.FullName);
            DeviceSession? session = null;
            using var manager = new DeviceManager(store, output)
            {
                StartInputSession = (_, engine, mode, _) => session = new DeviceSession(engine, mode),
            };
            var macro = Macro.Create("M1", [new KeyStep(F1, true), new KeyStep(F1, false)]);
            manager.SetMacros(new MacroLibrary([macro]));
            ushort m1 = LegacyRemapControls.FirstMacroSource;
            var settings = store.LoadDevice(ProductKey);
            var keymap = settings.ActiveKeymapOrFirst.With(m1, new MacroBinding(macro.Id));
            store.SaveDevice(ProductKey, settings with { Profiles = [settings.Active with { Keymaps = [keymap] }] });
            manager.Refresh([], [new InputNode(InputPath, 0x1532, LegacyRemapControls.BlackWidowChromaV2, "BlackWidow", "usb-emulated/input0")],
                _ => false, _ => throw new InvalidOperationException(), _ => new DiscoveredControls([Q, m1], "available"));
            var input = Assert.IsType<DeviceSession>(session);
            Assert.False(input.HasNativeMacroKeys);
            SendKey(input, Q, 1);
            SendKey(input, m1, 1);
            SendKey(input, m1, 0);
            await output.WaitForReleaseAsync(F1);
            if (earlyRelease)
                SendKey(input, Q, 0);
            SendKey(input, Q, 2);
            Assert.Equal(!earlyRelease, output.Keys.Contains((Q, 2)));
            SendKey(input, Q, 0);
            Assert.Single(output.Keys, entry => entry == (Q, 0));
            var info = Assert.Single(JsonSerializer.Deserialize(manager.DevicesJson(), ClientJson.Default.ListDeviceInfo)!);
            var health = DeviceHealth.From(info, DeviceCatalogue.Default.Find(LegacyRemapControls.BlackWidowChromaV2), genericControls: true);
            Assert.Equal("Driver-dependent", Assert.Single(health.Features, feature => feature.Name == "M1–M5").Status);
        }
        finally { root.Delete(recursive: true); }
    }

    [Theory]
    [InlineData(false, false)]
    [InlineData(true, false)]
    [InlineData(true, true)]
    public async Task Native_start_failure_keeps_ordinary_input_and_refresh_retries(bool hasFallback, bool rejectModeRestore)
    {
        var root = Directory.CreateTempSubdirectory("viper-macro-start-");
        try
        {
            var output = new Output();
            var store = new SettingsStore(root.FullName);
            var firmware = new ModeFirmware(0) { RejectRegularMode = rejectModeRestore };
            bool rejectStartup = true;
            DeviceSession? session = null;
            MacroReader? reader = null;
            using var manager = new DeviceManager(store, output)
            {
                IsNativeMacroNode = node => node.DevicePath == MacroPath,
                StartInputSession = (_, engine, mode, _) => session = new DeviceSession(engine, mode),
                OpenNativeMacroKeys = (_, input, _) => reader = new MacroReader(input, () =>
                {
                    Assert.Equal<byte>(3, firmware.Mode);
                    if (rejectStartup)
                        throw new IOException("emulated macro reader start failure");
                }),
            };
            var macro = Macro.Create("M1", [new KeyStep(F1, true), new KeyStep(F1, false)]);
            manager.SetMacros(new MacroLibrary([macro]));
            ushort m1 = LegacyRemapControls.FirstMacroSource;
            var settings = store.LoadDevice(ProductKey);
            var keymap = settings.ActiveKeymapOrFirst.With(m1, new MacroBinding(macro.Id));
            store.SaveDevice(ProductKey, settings with { Profiles = [settings.Active with { Keymaps = [keymap] }] });
            const ushort pid = LegacyRemapControls.BlackWidowChromaV2;
            void Refresh(bool force = false) =>
                manager.Refresh([new(ControlPath, 0x1532, pid, 2, "BlackWidow", "usb-test/input2"),
                    new(MacroPath, 0x1532, pid, 1, "BlackWidow", "usb-test/input1")],
                    [new(InputPath, 0x1532, pid, "BlackWidow", "usb-test/input0")], node => node.DevicePath == ControlPath,
                    _ => new RazerTransport(firmware) { AnswerDelay = TimeSpan.Zero },
                    _ => new DiscoveredControls(hasFallback ? [Q, m1] : [Q], "available"), force);
            Refresh();

            var input = Assert.IsType<DeviceSession>(session);
            Assert.True(reader?.Disposed);
            Assert.Equal(new byte[] { 3, 0 }, firmware.ModeWrites);
            Assert.Equal<byte>(rejectModeRestore ? (byte)3 : (byte)0, firmware.Mode);
            var info = Assert.Single(manager.Devices);
            Assert.True(info.Remapping);
            Assert.False(info.NativeMacroKeys);
            const string startupError = "Could not start native macro keys: emulated macro reader start failure";
            Assert.Equal(rejectModeRestore
                ? startupError + " Could not clean up native macro mode: The BlackWidow did not confirm its requested device mode."
                : startupError, info.InputIssue);
            Assert.Equal(hasFallback, info.InputControls!.Contains(m1));
            SendKey(input, Q, 1);
            if (hasFallback)
            {
                SendKey(input, m1, 1);
                SendKey(input, m1, 0);
                await output.WaitForReleaseAsync(F1);
            }
            SendKey(input, Q, 0);
            Assert.Equal(hasFallback ? [(Q, 1), (F1, 1), (F1, 0), (Q, 0)] : new[] { (Q, 1), (Q, 0) }, output.Keys);

            rejectStartup = false;
            firmware.RejectRegularMode = false;
            Refresh();
            Assert.False(Assert.Single(manager.Devices).NativeMacroKeys);
            Refresh(force: true);
            Assert.True(Assert.Single(manager.Devices).NativeMacroKeys);
            Assert.Null(Assert.Single(manager.Devices).InputIssue);
            Assert.False(Assert.IsType<MacroReader>(reader).Disposed);
            manager.Dispose();
            Assert.Equal<byte>(0, firmware.Mode);
        }
        finally { root.Delete(recursive: true); }
    }

    static async Task VerifyNativeMacrosAsync(string root, byte initialMode)
    {
        var store = new SettingsStore(root);
        var firmware = new ModeFirmware(initialMode);
        var clock = new PlaybackClock();
        var output = new Output();
        DeviceSession? session = null;
        MacroReader? reader = null;
        ushort[] sources = Enumerable.Range(LegacyRemapControls.FirstMacroSource, 5)
            .Select(source => (ushort)source).ToArray();
        var descriptor = Directory.CreateDirectory(Path.Combine(root, Path.GetFileName(MacroPath), "device"));
        File.WriteAllBytes(Path.Combine(descriptor.FullName, "report_descriptor"), Convert.FromHexString("85047508950F8100"));
        Directory.CreateSymbolicLink(Path.Combine(descriptor.FullName, "driver"), "../../drivers/hid-generic");
        using var manager = new DeviceManager(store, output)
        {
            InputTimeProvider = clock,
            IsNativeMacroNode = node => BlackWidowMacroKeys.IsNativeReportNode(node, root),
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
        var macros = sources.Select((_, i) => Macro.Create($"M{i + 1}", i == 0
            ? [new KeyStep(Q, true), new DelayStep(100), new KeyStep(Q, false), new KeyStep(F1, true), new KeyStep(F1, false)]
            : [new KeyStep((ushort)(F1 + i), true), new KeyStep((ushort)(F1 + i), false)])).ToArray();
        manager.SetMacros(new MacroLibrary(macros));
        for (int i = 0; i < sources.Length; i++)
            keymap = keymap.With(sources[i], new MacroBinding(macros[i].Id));
        store.SaveDevice(ProductKey, settings with { Profiles = [settings.Active with { Keymaps = [keymap] }] });
        manager.Apply(ProductKey);

        var activeSession = Assert.IsType<DeviceSession>(session);
        var activeReader = Assert.IsType<MacroReader>(reader);
        Assert.True(activeSession.HasNativeMacroKeys);
        Assert.True(Assert.Single(manager.Devices).Remapping);
        Assert.Equal<byte>(0x03, firmware.Mode);
        Assert.Equal(initialMode == 0x00 ? new byte[] { 0x03 } : [], firmware.ModeWrites);

        await clock.DelayRegistered.Task.WaitAsync(TimeSpan.FromSeconds(3), TestContext.Current.CancellationToken);
        activeReader.Read(0x14, 0x20, 0xe0);
        activeReader.Read(0x20);
        SendKey(activeSession, sources[0], 1);
        activeReader.Read();
        SendKey(activeSession, sources[0], 0);
        SendKey(activeSession, Q, 2);
        // The recorder sees the macro's attempted down on an already held key. Neither
        // producer releases Q until the physical key goes up, and the repeat still passes.
        Assert.Equal(new[] { (Ctrl, 1), (Q, 1), (Q, 1), (Q, 2) }, output.Keys);
        clock.Advance(TimeSpan.FromMilliseconds(100));
        await output.WaitForReleaseAsync(F1);
        Assert.Equal(new[] { (Ctrl, 1), (Q, 1), (Q, 1), (Q, 2), (F1, 1), (F1, 0) }, output.Keys);
        var expected = new List<(ushort Code, int Value)> { (Ctrl, 1), (Q, 1), (Q, 1), (Q, 2), (F1, 1), (F1, 0) };
        for (int i = 1; i < sources.Length; i++)
        {
            byte usage = (byte)(0x20 + i);
            activeReader.Read(0x14, usage, 0xe0);
            activeReader.Read(usage);
            SendKey(activeSession, sources[i], 1);
            activeReader.Read();
            SendKey(activeSession, sources[i], 0);
            await output.WaitForReleaseAsync((ushort)(F1 + i));
            expected.Add(((ushort)(F1 + i), 1));
            expected.Add(((ushort)(F1 + i), 0));
            SendKey(activeSession, Q, 2);
            expected.Add((Q, 2));
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

    sealed class PlaybackClock : FakeTimeProvider
    {
        public TaskCompletionSource DelayRegistered { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);

        public override ITimer CreateTimer(TimerCallback callback, object? state, TimeSpan dueTime, TimeSpan period)
        {
            var timer = base.CreateTimer(callback, state, dueTime, period);
            DelayRegistered.TrySetResult();
            return timer;
        }
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
        public bool RejectRegularMode { get; set; }
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
            byte mode = request.Arguments[0];
            ModeWrites.Add(mode);
            if (!RejectRegularMode || mode != 0)
                Mode = mode;
            return [Mode, 0];
        }

        public void Dispose() { }
    }

    sealed class Output : IInputSink
    {
        readonly List<(ushort Code, int Value)> _keys = [];
        readonly System.Collections.Concurrent.ConcurrentDictionary<ushort, TaskCompletionSource> _releases = new();
        public (ushort Code, int Value)[] Keys { get { lock (_keys) return _keys.ToArray(); } }
        public void Key(ushort code, int value)
        {
            lock (_keys) _keys.Add((code, value));
            if (value == 0)
                Released(code).TrySetResult();
        }
        TaskCompletionSource Released(ushort code) => _releases.GetOrAdd(code, _ => new(TaskCreationOptions.RunContinuationsAsynchronously));
        public Task WaitForReleaseAsync(ushort code) => Released(code).Task.WaitAsync(TimeSpan.FromSeconds(3), TestContext.Current.CancellationToken);
        public void Relative(ushort axis, int value) { }
        public void Sync() { }
    }
}
