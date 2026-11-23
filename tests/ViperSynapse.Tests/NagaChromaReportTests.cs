using ViperSynapse.Hardware;
using ViperSynapse.Hardware.Linux;

namespace ViperSynapse.Tests;

public class NagaChromaReportTests
{
    [Theory]
    [InlineData(0, "75019507810295018103", 1)]
    [InlineData(0, "750195088102750895078106", 8)]
    [InlineData(0, "750195068102", 0)]
    [InlineData(0, "750895098102", 0)]
    [InlineData(0, "750895088103", 0)]
    [InlineData(0, "8501750895088102", 0)]
    [InlineData(0, "7508950881028501750895018102", 0)]
    [InlineData(1, "85047508950F8100", 16)]
    [InlineData(1, "8504750195718100", 0)]
    [InlineData(1, "85047508950F8101", 0)]
    [InlineData(1, "8504750895108100", 0)]
    [InlineData(1, "85047508950F81008501750895078102", 16)]
    [InlineData(2, "85047508950F8100", 0)]
    public void Finds_only_supported_input_reports_on_the_wired_generic_device(int iface, string descriptor, int bytes)
    {
        using var folder = new TemporarySysfs();
        var node = new HidrawNode("/dev/hidraw-emulated", 0x1532, 0x0053, iface, "Naga Chroma", $"usb-test/input{iface}");
        var device = Directory.CreateDirectory(Path.Combine(folder.Path, "hidraw-emulated", "device"));
        var driver = Path.Combine(device.FullName, "driver");
        File.WriteAllBytes(Path.Combine(device.FullName, "report_descriptor"), Convert.FromHexString(descriptor));
        Directory.CreateSymbolicLink(driver, "../../drivers/hid-generic");

        var expected = bytes == 0 ? null : new NagaReportNode(node.DevicePath, iface == 0 ? NagaReportKind.Tilt : NagaReportKind.Dpi, bytes);
        Assert.Equal(expected, NagaChromaButtons.FindReportNode(node, folder.Path));
        Assert.Null(NagaChromaButtons.FindReportNode(node with { ProductId = 0x0040 }, folder.Path));
        Assert.Null(NagaChromaButtons.FindReportNode(node with { VendorId = 0x1111 }, folder.Path));
        Assert.Null(NagaChromaButtons.FindReportNode(node with { Bus = HidrawNode.BluetoothBus }, folder.Path));
        File.Delete(driver);
        Directory.CreateSymbolicLink(driver, "../../drivers/razermouse");
        Assert.Null(NagaChromaButtons.FindReportNode(node, folder.Path));
    }

    [Fact]
    public void Mouse_reports_track_tilt_edges_independently_of_motion_and_ordinary_buttons()
    {
        var events = new List<(ushort Key, int Value)>();
        var decoder = new NagaChromaButtons.Decoder(new("/dev/mouse", NagaReportKind.Tilt, 8), (key, value) => events.Add((key, value)));

        decoder.Read([0x21, 1, 2, 3, 4, 5, 6, 7]);
        decoder.Read([0x23, 7, 6, 5, 4, 3, 2, 1]);
        decoder.Read([0x00]);
        decoder.Read([0x63, 0, 0, 0, 0, 0, 0, 0]);
        decoder.Read([0x41, 0, 0, 0, 0, 0, 0, 0]);
        decoder.Read([0x01, 0, 0, 0, 0, 0, 0, 0]);

        Assert.Equal<(ushort, int)>([(0x116, 1), (0x115, 1), (0x116, 0), (0x115, 0)], events);
    }

    [Fact]
    public void Mouse_back_and_forward_track_only_buttons_four_and_five()
    {
        var events = new List<(ushort Key, int Value)>();
        var decoder = new NagaChromaButtons.Decoder(new("/dev/mouse", NagaReportKind.MouseBackForward, 4),
            (key, value) => events.Add((key, value)));

        decoder.Read([0x67, 1, 2, 3]); // Ordinary buttons and tilt only.
        decoder.Read([0x6F, 4, 5, 6]); // Back.
        decoder.Read([0x6B, 7, 8, 9]); // Motion and another button change.
        decoder.Read([0x00]); // Ignore a wrong-length report while Back is held.
        decoder.Read([0x73, 0, 0, 0]); // Back release and Forward press in one report.
        decoder.Read([0x03, 0, 0, 0]);
        decoder.Read([0x18, 0, 0, 0]); // Both pressed.
        decoder.Read([0x00, 0, 0, 0]);

        Assert.Equal<(ushort, int)>([
            (0x119, 1), (0x119, 0), (0x118, 1), (0x118, 0),
            (0x118, 1), (0x119, 1), (0x118, 0), (0x119, 0),
        ], events);
    }

