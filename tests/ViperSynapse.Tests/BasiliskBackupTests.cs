using System.Text;
using ViperSynapse.Core.Bindings;
using ViperSynapse.Core.Devices;
using ViperSynapse.Core.Lighting;
using ViperSynapse.Core.Protocol;
using ViperSynapse.Hardware;
using ViperSynapse.Hardware.Linux;
using ViperSynapse.Service;

namespace ViperSynapse.Tests;

public class BasiliskBackupTests
{
    [Theory]
    [InlineData(0x00E2)]
    [InlineData(0x00E3)]
    public void Backup_and_restore_round_trip_only_the_active_slots_supported_values(ushort productId)
    {
        var firmware = new BackupFirmware { InheritHypershift = true };
        firmware.Buttons[(0x60, 0)] = Convert.FromHexString("AA010000000000");
        firmware.Buttons[(0x39, 0)] = BasiliskOnboard.Keyboard(0x68, 1);
        using var control = Open(firmware, productId);

        var archive = OnboardBackupCodec.ReadBackup(OnboardBackupCodec.WriteBackup(control.BackupOnboard("BACKUP", 2)));
        Assert.Equal<byte>(2, archive.SourceSlot);
        Assert.Equal([new DpiStage(800, 801)], archive.Stages);
        Assert.Equal(4, archive.Version);
        Assert.Collection(archive.Brightness!.OrderBy(zone => zone.Led),
            zone => Assert.Equal(new(0x01, 17), zone), zone => Assert.Equal(new(0x04, 101), zone));
        Assert.Equal(new(true, false), archive.Wheel);
        Assert.Equal(4, archive.Bindings.Count);
        Assert.DoesNotContain(archive.Bindings, binding => binding.Layer != 0 || binding.Button == 0x60);
        Assert.Empty(firmware.Writes);

        firmware.Dpi = new DpiStage(1600, 1601);
        firmware.Brightness[0x01] = 24;
        firmware.Brightness[0x04] = 255;
        firmware.SmartReel = false;
        firmware.Acceleration = true;
        firmware.Buttons[(0x39, 0)] = BasiliskOnboard.Disabled();
        var result = control.RestoreOnboard("BACKUP", 2, archive);
        Assert.Equal(OnboardRestoreStatus.Restored, result.Status);
        Assert.Equal(new DpiStage(800, 801), firmware.Dpi);
        Assert.Equal(17, firmware.Brightness[0x01]);
        Assert.Equal(101, firmware.Brightness[0x04]);
        Assert.True(firmware.SmartReel);
        Assert.False(firmware.Acceleration);
        Assert.Equal(BasiliskOnboard.Keyboard(0x68, 1), firmware.Buttons[(0x39, 0)]);
        Assert.Equal(Convert.FromHexString("AA010000000000"), firmware.Buttons[(0x60, 0)]);
    }

    [Theory]
    [InlineData(false, OnboardRestoreStatus.RolledBack)]
    [InlineData(true, OnboardRestoreStatus.RecoveryRequired)]
    public void A_failed_write_rolls_back_even_if_the_mouse_applied_it_before_returning_failure(
        bool failRollback, OnboardRestoreStatus status)
    {
        var firmware = new BackupFirmware { FailKeyboardWrite = true, FailRollback = failRollback };
        using var control = Open(firmware);

        var result = control.RestoreOnboard("BACKUP", 2, Incoming());
        Assert.Equal(status, result.Status);
        Assert.Equal(new DpiStage(800, 801), firmware.Dpi);
        Assert.Equal(failRollback ? BasiliskOnboard.Keyboard(0x68, 1) : BasiliskOnboard.Disabled(),
            firmware.Buttons[(0x39, 0)]);
        Assert.Equal(new byte[] { 0x06, 0x0C, 0x0C, 0x06 }, firmware.Writes.Select(report => report.CommandId));
    }

    [Fact]
    public void An_inherited_destination_assignment_cannot_be_overwritten_without_a_reversible_clear()
    {
        var firmware = new BackupFirmware { InheritHypershift = true };
        using var control = Open(firmware);
        var error = Record.Exception(() => control.RestoreOnboard("BACKUP", 2, Incoming(layer: 1)));
        Assert.NotNull(error);
        Assert.Empty(firmware.Writes);
    }

