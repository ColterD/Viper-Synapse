using ViperSynapse.Hardware;
using ViperSynapse.Hardware.Linux;

namespace ViperSynapse.Tests;

public class BlackWidowMacroReportTests
{
    [Theory]
    [InlineData("85047508950F8100", true)]
    [InlineData("85047508950D8100A485017508950F8100B495028101", true)]
    [InlineData("85047508950F8101", false)]
    [InlineData("85047508950F9100", false)]
    [InlineData("85057508950F8100", false)]
    [InlineData("8504750895108100", false)]
    [InlineData("FE080185047508950F8100", false)]
    [InlineData("85047508950F81", false)]
    public void Detects_only_a_complete_sixteen_byte_macro_input_report(string descriptor, bool matches)
    {
        Assert.Equal(matches, BlackWidowMacroKeys.HasMacroInputReport(Convert.FromHexString(descriptor)));
    }

    [Fact]
    public void Only_the_generic_driver_on_the_same_model_and_interface_qualifies()
    {
        using var folder = new TemporarySysfs();
        var node = new HidrawNode("/dev/hidraw-emulated", 0x1532, 0x0221, 1, "BlackWidow", "usb-test/input1");
        var device = Directory.CreateDirectory(Path.Combine(folder.Path, "hidraw-emulated", "device"));
        var driver = Path.Combine(device.FullName, "driver");
        File.WriteAllBytes(Path.Combine(device.FullName, "report_descriptor"), Convert.FromHexString("85047508950F8100"));
        Directory.CreateSymbolicLink(driver, "../../drivers/hid-generic");

        Assert.True(BlackWidowMacroKeys.IsNativeReportNode(node, folder.Path));
        Assert.False(BlackWidowMacroKeys.IsNativeReportNode(node with { Interface = 0 }, folder.Path));
        Assert.False(BlackWidowMacroKeys.IsNativeReportNode(node with { ProductId = 0x0203 }, folder.Path));
        Assert.False(BlackWidowMacroKeys.IsNativeReportNode(node with { Bus = HidrawNode.BluetoothBus }, folder.Path));
        File.Delete(driver);
        Directory.CreateSymbolicLink(driver, "../../drivers/razerkbd");
        Assert.False(BlackWidowMacroKeys.IsNativeReportNode(node, folder.Path));
    }

    [Fact]
    public void Macro_snapshots_preserve_simultaneous_buttons_and_ignore_unrelated_reports()
    {
        var events = new List<(ushort Key, int Value)>();
        var decoder = new BlackWidowMacroKeys.Decoder((key, value) => events.Add((key, value)));

        decoder.Read([0x04, 0x01, 0x20, 0x20, 0x23, 0x52, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0x24]);
        decoder.Read([0x04, 0x01, 0x24, 0x23, 0x20, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0]);
        decoder.Read([0x04, 0, 0, 0, 0, 0, 0, 0]);
        decoder.Read([0x01, 0, 0x20, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0]);
        decoder.Read([0x04, 0x01, 0x21, 0x22, 0x23, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0]);
        decoder.Read([0x04, 0x01, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0]);

        Assert.Equal<(ushort, int)>([
            (183, 1), (186, 1), (187, 1),
            (183, 0), (187, 0), (184, 1), (185, 1),
            (184, 0), (185, 0), (186, 0),
        ], events);
    }

    sealed class TemporarySysfs : IDisposable
    {
        public string Path { get; } = Directory.CreateTempSubdirectory("viper-blackwidow-").FullName;
        public void Dispose() => Directory.Delete(Path, recursive: true);
    }
}
