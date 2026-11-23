using ViperSynapse.Core.Bindings;
using ViperSynapse.Core.Devices;
using ViperSynapse.Core.Lighting;
using ViperSynapse.Core.Protocol;
using ViperSynapse.Hardware;
using ViperSynapse.Hardware.Linux;
using ViperSynapse.Service;

namespace ViperSynapse.Tests;

public class NagaDpiTests
{
    [Fact]
    public void Naga_uses_its_own_transaction_for_live_DPI_and_reads_a_firmware_change()
    {
        var firmware = new Firmware();
        var transport = new RazerTransport(firmware) { AnswerDelay = TimeSpan.Zero };
        using var control = new DeviceControl(transport, 0x3F, DeviceCatalogue.Default.Find(LegacyRemapControls.NagaChroma));
        var profile = Profile.Create("Naga") with { Performance = new PerformanceSettings([new(875, 1200)], 0, 0) };

        control.Apply(profile);
        Assert.Null(control.LastProblem);
        Assert.Equal(new DpiStage(875, 1200), Assert.Single(control.ReadState().Stages!));

        firmware.Dpi = new(1600, 1600);
        var mapped = profile.Keymaps[0].With(LegacyRemapControls.NagaDpiUp, new KeyboardBinding(59));
        control.Apply(profile with { Keymaps = [mapped] });
        Assert.Equal(firmware.Dpi, Assert.Single(control.ReadState().Stages!));

        Assert.Collection(firmware.Requests.Where(r => r.CommandClass != 0 || r.CommandId != 0x85),
            request => AssertPacket(request, 0x05, [0x01, 0x03, 0x6B, 0x04, 0xB0, 0, 0]),
            request => AssertPacket(request, 0x85, new byte[7]),
            request => AssertPacket(request, 0x85, new byte[7]));
    }

    [Theory]
    [InlineData(4, 800, 800)]
    [InlineData(7, 0, 800)]
    [InlineData(7, 800, 16001)]
    public void Naga_omits_invalid_live_DPI_readback(int replyLength, int x, int y)
    {
        var firmware = new Firmware { ReplyLength = replyLength, Dpi = new(x, y) };
        var transport = new RazerTransport(firmware) { AnswerDelay = TimeSpan.Zero };
        using var control = new DeviceControl(transport, 0x3F, DeviceCatalogue.Default.Find(LegacyRemapControls.NagaChroma));

        Assert.Null(control.ReadState().Stages);
        var read = Assert.Single(firmware.Requests, r => r.CommandClass == 0x04);
        AssertPacket(read, 0x85, new byte[7]);
    }

    static void AssertPacket(RazerReport request, byte id, byte[] arguments)
    {
        Assert.Equal(0xFF, request.TransactionId);
        Assert.Equal(0x04, request.CommandClass);
        Assert.Equal(id, request.CommandId);
        Assert.Equal(arguments, request.Arguments[..request.DataSize].ToArray());
    }

    sealed class Firmware : IHidFeatureDevice
    {
        public string Path => "/dev/hidraw-emulated-naga";
        public DpiStage Dpi { get; set; } = new(800, 800);
        public int ReplyLength { get; init; } = 7;
        public List<RazerReport> Requests { get; } = [];

        public void SendFeature(ReadOnlySpan<byte> report) => Requests.Add(RazerReport.Parse(report[1..]));

        public int ReceiveFeature(Span<byte> report)
        {
            var request = Requests[^1];
            var args = request.Arguments;
            byte[]? response = (request.CommandClass, request.CommandId, request.TransactionId, args[0]) switch
            {
                (0x04, 0x05, 0xFF, 0x01) => ApplyDpi(args),
                (0x04, 0x85, 0xFF, 0x00) => new byte[]
                    { 0, (byte)(Dpi.X >> 8), (byte)Dpi.X, (byte)(Dpi.Y >> 8), (byte)Dpi.Y, 0, 0 }[..ReplyLength],
                (0x00, 0x85, _, _) => [1],
                _ => null,
            };
            var reply = RazerReport.Create(request.TransactionId, request.CommandClass, request.CommandId, response ?? []).Bytes.ToArray();
            reply[0] = (byte)(response != null ? ReportStatus.Successful : ReportStatus.NotSupported);
            reply.CopyTo(report[1..]);
            return report.Length;
        }

        byte[] ApplyDpi(ReadOnlySpan<byte> args)
        {
            Dpi = new((args[1] << 8) | args[2], (args[3] << 8) | args[4]);
            return args[..7].ToArray();
        }

        public void Dispose() { }
    }
}
