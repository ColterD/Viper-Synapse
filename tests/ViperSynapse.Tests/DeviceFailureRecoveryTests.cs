using System.Collections;
using System.Reflection;
using ViperSynapse.Core.Bindings;
using ViperSynapse.Core.Engine;
using ViperSynapse.Core.Input;
using ViperSynapse.Hardware;
using ViperSynapse.Hardware.Linux;
using ViperSynapse.Service;

namespace ViperSynapse.Tests;

public class DeviceFailureRecoveryTests
{
    sealed class Sink : IInputSink, IEngineHost
    {
        public List<(ushort Code, int Value)> Events { get; } = [];
        public bool FailReleases { get; set; }
        public bool FailDisposedReleases { get; set; }
        public bool FailSync { get; set; }
        public void Key(ushort code, int value)
        {
            if (FailDisposedReleases && value == 0)
                throw new ObjectDisposedException("simulated sink");
            if (FailReleases && value == 0)
                throw new IOException("simulated release failure");
            Events.Add((code, value));
        }
        public void Relative(ushort axis, int value) { }
        public void Sync() { if (FailSync) throw new IOException("simulated sync failure"); }
        public void Launch(string command) { }
        public void Open(string target) { }
        public void SwitchProfile(CycleDirection direction, Guid? profileId) { }
        public void SwitchKeymap(CycleDirection direction, int keymap) { }
        public Macro? FindMacro(Guid id) => null;
        public void Gamepad(GamepadControl control, bool down) { }
        public ShortcutList Shortcuts => ShortcutList.Empty;
        public void Report(Exception error) { }
    }

    sealed class BlockingMacroHost(Macro macro, ManualResetEventSlim entered, ManualResetEventSlim resume, ManualResetEventSlim resumed, ManualResetEventSlim reported) : IEngineHost
    {
        public void Launch(string command) { entered.Set(); resume.Wait(); resumed.Set(); }
        public void Open(string target) { }
        public void SwitchProfile(CycleDirection direction, Guid? profileId) { }
        public void SwitchKeymap(CycleDirection direction, int keymap) { }
        public Macro? FindMacro(Guid id) => id == macro.Id ? macro : null;
        public void Gamepad(GamepadControl control, bool down) { }
        public ShortcutList Shortcuts => ShortcutList.Empty;
        public void Report(Exception error) => reported.Set();
    }

    const BindingFlags InstancePrivate = BindingFlags.Instance | BindingFlags.NonPublic;

    static object AddDevice(DeviceManager manager, string port, DeviceSession? session)
    {
        var type = typeof(DeviceManager).GetNestedType("Connected", BindingFlags.NonPublic)!;
        var connected = Activator.CreateInstance(type, port, (ushort)0xffff, "Test")!;
        type.GetProperty("Session")!.SetValue(connected, session);
        var devices = (IDictionary)typeof(DeviceManager).GetField("_devices", InstancePrivate)!.GetValue(manager)!;
        devices.Add(port, connected);
        return connected;
    }

    static bool Retire(DeviceManager manager, object device, DeviceSession session)
    {
        var method = typeof(DeviceManager).GetMethod("RetireFailedSession", InstancePrivate);
        Assert.NotNull(method);
        return (bool)method.Invoke(manager, [device, session, "event-test", new IOException("simulated loss"), false])!;
    }

    static void ScanModel(DeviceManager manager, string key)
    {
        var method = typeof(DeviceManager).GetMethod("Apply", InstancePrivate)!;
        method.Invoke(manager, [key, true]);
    }

    static SettingsStore StoreWithBinding(string root)
    {
        var store = new SettingsStore(root);
        var settings = DeviceSettings.Create();
        var keymap = Keymap.Empty("k").With(30, new KeyboardBinding(46));
        store.SaveDevice("ffff", settings with { Profiles = [settings.Active with { Keymaps = [keymap] }] });
        return store;
    }

