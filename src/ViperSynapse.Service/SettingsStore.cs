using System.Text.Json;
using System.Text.Json.Serialization;
using ViperSynapse.Core.Bindings;
using ViperSynapse.Core.Protocol;

namespace ViperSynapse.Service;

/// <summary>
/// Settings on disk under $XDG_CONFIG_HOME/viper-synapse: one file per device model
/// (devices/02eb.json), the macro library (macros.json) and the global shortcuts (shortcuts.json). Writes go to a temporary file that is renamed
/// over the old one, so a crash never leaves half a file. A file that cannot be read is set aside as
/// *.broken and replaced with defaults rather than stopping the service.
/// </summary>
public sealed class SettingsStore(string root)
{
    readonly Lock _gate = new();
    readonly string _epoch = Guid.NewGuid().ToString("N");
    readonly Dictionary<string, long> _revisions = new(StringComparer.Ordinal);

    public string Root { get; } = root;

    public static string DefaultRoot()
    {
        var config = Environment.GetEnvironmentVariable("XDG_CONFIG_HOME");
        if (string.IsNullOrEmpty(config))
            config = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), ".config");
        return Path.Combine(config, "viper-synapse");
    }

    string DevicePath(string deviceKey) => Path.Combine(Root, "devices", deviceKey + ".json");
    string MacrosPath => Path.Combine(Root, "macros.json");

    string Revision(string path) => $"{_epoch}:{_revisions.GetValueOrDefault(path)}";

    void AdvanceRevision(string path) => _revisions[path] = _revisions.GetValueOrDefault(path) + 1;

    SettingsSnapshot Snapshot(string path, Func<string> read)
    {
        lock (_gate)
        {
            var json = read();
            return new(Revision(path), json);
        }
    }

    SettingsCommitResult Commit(string path, string? expected, Func<string> serialize)
    {
        lock (_gate)
        {
            if (expected != null && expected != Revision(path))
                return new(Revision(path), false);
            Save(path, serialize());
            return new(Revision(path), true);
        }
    }

    internal SettingsSnapshot DeviceSnapshot(string key) => Snapshot(DevicePath(key), () => SettingsJson.Write(LoadDevice(key)));
    internal SettingsSnapshot MacrosSnapshot() => Snapshot(MacrosPath, () => SettingsJson.Write(LoadMacros()));
    internal SettingsSnapshot ShortcutsSnapshot() => Snapshot(ShortcutsPath, () => SettingsJson.Write(LoadShortcuts()));

    internal SettingsCommitResult CommitDevice(string key, string? expected, DeviceSettings settings) =>
        Commit(DevicePath(key), expected, () => SettingsJson.Write(settings));

    internal SettingsCommitResult CommitMacros(string? expected, MacroLibrary macros) =>
        Commit(MacrosPath, expected, () => SettingsJson.Write(macros));

    internal SettingsCommitResult CommitShortcuts(string? expected, ShortcutList shortcuts) =>
        Commit(ShortcutsPath, expected, () => SettingsJson.Write(shortcuts));

    /// <summary>A device seen for the first time gets default settings, saved at once so their ids stay put.</summary>
    public DeviceSettings LoadDevice(string deviceKey) => Load(DevicePath(deviceKey), SettingsJson.ReadSettings, () =>
    {
        var created = DeviceSettings.Create();
        Save(DevicePath(deviceKey), SettingsJson.Write(created));
        return created;
    });

    public void SaveDevice(string deviceKey, DeviceSettings settings) => Save(DevicePath(deviceKey), SettingsJson.Write(settings));

    internal bool UpdateDevice(string key, Func<DeviceSettings, DeviceSettings> update)
    {
        lock (_gate)
        {
            var current = LoadDevice(key);
            var settings = update(current);
            if (settings == current)
                return false;
            SaveDevice(key, settings);
            return true;
        }
    }

    internal MacroLibrary ImportProfile(string key, PortableProfile archive, bool replace, bool allowLaunch)
    {
        lock (_gate)
        {
            var local = LoadMacros();
            var (settings, macros) = archive.Import(key, LoadDevice(key), local, replace, allowLaunch);
            if (ReferenceEquals(local, macros))
            {
                SaveDevice(key, settings);
                return macros;
            }

            string path = DevicePath(key);
            string pending = Stage(path, SettingsJson.Write(settings));
            bool hadMacros = File.Exists(MacrosPath);
            try
            {
                SaveMacros(macros);
                try
                {
                    File.Move(pending, path, overwrite: true);
                    AdvanceRevision(path);
                }
                catch (Exception e) when (e is IOException or UnauthorizedAccessException)
                {
                    if (hadMacros)
                        SaveMacros(local);
                    else
                    {
                        File.Delete(MacrosPath);
                        AdvanceRevision(MacrosPath);
                    }
                    throw;
                }
            }
            finally
            {
                File.Delete(pending);
            }
            return macros;
        }
    }

    public MacroLibrary LoadMacros() => Load(MacrosPath, SettingsJson.ReadMacros, () => MacroLibrary.Empty);

    public void SaveMacros(MacroLibrary macros) => Save(MacrosPath, SettingsJson.Write(macros));

    string ShortcutsPath => Path.Combine(Root, "shortcuts.json");

    public ShortcutList LoadShortcuts() => Load(ShortcutsPath, SettingsJson.ReadShortcuts, () => ShortcutList.Empty);

    public void SaveShortcuts(ShortcutList shortcuts) => Save(ShortcutsPath, SettingsJson.Write(shortcuts));

    internal IReadOnlyDictionary<byte, string> OnboardNames(string serial, IReadOnlyList<byte> slots,
        byte? changedSlot, string? name)
    {
        lock (_gate)
        {
            string path = Path.Combine(Root, "onboard-slot-names.json");
            var names = Load(path, ReadOnboardNames, () => []);
            if (changedSlot is byte slot)
            {
                var updated = names.Where(entry => entry.Serial != serial || entry.Slot != slot).ToList();
                if (name != null)
                    updated.Add(new OnboardSlotLabel(serial, slot, name));
                if (!updated.SequenceEqual(names))
                    Save(path, JsonSerializer.Serialize(updated.ToArray(), OnboardNamesJson.Default.OnboardSlotLabelArray));
                names = updated.ToArray();
            }
            return names.Where(entry => entry.Serial == serial && slots.Contains(entry.Slot))
                .GroupBy(entry => entry.Slot).ToDictionary(group => group.Key, group => group.Last().Name);
        }
    }

    static OnboardSlotLabel[] ReadOnboardNames(string json)
    {
        var labels = JsonSerializer.Deserialize(json, OnboardNamesJson.Default.OnboardSlotLabelArray);
        if (labels == null || labels.Any(entry => entry == null || string.IsNullOrWhiteSpace(entry.Serial) ||
            string.IsNullOrWhiteSpace(entry.Name) || entry.Name.Any(char.IsControl) || entry.Slot is < 1 or > 5))
            throw new InvalidDataException("Invalid onboard slot names.");
        return labels;
    }

    T Load<T>(string path, Func<string, T> parse, Func<T> fallback)
    {
        lock (_gate)
        {
            if (!File.Exists(path))
                return fallback();
            try
            {
                return parse(File.ReadAllText(path));
            }
            catch (Exception e) when (e is System.Text.Json.JsonException or InvalidDataException or NotSupportedException)
            {
                Console.Error.WriteLine($"{path}: unreadable ({e.Message}); kept as {path}.broken, starting from defaults.");
                File.Move(path, path + ".broken", overwrite: true);
                return fallback();
            }
        }
    }

    void Save(string path, string json)
    {
        lock (_gate)
        {
            File.Move(Stage(path, json), path, overwrite: true);
            AdvanceRevision(path);
        }
    }

    static string Stage(string path, string json)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        var pending = path + ".tmp";
        File.WriteAllText(pending, json);
        return pending;
    }
}

internal sealed record OnboardSlotLabel(string Serial, byte Slot, string Name);

[JsonSerializable(typeof(OnboardSlotLabel[]))]
internal sealed partial class OnboardNamesJson : JsonSerializerContext;