    [Theory]
    [InlineData("050919012905750195058102950381030501750895038106", 4)]
    [InlineData("0509190129087501950881020501750895078106", 8)]
    [InlineData("05091901290475019504810295048103", 0)]
    [InlineData("050919012905750195058106", 0)] // Relative field.
    [InlineData("050919012905750195068102", 0)] // The last usage would repeat.
    [InlineData("050919012905750195058100", 0)] // Array field.
    [InlineData("050919012905750195058103", 0)] // Constant field.
    [InlineData("05091901290575019505810295018102", 0)] // Ambiguous extra Button field.
    [InlineData("0501190129050509750195058102", 0)] // Usages declared on the wrong page.
    [InlineData("050919012905A100750195058102C0", 0)] // Main item ends local usage scope.
    [InlineData("0509190129057501950181031901290595058102", 0)] // Buttons at a nonzero offset.
    [InlineData("8501050919012905750195058102", 0)] // Numbered report.
    [InlineData("050919012905750195058102850195018102", 0)] // Mixed numbered input.
    [InlineData("050919012905750195058102750895098102", 0)] // Oversized report.
    [InlineData("0509190129057501950581", 0)] // Truncated input item.
    public void Finds_back_and_forward_only_in_a_qualified_mouse_report(string descriptor, int bytes)
    {
        using var folder = new TemporarySysfs();
        var node = new HidrawNode("/dev/hidraw-emulated", 0x1532, 0x0053, 0, "Naga Chroma", "usb-test/input0");
        var device = Directory.CreateDirectory(Path.Combine(folder.Path, "hidraw-emulated", "device"));
        var driver = Path.Combine(device.FullName, "driver");
        File.WriteAllBytes(Path.Combine(device.FullName, "report_descriptor"), Convert.FromHexString(descriptor));
        Directory.CreateSymbolicLink(driver, "../../drivers/hid-generic");

        var expected = bytes == 0 ? null : new NagaReportNode(node.DevicePath, NagaReportKind.MouseBackForward, bytes);
        Assert.Equal(expected, NagaChromaButtons.FindMouseBackForwardNode(node, folder.Path));
        Assert.Null(NagaChromaButtons.FindMouseBackForwardNode(node with { Interface = 1 }, folder.Path));
        Assert.Null(NagaChromaButtons.FindMouseBackForwardNode(node with { ProductId = 0x0040 }, folder.Path));
        Assert.Null(NagaChromaButtons.FindMouseBackForwardNode(node with { VendorId = 0x1111 }, folder.Path));
        Assert.Null(NagaChromaButtons.FindMouseBackForwardNode(node with { Bus = HidrawNode.BluetoothBus }, folder.Path));
        File.Delete(driver);
        Directory.CreateSymbolicLink(driver, "../../drivers/razermouse");
        Assert.Null(NagaChromaButtons.FindMouseBackForwardNode(node, folder.Path));
    }

    [Fact]
    public void Report_four_tracks_dpi_and_tilt_membership_without_releasing_other_buttons()
    {
        var events = new List<(ushort Key, int Value)>();
        var decoder = new NagaChromaButtons.Decoder(new("/dev/vendor", NagaReportKind.Dpi, 16), (key, value) => events.Add((key, value)));

        decoder.Read([0x04, 0x22, 0x20, 0x20, 0x50, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0x21]);
        decoder.Read([0x04, 0x21, 0x22, 0x20, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0]);
        decoder.Read([0x04, 0, 0, 0, 0, 0, 0, 0]);
        decoder.Read([0x01, 0x23, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0]);
        decoder.Read([0x04, 0x23, 0x22, 0x21, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0]);
        decoder.Read([0x04, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0]);

        Assert.Equal<(ushort, int)>([
            (0x118, 1), (0x119, 1), (0x116, 1),
            (0x118, 0), (0x115, 1),
            (0x119, 0), (0x116, 0), (0x115, 0),
        ], events);
    }

    sealed class TemporarySysfs : IDisposable
    {
        public string Path { get; } = Directory.CreateTempSubdirectory("viper-naga-").FullName;
        public void Dispose() => Directory.Delete(Path, recursive: true);
    }
}
