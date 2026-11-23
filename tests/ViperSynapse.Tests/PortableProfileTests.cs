using ViperSynapse.Core.Bindings;
using ViperSynapse.Core.Lighting;
using ViperSynapse.Service;

namespace ViperSynapse.Tests;

public class PortableProfileTests
{
    const string Device = "00e3";

    [Fact]
    public void Export_includes_only_referenced_macros_and_import_keeps_the_current_profile_active()
    {
        var child = Macro.Create("Scroll", [new ScrollStep(1)]);
        var parent = Macro.Create("Parent", [new MacroCallStep(child.Id)]);
        var unrelated = Macro.Create("Unrelated", []);
        var source = DeviceSettings.Create();
        var profile = source.Active with
        {
            Name = "Combat profile",
            Keymaps = [Keymap.Empty("Combat").With(SourceKeys.WheelUp, new MacroBinding(parent.Id), hypershift: true)],
        };
        var target = DeviceSettings.Create();
        var archive = PortableProfile.Export(Device, source with { Profiles = [profile] }, new MacroLibrary([parent, unrelated, child]), profile.Id);

        var (settings, macros) = PortableProfile.Read(archive.Write()).Import(Device, target, new MacroLibrary([child]));

        Assert.Equal(target.ActiveProfile, settings.ActiveProfile);
        Assert.Equal("Combat", settings.Profiles[1].Keymaps[0].Name);
        Assert.Equal(new MacroBinding(parent.Id), settings.Profiles[1].Keymaps[0].Resolve(SourceKeys.WheelUp, true));
        Assert.Equal([child.Id, parent.Id], macros.Macros.Select(m => m.Id));
    }

    [Fact]
    public void Existing_profile_requires_explicit_replacement_and_preserves_the_selected_keymap_when_possible()
    {
        var target = DeviceSettings.Create();
        target = target with { Profiles = [target.Active with { Keymaps = [Keymap.Empty("First"), Keymap.Empty("Second")] }], ActiveKeymap = 1 };
        var replacement = target.Active with { Name = "Imported", Keymaps = [Keymap.Empty("Only")] };
        var archive = new PortableProfile(1, Device, replacement, []);

        Assert.Throws<InvalidDataException>(() => archive.Import(Device, target, MacroLibrary.Empty));
        var (settings, _) = archive.Import(Device, target, MacroLibrary.Empty, replace: true);
        Assert.Equal(target.ActiveProfile, settings.ActiveProfile);
        Assert.Equal(0, settings.ActiveKeymap);
        Assert.Equal("Imported", Assert.Single(settings.Profiles).Name);
    }

    [Fact]
    public void A_matching_display_name_does_not_overwrite_a_different_profile()
    {
        var local = DeviceSettings.Create();
        var source = DeviceSettings.Create();
        var archive = PortableProfile.Export(Device, source, MacroLibrary.Empty, source.ActiveProfile);

        var (settings, _) = archive.Import(Device, local, MacroLibrary.Empty);

        Assert.Equal([local.ActiveProfile, source.ActiveProfile], settings.Profiles.Select(p => p.Id));
        Assert.All(settings.Profiles, p => Assert.Equal("Default", p.Name));
    }

    [Fact]
    public void A_global_macro_collision_cannot_replace_the_destination_macro()
    {
        var macro = Macro.Create("Imported action", [new ScrollStep(1)]);
        var profile = Profile.Create("Imported") with { Keymaps = [Keymap.Empty("Map").With(1, new MacroBinding(macro.Id))] };
        var archive = new PortableProfile(1, Device, profile, [macro]);
        var local = new MacroLibrary([macro with { Steps = [new ScrollStep(2)] }]);

        Assert.Throws<InvalidDataException>(() => archive.Import(Device, DeviceSettings.Create(), local, replace: true));
    }

    [Fact]
    public void Launch_actions_in_nested_macros_require_explicit_consent()
    {
        var child = Macro.Create("Run", [new RunStep("example")]);
        var parent = Macro.Create("Parent", [new MacroCallStep(child.Id)]);
        var profile = Profile.Create("Imported") with { Keymaps = [Keymap.Empty("Map").With(1, new MacroBinding(parent.Id))] };
        var archive = new PortableProfile(1, Device, profile, [parent, child]);
        var target = DeviceSettings.Create();

        Assert.Throws<InvalidDataException>(() => archive.Import(Device, target, MacroLibrary.Empty));
        var (_, macros) = archive.Import(Device, target, MacroLibrary.Empty, allowLaunch: true);
        Assert.Equal(2, macros.Macros.Count);
    }

    [Fact]
    public void Unknown_versions_invalid_devices_and_missing_macro_references_are_rejected()
    {
        var macroId = Guid.NewGuid();
        var profile = Profile.Create("Imported") with { Keymaps = [Keymap.Empty("Map").With(1, new MacroBinding(macroId))] };
        var archive = new PortableProfile(1, Device, profile, []);

        Assert.Throws<InvalidDataException>(() => PortableProfile.Read((archive with { Version = 2 }).Write()));
        Assert.Throws<InvalidDataException>(() => archive.Import("02eb", DeviceSettings.Create(), MacroLibrary.Empty));
        Assert.Throws<InvalidDataException>(() => archive.Import(Device, DeviceSettings.Create(), MacroLibrary.Empty));
    }

    [Fact]
    public void Invalid_led_frame_dimensions_are_rejected_before_import()
    {
        var profile = Profile.Create("Imported") with
        {
            Lighting = LightingSettings.None with { Frame = new CustomFrame(1, 7, [Rgb.RazerGreen]) },
        };
        var archive = new PortableProfile(1, Device, profile, []);

        Assert.Throws<InvalidDataException>(() => PortableProfile.Read(archive.Write()));
    }

    [Fact]
    public void A_failed_settings_file_write_does_not_import_macros()
    {
        string directory = Directory.CreateTempSubdirectory("viper-import-").FullName;
        try
        {
            var store = new SettingsStore(directory);
            var original = store.LoadDevice(Device);
            var macro = Macro.Create("Imported action", [new ScrollStep(1)]);
            var profile = Profile.Create("Imported") with
            {
                Keymaps = [Keymap.Empty("Map").With(1, new MacroBinding(macro.Id))]
            };
            Directory.CreateDirectory(Path.Combine(directory, "devices", $"{Device}.json.tmp"));
            var archive = new PortableProfile(1, Device, profile, [macro]);

            Assert.Throws<UnauthorizedAccessException>(() => store.ImportProfile(Device, archive, false, false));
            Assert.Equal(original.ActiveProfile, store.LoadDevice(Device).ActiveProfile);
            Assert.Empty(store.LoadMacros().Macros);
        }
        finally
        {
            Directory.Delete(directory, recursive: true);
        }
    }
}