    [Fact]
    public void A_backup_cannot_smuggle_an_undocumented_firmware_function()
    {
        var archive = Incoming() with { Bindings = [new(0x39, 0, Convert.FromHexString("AA010000000000"))] };
        Assert.Throws<InvalidDataException>(() => OnboardBackupCodec.Validate(archive));
    }

    [Fact]
    public void An_unchanged_archive_requires_no_firmware_writes()
    {
        var firmware = new BackupFirmware();
        using var control = Open(firmware);
        var archive = control.BackupOnboard("BACKUP", 2);
        Assert.Equal(OnboardRestoreStatus.Restored, control.RestoreOnboard("BACKUP", 2, archive).Status);
        Assert.Empty(firmware.Writes);
    }

    [Fact]
    public void Backup_omits_wrong_slot_wrong_zone_and_non_boolean_readback()
    {
        var firmware = new BackupFirmware
        {
            WrongLogoReadback = true, WrongSmartReelSlot = true, UnknownAcceleration = true
        };
        using var control = Open(firmware);

        var archive = control.BackupOnboard("BACKUP", 2);
        Assert.Equal(new(0x01, 17), Assert.Single(archive.Brightness!));
        Assert.Null(archive.Wheel);
        Assert.Empty(firmware.Writes);
    }

    [Fact]
    public void Missing_original_readback_aborts_the_entire_restore_before_writing()
    {
        var firmware = new BackupFirmware { UnknownAcceleration = true };
        using var control = Open(firmware);
        var archive = Incoming() with { Version = 2, Brightness = [new(0x01, 33)], Wheel = new(Acceleration: true) };

        Assert.ThrowsAny<IOException>(() => control.RestoreOnboard("BACKUP", 2, archive));
        Assert.Empty(firmware.Writes);
    }

    [Theory]
    [InlineData(0x00E2, 2)]
    [InlineData(0x00E3, 4)]
    public void Fixed_effects_round_trip_with_exact_slot_zone_and_parameters(ushort product, byte slot)
    {
        var firmware = new BackupFirmware { Slot = slot, EffectResetsBrightness = true };
        firmware.Effects[0x01] = [0x04, 1, 0x28, 0];
        firmware.Effects[0x04] = [0x01, 0, 0, 1, 17, 88, 231];
        using var control = Open(firmware, product);
        var archive = OnboardBackupCodec.ReadBackup(OnboardBackupCodec.WriteBackup(control.BackupOnboard("BACKUP", slot)));
        Assert.Equal(2, archive.Effects!.Count);
        Assert.Equal(new byte[] { 0x01, 0, 0, 1, 17, 88, 231 }, archive.Effects.Single(effect => effect.Led == 0x04).Body);
        Assert.Empty(firmware.Writes);
        firmware.Effects[0x01] = [0x03, 0, 0, 0];
        firmware.Effects[0x04] = [0, 0, 0, 0];

        var result = control.RestoreOnboard("BACKUP", slot, archive with { Brightness = null });

        Assert.Equal(OnboardRestoreStatus.Restored, result.Status);
        Assert.Equal(new byte[] { 0x04, 1, 0x28, 0 }, firmware.Effects[0x01]);
        Assert.Equal(new byte[] { 0x01, 0, 0, 1, 17, 88, 231 }, firmware.Effects[0x04]);
        Assert.Equal(17, firmware.Brightness[0x01]);
        Assert.Equal(101, firmware.Brightness[0x04]);
        Assert.Equal(new byte[] { 6, 9 }, firmware.Writes.Where(report => report.CommandId == 0x02).Select(report => report.DataSize));
        Assert.All(firmware.Writes, report => Assert.Equal(slot, report.Arguments[0]));
    }