    [Fact]
    public void A_failed_current_session_is_stopped_without_affecting_another_device()
    {
        var sink = new Sink();
        string root = Path.Join(Path.GetTempPath(), "viper-failed-session-" + Guid.NewGuid().ToString("N"));
        try
        {
            using var manager = new DeviceManager(new SettingsStore(root), sink);
            var firstEngine = new RemapEngine(sink, sink);
            var secondEngine = new RemapEngine(sink, sink);
            var first = DeviceSession.Start([], firstEngine);
            var second = DeviceSession.Start([], secondEngine);
            var failed = AddDevice(manager, "usb-first", first);
            AddDevice(manager, "usb-second", second);
            firstEngine.Key(30, 1);

            Assert.True(Retire(manager, failed, first));
            Assert.Contains(((ushort)30, 0), sink.Events);
            Assert.Contains(manager.Devices, d => d.Port == "usb-first" && !d.Remapping && d.Problem!.Contains("simulated loss"));
            Assert.Contains(manager.Devices, d => d.Port == "usb-second" && d.Remapping);
        }
        finally { if (Directory.Exists(root)) Directory.Delete(root, true); }
    }

    [Fact]
    public void A_stale_node_failure_does_not_stop_a_replacement_session()
    {
        var sink = new Sink();
        string root = Path.Join(Path.GetTempPath(), "viper-stale-session-" + Guid.NewGuid().ToString("N"));
        try
        {
            using var manager = new DeviceManager(new SettingsStore(root), sink);
            using var old = DeviceSession.Start([], new RemapEngine(sink, sink));
            var replacement = DeviceSession.Start([], new RemapEngine(sink, sink));
            var device = AddDevice(manager, "usb-one", replacement);
            Assert.False(Retire(manager, device, old));
            Assert.Contains(manager.Devices, d => d.Port == "usb-one" && d.Remapping && d.Problem == null);
        }
        finally { if (Directory.Exists(root)) Directory.Delete(root, true); }
    }

    [Fact]
    public void A_failure_from_a_disconnected_device_does_not_stop_a_reconnected_device_on_the_same_port()
    {
        var sink = new Sink();
        string root = Path.Join(Path.GetTempPath(), "viper-reconnected-session-" + Guid.NewGuid().ToString("N"));
        try
        {
            using var manager = new DeviceManager(new SettingsStore(root), sink);
            using var old = DeviceSession.Start([], new RemapEngine(sink, sink));
            var replacement = DeviceSession.Start([], new RemapEngine(sink, sink));
            var oldDevice = AddDevice(manager, "usb-one", old);
            var devices = (IDictionary)typeof(DeviceManager).GetField("_devices", InstancePrivate)!.GetValue(manager)!;
            devices.Remove("usb-one");
            AddDevice(manager, "usb-one", replacement);
            Assert.False(Retire(manager, oldDevice, old));
            Assert.Contains(manager.Devices, d => d.Port == "usb-one" && d.Remapping && d.Problem == null);
        }
        finally { if (Directory.Exists(root)) Directory.Delete(root, true); }
    }

    [Fact]
    public void A_callback_for_a_session_that_was_not_published_is_ignored()
    {
        var sink = new Sink();
        string root = Path.Join(Path.GetTempPath(), "viper-unpublished-session-" + Guid.NewGuid().ToString("N"));
        try
        {
            using var manager = new DeviceManager(new SettingsStore(root), sink);
            using var session = DeviceSession.Start([], new RemapEngine(sink, sink));
            var device = AddDevice(manager, "usb-one", session);
            device.GetType().GetProperty("Session")!.SetValue(device, null);
            Assert.False(Retire(manager, device, session));
            Assert.Contains(manager.Devices, d => d.Port == "usb-one" && !d.Remapping && d.Problem == null);
        }
        finally { if (Directory.Exists(root)) Directory.Delete(root, true); }
    }

