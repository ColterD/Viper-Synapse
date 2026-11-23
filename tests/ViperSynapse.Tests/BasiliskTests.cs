using ViperSynapse.Core.Devices;
using ViperSynapse.Core.Bindings;
using ViperSynapse.Core.Lighting;
using ViperSynapse.Core.Protocol;
using ViperSynapse.App.ViewModels;
using ViperSynapse.Hardware;
using ViperSynapse.Hardware.Linux;
using ViperSynapse.Service;

namespace ViperSynapse.Tests;

public class BasiliskTests
{
    [Fact]
    public void The_wired_and_receiver_ids_lookup_the_same_model_and_keep_their_usb_ids()
    {
        var wired = DeviceCatalogue.Default.Find(0x00E2);
        var receiver = DeviceCatalogue.Default.Find(0x00E3);
        Assert.NotNull(wired);
        Assert.NotNull(receiver);
        Assert.Equal(0x00E2, wired.ProductId);
        Assert.Equal(0x00E3, receiver.ProductId);
        Assert.Equal(receiver.Name, wired.Name);
        Assert.Equal(receiver.Keys, wired.Keys);
        Assert.Equal(receiver.PollRates, wired.PollRates);
    }

    [Theory]
    [InlineData(0x00E2)]
    [InlineData(0x00E3)]
    public void Firmware_settings_follow_the_active_slot_and_current_dpi_uses_the_stage_table(ushort productId)
    {
        var device = new Reports();
        using var control = Open(device, productId);
        var profile = Profile.Create("Test") with
        {
            Performance = new PerformanceSettings([new DpiStage(801, 802)], 0, 8000),
            Scroll = new ScrollSettings(1, true, true),
            Lighting = new LightingSettings(new Dictionary<string, ZoneLighting>
            {
                ["logo"] = new(ZoneEffect.Default, 47)
            })
        };

        control.Apply(profile);
        Assert.Equal(2, device.Last(0x04, 0x06).Arguments[0]);
        Assert.Equal(2, device.Last(0x02, 0x16).Arguments[0]);
        Assert.Equal(2, device.Last(0x02, 0x17).Arguments[0]);
        Assert.Equal(2, device.Last(0x0F, 0x04).Arguments[0]);
        Assert.Equal(1, device.Last(0x00, 0x40).Arguments[1]);
        Assert.DoesNotContain(device.Requests, r => r.CommandClass == 0x04 && r.CommandId == 0x05);
        Assert.Equal<byte?>(2, control.ReadState().OnboardProfile);
        Assert.Equal(2, device.Last(0x04, 0x86).Arguments[0]);

        device.Slot = 4;
        control.Apply(profile with
        {
            Performance = profile.Performance! with { Stages = [new DpiStage(803, 804)] },
            Scroll = profile.Scroll! with { Acceleration = false }
        });
        Assert.Equal(4, device.Last(0x04, 0x06).Arguments[0]);
        Assert.Equal(4, device.Last(0x02, 0x16).Arguments[0]);
    }

    [Fact]
    public void A_missing_slot_blocks_scoped_writes_but_polling_can_still_change()
    {
        var device = new Reports { Slot = 0 };
        using var control = Open(device, 0x00E3);
        var profile = Profile.Create("Test") with
        {
            Performance = new PerformanceSettings([new DpiStage(800, 800)], 0, 2000),
            Scroll = new ScrollSettings(1, true, true)
        };
        control.Apply(profile);
        Assert.NotNull(control.LastProblem);
        Assert.DoesNotContain(device.Requests, r => (r.CommandClass, r.CommandId) is (0x04, 0x06) or (0x02, 0x16) or (0x02, 0x17));
        Assert.Equal(4, device.Last(0x00, 0x40).Arguments[1]);

        device.Slot = 3;
        control.Apply(profile);
        Assert.Null(control.LastProblem);
        Assert.Equal(3, device.Last(0x04, 0x06).Arguments[0]);
    }

    [Fact]
    public void A_passive_refresh_does_not_overwrite_a_newly_selected_firmware_slot()
    {
        var device = new Reports();
        using var control = Open(device, 0x00E3);
        var profile = Profile.Create("Test") with { Scroll = new ScrollSettings(Acceleration: true) };
        control.Apply(profile, refreshing: true);
        device.Slot = 3;
        control.Apply(profile, refreshing: true);
        control.Apply(profile, refreshing: true);
        Assert.Equal(2, device.Last(0x02, 0x16).Arguments[0]);
        Assert.Equal<byte?>(3, control.ReadState().OnboardProfile);

        control.Apply(profile with { Name = "Unrelated edit" });
        Assert.Equal(2, device.Last(0x02, 0x16).Arguments[0]);
        control.Apply(profile with { Scroll = new ScrollSettings(Acceleration: false) });
        Assert.Equal(3, device.Last(0x02, 0x16).Arguments[0]);
    }

