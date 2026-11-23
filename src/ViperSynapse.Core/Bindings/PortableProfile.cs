using System.Text.Json;

namespace ViperSynapse.Core.Bindings;

/// <summary>One device profile and the macros it needs, for local interchange.</summary>
public sealed record PortableProfile(int Version, string Device, Profile Profile, IReadOnlyList<Macro> Macros)
{
    public const int CurrentVersion = 1;

    public string Write() => JsonSerializer.Serialize(this, SettingsJsonContext.Default.PortableProfile);

    public static PortableProfile Read(string json)
    {
        var archive = JsonSerializer.Deserialize(json, SettingsJsonContext.Default.PortableProfile)
            ?? throw new InvalidDataException("Empty profile archive.");
        archive.Validate();
        return archive;
    }

    public static PortableProfile Export(string device, DeviceSettings settings, MacroLibrary library, Guid profileId)
    {
        var profile = settings.Profiles.FirstOrDefault(p => p.Id == profileId)
            ?? throw new InvalidDataException($"No profile with ID {profileId}.");
        return new PortableProfile(CurrentVersion, device, profile, ReferencedMacros(profile, library.Macros));
    }

    public (DeviceSettings Settings, MacroLibrary Macros) Import(string device, DeviceSettings settings, MacroLibrary library,
        bool replace = false, bool allowLaunch = false)
    {
        Validate();
        if (!string.Equals(Device, device, StringComparison.OrdinalIgnoreCase))
            throw new InvalidDataException($"This profile is for device {Device}, not {device}.");

        var profiles = settings.Profiles.ToList();
        int index = profiles.FindIndex(p => p.Id == Profile.Id);
        if (index >= 0 && !replace)
            throw new InvalidDataException($"Profile {Profile.Id} already exists. Use --replace to update it.");
        var linked = Profile.LinkedPrograms.ToHashSet(StringComparer.OrdinalIgnoreCase);
        if (profiles.Any(p => p.Id != Profile.Id && p.LinkedPrograms.Any(linked.Contains)))
            throw new InvalidDataException("An imported linked program is already assigned to another profile.");

        foreach (var binding in Bindings(Profile).OfType<SwitchProfileBinding>())
            if (binding.Direction == CycleDirection.Specific && binding.ProfileId != Profile.Id && !profiles.Any(p => p.Id == binding.ProfileId))
                throw new InvalidDataException($"The profile switches to missing profile {binding.ProfileId}.");

        var referenced = ReferencedMacros(Profile, Macros);
        if (referenced.Count != Macros.Count)
            throw new InvalidDataException("The archive contains unused macros.");
        if (!allowLaunch && (Bindings(Profile).Any(b => b is LaunchBinding)
            || referenced.Any(m => m.Steps.Any(s => s is RunStep or LaunchStep))))
            throw new InvalidDataException("This profile launches programs, commands, files or links. Import a trusted archive with --allow-launch to permit this.");

        var macros = library.Macros.ToList();
        foreach (var macro in referenced)
        {
            if (library.Find(macro.Id) is not { } existing)
                macros.Add(macro);
            else if (existing.Name != macro.Name || existing.Type != macro.Type || !existing.Steps.SequenceEqual(macro.Steps))
                throw new InvalidDataException($"Macro {macro.Id} already exists with different content.");
        }

        if (index < 0)
            profiles.Add(Profile);
        else
            profiles[index] = Profile;
        int keymap = settings.ActiveProfile == Profile.Id ? Math.Clamp(settings.ActiveKeymap, 0, Profile.Keymaps.Count - 1) : settings.ActiveKeymap;
        return (settings with { Profiles = profiles, ActiveKeymap = keymap },
            macros.Count == library.Macros.Count ? library : new MacroLibrary(macros));
    }

    void Validate()
    {
        if (Version != CurrentVersion)
            throw new InvalidDataException($"Unsupported profile archive version {Version}.");
        if (Device is not { Length: 4 } || !Device.All(Uri.IsHexDigit))
            throw new InvalidDataException("The profile archive has an invalid device key.");
        if (Profile is null || Profile.Id == Guid.Empty || string.IsNullOrWhiteSpace(Profile.Name) || Profile.LinkedPrograms is null
            || Profile.LinkedPrograms.Any(string.IsNullOrWhiteSpace)
            || Profile.Keymaps is not { Count: >= 1 and <= ViperSynapse.Core.Bindings.Profile.MaxKeymaps }
            || Profile.Keymaps.Any(k => k is null || string.IsNullOrWhiteSpace(k.Name) || k.Standard is null || k.Hypershift is null
                || k.Standard.Values.Any(b => b is null) || k.Hypershift.Values.Any(b => b is null)))
            throw new InvalidDataException("The profile archive has an invalid profile.");
        if (Profile.Lighting is { } lighting && (lighting.Zones is null || lighting.Zones.Values.Any(z => z is null || z.Effect is null)
            || lighting.Frame is { } frame && (frame.Rows <= 0 || frame.Columns <= 0 || frame.Colors is null
                || (long)frame.Rows * frame.Columns != frame.Colors.Count)))
            throw new InvalidDataException("The profile archive has invalid lighting settings.");
        if (Profile.Performance is { } performance && (performance.Stages is null || performance.Stages.Any(s => s is null)))
            throw new InvalidDataException("The profile archive has invalid DPI stages.");
        if (Macros is null || Macros.Any(m => m is null || m.Id == Guid.Empty || string.IsNullOrWhiteSpace(m.Name)
                || m.Steps is null || m.Steps.Any(s => s is null))
            || Macros.Select(m => m.Id).Distinct().Count() != Macros.Count)
            throw new InvalidDataException("The profile archive has an invalid macro library.");
    }

    static IEnumerable<Binding> Bindings(Profile profile) =>
        profile.Keymaps.SelectMany(k => k.Standard.Values.Concat(k.Hypershift.Values));

    static IReadOnlyList<Macro> ReferencedMacros(Profile profile, IReadOnlyList<Macro> library)
    {
        var available = library.ToDictionary(m => m.Id);
        var pending = new Queue<Guid>(Bindings(profile).OfType<MacroBinding>().Select(b => b.MacroId));
        var seen = new HashSet<Guid>();
        var result = new List<Macro>();
        while (pending.TryDequeue(out var id))
        {
            if (!seen.Add(id))
                continue;
            if (!available.TryGetValue(id, out var macro))
                throw new InvalidDataException($"Referenced macro {id} is missing.");
            result.Add(macro);
            foreach (var step in macro.Steps.OfType<MacroCallStep>())
                pending.Enqueue(step.MacroId);
        }
        return result;
    }
}