    [Fact]
    public async Task A_failed_session_is_checked_after_it_is_published_under_the_manager_gate()
    {
        var sink = new Sink();
        string root = Path.Join(Path.GetTempPath(), "viper-publish-session-" + Guid.NewGuid().ToString("N"));
        try
        {
            using var manager = new DeviceManager(new SettingsStore(root), sink);
            var session = DeviceSession.Start([], new RemapEngine(sink, sink));
            var device = AddDevice(manager, "usb-one", session);
            var gate = (Lock)typeof(DeviceManager).GetField("_gate", InstancePrivate)!.GetValue(manager)!;
            var property = device.GetType().GetProperty("Session")!;
            Task<bool> task;
            using var entered = new ManualResetEventSlim();
            lock (gate)
            {
                property.SetValue(device, null);
                task = Task.Run(() => { entered.Set(); return Retire(manager, device, session); });
                Assert.True(entered.Wait(TimeSpan.FromSeconds(3), TestContext.Current.CancellationToken));
                Assert.False(task.IsCompleted);
                property.SetValue(device, session);
            }
            Assert.True(await task);
            Assert.Contains(manager.Devices, d => d.Port == "usb-one" && !d.Remapping);
        }
        finally { if (Directory.Exists(root)) Directory.Delete(root, true); }
    }

    [Fact]
    public void Start_accepts_a_failure_handler_before_it_launches_readers()
    {
        var sink = new Sink();
        DeviceSession? seen = null;
        Action<DeviceSession, string, Exception> handler = (session, _, _) => seen = session;
        using var instance = DeviceSession.Start([], new RemapEngine(sink, sink), onNodeLost: handler);
        var field = typeof(DeviceSession).GetField("NodeLost", InstancePrivate);
        Assert.NotNull(field);
        var callback = Assert.IsType<Action<string, Exception>>(field.GetValue(instance));
        callback("event-test", new IOException("loss"));
        Assert.Same(instance, seen);
    }

    [Fact]
    public void Unchanged_scans_do_not_restart_a_failed_session_or_erase_its_cleanup_error()
    {
        var sink = new Sink();
        string root = Path.Join(Path.GetTempPath(), "viper-no-restart-" + Guid.NewGuid().ToString("N"));
        try
        {
            using var manager = new DeviceManager(StoreWithBinding(root), sink);
            var engine = new RemapEngine(sink, sink);
            var session = DeviceSession.Start([], engine);
            var device = AddDevice(manager, "usb-one", session);
            engine.Key(30, 1);
            sink.FailReleases = true;

            Assert.True(Retire(manager, device, session));
            ScanModel(manager, "ffff");
            ScanModel(manager, "ffff");

            Assert.Contains(manager.Devices, d => d.Port == "usb-one" && !d.Remapping
                && d.Problem!.Contains("simulated loss") && d.Problem.Contains("simulated release failure"));
        }
        finally
        {
            sink.FailReleases = false;
            if (Directory.Exists(root)) Directory.Delete(root, true);
        }
    }

    [Fact]
    public void A_device_without_input_nodes_is_not_reported_as_remapping()
    {
        var sink = new Sink();
        string root = Path.Join(Path.GetTempPath(), "viper-no-input-" + Guid.NewGuid().ToString("N"));
        try
        {
            using var manager = new DeviceManager(StoreWithBinding(root), sink);
            AddDevice(manager, "usb-one", null);

            manager.Apply("ffff");

            Assert.Contains(manager.Devices, d => d.Port == "usb-one" && !d.Remapping
                && d.Problem!.Contains("input", StringComparison.OrdinalIgnoreCase));
        }
        finally { if (Directory.Exists(root)) Directory.Delete(root, true); }
    }

    [Fact]
    public void Explicit_settings_apply_retries_a_failed_device()
    {
        var sink = new Sink();
        string root = Path.Join(Path.GetTempPath(), "viper-explicit-retry-" + Guid.NewGuid().ToString("N"));
        try
        {
            using var manager = new DeviceManager(StoreWithBinding(root), sink);
            var session = DeviceSession.Start([], new RemapEngine(sink, sink));
            var device = AddDevice(manager, "usb-one", session);
            Assert.True(Retire(manager, device, session));
            var missing = Path.Join(root, "missing-input-node");
            device.GetType().GetProperty("InputNodes")!.SetValue(device, new List<string> { missing });

            manager.Apply("ffff");

            Assert.Contains(manager.Devices, d => d.Port == "usb-one" && !d.Remapping
                && d.Problem!.Contains("Could not take over") && d.Problem.Contains(missing));
        }
        finally { if (Directory.Exists(root)) Directory.Delete(root, true); }
    }

