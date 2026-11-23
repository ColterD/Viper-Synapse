using ViperSynapse.Core.Devices;
using ViperSynapse.Hardware.Linux;

namespace ViperSynapse.Hardware;

/// <summary>Reads the BlackWidow Chroma V2's driver-mode M keys independently of its ordinary evdev reports.</summary>
internal static class BlackWidowMacroKeys
{
    const byte MacroReportId = 0x04;
    const int MacroReportLength = 16;

    /// <summary>Opens input first so the kernel can queue snapshots while driver mode is enabled.</summary>
    public static IHidrawButtonReader Open(string path, Action<ushort, int> onKey, Action<Exception> onLost)
        => HidrawButtonReader.Open(path, new Decoder(onKey).Read, onLost);

    public static bool IsNativeReportNode(HidrawNode node, string sysRoot = "/sys/class/hidraw")
        => node is { Bus: HidrawNode.UsbBus, VendorId: 0x1532, ProductId: LegacyRemapControls.BlackWidowChromaV2, Interface: 1 }
            && HidrawButtonReader.TryReadGenericDescriptor(node, sysRoot, out var descriptor)
            && HasMacroInputReport(descriptor);

    internal static bool HasMacroInputReport(ReadOnlySpan<byte> descriptor)
        => HidrawButtonReader.TryGetInputReportBits(descriptor, MacroReportId, (MacroReportLength - 1) * 8, out int bits)
            && bits == (MacroReportLength - 1) * 8;

    internal sealed class Decoder(Action<ushort, int> onKey)
    {
        int _held;

        public void Read(ReadOnlySpan<byte> report)
        {
            if (report.Length != MacroReportLength || report[0] != MacroReportId)
                return;
            int next = 0;
            foreach (byte usage in report[1..])
                if (usage is >= 0x20 and <= 0x24)
                    next |= 1 << (usage - 0x20);

            int released = _held & ~next;
            int pressed = next & ~_held;
            _held = next;
            Emit(released, 0);
            Emit(pressed, 1);
        }

        void Emit(int mask, int value)
        {
            for (int key = 0; key < 5; key++)
                if ((mask & (1 << key)) != 0)
                    onKey((ushort)(LegacyRemapControls.FirstMacroSource + key), value);
        }
    }
}
