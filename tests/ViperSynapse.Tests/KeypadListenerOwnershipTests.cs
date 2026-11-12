using System.Collections;
using System.Reflection;
using System.Runtime.InteropServices;
using ViperSynapse.Core.Bindings;
using ViperSynapse.Core.Engine;
using ViperSynapse.Core.Input;
using ViperSynapse.Hardware;
using ViperSynapse.Hardware.Linux;
using ViperSynapse.Service;

namespace ViperSynapse.Tests;

public class KeypadListenerOwnershipTests
{
    [DllImport("libc", EntryPoint = "mkfifo", SetLastError = true)]
    static extern int MakeFifo(string path, uint mode);
    sealed class Sink : IInputSink, IEngineHost
    {
        public void Key(ushort code, int value) { }
        public void Relative(ushort axis, int value) { }
        public void Sync() { }
        public void Launch(string command) { }
        public void Open(string target) { }
        public void SwitchProfile(CycleDirection direction, Guid? profileId) { }
        public void SwitchKeymap(CycleDirection direction, int keymap) { }
        public Macro? FindMacro(Guid id) => null;
        public void Gamepad(GamepadControl control, bool down) { }
        public ShortcutList Shortcuts => ShortcutList.Empty;
        public void Report(Exception error) { }
    }

    const BindingFlags Private = BindingFlags.Instance | BindingFlags.NonPublic;

    static (object Device, IDictionary Devices) AddDevice(DeviceManager manager)
    {
        var type = typeof(DeviceManager).GetNestedType("Connected", BindingFlags.NonPublic)!;
        var device = Activator.CreateInstance(type, "usb-test", (ushort)0x02eb, "Tartarus")!;
        var devices = (IDictionary)typeof(DeviceManager).GetField("_devices", Private)!.GetValue(manager)!;
        devices.Add("usb-test", device);
        return (device, devices);
    }

    static void Start(DeviceManager manager, object device, string path)
    {
        var node = new HidrawNode(path, 0x1532, 0x02eb, 1, "Tartarus", "usb-test/input1");
        typeof(DeviceManager).GetMethod("StartButtons", Private)!.Invoke(manager, [device, node]);
    }

    static int OpenHandles(string path) => Directory.EnumerateFiles("/proc/self/fd").Count(fd =>
    {
        try { return File.ResolveLinkTarget(fd, false)?.FullName == path; }
        catch (IOException) { return false; }
    });