    [Fact]
    public void Session_options_are_updated_even_when_reset_reports_a_failure()
    {
        var sink = new Sink();
        var engine = new RemapEngine(sink, sink);
        using var session = DeviceSession.Start([], engine);
        engine.Key(30, 1);
        sink.FailReleases = true;
        var next = Keymap.Empty("next");

        Assert.Throws<IOException>(() => DeviceManager.UpdateSession(session, next, MacroInterruption.AnyKey, 3, false));

        Assert.Same(next, engine.Keymap);
        Assert.Equal(MacroInterruption.AnyKey, engine.Interruption);
        Assert.Equal(3, engine.ScrollLines);
        sink.FailReleases = false;
    }

    [Fact]
    public void A_successful_later_session_update_clears_its_previous_update_error()
    {
        var sink = new Sink();
        string root = Path.Join(Path.GetTempPath(), "viper-update-recovery-" + Guid.NewGuid().ToString("N"));
        try
        {
            using var manager = new DeviceManager(StoreWithBinding(root), sink);
            var engine = new RemapEngine(sink, sink);
            var session = DeviceSession.Start([], engine);
            AddDevice(manager, "usb-one", session);
            engine.Key(30, 1);
            sink.FailReleases = true;
            manager.Apply("ffff");
            Assert.Contains(manager.Devices, d => d.Port == "usb-one" && d.Problem!.Contains("Could not update"));

            sink.FailReleases = false;
            manager.Apply("ffff");
            Assert.Contains(manager.Devices, d => d.Port == "usb-one" && d.Problem == null);
        }
        finally
        {
            sink.FailReleases = false;
            if (Directory.Exists(root)) Directory.Delete(root, true);
        }
    }

    [Fact]
    public void A_successful_session_update_does_not_clear_an_unrelated_device_error()
    {
        var sink = new Sink();
        string root = Path.Join(Path.GetTempPath(), "viper-other-error-" + Guid.NewGuid().ToString("N"));
        try
        {
            using var manager = new DeviceManager(StoreWithBinding(root), sink);
            var device = AddDevice(manager, "usb-one", DeviceSession.Start([], new RemapEngine(sink, sink)));
            device.GetType().GetProperty("Problem")!.SetValue(device, "firmware failure");

            manager.Apply("ffff");

            Assert.Contains(manager.Devices, d => d.Port == "usb-one" && d.Problem == "firmware failure");
        }
        finally { if (Directory.Exists(root)) Directory.Delete(root, true); }
    }

    [Fact]
    public void A_transient_session_update_error_does_not_erase_a_keypad_listener_error()
    {
        var sink = new Sink();
        string root = Path.Join(Path.GetTempPath(), "viper-listener-update-" + Guid.NewGuid().ToString("N"));
        try
        {
            using var manager = new DeviceManager(StoreWithBinding(root), sink);
            var engine = new RemapEngine(sink, sink);
            var device = AddDevice(manager, "usb-one", DeviceSession.Start([], engine));
            var node = new HidrawNode("missing", 0x1532, 0xffff, 1, "Test", "usb-one/input1");
            Func<string, Action<bool>, Action<int>, Action<KeypadButtons>, KeypadButtons> fail =
                (_, _, _, _) => throw new IOException("listener failure");
            typeof(DeviceManager).GetMethod("StartButtonsUsing", InstancePrivate)!.Invoke(manager, [device, node, fail]);
            engine.Key(30, 1);
            sink.FailReleases = true;
            manager.Apply("ffff");
            Assert.Contains(manager.Devices, d => d.Problem!.Contains("listener failure") && d.Problem.Contains("Could not update"));

            sink.FailReleases = false;
            manager.Apply("ffff");
            Assert.Contains(manager.Devices, d => d.Problem!.Contains("listener failure") && !d.Problem.Contains("Could not update"));
        }
        finally
        {
            sink.FailReleases = false;
            if (Directory.Exists(root)) Directory.Delete(root, true);
        }
    }

