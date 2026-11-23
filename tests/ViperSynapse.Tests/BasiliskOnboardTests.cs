using System.Text;
using ViperSynapse.Core.Devices;
using ViperSynapse.Core.Engine;
using ViperSynapse.Core.Protocol;
using ViperSynapse.Hardware;
using ViperSynapse.Hardware.Linux;
using ViperSynapse.Service;

namespace ViperSynapse.Tests;

public class BasiliskOnboardTests
{
    [Theory]
    [InlineData(0x00E2)]
    [InlineData(0x00E3)]
    public void Creates_selects_and_deletes_only_confirmed_slots(ushort pid)
    {
        var receiver = new Firmware();
        using var control = Open(receiver, pid);
        Assert.Equal(new byte[] { 1, 2 }, Run(control, new(OnboardOperation.List)).Slots);
        Assert.Equal(new byte[] { 1, 2, 4 }, Run(control, new(OnboardOperation.Create, 4)).Slots);
        Assert.Equal(new byte[] { 4 }, receiver.Last(0x05, 0x02).Arguments[..1].ToArray());
        Assert.Equal(4, Run(control, new(OnboardOperation.Select, 4)).ActiveSlot);
        Assert.Equal<byte?>(4, control.OnboardProfile);
        Assert.Equal(new byte[] { 1, 4 }, Run(control, new(OnboardOperation.Delete, 2, ConfirmDelete: true)).Slots);
        Assert.Equal(2, receiver.Last(0x05, 0x03).Arguments[0]);
    }

    [Fact]
    public void Cannot_delete_the_default_or_active_slot_or_delete_without_confirmation()
    {
        var receiver = new Firmware();
        using var control = Open(receiver);
        Assert.NotNull(Record.Exception(() => Run(control, new(OnboardOperation.Delete, 1, ConfirmDelete: true))));
        Assert.NotNull(Record.Exception(() => Run(control, new(OnboardOperation.Delete, 2, ConfirmDelete: true))));
        Assert.NotNull(Record.Exception(() => Run(control, new(OnboardOperation.Delete, 1))));
        Assert.DoesNotContain(receiver.Requests, r => (r.CommandClass, r.CommandId) == (0x05, 0x03));
    }

    [Fact]
    public void A_slot_acknowledgement_without_the_expected_readback_is_a_failure()
    {
        var receiver = new Firmware { IgnoreChanges = true };
        using var control = Open(receiver);
        Assert.NotNull(Record.Exception(() => Run(control, new(OnboardOperation.Select, 1))));
        Assert.Equal<byte?>(2, control.OnboardProfile);
        Assert.NotNull(Record.Exception(() => Run(control, new(OnboardOperation.Create, 4))));
        Assert.DoesNotContain((byte)4, receiver.Slots);
    }

    [Theory]
    [InlineData(0x0E)]
    [InlineData(0x39)]
    [InlineData(0x60)]
    [InlineData(0x6A)]
    [InlineData(0x80)]
    public void Button_writes_are_addressed_to_the_active_slot_and_layer(byte button)
    {
        var receiver = new Firmware();
        using var control = Open(receiver);
        var result = Run(control, new(OnboardOperation.SetKeyboard, 2, button, 1, 0x68, 0x02));
        Assert.NotNull(result.Binding);
        Assert.Equal(1, result.Binding.Layer);
        Assert.Equal(Convert.FromHexString("02020268000000"), result.Binding.Block);
        var write = receiver.Last(0x02, 0x0C);
        Assert.Equal(10, write.DataSize);
        Assert.Equal(new byte[] { 2, button, 1, 2, 2, 2, 0x68, 0, 0, 0 }, write.Arguments[..10].ToArray());
    }

    [Fact]
    public void A_mirrored_normal_layer_can_be_read_but_cannot_confirm_a_hypershift_write()
    {
        var receiver = new Firmware { MirrorNormalLayer = true };
        using var control = Open(receiver);
        var read = Run(control, new(OnboardOperation.ReadBinding, 2, 0x39, 1));
        Assert.Equal(0, read.Binding!.Layer);
        Assert.NotNull(Record.Exception(() => Run(control, new(OnboardOperation.Disable, 2, 0x39, 1))));
    }

