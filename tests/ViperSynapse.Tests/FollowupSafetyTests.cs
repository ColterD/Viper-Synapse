using ViperSynapse.App.Services;
using ViperSynapse.App.ViewModels;
using ViperSynapse.Core.Bindings;
using ViperSynapse.Core.Engine;
using ViperSynapse.Core.Input;

namespace ViperSynapse.Tests;

public class FollowupSafetyTests
{
    sealed class Host : IEngineHost
    {
        public readonly TaskCompletionSource<Exception> Error = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public void Launch(string command) { }
        public void Open(string target) { }
        public void SwitchProfile(CycleDirection direction, Guid? profileId) { }
        public void SwitchKeymap(CycleDirection direction, int keymap) { }
        public Macro? FindMacro(Guid id) => null;
        public void Gamepad(GamepadControl control, bool down) { }
        public ShortcutList Shortcuts => ShortcutList.Empty;
        public void Report(Exception error) => Error.TrySetResult(error);
    }

    sealed class Sink : IInputSink
    {
        public bool FailCleanup { get; set; } = true;
        public void Key(ushort code, int value)
        {
            if (code == 46 && value == 1)
                throw new IOException("primary press failure");
            if (code == KeyCodes.LeftCtrl && value == 0 && FailCleanup)
                throw new IOException("cleanup failure");
        }
        public void Relative(ushort axis, int value) { }
        public void Sync() { }
    }

    [Fact]
    public void Wheel_preserves_primary_failure_when_owner_cleanup_fails()
    {
        var host = new Host();
        var engine = new RemapEngine(new Sink(), host)
        {
            Keymap = Keymap.Empty("k").With(SourceKeys.WheelUp, new KeyboardBinding(46, Modifiers.Ctrl)),
        };
        var error = Assert.Throws<IOException>(() => engine.Wheel(RelativeAxes.Wheel, 1));
        Assert.Equal("primary press failure", error.Message);
        Assert.Equal("cleanup failure", Assert.IsType<IOException>(error.Data["ViperSynapse.CleanupFailure"]).Message);
    }

    [Fact]
    public async Task Turbo_reports_primary_failure_when_owner_cleanup_fails()
    {
        var host = new Host();
        var sink = new Sink();
        var engine = new RemapEngine(sink, host)
        {
            Keymap = Keymap.Empty("k").With(30, new KeyboardBinding(46, Modifiers.Ctrl, Turbo: true)),
        };
        engine.Key(30, 1);
        var error = await host.Error.Task.WaitAsync(TimeSpan.FromSeconds(5), TestContext.Current.CancellationToken);
        Assert.Equal("primary press failure", error.Message);
        Assert.Equal("cleanup failure", Assert.IsType<IOException>(error.Data["ViperSynapse.CleanupFailure"]).Message);
        sink.FailCleanup = false;
        engine.Key(30, 0);
    }

    [Fact]
    public Task Opening_another_shortcut_does_not_show_stale_change_warning() => HeadlessApp.Session.Dispatch(() =>
    {
        var main = new MainViewModel(new DaemonClient(), new AppPreferences(), false);
        var first = new DeviceInfo("fffe", "fffe", "First", "unknown", "usb-one", false, false,
            null, null, null, [], false, null, [30, 48], "available");
        var second = new DeviceInfo("ffff", "ffff", "Second", "unknown", "usb-two", false, false,
            null, null, null, [], false, null, [46, 15], "available");
        main.Tabs.Add(new DeviceViewModel(first, main));
        main.Tabs.Add(new DeviceViewModel(second, main));
        var shortcuts = new ShortcutsViewModel(main);
        var one = GlobalShortcut.Create([30, 48], new KeyboardBinding(1));
        var two = GlobalShortcut.Create([46, 15], new KeyboardBinding(1));
        shortcuts.ChangeCommand.Execute(new ShortcutRow(one, "", "", ""));
        shortcuts.ChangeCommand.Execute(new ShortcutRow(two, "", "", ""));
        Assert.Null(shortcuts.Problem);
        Assert.Equal<ushort>([46, 15], new[] { shortcuts.Key1!, shortcuts.Key2! }.Select(k => k.Source));
    }, CancellationToken.None);
}