    [Fact]
    public void A_session_update_keeps_reporting_a_pending_release_until_output_recovers()
    {
        var sink = new Sink();
        string root = Path.Join(Path.GetTempPath(), "viper-pending-update-" + Guid.NewGuid().ToString("N"));
        try
        {
            using var manager = new DeviceManager(StoreWithBinding(root), sink);
            var engine = new RemapEngine(sink, sink);
            AddDevice(manager, "usb-one", DeviceSession.Start([], engine));
            engine.Key(30, 1);
            sink.FailReleases = true;
            manager.Apply("ffff");
            manager.Apply("ffff");
            Assert.Contains(manager.Devices, d => d.Problem != null && d.Problem.Contains("simulated release failure"));
            Assert.DoesNotContain(((ushort)30, 0), sink.Events);

            sink.FailReleases = false;
            manager.Apply("ffff");
            Assert.Contains(((ushort)30, 0), sink.Events);
            Assert.Contains(manager.Devices, d => d.Problem == null);
        }
        finally
        {
            sink.FailReleases = false;
            if (Directory.Exists(root)) Directory.Delete(root, true);
        }
    }

    [Fact]
    public void An_unchanged_scan_does_not_clear_an_unresolved_update_failure()
    {
        var sink = new Sink();
        string root = Path.Join(Path.GetTempPath(), "viper-scan-pending-" + Guid.NewGuid().ToString("N"));
        try
        {
            using var manager = new DeviceManager(StoreWithBinding(root), sink);
            var engine = new RemapEngine(sink, sink);
            AddDevice(manager, "usb-one", DeviceSession.Start([], engine));
            engine.Key(30, 1);
            sink.FailReleases = true;
            manager.Apply("ffff");
            ScanModel(manager, "ffff");
            Assert.Contains(manager.Devices, d => d.Problem != null && d.Problem.Contains("simulated release failure"));
            Assert.DoesNotContain(((ushort)30, 0), sink.Events);
        }
        finally
        {
            sink.FailReleases = false;
            if (Directory.Exists(root)) Directory.Delete(root, true);
        }
    }

    [Fact]
    public void An_update_warning_is_not_cleared_while_a_cancelled_macro_is_still_finishing()
    {
        var sink = new Sink();
        using var entered = new ManualResetEventSlim();
        using var resume = new ManualResetEventSlim();
        using var resumed = new ManualResetEventSlim();
        using var reported = new ManualResetEventSlim();
        string root = Path.Join(Path.GetTempPath(), "viper-macro-cleanup-" + Guid.NewGuid().ToString("N"));
        try
        {
            using var manager = new DeviceManager(StoreWithBinding(root), sink);
            var macro = new Macro(Guid.NewGuid(), "blocked", [new KeyStep(32, true), new RunStep("blocked")]);
            var engine = new RemapEngine(sink, new BlockingMacroHost(macro, entered, resume, resumed, reported))
            {
                Keymap = Keymap.Empty("k").With(31, new MacroBinding(macro.Id, MacroPlayback.WhileHeld)),
            };
            AddDevice(manager, "usb-one", DeviceSession.Start([], engine));
            engine.Key(30, 1);
            engine.Key(31, 1);
            Assert.True(entered.Wait(TimeSpan.FromSeconds(3), TestContext.Current.CancellationToken));
            sink.FailReleases = true;
            manager.Apply("ffff");
            sink.FailReleases = false;
            manager.Apply("ffff");
            Assert.Contains(manager.Devices, d => d.Problem != null && d.Problem.Contains("simulated release failure"));
            sink.FailReleases = true;
            resume.Set();
            Assert.True(reported.Wait(TimeSpan.FromSeconds(3), TestContext.Current.CancellationToken));
            manager.Apply("ffff");
            Assert.Contains(manager.Devices, d => d.Problem != null && d.Problem.Contains("simulated release failure"));
            sink.FailReleases = false;
            manager.Apply("ffff");
            Assert.Contains(manager.Devices, d => d.Problem == null);
        }
        finally
        {
            sink.FailReleases = false;
            resume.Set();
            if (entered.IsSet)
                resumed.Wait(TimeSpan.FromSeconds(3), TestContext.Current.CancellationToken);
            if (Directory.Exists(root)) Directory.Delete(root, true);
        }
    }

