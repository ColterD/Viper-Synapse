using ViperSynapse.Core.Devices;
using ViperSynapse.Core.Protocol;
using ViperSynapse.Hardware;
using ViperSynapse.Hardware.Linux;
using ViperSynapse.Service;

namespace ViperSynapse.Tests;

public class BlackWidowMacroModeTests
{
    [Fact]
    public void Owns_only_the_mode_change_it_makes_and_restores_it_once()
    {
        var device = new ModeDevice { RejectWriteAcknowledgement = true };
        using var control = Open(device);
        var lease = control.EnableNativeMacroKeys();

        Assert.Equal(3, device.Mode);
        Assert.Throws<IOException>(() => control.EnableNativeMacroKeys());

        lease.Dispose();
        lease.Dispose();

        Assert.Equal(0, device.Mode);
        Assert.Equal(new byte[] { 0x84, 0x04, 0x84, 0x84, 0x04, 0x84 }, device.Requests);
        Assert.Equal(new byte[] { 0x03, 0x00 }, device.ModeWrites);
    }

    [Fact]
    public void Leaves_preexisting_driver_mode_alone()
    {
        var device = new ModeDevice { Mode = 3 };
        using var control = Open(device);

        control.EnableNativeMacroKeys().Dispose();

        Assert.Equal(3, device.Mode);
        Assert.Equal(new byte[] { 0x84 }, device.Requests);
    }

    [Theory]
    [InlineData(0, 1, false)]
    [InlineData(2, 0, false)]
    [InlineData(0, 0, true)]
    public void Does_not_write_without_a_valid_supported_starting_mode(byte mode, byte parameter, bool invalidChecksum)
    {
        var device = new ModeDevice { Mode = mode, Parameter = parameter, BadRead = invalidChecksum ? 1 : 0 };
        using var control = Open(device);

        Assert.Throws<IOException>(() => control.EnableNativeMacroKeys());

        Assert.Equal(new byte[] { 0x84 }, device.Requests);
        Assert.Empty(device.ModeWrites);
    }

    [Fact]
    public void Recovers_if_enable_applies_but_initial_verification_fails()
    {
        var device = new ModeDevice { BadRead = 2 };
        using var control = Open(device);

        Assert.Throws<IOException>(() => control.EnableNativeMacroKeys());

        Assert.Equal(0, device.Mode);
        Assert.Equal(new byte[] { 0x03, 0x00 }, device.ModeWrites);
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public void Failed_lease_cleanup_can_retry_on_the_next_lease_or_control_disposal(bool anotherLease)
    {
        // The first read for cleanup fails transiently before regular mode can be restored.
        var device = new ModeDevice { BadRead = 3 };
        using var control = Open(device);
        var lease = control.EnableNativeMacroKeys();
        Assert.Throws<IOException>(() => lease.Dispose());
        Assert.Equal(3, device.Mode);
        Assert.Equal(new byte[] { 0x03 }, device.ModeWrites);

        if (anotherLease)
            control.EnableNativeMacroKeys().Dispose();
        else
            control.Dispose();

        Assert.Equal(0, device.Mode);
        Assert.Equal(new byte[] { 0x03, 0x00 }, device.ModeWrites);
    }

    [Fact]
    public void A_different_keyboard_cannot_be_switched()
    {
        var device = new ModeDevice();
        using var control = Open(device, LegacyRemapControls.NagaChroma);

        Assert.Throws<NotSupportedException>(() => control.EnableNativeMacroKeys());

        Assert.Empty(device.Requests);
    }

    static DeviceControl Open(ModeDevice device, ushort product = LegacyRemapControls.BlackWidowChromaV2) =>
        new(new RazerTransport(device) { AnswerDelay = TimeSpan.Zero }, 0x3F, DeviceCatalogue.Default.Find(product));

    sealed class ModeDevice : IHidFeatureDevice
    {
        RazerReport? _request;
        int _reads;
        public byte Mode { get; set; }
        public byte Parameter { get; init; }
        public int BadRead { get; init; }
        public bool RejectWriteAcknowledgement { get; init; }
        public List<byte> Requests { get; } = [];
        public List<byte> ModeWrites { get; } = [];
        public string Path => "/tmp/blackwidow-control";

        public void SendFeature(ReadOnlySpan<byte> report)
        {
            Assert.Equal(0, report[0]);
            var request = RazerReport.Parse(report[1..]);
            Assert.True(request.ChecksumValid);
            Assert.Equal(0xFF, request.TransactionId);
            Assert.Equal(0x00, request.CommandClass);
            Assert.Equal(2, request.DataSize);
            Assert.Equal(0, request.Arguments[1]);
            Requests.Add(request.CommandId);
            _request = request;
            if (request.CommandId == 0x04)
            {
                Mode = request.Arguments[0];
                ModeWrites.Add(Mode);
            }
            else
            {
                Assert.Equal(0x84, request.CommandId);
                Assert.Equal(0, request.Arguments[0]);
                _reads++;
            }
        }

        public int ReceiveFeature(Span<byte> report)
        {
            var request = _request!;
            var response = RazerReport.Create(0xFF, 0x00, request.CommandId, Mode, Parameter);
            response.Bytes.CopyTo(report[1..]);
            report[1] = request.CommandId == 0x04 && RejectWriteAcknowledgement
                ? (byte)ReportStatus.Failure : (byte)ReportStatus.Successful;
            if (request.CommandId == 0x84 && _reads == BadRead)
                report[^2] ^= 0xFF;
            return report.Length;
        }

        public void Dispose() { }
    }
}
