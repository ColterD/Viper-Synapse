using ViperSynapse.Core.Bindings;
using ViperSynapse.Service;

namespace ViperSynapse.Tests;

public sealed class SettingsRecoveryTests : IDisposable
{
    readonly DirectoryInfo _directory = Directory.CreateTempSubdirectory("viper-settings-recovery-");

    public void Dispose() => _directory.Delete(recursive: true);

    [Fact]
    public void Invalid_commits_leave_each_settings_snapshot_and_revision_intact()
    {
        var store = new SettingsStore(_directory.FullName);
        var device = store.DeviceSnapshot("02eb");
        var validMacro = Macro.Create("Jump", [new KeyStep(57, true), new KeyStep(57, false)]);
        store.SaveMacros(new MacroLibrary([validMacro]));
        store.SaveShortcuts(new ShortcutList([GlobalShortcut.Create([29, 57], new MacroBinding(validMacro.Id))]));
        var macros = store.MacrosSnapshot();
        var shortcuts = store.ShortcutsSnapshot();
        string[] paths = [Path.Combine(_directory.FullName, "devices", "02eb.json"),
            Path.Combine(_directory.FullName, "macros.json"), Path.Combine(_directory.FullName, "shortcuts.json")];
        var originalContents = paths.Select(File.ReadAllText).ToArray();

        Assert.Throws<InvalidDataException>(() => store.CommitDevice("02eb", device.Revision,
            new DeviceSettings([], Guid.Empty, 0)));
        Assert.Throws<InvalidDataException>(() => store.CommitMacros(macros.Revision,
            new MacroLibrary([Macro.Create("Broken", [null!])])));
        Assert.Throws<InvalidDataException>(() => store.CommitShortcuts(shortcuts.Revision,
            new ShortcutList([GlobalShortcut.Create([30, 31], null!)])));

        Assert.Equal(device, store.DeviceSnapshot("02eb"));
        Assert.Equal(macros, store.MacrosSnapshot());
        Assert.Equal(shortcuts, store.ShortcutsSnapshot());
        Assert.Equal(originalContents, paths.Select(File.ReadAllText));

        Assert.True(store.CommitDevice("02eb", device.Revision, SettingsJson.ReadSettings(device.Json)).Saved);
        Assert.True(store.CommitMacros(macros.Revision, SettingsJson.ReadMacros(macros.Json)).Saved);
        Assert.True(store.CommitShortcuts(shortcuts.Revision, SettingsJson.ReadShortcuts(shortcuts.Json)).Saved);
    }

    [Fact]
    public void Invalid_settings_preserve_each_original_in_a_separate_recovery_copy()
    {
        var store = new SettingsStore(_directory.FullName);
        const string device = "02eb";
        store.LoadDevice(device);
        string path = Path.Combine(_directory.FullName, "devices", device + ".json");
        const string legacyCopy = "previous recovery copy";
        const string invalidState = """{"profiles":[],"activeProfile":"00000000-0000-0000-0000-000000000000","activeKeymap":0}""";
        const string invalidJson = "{";
        File.WriteAllText(path + ".broken", legacyCopy);

        File.WriteAllText(path, invalidState);
        Assert.Single(store.LoadDevice(device).Profiles);
        File.WriteAllText(path, invalidJson);
        Assert.Single(store.LoadDevice(device).Profiles);

        var originals = Directory.GetFiles(Path.GetDirectoryName(path)!, device + ".json.broken*")
            .Select(File.ReadAllText).ToArray();
        Assert.Equal(3, originals.Length);
        Assert.Contains(legacyCopy, originals);
        Assert.Contains(invalidState, originals);
        Assert.Contains(invalidJson, originals);
    }

    [Fact]
    public void Recovering_invalid_macro_and_shortcut_files_invalidates_their_open_snapshots()
    {
        var store = new SettingsStore(_directory.FullName);
        var macros = store.MacrosSnapshot();
        var shortcuts = store.ShortcutsSnapshot();
        string macroPath = Path.Combine(_directory.FullName, "macros.json");
        string shortcutPath = Path.Combine(_directory.FullName, "shortcuts.json");
        const string badMacros = """{"macros":[null]}""";
        const string badShortcuts = """{"shortcuts":[null]}""";
        File.WriteAllText(macroPath, badMacros);
        File.WriteAllText(shortcutPath, badShortcuts);

        Assert.Empty(store.LoadMacros().Macros);
        Assert.Empty(store.LoadShortcuts().Shortcuts);

        Assert.Equal(badMacros, File.ReadAllText(Assert.Single(Directory.GetFiles(_directory.FullName, "macros.json.broken-*"))));
        Assert.Equal(badShortcuts, File.ReadAllText(Assert.Single(Directory.GetFiles(_directory.FullName, "shortcuts.json.broken-*"))));
        Assert.False(store.CommitMacros(macros.Revision, MacroLibrary.Empty).Saved);
        Assert.False(store.CommitShortcuts(shortcuts.Revision, ShortcutList.Empty).Saved);
    }

    [Fact]
    public void Settings_keep_legacy_selection_fallbacks_and_ignore_additive_fields()
    {
        const string legacy = """
            {"profiles":[{"id":"6998f8d5-ee67-4c15-a14f-3e4918aa2aaa","name":"Old","keymaps":[],"linkedPrograms":[],
                         "previousSetting":true}],"activeKeymap":18}
            """;

        var settings = SettingsJson.ReadSettings(legacy);

        Assert.Equal("Old", settings.Active.Name);
        Assert.Empty(settings.ActiveKeymapOrFirst.Standard);
        Assert.Null(settings.Active.Scroll);
        Assert.Equal(settings.Active.Id, SettingsJson.ReadSettings(SettingsJson.Write(settings)).Active.Id);
    }

    [Fact]
    public void Invalid_ids_and_bindings_are_rejected_after_deserialization()
    {
        const string duplicateProfiles = """
            {"profiles":[
                {"id":"6998f8d5-ee67-4c15-a14f-3e4918aa2aaa","name":"One","keymaps":[],"linkedPrograms":[]},
                {"id":"6998f8d5-ee67-4c15-a14f-3e4918aa2aaa","name":"Two","keymaps":[],"linkedPrograms":[]}]}
            """;
        const string badBinding = """
            {"shortcuts":[{"id":"6998f8d5-ee67-4c15-a14f-3e4918aa2aaa","keys":[29,57],
                           "binding":{"kind":"keyboard","key":65535}}]}
            """;

        Assert.Throws<InvalidDataException>(() => SettingsJson.ReadSettings(duplicateProfiles));
        Assert.Throws<InvalidDataException>(() => SettingsJson.ReadShortcuts(badBinding));
    }
}
