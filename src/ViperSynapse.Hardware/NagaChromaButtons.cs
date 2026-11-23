using ViperSynapse.Core.Devices;
using ViperSynapse.Hardware.Linux;

namespace ViperSynapse.Hardware;

internal enum NagaReportKind { Tilt, Dpi, MouseBackForward }

internal sealed record NagaReportNode(string Path, NagaReportKind Kind, int ReportLength);

/// <summary>Reads the Naga Chroma's tilt and DPI controls from its wired HID reports.</summary>
internal static class NagaChromaButtons
{
    const byte VendorReportId = 0x04;
    internal const int VendorReportLength = 16;
    internal const int MaxMouseReportLength = 8;
    const byte BackMask = 1 << 3, ForwardMask = 1 << 4;
    const int TiltLeftBit = 5, TiltRightBit = 6;
    const byte FirstVendorButton = 0x20, LastVendorButton = 0x23;

    static readonly ushort[] Sources =
    [
        LegacyRemapControls.NagaDpiUp, LegacyRemapControls.NagaDpiDown,
        LegacyRemapControls.NagaTiltLeft, LegacyRemapControls.NagaTiltRight,
    ];

    internal static NagaReportNode? FindReportNode(HidrawNode node, string sysRoot = "/sys/class/hidraw")
    {
        if (node is not { Bus: HidrawNode.UsbBus, VendorId: 0x1532, ProductId: LegacyRemapControls.NagaChroma, Interface: 0 or 1 } ||
            !HidrawButtonReader.TryReadGenericDescriptor(node, sysRoot, out var descriptor))
            return null;

        if (node.Interface == 0)
        {
            // The mouse interface puts tilt in the first byte of the unnumbered input report.
            if (HidrawButtonReader.TryGetInputReportBits(descriptor, 0, MaxMouseReportLength * 8, out int mouseBits) &&
                mouseBits > TiltRightBit)
                return new(node.DevicePath, NagaReportKind.Tilt, (mouseBits + 7) / 8);
            return null;
        }

        const int vendorBits = (VendorReportLength - 1) * 8;
        return HidrawButtonReader.TryGetInputReportBits(descriptor, VendorReportId, vendorBits, out int bits) && bits == vendorBits
            ? new(node.DevicePath, NagaReportKind.Dpi, VendorReportLength)
            : null;
    }

    internal static IHidrawButtonReader Open(NagaReportNode node, Action<ushort, int> onKey, Action<Exception> onLost)
        => HidrawButtonReader.Open(node.Path, new Decoder(node, onKey).Read, onLost);

    internal static NagaReportNode? FindMouseBackForwardNode(HidrawNode node, string sysRoot = "/sys/class/hidraw")
    {
        if (node is not { Bus: HidrawNode.UsbBus, VendorId: 0x1532, ProductId: LegacyRemapControls.NagaChroma, Interface: 0 } ||
            !HidrawButtonReader.TryReadGenericDescriptor(node, sysRoot, out var descriptor) ||
            !HidrawButtonReader.TryGetMouseButtonReportBits(descriptor, MaxMouseReportLength * 8, out int bits))
            return null;

        return new(node.DevicePath, NagaReportKind.MouseBackForward, (bits + 7) / 8);
    }

    internal sealed class Decoder(NagaReportNode node, Action<ushort, int> onKey)
    {
        int _held;

        public void Read(ReadOnlySpan<byte> report)
        {
            if (report.IsEmpty || report.Length != node.ReportLength)
                return;

            int next = 0;
            if (node.Kind == NagaReportKind.Tilt)
            {
                if ((report[0] & (1 << TiltLeftBit)) != 0)
                    next |= 1 << 2;
                if ((report[0] & (1 << TiltRightBit)) != 0)
                    next |= 1 << 3;
            }
            else if (node.Kind == NagaReportKind.MouseBackForward)
            {
                // The diagnostic convention maps DPI Up to Forward (MB5) and DPI Down to Back (MB4).
                if ((report[0] & ForwardMask) != 0)
                    next |= 1;
                if ((report[0] & BackMask) != 0)
                    next |= 1 << 1;
            }
            else
            {
                if (report[0] != VendorReportId)
                    return;
                foreach (byte usage in report[1..])
                    if (usage is >= FirstVendorButton and <= LastVendorButton)
                        next |= 1 << (usage - FirstVendorButton);
            }

            int released = _held & ~next;
            int pressed = next & ~_held;
            _held = next;
            Emit(released, 0);
            Emit(pressed, 1);
        }

        void Emit(int mask, int value)
        {
            for (int i = 0; i < Sources.Length; i++)
                if ((mask & (1 << i)) != 0)
                    onKey(Sources[i], value);
        }
    }
}