    [Theory]
    [InlineData(1, 4, "01000001000102")]
    [InlineData(2, 1, "01000001000102")]
    [InlineData(2, 4, "010000010001")]
    [InlineData(2, 4, "01000001000102FF0000")]
    [InlineData(2, 4, "02012801000102")]
    public void Unknown_or_stale_effect_readback_is_omitted_and_blocks_overwrite(byte replySlot, byte replyLed, string body)
    {
        var firmware = new BackupFirmware { RawLogoEffectReply = [replySlot, replyLed, .. Convert.FromHexString(body)] };
        firmware.Effects[0x01] = [0, 0, 0, 0];
        using var control = Open(firmware);

        var archive = control.BackupOnboard("BACKUP", 2);
        Assert.Equal<byte>(0x01, Assert.Single(archive.Effects!).Led);
        var incoming = Incoming() with { Version = 3, Effects = [new(0x04, [0, 0, 0, 0])] };
        Assert.ThrowsAny<IOException>(() => control.RestoreOnboard("BACKUP", 2, incoming));
        Assert.Empty(firmware.Writes);
    }

    [Theory]
    [InlineData(false, OnboardRestoreStatus.RolledBack)]
    [InlineData(true, OnboardRestoreStatus.RecoveryRequired)]
    public void Effect_written_before_failed_ack_rolls_back_and_repairs_brightness(
        bool failRollback, OnboardRestoreStatus status)
    {
        var firmware = new BackupFirmware { EffectResetsBrightness = true, FailArchiveCommand = 0x02, FailRollback = failRollback };
        firmware.Effects[0x04] = [0x01, 0, 0, 1, 13, 37, 72];
        using var control = Open(firmware);
        var archive = Incoming() with { Version = 3, Effects = [new(0x04, [0x03, 0, 0, 0])] };

        var result = control.RestoreOnboard("BACKUP", 2, archive);

        Assert.Equal(status, result.Status);
        Assert.Equal(failRollback ? new byte[] { 0x03, 0, 0, 0 } : new byte[] { 0x01, 0, 0, 1, 13, 37, 72 },
            firmware.Effects[0x04]);
        Assert.Equal(101, firmware.Brightness[0x04]);
        Assert.Equal(new DpiStage(800, 801), firmware.Dpi);
        Assert.Equal(new byte[] { 0x06, 0x0C, 0x02, 0x02, 0x04, 0x0C, 0x06 },
            firmware.Writes.Select(report => report.CommandId));
    }

    [Fact]
    public void Failed_brightness_write_after_an_effect_change_rolls_back_both_values()
    {
        var firmware = new BackupFirmware { EffectResetsBrightness = true, FailArchiveCommand = 0x04 };
        byte[] original = [0x01, 0, 0, 1, 13, 37, 72];
        firmware.Effects[0x04] = original;
        using var control = Open(firmware);
        var archive = control.BackupOnboard("BACKUP", 2) with
        {
            Brightness = [new(0x04, 56)],
            Effects = [new(0x04, [0x03, 0, 0, 0])]
        };

        Assert.Equal(OnboardRestoreStatus.RolledBack, control.RestoreOnboard("BACKUP", 2, archive).Status);
        Assert.Equal(original, firmware.Effects[0x04]);
        Assert.Equal(101, firmware.Brightness[0x04]);
        Assert.Equal(new byte[] { 0x02, 0x04, 0x02, 0x04 }, firmware.Writes.Select(report => report.CommandId));
    }

