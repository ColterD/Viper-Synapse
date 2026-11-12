using ViperSynapse.Core.Bindings;
using ViperSynapse.Core.Engine;
using ViperSynapse.Core.Input;
using ViperSynapse.Hardware;
using ViperSynapse.Service;

namespace ViperSynapse.Tests;

public class DeviceRefreshTests
{
    sealed class Sink : IInputSink, IEngineHost
    {
        public List<string> Events { get; } = [];
        public bool FailRelease { get; set; }
        public void Key(ushort code, int value)
        {
            if (FailRelease && value == 0)
                throw new IOException("test output failure");
            Events.Add($"{code}:{value}");
        }
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
    public void Refresh_keeps_a_held_binding_active_on_an_unchanged_session()
    {
        var sink = new Sink();
        var keymap = Keymap.Empty("k").With(30, new KeyboardBinding(46));
        var engine = new RemapEngine(sink, sink) { Keymap = keymap };
        using var session = DeviceSession.Start([], engine);
        engine.Key(30, 1);

        DeviceManager.UpdateSession(session, keymap, MacroInterruption.DedicatedKey, 1, true);

        Assert.Equal(["46:1"], sink.Events);
        engine.Key(30, 0);
        Assert.Equal(["46:1", "46:0"], sink.Events);
    }

    [Fact]
    public void Explicit_settings_change_resets_held_bindings_and_changes_keymap()
    {
        var sink = new Sink();
        var keymap = Keymap.Empty("k").With(30, new KeyboardBinding(46));
        var next = Keymap.Empty("k").With(30, new KeyboardBinding(48));
        var engine = new RemapEngine(sink, sink) { Keymap = keymap };
        using var session = DeviceSession.Start([], engine);
        engine.Key(30, 1);

        DeviceManager.UpdateSession(session, next, MacroInterruption.DedicatedKey, 1, false);
        engine.Key(30, 1);
        engine.Key(30, 0);

        Assert.Equal(["46:1", "46:0", "48:1", "48:0"], sink.Events);
    }

    [Fact]
    public void An_output_failure_on_one_session_does_not_block_other_devices()
    {
        string root = Path.Combine(Path.GetTempPath(), "viper-refresh-test-" + Guid.NewGuid().ToString("N"));
        try
        {
            var store = new SettingsStore(root);
            var settings = DeviceSettings.Create();
            var keymap = Keymap.Empty("k").With(30, new KeyboardBinding(46));
            store.SaveDevice("ffff", settings with { Profiles = [settings.Active with { Keymaps = [keymap] }] });
            var first = new Sink();
            var second = new Sink();
            var firstEngine = new RemapEngine(first, first) { Keymap = keymap };
            var secondEngine = new RemapEngine(second, second) { Keymap = keymap };
            using var manager = new DeviceManager(store, new Sink());
            AddSession(manager, "usb-first", firstEngine);
            AddSession(manager, "usb-second", secondEngine);
            firstEngine.Key(30, 1);
            secondEngine.Key(30, 1);
            first.FailRelease = true;

            manager.Apply("ffff");

            Assert.Contains("46:0", second.Events);
            Assert.Contains(manager.Devices, d => d.Port == "usb-first" && d.Problem != null);
        }
        finally
        {
            if (Directory.Exists(root)) Directory.Delete(root, recursive: true);
        }
    }

    [Fact]
    public void A_failed_session_release_does_not_block_stopping_other_devices()
    {
        string root = Path.Combine(Path.GetTempPath(), "viper-refresh-test-" + Guid.NewGuid().ToString("N"));
        try
        {
            var store = new SettingsStore(root);
            store.SaveDevice("ffff", DeviceSettings.Create());
            var first = new Sink();
            var second = new Sink();
            var keymap = Keymap.Empty("k").With(30, new KeyboardBinding(46));
            var firstEngine = new RemapEngine(first, first) { Keymap = keymap };
            var secondEngine = new RemapEngine(second, second) { Keymap = keymap };
            using var manager = new DeviceManager(store, new Sink());
            AddSession(manager, "usb-first", firstEngine);
            AddSession(manager, "usb-second", secondEngine);
            firstEngine.Key(30, 1);
            secondEngine.Key(30, 1);
            first.FailRelease = true;

            manager.Apply("ffff");

            Assert.Contains("46:0", second.Events);
            Assert.All(manager.Devices, d => Assert.False(d.Remapping));
            Assert.Contains(manager.Devices, d => d.Port == "usb-first" && d.Problem != null);
        }
        finally
        {
            if (Directory.Exists(root)) Directory.Delete(root, recursive: true);
        }
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void Input_thread_reads_snapshots_without_waiting_for_the_device_manager_lock(bool readMacros)
    {
        string root = Path.Combine(Path.GetTempPath(), "viper-refresh-test-" + Guid.NewGuid().ToString("N"));
        try
        {
            using var manager = new DeviceManager(new SettingsStore(root), new Sink());
            var hostType = typeof(DeviceManager).GetNestedType("Host", System.Reflection.BindingFlags.NonPublic)!;
            var host = (IEngineHost)Activator.CreateInstance(hostType, manager, "ffff")!;
            var engine = new RemapEngine(new Sink(), host);
            using var session = DeviceSession.Start([], engine);
            using var entered = new ManualResetEventSlim();
            var thread = new Thread(() => session.WithEngine(unused =>
            {
                entered.Set();
                if (readMacros) _ = host.FindMacro(Guid.Empty);
                else _ = host.Shortcuts;
            })) { IsBackground = true };
            var gate = (Lock)typeof(DeviceManager).GetField("_gate", System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic)!.GetValue(manager)!;
            bool finished;
            lock (gate)
            {
                thread.Start();
                Assert.True(entered.Wait(TimeSpan.FromSeconds(3), TestContext.Current.CancellationToken));
                finished = thread.Join(TimeSpan.FromMilliseconds(500));
            }
            thread.Join(TimeSpan.FromSeconds(3));
            Assert.True(finished, "The input thread waited on the manager lock while holding the session lock.");
        }
        finally
        {
            if (Directory.Exists(root)) Directory.Delete(root, recursive: true);
        }
    }

    static void AddSession(DeviceManager manager, string port, RemapEngine engine)
    {
        var connected = typeof(DeviceManager).GetNestedType("Connected", System.Reflection.BindingFlags.NonPublic)!;
        var instance = Activator.CreateInstance(connected, port, (ushort)0xffff, "Test")!;
        connected.GetProperty("Session")!.SetValue(instance, DeviceSession.Start([], engine));
        connected.GetProperty("Engine")!.SetValue(instance, engine);
        var devices = (System.Collections.IDictionary)typeof(DeviceManager)
            .GetField("_devices", System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic)!.GetValue(manager)!;
        devices.Add(port, instance);
    }
}
