using System.Text.Json.Serialization;

namespace ViperSynapse.Core.Protocol;

public sealed record SettingsSnapshot(string Revision, string Json);

public enum UnitApplyStatus
{
    Unavailable,
    Disconnected,
    NoFirmwareChanges,
    Acknowledged,
    ReadbackConfirmed,
    Failed,
}

public sealed record UnitApplyOutcome(string Port, string ProductId, string? Serial, UnitApplyStatus Status,
    string? Detail = null, int AcknowledgedCommands = 0, int ReadbackConfirmations = 0);

public sealed record SettingsCommitResult(string Revision, bool Saved, IReadOnlyList<UnitApplyOutcome>? Units = null);

[JsonSourceGenerationOptions(PropertyNamingPolicy = JsonKnownNamingPolicy.CamelCase, UseStringEnumConverter = true)]
[JsonSerializable(typeof(SettingsSnapshot))]
[JsonSerializable(typeof(SettingsCommitResult))]
[JsonSerializable(typeof(UnitApplyOutcome))]
public sealed partial class SettingsCommitJson : JsonSerializerContext;