    [Theory]
    [InlineData(0x00E2, 2, false)]
    [InlineData(0x00E3, 4, false)]
    [InlineData(0x00E3, 4, true)]
    public void Underglow_archive_restores_raw_values_and_rolls_back_a_failed_brightness_write(
        ushort product, byte slot, bool failBrightness)
    {
        const byte led = OnboardBackupBrightness.Underglow;
        var firmware = new BackupFirmware
        {
            Slot = slot, EffectResetsBrightness = true,
            FailArchiveCommand = failBrightness ? (byte)0x04 : null
        };
        byte[] archivedEffect = [0x01, 0, 0, 1, 7, 23, 173];
        byte[] originalEffect = [0x04, 1, 0x28, 0];
        firmware.Brightness[led] = 73;
        firmware.Effects[led] = archivedEffect;
        using var control = Open(firmware, product);
        var archive = OnboardBackupCodec.ReadBackup(OnboardBackupCodec.WriteBackup(control.BackupOnboard("BACKUP", slot)));
        Assert.Equal(new(led, 73), archive.Brightness!.Single(zone => zone.Led == led));
        Assert.Equal(archivedEffect, Assert.Single(archive.Effects!).Body);
        Assert.Empty(firmware.Writes);
        firmware.Brightness[led] = 142;
        firmware.Effects[led] = originalEffect;

        var result = control.RestoreOnboard("BACKUP", slot, archive);

        Assert.Equal(failBrightness ? OnboardRestoreStatus.RolledBack : OnboardRestoreStatus.Restored, result.Status);
        Assert.Equal(failBrightness ? 142 : 73, firmware.Brightness[led]);
        Assert.Equal(failBrightness ? originalEffect : archivedEffect, firmware.Effects[led]);
        Assert.Equal(failBrightness ? new byte[] { 0x02, 0x04, 0x02, 0x04 } : new byte[] { 0x02, 0x04 },
            firmware.Writes.Select(report => report.CommandId));
        Assert.All(firmware.Writes, report =>
        {
            Assert.Equal(0x0F, report.CommandClass);
            Assert.Equal(slot, report.Arguments[0]);
            Assert.Equal(led, report.Arguments[1]);
        });
    }

    [Fact]
    public void Underglow_brightness_can_round_trip_without_an_effect_and_refuses_a_wrong_slot_reply()
    {
        const byte led = OnboardBackupBrightness.Underglow;
        var firmware = new BackupFirmware();
        firmware.Brightness[led] = 127;
        firmware.Effects[led] = [0x05, 0, 0x28, 1, 10, 11, 12];
        using var control = Open(firmware);
        var archive = control.BackupOnboard("BACKUP", 2);
        Assert.Null(archive.Effects);
        Assert.Equal(new(led, 127), archive.Brightness!.Single(zone => zone.Led == led));

        firmware.Brightness[led] = 219;
        firmware.StaleUnderglowBrightness = true;
        Assert.DoesNotContain(control.BackupOnboard("BACKUP", 2).Brightness!, zone => zone.Led == led);
        Assert.ThrowsAny<IOException>(() => control.RestoreOnboard("BACKUP", 2, archive));
        Assert.Empty(firmware.Writes);
        firmware.StaleUnderglowBrightness = false;
        Assert.Equal(OnboardRestoreStatus.Restored, control.RestoreOnboard("BACKUP", 2, archive).Status);
        Assert.Equal(127, firmware.Brightness[led]);
        Assert.Equal(0x04, Assert.Single(firmware.Writes).CommandId);
    }

    [Fact]
    public void Receiver_change_between_effect_and_brightness_blocks_further_writes()
    {
        var firmware = new BackupFirmware { EffectResetsBrightness = true, ChangeSerialAfterWrite = true };
        firmware.Effects[0x04] = [0x01, 0, 0, 1, 13, 37, 72];
        using var control = Open(firmware);
        var archive = control.BackupOnboard("BACKUP", 2) with { Effects = [new(0x04, [0x03, 0, 0, 0])] };

        Assert.Equal(OnboardRestoreStatus.RecoveryRequired, control.RestoreOnboard("BACKUP", 2, archive).Status);
        Assert.Equal(0x02, Assert.Single(firmware.Writes).CommandId);
    }

    [Fact]
    public void Effect_archives_reject_arbitrary_bodies_and_keep_version_two_compatible()
    {
        var original = Incoming() with { Version = 2, Wheel = new(true) };
        Assert.Null(OnboardBackupCodec.ReadBackup(OnboardBackupCodec.WriteBackup(original)).Effects);
        Assert.Throws<InvalidDataException>(() => OnboardBackupCodec.WriteBackup(original with { Effects = [new(0x04, [0, 0, 0, 0])] }));
        Assert.Throws<InvalidDataException>(() => OnboardBackupCodec.WriteBackup(original with { Version = 3, Effects = [new(0x0A, [0, 0, 0, 0])] }));
        Assert.Throws<InvalidDataException>(() => OnboardBackupCodec.WriteBackup(original with { Version = 3, Brightness = [new(0x0A, 1)] }));
        Assert.Throws<InvalidDataException>(() => OnboardBackupCodec.WriteBackup(original with { Version = 4, Effects = [new(0x00, [0, 0, 0, 0])] }));
        Assert.Throws<InvalidDataException>(() => OnboardBackupCodec.WriteBackup(original with { Version = 3, Effects = [new(0x04, [0x08, 0, 0, 0])] }));
        Assert.Throws<InvalidDataException>(() => OnboardBackupCodec.WriteBackup(original with { Version = 3, Effects = [new(0x04, [0, 0, 0, 0]), new(0x04, [0, 0, 0, 0])] }));
    }

