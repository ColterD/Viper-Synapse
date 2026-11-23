namespace ViperSynapse.Core.Devices;

/// <summary>Physical names for legacy Chroma controls exposed by evdev or native HID reports.</summary>
public static class LegacyRemapControls
{
    public const ushort BlackWidowChromaV2 = 0x0221;
    public const ushort NagaChroma = 0x0053;
    public const ushort FirstMacroSource = 183, LastMacroSource = 187;
    public const ushort NagaTiltLeft = 0x116, NagaTiltRight = 0x115;
    public const ushort NagaDpiUp = 0x118, NagaDpiDown = 0x119;

    public static bool IsMacroSource(ushort source) => source is >= FirstMacroSource and <= LastMacroSource;
    public static bool IsNagaTiltSource(ushort source) => source is NagaTiltLeft or NagaTiltRight;
    public static bool IsNagaDpiSource(ushort source) => source is NagaDpiUp or NagaDpiDown;

    public static bool SupportsProduct(ushort? productId) => productId is BlackWidowChromaV2 or NagaChroma;

    public static DeviceKey? Describe(ushort productId, ushort source) => (productId, source) switch
    {
        // OpenRazer translates the five vendor M keys into F13–F17.
        (BlackWidowChromaV2, >= FirstMacroSource and <= LastMacroSource) =>
            new(source - 182, $"M{source - 182}", source, 0, "macro keys", source),
        // The factory thumb-grid outputs are the twelve consecutive number-row keys, 1 through =.
        (NagaChroma, >= 2 and <= 13) =>
            new(source - 1, $"Thumb {source - 1}", source, 0, "thumb grid", source),
        (NagaChroma, NagaTiltLeft) => new(13, "Tilt Left", source, 0, "tilt wheel", source),
        (NagaChroma, NagaTiltRight) => new(14, "Tilt Right", source, 0, "tilt wheel", source),
        (NagaChroma, NagaDpiUp) => new(15, "DPI Up", 0, 0, "dpi buttons", source),
        (NagaChroma, NagaDpiDown) => new(16, "DPI Down", 0, 0, "dpi buttons", source),
        _ => null,
    };
}