    [Fact]
    public void Replacing_a_session_keeps_its_update_warning_while_old_macro_cleanup_is_pending()
    {
        var sink = new Sink();
        using var entered = new ManualResetEventSlim();
        using var resume = new ManualResetEventSlim();
        using var resumed = new ManualResetEventSlim();
        using var reported = new ManualResetEventSlim();
        string root = Path.Join(Path.GetTempPath(), "viper-replace-cleanup-" + Guid.NewGuid().ToString("N"));
        try
        {
            using var manager = new DeviceManager(StoreWithBinding(root), sink);
            var macro = new Macro(Guid.NewGuid(), "blocked", [new KeyStep(32, true), new RunStep("blocked")]);
            var engine = new RemapEngine(sink, new BlockingMacroHost(macro, entered, resume, resumed, reported))
            {
                Keymap = Keymap.Empty("k").With(31, new MacroBinding(macro.Id, MacroPlayback.WhileHeld)),
            };
            var device = AddDevice(manager, "usb-one", DeviceSession.Start([], engine));
            engine.Key(30, 1);
            engine.Key(31, 1);
            Assert.True(entered.Wait(TimeSpan.FromSeconds(3), TestContext.Current.CancellationToken));
            sink.FailReleases = true;
            manager.Apply("ffff");
            sink.FailReleases = false;
            device.GetType().GetProperty("SessionStick")!.SetValue(device, ThumbstickMode.FourWay);
            manager.Apply("ffff"); // Retires the old session; the test has no real input nodes to take over.
            Assert.Contains(manager.Devices, d => d.Problem != null && d.Problem.Contains("simulated release failure"));

            var replacement = DeviceSession.Start([], new RemapEngine(sink, sink));
            device.GetType().GetProperty("Session")!.SetValue(device, replacement);
            device.GetType().GetProperty("SessionStick")!.SetValue(device, ThumbstickMode.Analog);
            manager.Apply("ffff");
            Assert.Contains(manager.Devices, d => d.Problem != null && d.Problem.Contains("simulated release failure"));
        }
        finally
        {
            sink.FailReleases = false;
            resume.Set();
            if (entered.IsSet)
                resumed.Wait(TimeSpan.FromSeconds(3), TestContext.Current.CancellationToken);
            if (Directory.Exists(root)) Directory.Delete(root, true);
        }
    }

    [Fact]
    public void A_failed_stop_keeps_its_cleanup_warning_when_the_next_start_fails()
    {
        var sink = new Sink();
        string root = Path.Join(Path.GetTempPath(), "viper-stop-start-" + Guid.NewGuid().ToString("N"));
        try
        {
            using var manager = new DeviceManager(StoreWithBinding(root), sink);
            var engine = new RemapEngine(sink, sink);
            var session = DeviceSession.Start([], engine);
            var device = AddDevice(manager, "usb-one", session);
            engine.Key(30, 1);
            sink.FailReleases = true;
            Assert.True(Retire(manager, device, session));
            device.GetType().GetProperty("InputNodes")!.SetValue(device, new List<string> { Path.Join(root, "missing") });
            manager.Apply("ffff");
            Assert.Contains(manager.Devices, d => d.Problem!.Contains("Could not take over") && d.Problem.Contains("simulated release failure"));
        }
        finally
        {
            sink.FailReleases = false;
            if (Directory.Exists(root)) Directory.Delete(root, true);
        }
    }

    [Fact]
    public void A_session_update_keeps_reporting_a_pending_sync_until_output_recovers()
    {
        var sink = new Sink();
        string root = Path.Join(Path.GetTempPath(), "viper-pending-sync-" + Guid.NewGuid().ToString("N"));
        try
        {
            using var manager = new DeviceManager(StoreWithBinding(root), sink);
            var engine = new RemapEngine(sink, sink);
            AddDevice(manager, "usb-one", DeviceSession.Start([], engine));
            engine.Key(30, 1);
            sink.FailSync = true;
            manager.Apply("ffff");
            manager.Apply("ffff");
            Assert.Contains(manager.Devices, d => d.Problem != null && d.Problem.Contains("simulated sync failure"));
            sink.FailSync = false;
            manager.Apply("ffff");
            Assert.Contains(manager.Devices, d => d.Problem == null);
        }
        finally
        {
            sink.FailSync = false;
            if (Directory.Exists(root)) Directory.Delete(root, true);
        }
    }