    [Theory]
    [InlineData(0x04, false, false, OnboardRestoreStatus.RolledBack)]
    [InlineData(0x16, false, false, OnboardRestoreStatus.RolledBack)]
    [InlineData(0x16, false, true, OnboardRestoreStatus.RecoveryRequired)]
    [InlineData(0x16, true, false, OnboardRestoreStatus.RolledBack)]
    public void Partial_restore_journals_lighting_and_wheel_writes_in_reverse_order(
        byte failCommand, bool acknowledgeWithoutApplying, bool failRollback, OnboardRestoreStatus status)
    {
        var firmware = new BackupFirmware
        {
            FailArchiveCommand = failCommand, AcknowledgeWithoutApplying = acknowledgeWithoutApplying, FailRollback = failRollback
        };
        using var control = Open(firmware);
        var archive = Incoming() with { Version = 2, Brightness = [new(0x01, 33), new(0x04, 42)], Wheel = new(false, true) };

        var result = control.RestoreOnboard("BACKUP", 2, archive);
        Assert.Equal(status, result.Status);
        Assert.Equal(new DpiStage(800, 801), firmware.Dpi);
        Assert.Equal(BasiliskOnboard.Disabled(), firmware.Buttons[(0x39, 0)]);
        Assert.Equal(17, firmware.Brightness[0x01]);
        Assert.Equal(101, firmware.Brightness[0x04]);
        Assert.True(firmware.SmartReel);
        Assert.Equal(failRollback, firmware.Acceleration);
        byte[] expected = failCommand == 0x04
            ? [0x06, 0x0C, 0x04, 0x04, 0x0C, 0x06]
            : [0x06, 0x0C, 0x04, 0x04, 0x17, 0x16, 0x16, 0x17, 0x04, 0x04, 0x0C, 0x06];
        Assert.Equal(expected, firmware.Writes.Select(report => report.CommandId));
    }

    [Fact]
    public void Version_one_files_restore_without_touching_lighting_or_wheel()
    {
        var firmware = new BackupFirmware();
        using var control = Open(firmware);
        var json = OnboardBackupCodec.WriteBackup(Incoming());
        Assert.DoesNotContain("\"brightness\"", json);
        Assert.DoesNotContain("\"wheel\"", json);
        Assert.Equal(OnboardRestoreStatus.Restored, control.RestoreOnboard("BACKUP", 2, OnboardBackupCodec.ReadBackup(json)).Status);
        Assert.Equal(new byte[] { 0x06, 0x0C }, firmware.Writes.Select(report => report.CommandId));
        Assert.Throws<InvalidDataException>(() => OnboardBackupCodec.WriteBackup(Incoming() with { Wheel = new(true) }));
        Assert.Throws<InvalidDataException>(() => OnboardBackupCodec.WriteBackup(Incoming() with { Version = 2, Brightness = [new(0x0A, 0)] }));
        Assert.Throws<InvalidDataException>(() => OnboardBackupCodec.WriteBackup(Incoming() with { Version = 2, Brightness = [new(0x01, 0), new(0x01, 1)] }));
    }

