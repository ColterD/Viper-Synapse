using System.Diagnostics.CodeAnalysis;
using ViperSynapse.Core.Lighting;

namespace ViperSynapse.Core.Bindings;

internal static class SettingsValidation
{
    const Modifiers KnownModifiers = Modifiers.Ctrl | Modifiers.Shift | Modifiers.Alt | Modifiers.Super;
    const ushort MaxLinuxKeyCode = 0x2ff;

    internal static void Validate([NotNull] DeviceSettings? settings)
    {
        if (settings?.Profiles is not { Count: > 0 } profiles)
            throw new InvalidDataException("Device settings must contain at least one profile.");

        var ids = new HashSet<Guid>();
        foreach (var profile in profiles)
        {
            if (profile is null || profile.Id == Guid.Empty || !ids.Add(profile.Id) ||
                string.IsNullOrWhiteSpace(profile.Name) || profile.LinkedPrograms is null ||
                profile.LinkedPrograms.Any(string.IsNullOrWhiteSpace) ||
                profile.Keymaps is not { Count: <= Profile.MaxKeymaps } ||
                !Enum.IsDefined(profile.Thumbstick) || !Enum.IsDefined(profile.Interruption))
                throw new InvalidDataException("Device settings contain an invalid profile or a duplicate profile ID.");

            foreach (var map in profile.Keymaps)
            {
                if (map is null || string.IsNullOrWhiteSpace(map.Name) ||
                    !ValidLayer(map.Standard) || !ValidLayer(map.Hypershift))
                    throw new InvalidDataException("A profile contains an invalid keymap or binding.");
            }

            if (profile.Lighting is { } lighting && !ValidLighting(lighting))
                throw new InvalidDataException("A profile contains invalid lighting settings.");
            if (profile.Performance is { } performance &&
                (performance.Stages is not { Count: <= PerformanceSettings.MaxStages } || performance.Stages.Any(s => s is null)))
                throw new InvalidDataException("A profile contains invalid DPI stages.");
            if (profile.Scroll?.WheelMode is { } wheelMode && !Enum.IsDefined(wheelMode))
                throw new InvalidDataException("A profile contains an invalid wheel mode.");
        }
    }

    internal static void Validate([NotNull] MacroLibrary? library)
    {
        if (library?.Macros is not { } macros)
            throw new InvalidDataException("The macro library is missing its macro list.");

        var ids = new HashSet<Guid>();
        foreach (var macro in macros)
        {
            if (macro is null || macro.Id == Guid.Empty || !ids.Add(macro.Id) ||
                string.IsNullOrWhiteSpace(macro.Name) || !Enum.IsDefined(macro.Type) ||
                macro.Steps is null || !macro.Steps.All(ValidStep))
                throw new InvalidDataException("The macro library contains an invalid macro, invalid step, or duplicate macro ID.");

            // Both JSON serialization and the macro editor use the computed duration.
            try { _ = macro.Duration; }
            catch (OverflowException ex) { throw new InvalidDataException("A macro duration is too large.", ex); }
        }
    }

    internal static void Validate([NotNull] ShortcutList? list)
    {
        if (list?.Shortcuts is not { } shortcuts)
            throw new InvalidDataException("Global shortcuts are missing their shortcut list.");

        var ids = new HashSet<Guid>();
        foreach (var shortcut in shortcuts)
        {
            if (shortcut is null || shortcut.Id == Guid.Empty || !ids.Add(shortcut.Id) ||
                shortcut.Keys is not { Count: > 0 } keys || !keys.All(IsSourceKey) ||
                keys.Distinct().Count() != keys.Count || !ValidBinding(shortcut.Binding))
                throw new InvalidDataException("Global shortcuts contain an invalid shortcut, invalid binding, or duplicate shortcut ID.");
        }
    }

    static bool ValidLayer(IReadOnlyDictionary<ushort, Binding>? layer) =>
        layer is not null && layer.All(pair => IsSourceKey(pair.Key) && ValidBinding(pair.Value));

    static bool ValidLighting(LightingSettings lighting)
    {
        if (lighting.Zones is null || lighting.Zones.Any(pair => string.IsNullOrWhiteSpace(pair.Key) ||
            pair.Value?.Effect is not { } effect || !Enum.IsDefined(effect.Kind) || !Enum.IsDefined(effect.Colors)))
            return false;
        return lighting.Frame is not { } frame || (frame.Rows > 0 && frame.Columns > 0 &&
            frame.Colors is not null && (long)frame.Rows * frame.Columns == frame.Colors.Count);
    }

    static bool IsLinuxKey(ushort code) => code is > 0 and <= MaxLinuxKeyCode;

    static bool IsSourceKey(ushort code) => IsLinuxKey(code) || SourceKeys.IsWheel(code) || SourceKeys.IsStick(code);

    static bool ValidBinding(Binding? binding) => binding switch
    {
        DefaultBinding or DisableBinding or HypershiftBinding => true,
        KeyboardBinding key => IsLinuxKey(key.Key) && (key.Modifiers & ~KnownModifiers) == 0,
        MouseBinding mouse => Enum.IsDefined(mouse.Function),
        MacroBinding macro => macro.MacroId != Guid.Empty && Enum.IsDefined(macro.Playback),
        SwitchProfileBinding profile => Enum.IsDefined(profile.Direction) &&
            (profile.Direction != CycleDirection.Specific || profile.ProfileId is Guid id && id != Guid.Empty),
        SwitchKeymapBinding map => Enum.IsDefined(map.Direction),
        LaunchBinding launch => launch.Command is not null,
        TextBinding text => text.Text is not null,
        GamepadBinding gamepad => Enum.IsDefined(gamepad.Control),
        _ => false,
    };

    static bool ValidStep(MacroStep? step) => step switch
    {
        KeyStep key => IsLinuxKey(key.Code),
        MacroCallStep call => call.MacroId != Guid.Empty,
        LaunchStep launch => launch.Target is not null,
        RunStep run => run.Command is not null,
        TextStep text => text.Text is not null,
        DelayStep or RandomDelayStep or ScrollStep or LoopStartStep or LoopEndStep => true,
        _ => false,
    };
}
