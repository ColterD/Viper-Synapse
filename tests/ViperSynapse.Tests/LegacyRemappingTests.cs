using ViperSynapse.App.Services;
using ViperSynapse.App.ViewModels;
using ViperSynapse.Core.Bindings;
using ViperSynapse.Core.Engine;
using ViperSynapse.Core.Input;
using ViperSynapse.Hardware;
using ViperSynapse.Hardware.Linux;
using DeviceManager = ViperSynapse.Service.DeviceManager;
using SettingsStore = ViperSynapse.Service.SettingsStore;

namespace ViperSynapse.Tests;

public class LegacyRemappingTests
{
    const ushort Q = 16;
    const ushort LeftCtrl = KeyCodes.LeftCtrl;
    const ushort F1 = 59;
    const string KeyboardNode = "/dev/input/emulated-keyboard";
    const string ExtraNode = "/dev/input/emulated-extra";

    [Theory]
    [InlineData(183, 5)]
    [InlineData(2, 12)]
    public void Repeated_legacy_buttons_do_not_release_a_held_key_or_modifier(int first, int count)
    {
        var sink = new Output();
        var keymap = Keymap.Empty("Legacy controls");
        foreach (int source in Enumerable.Range(first, count))
            keymap = keymap.With((ushort)source, new KeyboardBinding(F1));
        var engine = new RemapEngine(sink, sink) { Keymap = keymap };
        using var session = new DeviceSession(engine, ThumbstickMode.Analog);

        SendKey(session, KeyboardNode, LeftCtrl, 1);
        SendKey(session, KeyboardNode, Q, 1);
        foreach (int source in Enumerable.Range(first, count))
        {
            SendKey(session, ExtraNode, (ushort)source, 1);
            SendKey(session, ExtraNode, (ushort)source, 2);
            SendKey(session, ExtraNode, (ushort)source, 0);
        }

        Assert.Equal(new[] { (LeftCtrl, 1), (Q, 1) }
            .Concat(Enumerable.Range(0, count).SelectMany(_ => new[] { (F1, 1), (F1, 0) })),
            sink.Keys);
        SendKey(session, KeyboardNode, Q, 0);
        SendKey(session, KeyboardNode, LeftCtrl, 0);
        Assert.Equal(new[] { (Q, 0), (LeftCtrl, 0) }, sink.Keys.TakeLast(2));
    }

    [Fact]
    public void An_unmatched_release_on_another_node_does_not_release_the_original_hold()
    {
        var sink = new Output();
        using var session = new DeviceSession(new RemapEngine(sink, sink), ThumbstickMode.Analog);

        SendKey(session, KeyboardNode, Q, 1);
        SendKey(session, ExtraNode, Q, 0);
        SendKey(session, KeyboardNode, Q, 2);
        SendKey(session, KeyboardNode, Q, 0);

        Assert.Equal(new[] { (Q, 1), (Q, 2), (Q, 0) }, sink.Keys);
    }

    [Fact]
    public void A_key_owned_by_two_input_nodes_remains_down_until_both_release_it()
    {
        var sink = new Output();
        using var session = new DeviceSession(new RemapEngine(sink, sink), ThumbstickMode.Analog);

        SendKey(session, KeyboardNode, Q, 1);
        SendKey(session, ExtraNode, Q, 1);
        SendKey(session, KeyboardNode, Q, 0);
        SendKey(session, ExtraNode, Q, 2);
        SendKey(session, ExtraNode, Q, 0);

        Assert.Equal(new[] { (Q, 1), (Q, 2), (Q, 0) }, sink.Keys);
    }

    [Theory]
    [InlineData(0x0221, 183)]
    [InlineData(0x0053, 2)]
    public void A_saved_button_binding_waits_for_its_advertised_evdev_source(ushort productId, ushort source)
    {
        var root = Directory.CreateTempSubdirectory("viper-legacy-remap-");
        try
        {
            string key = productId.ToString("x4");
            var store = new SettingsStore(root.FullName);
            var settings = store.LoadDevice(key);
            var keymap = settings.ActiveKeymapOrFirst.With(source, new KeyboardBinding(F1));
            store.SaveDevice(key, settings with { Profiles = [settings.Active with { Keymaps = [keymap] }] });
            var sink = new Output();
            int sessions = 0;
            using var manager = new DeviceManager(store, sink)
            {
                StartInputSession = (paths, engine, mode, _) =>
                {
                    sessions++;
                    Assert.Equal(new[] { KeyboardNode, ExtraNode }, paths);
                    var session = new DeviceSession(engine, mode);
                    SendKey(session, ExtraNode, source, 1);
                    return session;
                }
            };
            var regular = new InputNode(KeyboardNode, 0x1532, productId, "Legacy keyboard input", "usb-legacy/input0");
            var extra = new InputNode(ExtraNode, 0x1532, productId, "Legacy extra input", "usb-legacy/input1");
            void Refresh(IReadOnlyList<ushort> codes, string status) =>
                manager.Refresh([], [regular, extra], _ => false, _ => throw new InvalidOperationException("Unexpected feature access"),
                    _ => new DiscoveredControls(codes, status));

            Refresh([Q], "available");
            Refresh([Q, source], "partial");
            Assert.Equal(0, sessions);
            Assert.Empty(sink.Keys);

            Refresh([Q, source], "available");
            Assert.Equal(1, sessions);
            Assert.True(Assert.Single(manager.Devices).Remapping);
            Assert.Equal(new[] { (F1, 1) }, sink.Keys);
        }
        finally
        {
            root.Delete(recursive: true);
        }
    }