    [Fact]
    public void Passive_profile_refresh_preserves_restored_brightness_and_wheel_toggles()
    {
        var firmware = new BackupFirmware();
        using var control = Open(firmware);
        var profile = Profile.Create("Local") with
        {
            Lighting = new LightingSettings(new Dictionary<string, ZoneLighting> { ["logo"] = new(ZoneEffect.Default, 10) }),
            Scroll = new ScrollSettings(Acceleration: false, SmartReel: true)
        };
        control.Apply(profile);
        Assert.Equal(26, firmware.Brightness[0x04]);
        Assert.True(firmware.SmartReel);
        Assert.False(firmware.Acceleration);

        var archive = Incoming() with { Version = 2, Brightness = [new(0x04, 101)], Wheel = new(false, true) };
        Assert.Equal(OnboardRestoreStatus.Restored, control.RestoreOnboard("BACKUP", 2, archive).Status);
        firmware.Writes.Clear();
        control.Apply(profile, refreshing: true);
        Assert.Empty(firmware.Writes);
        Assert.Equal(101, firmware.Brightness[0x04]);
        Assert.False(firmware.SmartReel);
        Assert.True(firmware.Acceleration);

        control.Apply(profile with { Scroll = profile.Scroll! with { Acceleration = true } });
        Assert.True(firmware.SmartReel);
        Assert.Equal(101, firmware.Brightness[0x04]);
    }

    [Fact]
    public void A_receiver_which_changes_mouse_mid_restore_does_not_receive_rollback_writes()
    {
        var firmware = new BackupFirmware { ChangeSerialAfterWrite = true };
        using var control = Open(firmware);
        var result = control.RestoreOnboard("BACKUP", 2, Incoming());
        Assert.Equal(OnboardRestoreStatus.RecoveryRequired, result.Status);
        Assert.Equal(0x06, Assert.Single(firmware.Writes).CommandId);
    }

    internal static OnboardBackup Incoming(byte layer = 0) =>
        new(1, 1, 0, [new(1200, 1201)], [new(0x39, layer, BasiliskOnboard.Keyboard(0x68, 1))]);

    static DeviceControl Open(BackupFirmware firmware, ushort product = 0x00E3) =>
        new(new RazerTransport(firmware) { AnswerDelay = TimeSpan.Zero }, 0x1F, DeviceCatalogue.Default.Find(product));

    internal sealed class BackupFirmware : IHidFeatureDevice
    {
        RazerReport? _request;
        bool _failed;
        public string Path => "/emulated/basilisk";
        public byte Slot { get; init; } = 2;
        public DpiStage Dpi { get; set; } = new(800, 801);
        public bool InheritHypershift { get; init; }
        public bool FailKeyboardWrite { get; init; }
        public bool FailRollback { get; init; }
        public byte? FailArchiveCommand { get; init; }
        public bool AcknowledgeWithoutApplying { get; init; }
        public bool WrongLogoReadback { get; init; }
        public bool WrongSmartReelSlot { get; init; }
        public bool UnknownAcceleration { get; init; }
        public bool ChangeSerialAfterWrite { get; init; }
        public bool SmartReel { get; set; } = true;
        public bool Acceleration { get; set; }
        public bool EffectResetsBrightness { get; init; }
        public bool StaleUnderglowBrightness { get; set; }
        public byte[]? RawLogoEffectReply { get; init; }
        public Dictionary<byte, byte[]> Effects { get; } = [];
        public Dictionary<byte, byte> Brightness { get; } = new() { [0x01] = 17, [0x04] = 101 };
        public Dictionary<(byte Id, byte Layer), byte[]> Buttons { get; } = [];
        public List<RazerReport> Writes { get; } = [];

        public void SendFeature(ReadOnlySpan<byte> report)
        {
            Assert.Equal(0, report[0]);
            _request = RazerReport.Parse(report[1..]);
            Assert.True(_request.ChecksumValid);
        }