    [Theory]
    [InlineData(2)]
    [InlineData(4)]
    public void A_replacement_transport_keeps_the_onboard_slot_intact(byte slot)
    {
        var device = new Reports { Slot = slot };
        using var control = new DeviceControl(new RazerTransport(device) { AnswerDelay = TimeSpan.Zero }, 0x1F,
            DeviceCatalogue.Default.Find(0x00E3), previousOnboardProfile: 2);
        control.Apply(Profile.Create("Test") with { Scroll = new ScrollSettings(Acceleration: true) }, refreshing: true);
        Assert.Equal<byte?>(slot, control.OnboardProfile);
        Assert.DoesNotContain(device.Requests, r => (r.CommandClass, r.CommandId) == (0x02, 0x16));
    }

    [Fact]
    public void A_corrupt_active_profile_reply_cannot_direct_writes()
    {
        var device = new Reports { CorruptSlotReply = true };
        using var control = Open(device, 0x00E3);
        control.Apply(Profile.Create("Test") with { Scroll = new ScrollSettings(Acceleration: true) });
        Assert.NotNull(control.LastProblem);
        Assert.DoesNotContain(device.Requests, r => (r.CommandClass, r.CommandId) == (0x02, 0x16));
    }

    [Theory]
    [InlineData(ScrollWheelMode.Tactile, 0)]
    [InlineData(ScrollWheelMode.FreeSpin, 1)]
    [InlineData(ScrollWheelMode.PrecisionTactile, 2)]
    public void Wheel_mode_uses_both_bytes_and_reads_the_selected_mode(ScrollWheelMode mode, byte code)
    {
        var device = new Reports();
        using var control = Open(device, 0x00E3);
        control.Apply(Profile.Create("Test") with { Scroll = new ScrollSettings(WheelMode: mode) });
        Assert.Equal(new byte[] { 0, code }, device.Last(0x02, 0x30).Arguments[..2].ToArray());
        Assert.Equal(mode, control.ReadState().WheelMode);
    }

    [Fact]
    public void Basilisk_dpi_steps_preserve_single_unit_precision()
    {
        var basilisk = new PerformanceViewModel(DeviceCatalogue.Default.Find(0x00E3), _ => { });
        var naga = new PerformanceViewModel(DeviceCatalogue.Default.Find(0x008D), _ => { });
        Assert.Equal(1, basilisk.DpiStep);
        Assert.Equal(50, naga.DpiStep);
    }

    [Fact]
    public void The_scroll_editor_saves_precision_tactile_and_can_leave_the_mode_to_the_mouse()
    {
        ScrollSettings? saved = null;
        var wheel = new ScrollWheelViewModel(settings => saved = settings);
        wheel.Load(new ScrollSettings(3, true, false, ScrollWheelMode.FreeSpin));
        Assert.Equal(ScrollWheelMode.FreeSpin, wheel.WheelMode?.Mode);
        Assert.Null(saved);
        wheel.WheelMode = wheel.WheelModes.Single(option => option.Mode == ScrollWheelMode.PrecisionTactile);
        Assert.Equal(new ScrollSettings(3, true, false, ScrollWheelMode.PrecisionTactile), saved);
        wheel.WheelMode = wheel.WheelModes.Single(option => option.Mode == null);
        Assert.Equal(new ScrollSettings(3, true, false), saved);
        wheel.Load(ScrollSettings.Default, ScrollWheelMode.FreeSpin);
        Assert.Equal("Free-spin", wheel.ReportedWheelMode);
        wheel.LinesPerNotch = 2;
        Assert.Equal(new ScrollSettings(2), saved);
    }

    [Fact]
    public void Short_stage_tables_are_not_exposed_to_the_editor()
    {
        var device = new Reports { ShortStageReply = true };
        using var control = Open(device, 0x00E3);
        Assert.Null(control.ReadState().Stages);
        Assert.NotNull(control.LastProblem);
    }

    [Fact]
    public void A_single_stage_edit_preserves_the_other_values_in_the_selected_slot()
    {
        var device = new Reports { Slot = 3, InitialStages = Convert.FromHexString("0301020103E803E803E80207D007D007D0") };
        using var control = new DeviceControl(new RazerTransport(device) { AnswerDelay = TimeSpan.Zero }, 0x1F,
            DeviceCatalogue.Default.Find(0x00E3), previousOnboardProfile: 2);
        var profile = Profile.Create("Test") with
        {
            Performance = new PerformanceSettings([new(400, 400), new(800, 800)], 0, 1000)
        };
        control.Apply(profile, refreshing: true);
        control.Apply(profile with { Performance = profile.Performance! with { Stages = [new(401, 400), new(800, 800)] } });

        Assert.Equal([new DpiStage(401, 1000), new DpiStage(2000, 2000)], control.ReadState().Stages);
    }