    [Theory]
    [InlineData("0221", "keyboard", 183, 5, "macro keys", "M")]
    [InlineData("0053", "mouse", 2, 12, "thumb grid", "Thumb ")]
    public Task Customize_labels_and_edits_only_advertised_legacy_buttons(
        string productId, string type, int first, int count, string group, string prefix) =>
        HeadlessApp.Session.Dispatch(() =>
        {
            ushort[] sources = Enumerable.Range(first, count).Select(code => (ushort)code).ToArray();
            var info = new DeviceInfo(productId, productId, "Legacy Razer device", type, "usb-legacy",
                true, true, null, null, null, [], false, null, [Q, ..sources], "available", "wired");
            var main = new MainViewModel(new DaemonClient(), new AppPreferences(), false);
            var device = new DeviceViewModel(info, main);

            var rows = device.Rows.Where(row => row.Key.Group == group).ToArray();
            Assert.Equal(sources, rows.Select(row => row.Key.Source));
            Assert.Equal(Enumerable.Range(1, count).Select(i => prefix + i), rows.Select(row => row.Key.Label));
            Assert.All(rows, row => Assert.True(row.Bindable));
            Assert.Equal("KEY_Q", Assert.Single(device.Rows, row => row.Key.Source == Q).Key.Label);

            device.SelectedKey = rows[^1];
            var editor = Assert.IsType<BindingEditorViewModel>(device.Editor);
            Assert.Equal(sources[^1], editor.Row.Key.Source);
            editor.SelectedFunction = BindingEditorViewModel.Functions.Single(function => function.Kind == FunctionKind.Keyboard);
            editor.CapturedKey = F1;
            editor.SaveCommand.Execute(null);
            Assert.Equal(new KeyboardBinding(F1), device.CurrentSettings.ActiveKeymapOrFirst.Standard[sources[^1]]);

            device.Info = info with { InputControls = [Q, sources[0]] };
            Assert.Equal(sources[0], Assert.Single(device.Rows, row => row.Key.Group == group).Key.Source);
            device.Info = info with { InputControlsStatus = "partial" };
            Assert.All(device.Rows, row => Assert.False(row.Bindable));
        }, CancellationToken.None);

    [Theory]
    [InlineData(0x116, "Tilt Left", "Tilt Wheel")]
    [InlineData(0x115, "Tilt Right", "Tilt Wheel")]
    [InlineData(0x118, "DPI Up", "DPI Buttons")]
    [InlineData(0x119, "DPI Down", "DPI Buttons")]
    public Task Customize_saves_an_available_Naga_wheel_or_DPI_button(
        ushort source, string label, string filter) => HeadlessApp.Session.Dispatch(() =>
    {
        var info = new DeviceInfo("0053", "0053", "Naga Chroma", "mouse", "usb-legacy",
            true, true, null, null, null, [], false, null, [2, source], "available", "wired");
        var main = new MainViewModel(new DaemonClient(), new AppPreferences(), false);
        var device = new DeviceViewModel(info, main);

        Assert.Equal(new[] { "All Buttons", "Thumb Grid", filter }, device.KeyFilters);
        device.SelectedKeyFilter = filter;
        var row = Assert.Single(device.KeyLines, line => line.Left != null).Left!;
        Assert.Equal(source, row.Key.Source);
        Assert.Equal(label, row.ListCategory);
        Assert.True(row.Bindable);

        device.SelectKeyCommand.Execute(row);
        var editor = Assert.IsType<BindingEditorViewModel>(device.Editor);
        Assert.Equal(filter == "DPI Buttons", editor.ShowNagaDpiNotice);
        editor.SelectedFunction = BindingEditorViewModel.Functions.Single(function => function.Kind == FunctionKind.Keyboard);
        editor.CapturedKey = F1;
        editor.SaveCommand.Execute(null);
        Assert.Equal(new KeyboardBinding(F1), device.CurrentSettings.ActiveKeymapOrFirst.Standard[source]);
        Assert.Equal(label, row.ListCategory);
        Assert.True(row.Changed);

        device.Info = info with { InputControlsStatus = "partial" };
        Assert.All(device.Rows, available => Assert.False(available.Bindable));
        device.Info = info with { InputControls = [2] };
        Assert.DoesNotContain(filter, device.KeyFilters);
        Assert.Equal("Thumb 1", Assert.Single(device.Rows).ListCategory);
    }, CancellationToken.None);

    static void SendKey(DeviceSession session, string node, ushort code, int value)
    {
        session.Dispatch(node, new InputEvent { Type = EventTypes.Key, Code = code, Value = value });
        session.Dispatch(node, new InputEvent { Type = EventTypes.Syn, Code = EventTypes.SynReport });
    }

    sealed class Output : IInputSink, IEngineHost
    {
        public List<(ushort Code, int Value)> Keys { get; } = [];
        public void Key(ushort code, int value) => Keys.Add((code, value));
        public void Relative(ushort axis, int value) { }
        public void Sync() { }
        public ShortcutList Shortcuts => ShortcutList.Empty;
        public Macro? FindMacro(Guid id) => null;
        public void Report(Exception error) => throw error;
        public void Launch(string command) { }
        public void Open(string target) { }
        public void SwitchProfile(CycleDirection direction, Guid? profileId) { }
        public void SwitchKeymap(CycleDirection direction, int keymap) { }
        public void Gamepad(GamepadControl control, bool down) { }
    }
}