        public int ReceiveFeature(Span<byte> report)
        {
            var request = _request!;
            var args = request.Arguments;
            var status = ReportStatus.Successful;
            byte[] response = args[..request.DataSize].ToArray();
            switch (request.CommandClass, request.CommandId)
            {
                case (0x00, 0x81): response = [1, 0]; break;
                case (0x00, 0x82):
                    response = Encoding.ASCII.GetBytes(ChangeSerialAfterWrite && Writes.Count > 0 ? "REPLACEMENT" : "BACKUP");
                    break;
                case (0x05, 0x81): response = [5, 1, Slot]; break;
                case (0x05, 0x84): response = [Slot]; break;
                case (0x04, 0x86):
                    Assert.Equal(Slot, args[0]);
                    response = DeviceCommands.DpiStages(request.TransactionId, new PerformanceSettings([Dpi], 0, 0),
                        thirdValue: true, store: Slot).Arguments[..0x26].ToArray();
                    break;
                case (0x04, 0x06):
                    Assert.Equal(Slot, args[0]);
                    Writes.Add(request);
                    Dpi = DeviceCommands.ReadStages(request, 0).Stages[0];
                    break;
                case (0x02, 0x8C):
                    Assert.Equal(Slot, args[0]);
                    byte layer = InheritHypershift ? (byte)0 : args[2];
                    response = [Slot, args[1], layer, .. Buttons.GetValueOrDefault((args[1], layer), BasiliskOnboard.Disabled())];
                    break;
                case (0x02, 0x0C):
                    Assert.Equal(Slot, args[0]);
                    Writes.Add(request);
                    if (FailKeyboardWrite && _failed && FailRollback)
                        status = ReportStatus.Failure;
                    else
                        Buttons[(args[1], args[2])] = args[3..10].ToArray();
                    if (FailKeyboardWrite && args[3] == 2)
                    {
                        _failed = true;
                        status = ReportStatus.Failure;
                    }
                    break;
                case (0x0F, 0x82):
                    Assert.Equal(Slot, args[0]);
                    Assert.Equal(12, request.DataSize);
                    if (args[1] == 0x04 && RawLogoEffectReply != null)
                        response = RawLogoEffectReply;
                    else if (Effects.TryGetValue(args[1], out var effect))
                        response = args[1] == 0x04
                            ? [Slot, args[1], .. effect, .. new byte[10 - effect.Length]]
                            : [Slot, args[1], .. effect];
                    else
                        status = ReportStatus.Failure;
                    break;
                case (0x0F, 0x84):
                    Assert.Equal(Slot, args[0]);
                    Assert.Equal(3, request.DataSize);
                    if (Brightness.TryGetValue(args[1], out byte brightness))
                        response = [StaleUnderglowBrightness && args[1] == 0x0A ? (byte)1 : Slot,
                            WrongLogoReadback && args[1] == 0x04 ? (byte)0x01 : args[1], brightness];
                    else
                        status = ReportStatus.Failure;
                    break;
                case (0x02, 0x97):
                    Assert.Equal(Slot, args[0]);
                    Assert.Equal(2, request.DataSize);
                    response = [WrongSmartReelSlot ? (byte)1 : Slot, SmartReel ? (byte)1 : (byte)0];
                    break;
                case (0x02, 0x96):
                    Assert.Equal(Slot, args[0]);
                    Assert.Equal(2, request.DataSize);
                    response = [Slot, UnknownAcceleration ? (byte)0xFF : Acceleration ? (byte)1 : (byte)0];
                    break;
                case (0x0F, 0x02):
                case (0x0F, 0x04):
                case (0x02, 0x17):
                case (0x02, 0x16):
                    Assert.Equal(Slot, args[0]);
                    Writes.Add(request);
                    if (request.CommandId == FailArchiveCommand)
                    {
                        if (_failed && FailRollback)
                        {
                            status = ReportStatus.Failure;
                            break;
                        }
                        if (!_failed)
                        {
                            _failed = true;
                            if (AcknowledgeWithoutApplying)
                                break;
                            status = ReportStatus.Failure;
                        }
                    }
                    if (request.CommandId == 0x02)
                    {
                        Effects[args[1]] = args[2..request.DataSize].ToArray();
                        if (EffectResetsBrightness)
                            Brightness[args[1]] = 255;
                    }
                    else if (request.CommandClass == 0x0F)
                        Brightness[args[1]] = args[2];
                    else if (request.CommandId == 0x17)
                        SmartReel = args[1] == 1;
                    else
                        Acceleration = args[1] == 1;
                    break;
            }
            RazerReport.Create(request.TransactionId, request.CommandClass, request.CommandId, response).Bytes.CopyTo(report[1..]);
            report[1] = (byte)status;
            return report.Length;
        }

        public void Dispose() { }
    }
}
