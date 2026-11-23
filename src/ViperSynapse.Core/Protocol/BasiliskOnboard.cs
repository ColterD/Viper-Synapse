using System.Text.Json;
using System.Text.Json.Serialization;

namespace ViperSynapse.Core.Protocol;

public enum OnboardOperation { List, Create, Select, Delete, ReadBinding, SetKeyboard, Disable, Restore, Rename }

public sealed record OnboardRequest(OnboardOperation Operation, byte Slot = 0, byte Button = 0, byte Layer = 0,
    byte Usage = 0, byte Modifiers = 0, string? Serial = null, string? Name = null, bool ConfirmDelete = false);

public sealed record OnboardBinding(byte Layer, byte[] Block);
public sealed record OnboardButton(string Name, byte Id, ReadOnlyMemory<byte> Factory);

public sealed record OnboardResult(string Serial, byte ActiveSlot, IReadOnlyList<byte> Slots,
    OnboardBinding? Binding = null, IReadOnlyDictionary<byte, string>? Names = null);

/// <summary>Basilisk V4 Pro USB onboard commands. Button payloads are addressed by slot, hardware button ID and layer.</summary>
public static class BasiliskOnboard
{
    const int FunctionSize = 7, BindingSize = 10;

    // Button IDs and firmware defaults from Synapsex, matched to Razer's numbered control list.
    public static IReadOnlyList<OnboardButton> Buttons { get; } = Array.AsReadOnly<OnboardButton>(
    [
        new("Profile cycle (M1 edge)", 0x0E, Convert.FromHexString("07010400000000")),
        new("Hypershift clutch", 0x39, Convert.FromHexString("0C013900000000")),
        new("DPI cycle", 0x60, Convert.FromHexString("06010600000000")),
        new("Scroll mode", 0x6A, Convert.FromHexString("12010100000000")),
        new("Side front (AI Prompt Master)", 0x80, Disabled()),
    ]);

    public static RazerReport ListSlots(byte tid) => RazerReport.Create(tid, 0x05, 0x81, [], 10);

    public static RazerReport ChangeSlot(byte tid, OnboardOperation operation, byte slot)
    {
        if (!IsSlot(slot))
            throw new ArgumentOutOfRangeException(nameof(slot));
        byte command = operation switch
        {
            OnboardOperation.Create => 0x02,
            OnboardOperation.Delete => 0x03,
            OnboardOperation.Select => 0x04,
            _ => throw new ArgumentOutOfRangeException(nameof(operation)),
        };
        return RazerReport.Create(tid, 0x05, command, slot);
    }

    public static byte[]? ParseSlots(RazerReport report)
    {
        if (report.CommandClass != 0x05 || report.CommandId != 0x81 || report.DataSize is < 2 or > 10)
            return null;
        var slots = new SortedSet<byte>();
        // V4 source skips the first byte. V3 captures report capacity there, so it isn't a slot count.
        // Zero entries in the slot array are padding.
        foreach (byte slot in report.Arguments[1..report.DataSize])
            if (slot != 0 && (!IsSlot(slot) || !slots.Add(slot)))
                return null;
        return slots.Contains(1) ? slots.ToArray() : null;
    }

    public static RazerReport ReadBinding(byte tid, byte slot, byte id, byte layer) =>
        RazerReport.Create(tid, 0x02, 0x8C, [slot, id, layer], BindingSize);

    public static RazerReport WriteBinding(byte tid, byte slot, byte id, byte layer, ReadOnlySpan<byte> block)
    {
        if (block.Length != FunctionSize)
            throw new ArgumentException("An onboard button function must contain seven bytes.", nameof(block));
        Span<byte> arguments = stackalloc byte[BindingSize];
        arguments[0] = slot;
        arguments[1] = id;
        arguments[2] = layer;
        block.CopyTo(arguments[3..]);
        return RazerReport.Create(tid, 0x02, 0x0C, arguments, BindingSize);
    }

    public static OnboardBinding? ParseBinding(RazerReport report, byte slot, byte id, byte layer, bool allowInherited,
        bool allowOpaque = false)
    {
        var arguments = report.Arguments;
        if (report.CommandClass != 0x02 || report.CommandId != 0x8C ||
            report.DataSize != BindingSize ||
            arguments[0] != slot || arguments[1] != id || !allowOpaque && arguments[4] is not (1 or 2))
            return null;
        byte actualLayer = arguments[2];
        if (actualLayer != layer && !(allowInherited && layer == 1 && actualLayer == 0))
            return null;
        return new OnboardBinding(actualLayer, arguments[3..BindingSize].ToArray());
    }

    public static byte[] Keyboard(byte usage, byte modifiers) => [2, 2, modifiers, usage, 0, 0, 0];
    public static byte[] Disabled() => [0, 1, 0, 0, 0, 0, 0];
    public static bool IsSlot(byte slot) => slot is >= 1 and <= 5;

    public static OnboardRequest ReadRequest(string json) => JsonSerializer.Deserialize(json, OnboardJson.Default.OnboardRequest)
        ?? throw new InvalidDataException("Missing onboard request.");
    public static string WriteRequest(OnboardRequest request) => JsonSerializer.Serialize(request, OnboardJson.Default.OnboardRequest);
    public static OnboardResult ReadResult(string json) => JsonSerializer.Deserialize(json, OnboardJson.Default.OnboardResult)
        ?? throw new InvalidDataException("Missing onboard result.");
    public static string WriteResult(OnboardResult result) => JsonSerializer.Serialize(result, OnboardJson.Default.OnboardResult);
}

[JsonSourceGenerationOptions(PropertyNamingPolicy = JsonKnownNamingPolicy.CamelCase)]
[JsonSerializable(typeof(OnboardRequest))]
[JsonSerializable(typeof(OnboardResult))]
internal partial class OnboardJson : JsonSerializerContext;