    [Fact]
    public void Starting_a_replacement_firmware_listener_clears_the_previous_buttons_state()
    {
        var sink = new Sink();
        string root = Path.Join(Path.GetTempPath(), "viper-new-listener-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        string path = Path.Join(root, "hidraw-test");
        File.WriteAllBytes(path, []);
        try
        {
            using var manager = new DeviceManager(new SettingsStore(root), sink);
            var engine = new RemapEngine(sink, sink);
            var type = typeof(DeviceManager).GetNestedType("Connected", BindingFlags.NonPublic)!;
            var device = Activator.CreateInstance(type, "usb-test", (ushort)0x02eb, "Tartarus")!;
            ((IDictionary)typeof(DeviceManager).GetField("_devices", InstancePrivate)!.GetValue(manager)!).Add("usb-test", device);
            type.GetProperty("Engine")!.SetValue(device, engine);
            type.GetMethod("SetPhysicalHypershift")!.Invoke(device, [true]);
            Assert.True(engine.HypershiftActive);
            var node = new ViperSynapse.Hardware.Linux.HidrawNode(path, 0x1532, 0x02eb, 1, "Tartarus", "usb-test/input1");

            typeof(DeviceManager).GetMethod("StartButtons", InstancePrivate)!.Invoke(manager, [device, node]);
            Assert.False(engine.HypershiftActive);
        }
        finally { if (Directory.Exists(root)) Directory.Delete(root, true); }
    }

    [Fact]
    public void Incomplete_discovery_does_not_interrupt_an_existing_shortcut_session_on_rescan()
    {
        var sink = new Sink();
        string root = Path.Join(Path.GetTempPath(), "viper-shortcut-rescan-" + Guid.NewGuid().ToString("N"));
        try
        {
            using var manager = new DeviceManager(new SettingsStore(root), sink);
            manager.SetShortcuts(new ShortcutList([GlobalShortcut.Create([30], new KeyboardBinding(46))]));
            var engine = new RemapEngine(sink, sink);
            var session = DeviceSession.Start([], engine);
            var device = AddDevice(manager, "usb-one", session);
            device.GetType().GetProperty("InputControlsStatus")!.SetValue(device, "partial");
            device.GetType().GetProperty("EvdevControls")!.SetValue(device, new ushort[] { 30 });
            engine.Key(30, 1);

            ScanModel(manager, "ffff");

            Assert.Contains(manager.Devices, d => d.Port == "usb-one" && d.Remapping);
            Assert.DoesNotContain(((ushort)30, 0), sink.Events);
            engine.Key(30, 0);
        }
        finally { if (Directory.Exists(root)) Directory.Delete(root, true); }
    }

    [Fact]
    public void A_disposed_output_on_one_session_does_not_block_stopping_other_devices()
    {
        var firstSink = new Sink();
        var secondSink = new Sink();
        string root = Path.Join(Path.GetTempPath(), "viper-disposed-sink-" + Guid.NewGuid().ToString("N"));
        try
        {
            using var manager = new DeviceManager(new SettingsStore(root), new Sink());
            try
            {
                var firstEngine = new RemapEngine(firstSink, firstSink);
                var secondEngine = new RemapEngine(secondSink, secondSink);
                AddDevice(manager, "usb-first", DeviceSession.Start([], firstEngine));
                AddDevice(manager, "usb-second", DeviceSession.Start([], secondEngine));
                firstEngine.Key(30, 1);
                secondEngine.Key(30, 1);
                firstSink.FailDisposedReleases = true;

                Assert.Null(Record.Exception(() => manager.Apply("ffff")));
                Assert.All(manager.Devices, d => Assert.False(d.Remapping));
                Assert.Contains(((ushort)30, 0), secondSink.Events);
                Assert.Contains(manager.Devices, d => d.Port == "usb-first" && d.Problem != null);
            }
            finally { firstSink.FailDisposedReleases = false; }
        }
        finally { if (Directory.Exists(root)) Directory.Delete(root, true); }
    }
}