    [Fact]
    public void A_stale_matching_read_is_retried_without_repeating_the_write()
    {
        var receiver = new Firmware { DelayBindingReadback = true };
        using var control = Open(receiver);
        var result = Run(control, new(OnboardOperation.SetKeyboard, 2, 0x39, 0, 0x68));
        Assert.Equal(Convert.FromHexString("02020068000000"), result.Binding!.Block);
        Assert.Single(receiver.Requests, r => (r.CommandClass, r.CommandId) == (0x02, 0x0C));
    }

    [Fact]
    public void Disabling_and_restoring_a_clutch_uses_the_firmware_function_blocks()
    {
        var receiver = new Firmware();
        using var control = Open(receiver);
        Run(control, new(OnboardOperation.SetKeyboard, 2, 0x39, 0, 0x68));
        var disabled = Run(control, new(OnboardOperation.Disable, 2, 0x39));
        Assert.Equal(Convert.FromHexString("00010000000000"), disabled.Binding!.Block);
        var restored = Run(control, new(OnboardOperation.Restore, 2, 0x39));
        Assert.Equal(Convert.FromHexString("0C013900000000"), restored.Binding!.Block);
        Assert.NotNull(Record.Exception(() => Run(control, new(OnboardOperation.Restore, 2, 0x39, 1))));
    }

