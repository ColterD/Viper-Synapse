using ViperSynapse.Core.Lighting;

namespace ViperSynapse.Core.Protocol;

/// <summary>OpenRazer's class 03 lighting paths for the BlackWidow Chroma V2 and Naga Chroma.</summary>
public static class LegacyLightingCommands
{
    const ushort BlackWidowChromaV2 = 0x0221;
    const ushort NagaChroma = 0x0053;
    const byte EffectTransaction = 0x3F;

    public static bool SupportsProduct(ushort? productId) => productId is BlackWidowChromaV2 or NagaChroma;

    public static bool SupportsEffect(EffectKind effect) =>
        effect is EffectKind.Off or EffectKind.Static or EffectKind.Spectrum;

    public static byte? Led(ushort productId, string zone) => (productId, zone) switch
    {
        (BlackWidowChromaV2, "main") or (NagaChroma, "backlight") => 0x05,
        (NagaChroma, "logo") => 0x04,
        (NagaChroma, "scroll") => 0x01,
        _ => null,
    };

    public static RazerReport Effect(ushort productId, byte led, ZoneEffect effect)
    {
        byte id = effect.Kind switch
        {
            EffectKind.Off => 0x00,
            EffectKind.Spectrum => 0x04,
            EffectKind.Static => 0x06,
            _ => throw new ArgumentOutOfRangeException(nameof(effect)),
        };
        var color = effect.Color1;
        return productId switch
        {
            BlackWidowChromaV2 => effect.Kind == EffectKind.Static
                ? RazerReport.Create(EffectTransaction, 0x03, 0x0A, id, color.R, color.G, color.B)
                : RazerReport.Create(EffectTransaction, 0x03, 0x0A, id),
            NagaChroma => effect.Kind == EffectKind.Static
                ? RazerReport.Create(EffectTransaction, 0x03, 0x0D, DeviceCommands.VarStore, led, id, color.R, color.G, color.B)
                : RazerReport.Create(EffectTransaction, 0x03, 0x0D, DeviceCommands.VarStore, led, id),
            _ => throw new ArgumentOutOfRangeException(nameof(productId)),
        };
    }

    public static bool MatchesFrame(ushort productId, CustomFrame frame) =>
        (productId, frame.Rows, frame.Columns, frame.Colors?.Count) is
            (BlackWidowChromaV2, 6, 22, 132) or (NagaChroma, 1, 3, 3);

    public static IEnumerable<RazerReport> FrameRows(ushort productId, CustomFrame frame)
    {
        if (!MatchesFrame(productId, frame))
            throw new ArgumentException("The frame does not match the device's LED matrix.", nameof(frame));
        return productId == BlackWidowChromaV2 ? KeyboardRows(frame) : DeviceCommands.CustomFrame(EffectTransaction, frame);
    }

    static IEnumerable<RazerReport> KeyboardRows(CustomFrame frame)
    {
        for (int row = 0; row < frame.Rows; row++)
        {
            var args = new byte[0x46];
            args[0] = 0xFF;
            args[1] = (byte)row;
            args[3] = (byte)(frame.Columns - 1);
            for (int column = 0; column < frame.Columns; column++)
            {
                var color = frame[row, column];
                int offset = 4 + column * 3;
                args[offset] = color.R;
                args[offset + 1] = color.G;
                args[offset + 2] = color.B;
            }
            yield return RazerReport.Create(EffectTransaction, 0x03, 0x0B, args);
        }
    }

    public static RazerReport ActivateFrame(ushort productId) => productId switch
    {
        BlackWidowChromaV2 => RazerReport.Create(EffectTransaction, 0x03, 0x0A, 0x05, DeviceCommands.NoStore),
        NagaChroma => DeviceCommands.CustomFrameEffect(EffectTransaction),
        _ => throw new ArgumentOutOfRangeException(nameof(productId)),
    };

    public static RazerReport Brightness(byte led, byte value) =>
        RazerReport.Create(0xFF, 0x03, 0x03, DeviceCommands.VarStore, led, value);

    public static RazerReport ReadBrightness(byte led) =>
        RazerReport.Create(0xFF, 0x03, 0x83, DeviceCommands.VarStore, led, 0);
}