    [Theory]
    [InlineData(0x00E2)]
    [InlineData(0x00E3)]
    public void Wireless_power_and_polling_reads_use_the_documented_payload_sizes(ushort productId)
    {
        var device = new Reports();
        using var control = Open(device, productId);
        var state = control.ReadState();

        Assert.Equal<int?>(80, state.BatteryPercent);
        Assert.True(state.Charging);
        Assert.Equal(600, state.SleepSeconds);
        Assert.Equal(30, state.LowBatteryPercent);
        Assert.Equal(8000, state.PollingRate);
        Assert.Equal(2, device.Last(0x07, 0x83).DataSize);
        Assert.Equal(1, device.Last(0x07, 0x81).DataSize);
        Assert.Equal(2, device.Last(0x02, 0x96).DataSize);
        Assert.Equal(2, device.Last(0x02, 0x97).DataSize);
    }

    [Fact]
    public void Advanced_lighting_sends_all_seven_pixels_as_one_live_frame()
    {
        var device = new Reports();
        using var control = Open(device, 0x00E3);
        var model = Assert.IsType<DeviceModel>(DeviceCatalogue.Default.Find(0x00E3));
        Assert.Equal((1, 7), (model.MatrixRows, model.MatrixColumns));
        var pixels = Enumerable.Range(1, 7).Select(i => new Rgb((byte)i, 0, 0)).ToArray();
        var lighting = new LightingSettings(
            new Dictionary<string, ZoneLighting> { ["underglow"] = new(ZoneEffect.Default with { Kind = EffectKind.Custom }) },
            new CustomFrame(1, 7, pixels));

        control.Apply(Profile.Create("Test") with { Lighting = lighting });
        var row = device.Last(0x0F, 0x03);
        Assert.Equal(6, row.Arguments[4]);
        Assert.Equal(Enumerable.Range(1, 7), Enumerable.Range(0, 7).Select(i => (int)row.Arguments[5 + i * 3]));
        var effect = device.Last(0x0F, 0x02);
        Assert.Equal(0, effect.Arguments[0]);
        Assert.Equal(8, effect.Arguments[2]);
    }

    static DeviceControl Open(Reports device, ushort productId) =>
        new(new RazerTransport(device) { AnswerDelay = TimeSpan.Zero }, 0x1F, DeviceCatalogue.Default.Find(productId));

    sealed class Reports : IHidFeatureDevice
    {
        RazerReport? _pending;
        byte _wheelMode;
        public byte Slot { get; set; } = 2;
        public bool CorruptSlotReply { get; set; }
        public bool ShortStageReply { get; set; }
        public byte[]? InitialStages { get; set; }
        public string Path => "/emulated/hidraw";
        public List<RazerReport> Requests { get; } = [];
        public RazerReport Last(byte commandClass, byte commandId) =>
            Requests.Last(r => r.CommandClass == commandClass && r.CommandId == commandId);

        public void SendFeature(ReadOnlySpan<byte> report)
        {
            Assert.Equal(0, report[0]);
            var command = RazerReport.Parse(report[1..]);
            Assert.True(command.ChecksumValid);
            Requests.Add(command);
            _pending = command;
        }

        public int ReceiveFeature(Span<byte> report)
        {
            var request = Assert.IsType<RazerReport>(_pending);
            _pending = null;
            if ((request.CommandClass, request.CommandId) == (0x02, 0x30))
                _wheelMode = request.Arguments[1];
            var args = (request.CommandClass, request.CommandId) switch
            {
                (0x05, 0x84) => new byte[] { Slot },
                (0x02, 0xB0) => new byte[] { 0, _wheelMode },
                (0x00, 0xC0) => new byte[] { 0, 1 },
                (0x07, 0x80) => new byte[] { 0, 204 },
                (0x07, 0x81) => new byte[] { 77 },
                (0x07, 0x83) => new byte[] { 2, 88 },
                (0x07, 0x84) => new byte[] { 0, 1 },
                (0x04, 0x86) when ShortStageReply => new byte[] { Slot, 1, 5 },
                (0x04, 0x86) => ReadStageReply(),
                _ => request.Arguments[..request.DataSize].ToArray()
            };
            RazerReport.Create(request.TransactionId, request.CommandClass, request.CommandId, args).Bytes.CopyTo(report[1..]);
            report[1] = (byte)ReportStatus.Successful;
            if (CorruptSlotReply && (request.CommandClass, request.CommandId) == (0x05, 0x84))
                report[9] ^= 1;
            return report.Length;
        }

        byte[] ReadStageReply()
        {
            var write = Requests.LastOrDefault(r => (r.CommandClass, r.CommandId) == (0x04, 0x06) && r.Arguments[0] == Slot);
            if (write != null)
                return write.Arguments[..0x26].ToArray();
            var result = InitialStages?.ToArray() ?? Convert.FromHexString("02010101032003200320");
            result[0] = Slot;
            return result;
        }

        public void Dispose() { }
    }
}