    [Fact]
    public async Task A_listener_opened_for_a_removed_device_is_not_published_and_is_closed()
    {
        string root = Path.Combine(Path.GetTempPath(), "viper-orphan-listener-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        string path = Path.Combine(root, "hidraw-test");
        Assert.Equal(0, MakeFifo(path, 0x180)); // 0600
        try
        {
            using var manager = new DeviceManager(new SettingsStore(root), new Sink());
            var (device, devices) = AddDevice(manager);
            var gate = (Lock)typeof(DeviceManager).GetField("_gate", Private)!.GetValue(manager)!;
            var buttonsProperty = device.GetType().GetProperty("Buttons")!;
            var started = Task.Run(() => Start(manager, device, path), TestContext.Current.CancellationToken);
            Assert.True(SpinWait.SpinUntil(() =>
            {
                lock (gate)
                    return (bool)device.GetType().GetProperty("ButtonsStarting")!.GetValue(device)!;
            }, TimeSpan.FromSeconds(3)));
            lock (gate)
                devices.Remove("usb-test");
            // The reader is blocked in open(); pairing a writer lets it finish opening after removal.
            using (var writer = new FileStream(path, FileMode.Open, FileAccess.Write, FileShare.ReadWrite))
            {
                await started;
            }
            var orphan = (KeypadButtons?)buttonsProperty.GetValue(device);
            try
            {
                Assert.Null(orphan);
                Assert.Equal(0, OpenHandles(path));
            }
            finally { orphan?.Dispose(); }
        }
        finally { if (Directory.Exists(root)) Directory.Delete(root, true); }
    }

    [Fact]
    public void A_queued_keymap_report_for_a_removed_device_does_not_change_settings()
    {
        string root = Path.Combine(Path.GetTempPath(), "viper-stale-keymap-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        string path = Path.Combine(root, "hidraw-test");
        File.WriteAllBytes(path, []);
        try
        {
            using var manager = new DeviceManager(new SettingsStore(root), new Sink());
            var (device, devices) = AddDevice(manager);
            using var buttons = KeypadButtons.Start(path);
            device.GetType().GetProperty("Buttons")!.SetValue(device, buttons);
            int before = manager.GetSettings("02eb").ActiveKeymap;
            devices.Remove("usb-test");
            var handler = typeof(DeviceManager).GetMethod("HandleKeymapReport", Private);
            Assert.NotNull(handler);

            handler.Invoke(manager, [device, buttons, 4]);

            Assert.Equal(before, manager.GetSettings("02eb").ActiveKeymap);
        }
        finally { if (Directory.Exists(root)) Directory.Delete(root, true); }
    }

    [Fact]
    public void An_unexpected_listener_exit_is_reported_and_not_restarted_automatically()
    {
        string root = Path.Combine(Path.GetTempPath(), "viper-listener-exit-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        string path = Path.Combine(root, "hidraw-test");
        File.WriteAllBytes(path, []);
        try
        {
            using var manager = new DeviceManager(new SettingsStore(root), new Sink());
            var (device, _) = AddDevice(manager);
            Start(manager, device, path);

            Assert.True(SpinWait.SpinUntil(() => manager.Devices.Any(d => d.Port == "usb-test" && d.Problem != null), TimeSpan.FromSeconds(3)));
            Assert.Contains(manager.Devices, d => d.Port == "usb-test" && d.Problem!.Contains("keypad", StringComparison.OrdinalIgnoreCase));
            Assert.Null(device.GetType().GetProperty("Buttons")!.GetValue(device));
        }
        finally { if (Directory.Exists(root)) Directory.Delete(root, true); }
    }

    [Fact]
    public void An_unexpected_start_failure_releases_the_starting_guard_and_preserves_the_error()
    {
        string root = Path.Combine(Path.GetTempPath(), "viper-listener-start-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        try
        {
            using var manager = new DeviceManager(new SettingsStore(root), new Sink());
            var (device, _) = AddDevice(manager);
            var node = new HidrawNode(Path.Combine(root, "hidraw-test"), 0x1532, 0x02eb, 1, "Tartarus", "usb-test/input1");
            Func<string, Action<bool>, Action<int>, Action<KeypadButtons>, KeypadButtons> fail =
                (_, _, _, _) => throw new InvalidOperationException("simulated thread-start failure");
            var start = typeof(DeviceManager).GetMethod("StartButtonsUsing", Private);
            Assert.NotNull(start);

            var error = Assert.Throws<TargetInvocationException>(() => start.Invoke(manager, [device, node, fail]));

            Assert.Equal("simulated thread-start failure", Assert.IsType<InvalidOperationException>(error.InnerException).Message);
            Assert.False((bool)device.GetType().GetProperty("ButtonsStarting")!.GetValue(device)!);
            Assert.Null(device.GetType().GetProperty("Buttons")!.GetValue(device));
        }
        finally { if (Directory.Exists(root)) Directory.Delete(root, true); }
    }

    [Fact]
    public async Task A_replacement_waits_until_the_old_listeners_final_callback_finishes()
    {
        string root = Path.Combine(Path.GetTempPath(), "viper-old-listener-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        string path = Path.Combine(root, "hidraw-fifo");
        string replacementPath = Path.Combine(root, "hidraw-replacement");
        Assert.Equal(0, MakeFifo(path, 0x180));
        File.WriteAllBytes(replacementPath, []);
        using var entered = new ManualResetEventSlim();
        using var release = new ManualResetEventSlim();
        Task? retiring = null;
        try
        {
            using var manager = new DeviceManager(new SettingsStore(root), new Sink());
            var (device, _) = AddDevice(manager);
            var setter = device.GetType().GetMethod("SetPhysicalHypershift")!;
            var starter = Task.Run(() => KeypadButtons.Start(path, held =>
            {
                setter.Invoke(device, [held]);
                if (held) { entered.Set(); release.Wait(); }
            }), TestContext.Current.CancellationToken);
            using var writer = new FileStream(path, FileMode.Open, FileAccess.Write, FileShare.ReadWrite);
            var old = await starter;
            device.GetType().GetProperty("Buttons")!.SetValue(device, old);
            writer.Write([0x04, 0x01]);
            writer.Flush();
            Assert.True(entered.Wait(TimeSpan.FromSeconds(3), TestContext.Current.CancellationToken));
            var retire = typeof(DeviceManager).GetMethod("RetireStoppedButtons", Private)!;
            retiring = Task.Run(() => retire.Invoke(manager, [device, old]), TestContext.Current.CancellationToken);
            Assert.True(SpinWait.SpinUntil(() => (bool)device.GetType().GetProperty("ButtonsStarting")!.GetValue(device)!, TimeSpan.FromSeconds(3)));

            Start(manager, device, replacementPath);

            Assert.Null(device.GetType().GetProperty("Buttons")!.GetValue(device));
            Assert.False(retiring.IsCompleted);
            release.Set();
            await retiring.WaitAsync(TimeSpan.FromSeconds(3), TestContext.Current.CancellationToken);
            Assert.False((bool)device.GetType().GetProperty("ButtonsStarting")!.GetValue(device)!);
        }
        finally
        {
            release.Set();
            if (retiring != null)
                await retiring.WaitAsync(TimeSpan.FromSeconds(3), TestContext.Current.CancellationToken);
            if (Directory.Exists(root)) Directory.Delete(root, true);
        }
    }
}
