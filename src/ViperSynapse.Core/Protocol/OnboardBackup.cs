using System.Text.Json;
using System.Text.Json.Serialization;
using System.Text.Json.Serialization.Metadata;
using ViperSynapse.Core.Lighting;

namespace ViperSynapse.Core.Protocol;

public sealed record OnboardBackup(int Version, byte SourceSlot, int ActiveStage,
    IReadOnlyList<DpiStage> Stages, IReadOnlyList<OnboardBackupBinding> Bindings,
    IReadOnlyList<OnboardBackupBrightness>? Brightness = null, OnboardBackupWheel? Wheel = null,
    IReadOnlyList<OnboardBackupEffect>? Effects = null);

public sealed record OnboardBackupBinding(byte Button, byte Layer, byte[] Block);

public sealed record OnboardBackupBrightness(byte Led, byte Value)
{
    public const byte Scroll = 0x01;
    public const byte Logo = 0x04;
    public const byte Underglow = 0x0A;
}

public sealed record OnboardBackupWheel(bool? SmartReel = null, bool? Acceleration = null);

public sealed record OnboardBackupEffect(byte Led, byte[] Body);

public sealed record OnboardArchiveRequest(string Serial, byte Slot, OnboardBackup? Backup = null);

public enum OnboardRestoreStatus { Restored, RolledBack, RecoveryRequired }

public sealed record OnboardRestoreResult(OnboardRestoreStatus Status, string Message);

public static class OnboardBackupCodec
{
    public const int CurrentVersion = 4;
    public const int MaxJsonLength = 16384;

    public static bool IsSupportedBinding(byte id, byte layer, ReadOnlySpan<byte> block)
    {
        var button = BasiliskOnboard.Buttons.FirstOrDefault(button => button.Id == id);
        if (button == null || layer > 1)
            return false;
        return block is [0, 1, 0, 0, 0, 0, 0] or [2, 2, _, >= 0x04 and <= 0x73, 0, 0, 0] ||
            (layer == 0 && block.SequenceEqual(button.Factory.Span));
    }

    public static bool IsSupportedEffect(byte led, ReadOnlySpan<byte> body, int version = CurrentVersion) =>
        IsArchiveLightingZone(led, version) &&
        body is [0x00, 0, 0, 0] or [0x01, 0, 0, 1, _, _, _] or [0x03, 0, 0, 0] or [0x04, 1, 0x28, 0];

    static bool IsArchiveLightingZone(byte led, int version) =>
        led is OnboardBackupBrightness.Scroll or OnboardBackupBrightness.Logo ||
        (version >= 4 && led == OnboardBackupBrightness.Underglow);

    public static void Validate(OnboardBackup backup)
    {
        if (backup.Version is < 1 or > CurrentVersion)
            throw new InvalidDataException("This version of the onboard backup is unsupported.");
        if (backup.Version == 1 && (backup.Brightness != null || backup.Wheel != null))
            throw new InvalidDataException("Lighting and wheel settings require version 2 of the onboard backup.");
        if (backup.Version < 3 && backup.Effects != null)
            throw new InvalidDataException("Lighting effects require version 3 of the onboard backup.");
        if (!BasiliskOnboard.IsSlot(backup.SourceSlot) ||
            backup.Stages is not { Count: >= 1 and <= PerformanceSettings.MaxStages } ||
            backup.ActiveStage < 0 || backup.ActiveStage >= backup.Stages.Count ||
            backup.Stages.Any(stage => stage is not { X: >= 100 and <= 50000, Y: >= 100 and <= 50000 }))
            throw new InvalidDataException("The onboard backup contains an invalid slot or DPI table.");
        if (backup.Bindings == null || backup.Bindings.Count > BasiliskOnboard.Buttons.Count * 2)
            throw new InvalidDataException("The onboard backup contains an invalid binding list.");
        var keys = new HashSet<(byte Button, byte Layer)>();
        foreach (var binding in backup.Bindings)
            if (binding == null || !keys.Add((binding.Button, binding.Layer)) ||
                !IsSupportedBinding(binding.Button, binding.Layer, binding.Block))
                throw new InvalidDataException("The onboard backup contains an unsupported or duplicate button binding.");
        if (backup.Brightness is { } brightness &&
            (brightness.Count is < 1 or > 3 ||
                brightness.Any(zone => zone == null || !IsArchiveLightingZone(zone.Led, backup.Version)) ||
                brightness.Select(zone => zone.Led).Distinct().Count() != brightness.Count))
            throw new InvalidDataException("The onboard backup contains an unsupported or duplicate brightness zone.");
        if (backup.Wheel is { SmartReel: null, Acceleration: null })
            throw new InvalidDataException("The onboard backup contains no wheel settings.");
        if (backup.Effects is { } effects &&
            (effects.Count is < 1 or > 3 ||
                effects.Any(effect => effect == null || !IsSupportedEffect(effect.Led, effect.Body, backup.Version)) ||
                effects.Select(effect => effect.Led).Distinct().Count() != effects.Count))
            throw new InvalidDataException("The onboard backup contains an unsupported or duplicate lighting effect.");
    }

    public static OnboardBackup ReadBackup(string json)
    {
        var backup = Read(json, OnboardBackupJson.Default.OnboardBackup);
        Validate(backup);
        return backup;
    }

    public static string WriteBackup(OnboardBackup backup)
    {
        Validate(backup);
        return JsonSerializer.Serialize(backup, OnboardBackupJson.Default.OnboardBackup);
    }

    public static OnboardArchiveRequest ReadRequest(string json)
    {
        var request = Read(json, OnboardBackupJson.Default.OnboardArchiveRequest);
        if (string.IsNullOrWhiteSpace(request.Serial) || request.Serial.Length > 22 || request.Serial.Any(char.IsControl) ||
            !BasiliskOnboard.IsSlot(request.Slot))
            throw new InvalidDataException("Select a valid mouse and onboard slot.");
        if (request.Backup != null)
            Validate(request.Backup);
        return request;
    }

    public static string WriteRequest(OnboardArchiveRequest request) =>
        JsonSerializer.Serialize(request, OnboardBackupJson.Default.OnboardArchiveRequest);

    public static OnboardRestoreResult ReadResult(string json)
    {
        var result = Read(json, OnboardBackupJson.Default.OnboardRestoreResult);
        if (!Enum.IsDefined(result.Status) || string.IsNullOrWhiteSpace(result.Message))
            throw new InvalidDataException("The service returned an invalid restore result.");
        return result;
    }

    public static string WriteResult(OnboardRestoreResult result) =>
        JsonSerializer.Serialize(result, OnboardBackupJson.Default.OnboardRestoreResult);

    static T Read<T>(string json, JsonTypeInfo<T> type) where T : class
    {
        if (json.Length > MaxJsonLength)
            throw new InvalidDataException("The onboard backup message is too large.");
        return JsonSerializer.Deserialize(json, type) ?? throw new InvalidDataException("The onboard backup message is empty.");
    }
}

[JsonSourceGenerationOptions(PropertyNamingPolicy = JsonKnownNamingPolicy.CamelCase, DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
    UnmappedMemberHandling = JsonUnmappedMemberHandling.Disallow)]
[JsonSerializable(typeof(OnboardArchiveRequest))]
[JsonSerializable(typeof(OnboardBackup))]
[JsonSerializable(typeof(OnboardRestoreResult))]
internal partial class OnboardBackupJson : JsonSerializerContext;