    [Fact]
    public void Labels_are_local_to_the_selected_mouse_and_ambiguous_targets_cannot_write()
    {
        string root = Directory.CreateTempSubdirectory("viper-onboard-").FullName;
        try
        {
            var first = new Firmware { Serial = "FIRST" };
            var second = new Firmware { Serial = "SECOND" };
            using var manager = new DeviceManager(new SettingsStore(root), new Sink());
            manager.Refresh(
                [new("/dev/raw1", 0x1532, 0x00E2, 3, "Basilisk", "usb-a/input3"),
                 new("/dev/raw2", 0x1532, 0x00E3, 3, "Basilisk", "usb-b/input3")],
                [], _ => true, path => new RazerTransport(path == "/dev/raw1" ? first : second) { AnswerDelay = TimeSpan.Zero });

            Assert.Throws<InvalidDataException>(() => manager.ManageOnboard(new(OnboardOperation.Select, 1)));
            Assert.DoesNotContain(first.Requests.Concat(second.Requests), r => (r.CommandClass, r.CommandId) == (0x05, 0x04));
            var named = manager.ManageOnboard(new(OnboardOperation.Rename, 2, Serial: "FIRST", Name: "Work"));
            Assert.Equal("Work", named.Names![(byte)2]);
            Assert.Empty(manager.ManageOnboard(new(OnboardOperation.List, Serial: "SECOND")).Names!);
            Assert.Equal("Work", new SettingsStore(root).OnboardNames("FIRST", [1, 2], null, null)[(byte)2]);
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }

    [Fact]
    public void Unsupported_buttons_and_inactive_slot_writes_are_rejected()
    {
        var receiver = new Firmware();
        using var control = Open(receiver);
        Assert.NotNull(Record.Exception(() => Run(control, new(OnboardOperation.Disable, 1, 0x39))));
        Assert.NotNull(Record.Exception(() => Run(control, new(OnboardOperation.Disable, 2, 0x01))));
        Assert.DoesNotContain(receiver.Requests, r => (r.CommandClass, r.CommandId) == (0x02, 0x0C));
    }

    [Fact]
    public void Rejects_malformed_slot_lists_and_binding_echoes()
    {
        Assert.Null(BasiliskOnboard.ParseSlots(RazerReport.Create(0x1F, 0x05, 0x81, 1, 2)));
        Assert.Equal(new byte[] { 1, 3 }, BasiliskOnboard.ParseSlots(RazerReport.Create(0x1F, 0x05, 0x81, 5, 1, 3, 0)));
        var report = RazerReport.Create(0x1F, 0x02, 0x8C, Convert.FromHexString("01390002020068000000"));
        Assert.Null(BasiliskOnboard.ParseBinding(report, 2, 0x39, 0, allowInherited: true));
        Assert.Null(BasiliskOnboard.ParseBinding(report, 1, 0x60, 0, allowInherited: true));
        Assert.Null(BasiliskOnboard.ParseBinding(RazerReport.Create(0x1F, 0x02, 0x8C, report.Arguments[..9], 9),
            1, 0x39, 0, allowInherited: false));
        Assert.Null(BasiliskOnboard.ParseBinding(RazerReport.Create(0x1F, 0x02, 0x8C, report.Arguments[..11], 11),
            1, 0x39, 0, allowInherited: false));
    }

    static OnboardResult Run(DeviceControl control, OnboardRequest request) => control.ManageOnboard("TEST01", request);

    static DeviceControl Open(Firmware firmware, ushort pid = 0x00E3) =>
        new(new RazerTransport(firmware) { AnswerDelay = TimeSpan.Zero }, 0x1F, DeviceCatalogue.Default.Find(pid));

    sealed class Sink : IInputSink
    {
        public void Key(ushort code, int value) { }
        public void Relative(ushort axis, int value) { }
        public void Sync() { }
    }

    internal sealed class Firmware : IHidFeatureDevice
    {
        readonly Dictionary<(byte Slot, byte Button, byte Layer), byte[]> _buttons = [];
        RazerReport? _pending;
        byte _activeSlot = 2;
        bool _staleBinding;
        public string Path => "/emulated/basilisk";
        public string Serial { get; init; } = "TEST01";
        public bool IgnoreChanges { get; init; }
        public bool MirrorNormalLayer { get; init; }
        public bool DelayBindingReadback { get; init; }
        public SortedSet<byte> Slots { get; } = [1, 2];
        public List<RazerReport> Requests { get; } = [];

        public RazerReport Last(byte commandClass, byte commandId) =>
            Requests.Last(r => (r.CommandClass, r.CommandId) == (commandClass, commandId));

        public void SendFeature(ReadOnlySpan<byte> report)
        {
            Assert.Equal(0, report[0]);
            _pending = RazerReport.Parse(report[1..]);
            Assert.True(_pending.ChecksumValid);
            Requests.Add(_pending);
        }

        public int ReceiveFeature(Span<byte> report)
        {
            var request = Assert.IsType<RazerReport>(_pending);
            _pending = null;
            var args = request.Arguments;
            byte[] response = args[..request.DataSize].ToArray();
            switch (request.CommandClass, request.CommandId)
            {
                case (0x00, 0x81): response = [1, 0]; break;
                case (0x00, 0x82): response = Encoding.ASCII.GetBytes(Serial); break;
                case (0x05, 0x81):
                    response = new byte[10];
                    response[0] = 5;
                    Slots.CopyTo(response, 1);
                    break;
                case (0x05, 0x84): response = [_activeSlot]; break;
                case (0x05, 0x02): if (!IgnoreChanges) Slots.Add(args[0]); break;
                case (0x05, 0x03): if (!IgnoreChanges) Slots.Remove(args[0]); break;
                case (0x05, 0x04): if (!IgnoreChanges) _activeSlot = args[0]; break;
                case (0x02, 0x0C):
                    if (!IgnoreChanges)
                        _buttons[(args[0], args[1], args[2])] = args[3..10].ToArray();
                    _staleBinding = DelayBindingReadback;
                    break;
                case (0x02, 0x8C):
                    byte layer = MirrorNormalLayer ? (byte)0 : args[2];
                    byte[] fallback = Convert.FromHexString("00010000000000");
                    byte[] binding = _staleBinding ? fallback : _buttons.GetValueOrDefault((args[0], args[1], layer), fallback);
                    _staleBinding = false;
                    response = [args[0], args[1], layer, .. binding];
                    break;
            }
            RazerReport.Create(request.TransactionId, request.CommandClass, request.CommandId, response).Bytes.CopyTo(report[1..]);
            report[1] = (byte)ReportStatus.Successful;
            return report.Length;
        }

        public void Dispose() { }
    }
}
