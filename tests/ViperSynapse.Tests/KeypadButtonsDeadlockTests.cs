using System.Reflection;
using System.Runtime.InteropServices;
using System.Collections;
using ViperSynapse.Core.Bindings;
using ViperSynapse.Core.Engine;
using ViperSynapse.Core.Input;
using ViperSynapse.Hardware;
using ViperSynapse.Hardware.Linux;
using ViperSynapse.Service;

namespace ViperSynapse.Tests;

public class KeypadButtonsDeadlockTests
{
    [DllImport("libc", SetLastError = true)]
    static extern int mkfifo(string path, uint mode);

    [DllImport("libc", SetLastError = true)]
    static extern int open(string path, int flags);

    [DllImport("libc", SetLastError = true)]
    static extern int close(int fd);

    static int CreateListeningNode(string path)
    {
        Assert.Equal(0, mkfifo(path, 0x180)); // 0600
        int writer = open(path, 2 | 0x800); // O_RDWR | O_NONBLOCK
        Assert.True(writer >= 0);
        return writer;
    }

    static IDictionary Devices(DeviceManager manager) => (IDictionary)typeof(DeviceManager)
        .GetField("_devices", BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(manager)!;

    sealed class Sink : IInputSink, IEngineHost
    {
        public List<(ushort Code, int Value)> Events { get; } = [];
        public void Key(ushort code, int value) => Events.Add((code, value));
        public void Relative(ushort axis, int value) { }
        public void Sync() { }
        public void Launch(string command) { }
        public void Open(string target) { }
        public void SwitchProfile(CycleDirection direction, Guid? profileId) { }
        public void SwitchKeymap(CycleDirection direction, int keymap) { }
        public Macro? FindMacro(Guid id) => null;
        public void Gamepad(GamepadControl control, bool down) { }
        public ShortcutList Shortcuts => ShortcutList.Empty;
        public void Report(Exception error) => throw error;
    }

    [Fact]
    public void A_stale_callback_does_not_modify_a_stopped_session()
    {
        var sink = new Sink();
        var engine = new RemapEngine(sink, sink);
        var session = DeviceSession.Start([], engine);
        session.Dispose();

        session.WithEngine(e => e.HypershiftButton(true));

        Assert.False(engine.HypershiftActive);
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public void Physical_hypershift_state_survives_the_gap_between_sessions(bool stillHeld)
    {
        string root = Path.Combine(Path.GetTempPath(), "viper-keypad-gap-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        string nodePath = Path.Combine(root, "hidraw-test");
        int writer = CreateListeningNode(nodePath);
        try
        {
            var sink = new Sink();
            using var manager = new DeviceManager(new SettingsStore(root), sink);
            var type = typeof(DeviceManager).GetNestedType("Connected", BindingFlags.NonPublic)!;
            var device = Activator.CreateInstance(type, "usb-test", (ushort)0x02eb, "Tartarus")!;
            Devices(manager).Add("usb-test", device);
            var node = new HidrawNode(nodePath, 0x1532, 0x02eb, 1, "Tartarus", "usb-test/input1");
            typeof(DeviceManager).GetMethod("StartButtons", BindingFlags.Instance | BindingFlags.NonPublic)!.Invoke(manager, [device, node]);
            var buttons = (KeypadButtons)type.GetProperty("Buttons")!.GetValue(device)!;
            try
            {
                // Both reports arrive while the previous session is gone and the replacement is not yet created.
                buttons.Handle([0x04, 0x01]);
                if (!stillHeld)
                    buttons.Handle([0x04, 0x00]);

                var engine = new RemapEngine(sink, sink)
                {
                    Keymap = Keymap.Empty("k").With(30, new KeyboardBinding(48)).With(30, new KeyboardBinding(46), hypershift: true),
                };
                type.GetProperty("Engine")!.SetValue(device, engine);
                using var session = DeviceSession.Start([], engine);
                type.GetProperty("Session")!.SetValue(device, session);
                Assert.Equal(stillHeld, engine.HypershiftActive);
                engine.Key(30, 1);
                engine.Key(30, 0);
                Assert.Contains((stillHeld ? (ushort)46 : (ushort)48, 1), sink.Events);

                // A report in the replacement session is observed without waiting for another event.
                buttons.Handle([0x04, 0x00]);
                Assert.False(engine.HypershiftActive);
            }
            finally { Devices(manager).Remove("usb-test"); buttons.Dispose(); }
        }
        finally { close(writer); Directory.Delete(root, recursive: true); }
    }

    [Fact]
    public void Physical_and_keyboard_hypershift_sources_are_additive()
    {
        string root = Path.Combine(Path.GetTempPath(), "viper-keypad-additive-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        string nodePath = Path.Combine(root, "hidraw-test");
        int writer = CreateListeningNode(nodePath);
        try
        {
            var sink = new Sink();
            using var manager = new DeviceManager(new SettingsStore(root), sink);
            var type = typeof(DeviceManager).GetNestedType("Connected", BindingFlags.NonPublic)!;
            var device = Activator.CreateInstance(type, "usb-test", (ushort)0x02eb, "Tartarus")!;
            Devices(manager).Add("usb-test", device);
            var engine = new RemapEngine(sink, sink) { Keymap = Keymap.Empty("k").With(30, new HypershiftBinding()) };
            type.GetProperty("Engine")!.SetValue(device, engine);
            using var session = DeviceSession.Start([], engine);
            type.GetProperty("Session")!.SetValue(device, session);
            var node = new HidrawNode(nodePath, 0x1532, 0x02eb, 1, "Tartarus", "usb-test/input1");
            typeof(DeviceManager).GetMethod("StartButtons", BindingFlags.Instance | BindingFlags.NonPublic)!.Invoke(manager, [device, node]);
            var buttons = (KeypadButtons)type.GetProperty("Buttons")!.GetValue(device)!;
            try
            {
                engine.Key(30, 1);
                buttons.Handle([0x04, 0x01]);
                engine.Key(30, 0);
                Assert.True(engine.HypershiftActive);
                engine.Key(30, 1);
                buttons.Handle([0x04, 0x00]);
                Assert.True(engine.HypershiftActive);
                engine.Key(30, 0);
                Assert.False(engine.HypershiftActive);
            }
            finally { Devices(manager).Remove("usb-test"); buttons.Dispose(); }
        }
        finally { close(writer); Directory.Delete(root, recursive: true); }
    }

    [Fact]
    public void Hypershift_report_does_not_wait_for_manager_lock_while_listener_is_stopping()
    {
        string root = Path.Combine(Path.GetTempPath(), "viper-keypad-lock-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        string nodePath = Path.Combine(root, "hidraw-test");
        int writer = CreateListeningNode(nodePath);
        try
        {
            var sink = new Sink();
            var engine = new RemapEngine(sink, sink);
            using var session = DeviceSession.Start([], engine);
            using var manager = new DeviceManager(new SettingsStore(root), sink);
            var type = typeof(DeviceManager).GetNestedType("Connected", BindingFlags.NonPublic)!;
            var device = Activator.CreateInstance(type, "usb-test", (ushort)0x02eb, "Tartarus")!;
            Devices(manager).Add("usb-test", device);
            type.GetProperty("Engine")!.SetValue(device, engine);
            type.GetProperty("Session")!.SetValue(device, session);
            var node = new HidrawNode(nodePath, 0x1532, 0x02eb, 1, "Tartarus", "usb-test/input1");
            typeof(DeviceManager).GetMethod("StartButtons", BindingFlags.Instance | BindingFlags.NonPublic)!.Invoke(manager, [device, node]);
            var buttons = (KeypadButtons)type.GetProperty("Buttons")!.GetValue(device)!;
            try
            {
                var gate = (Lock)typeof(DeviceManager).GetField("_gate", BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(manager)!;
                using var started = new ManualResetEventSlim();
                var handler = new Thread(() => { started.Set(); buttons.Handle([0x04, 0x01]); }) { IsBackground = true };
                bool finished;
                lock (gate)
                {
                    handler.Start();
                    Assert.True(started.Wait(TimeSpan.FromSeconds(3), TestContext.Current.CancellationToken));
                    finished = handler.Join(TimeSpan.FromMilliseconds(500));
                }
                Assert.True(handler.Join(TimeSpan.FromSeconds(3)));
                Assert.True(finished, "The Hypershift listener waited on the manager lock; disposing it while holding the lock can deadlock.");
                Assert.True(engine.HypershiftActive);
                buttons.Handle([0x04, 0x00]);
                Assert.False(engine.HypershiftActive);
            }
            finally { Devices(manager).Remove("usb-test"); buttons.Dispose(); }
        }
        finally { close(writer); Directory.Delete(root, recursive: true); }
    }
}
