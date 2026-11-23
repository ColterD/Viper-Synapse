using System.Text;
using ViperSynapse.Core.Lighting;
using ViperSynapse.Core.Protocol;

namespace ViperSynapse.Service;

public sealed partial class DeviceControl
{
    internal OnboardBackup BackupOnboard(string serial, byte slot)
    {
        lock (_gate)
        {
            CheckArchiveSlot(serial, slot);
            var dpi = ReadArchiveDpi(slot);
            var bindings = new List<OnboardBackupBinding>();
            foreach (var button in BasiliskOnboard.Buttons)
            {
                for (byte layer = 0; layer < 2; layer++)
                {
                    var binding = ReadArchiveBinding(slot, button.Id, layer);
                    // An inherited HyperShift action has no independent block to restore.
                    if (binding.Layer == layer && OnboardBackupCodec.IsSupportedBinding(button.Id, layer, binding.Block))
                        bindings.Add(new OnboardBackupBinding(button.Id, layer, binding.Block));
                }
            }
            List<OnboardBackupBrightness> brightness = [];
            List<OnboardBackupEffect> effects = [];
            foreach (byte led in ArchiveLightingLeds)
            {
                if (ReadArchiveValue(() => TryReadArchiveBrightness(slot, led)) is byte value)
                    brightness.Add(new OnboardBackupBrightness(led, value));
                if (ReadArchiveEffect(slot, led) is { } effect)
                    effects.Add(new OnboardBackupEffect(led, effect));
            }
            byte? smartReel = ReadArchiveValue(() => TryReadArchiveWheel(slot, Commands.GetSmartReel));
            byte? acceleration = ReadArchiveValue(() => TryReadArchiveWheel(slot, Commands.GetScrollAcceleration));
            var wheel = smartReel.HasValue || acceleration.HasValue
                ? new OnboardBackupWheel(smartReel is byte reel ? reel == 1 : null,
                    acceleration is byte accel ? accel == 1 : null)
                : null;
            RequireArchiveDestination(serial, slot);
            var backup = new OnboardBackup(OnboardBackupCodec.CurrentVersion, slot, dpi.ActiveStage, dpi.Stages, bindings,
                brightness.Count > 0 ? brightness : null, wheel, effects.Count > 0 ? effects : null);
            OnboardBackupCodec.Validate(backup);
            return backup;
        }
    }

    internal OnboardRestoreResult RestoreOnboard(string serial, byte slot, OnboardBackup backup)
    {
        OnboardBackupCodec.Validate(backup);
        lock (_gate)
        {
            CheckArchiveSlot(serial, slot);
            var changes = PlanArchiveRestore(serial, slot, backup);
            RequireArchiveDestination(serial, slot);

            // Preserve the archived values through background refresh until the local category is edited.
            _protectedSettings |= SlotSettings.Dpi;
            _sentPerformance = null;
            _dpiEditBaseline = null;
            if (backup.Brightness != null || backup.Effects != null)
            {
                _protectedSettings |= SlotSettings.Lighting;
                _sentLighting = null;
            }
            if (backup.Wheel != null)
            {
                _protectedSettings |= SlotSettings.Scroll;
                _sentScroll = null;
            }
            if (changes.Count == 0)
                return new(OnboardRestoreStatus.Restored, $"Slot {slot} already matches the backup.");

            var attempted = new Stack<ArchiveChange>();
            try
            {
                foreach (var change in changes)
                {
                    RequireArchiveDestination(serial, slot);
                    // Even a write without a successful acknowledgement may have reached the mouse.
                    attempted.Push(change);
                    change.Apply();
                }
                RequireArchiveDestination(serial, slot);
                return new(OnboardRestoreStatus.Restored, $"Restored and verified slot {slot}.");
            }
            catch (IOException error) when (attempted.Count > 0)
            {
                bool recovered = RollBackArchive(serial, slot, attempted);
                return recovered
                    ? new(OnboardRestoreStatus.RolledBack, $"Restore failed. The original settings for slot {slot} were verified after rollback. {error.Message}")
                    : new(OnboardRestoreStatus.RecoveryRequired, $"Restore failed and rollback could not be verified for slot {slot}. {error.Message}");
            }
        }
    }

    void CheckArchiveSlot(string serial, byte slot)
    {
        if (_disposed || !HasOnboardProfiles)
            throw new NotSupportedException("An online wired or HyperSpeed Basilisk V4 Pro is required.");
        if (!BasiliskOnboard.IsSlot(slot))
            throw new InvalidDataException("Onboard slots are numbered 1 through 5.");
        LastProblem = null;
        if (!MatchesArchiveSerial(serial))
            throw new IOException("The mouse's identity could not be confirmed.");
        if (ReadOnboardSlots(serial).ActiveSlot != slot)
            throw new InvalidDataException($"Select onboard slot {slot} before backing it up or restoring it.");
    }

    bool MatchesArchiveSerial(string serial) =>
        Read(Commands.GetSerial) is { DataSize: >= 1 and <= 22 } reply &&
        Encoding.ASCII.GetString(reply.Arguments[..reply.DataSize]).TrimEnd('\0', ' ') == serial;

    bool MatchesArchiveDestination(string serial, byte slot) =>
        ReadOnboardProfile() == slot && MatchesArchiveSerial(serial);

    void RequireArchiveDestination(string serial, byte slot)
    {
        if (!MatchesArchiveDestination(serial, slot))
            throw new IOException($"Slot {slot} is no longer confirmed as active on the selected mouse.");
    }

    sealed record ArchiveChange(Action Apply, Action RollBack);

    static ReadOnlySpan<byte> ArchiveLightingLeds =>
        [OnboardBackupBrightness.Scroll, OnboardBackupBrightness.Logo, OnboardBackupBrightness.Underglow];

    List<ArchiveChange> PlanArchiveRestore(string serial, byte slot, OnboardBackup backup)
    {
        var originalDpi = ReadArchiveDpi(slot);
        var newDpi = new PerformanceSettings(backup.Stages, backup.ActiveStage, 0);
        var changes = new List<ArchiveChange>();
        if (!SameArchiveDpi(originalDpi, newDpi))
            changes.Add(new(() => WriteArchiveDpi(slot, newDpi), () => WriteArchiveDpi(slot, originalDpi, restoringOriginal: true)));

        foreach (var expected in backup.Bindings)
        {
            var current = ReadArchiveBinding(slot, expected.Button, expected.Layer);
            if (current.Layer != expected.Layer ||
                !OnboardBackupCodec.IsSupportedBinding(expected.Button, expected.Layer, current.Block))
                throw new InvalidDataException(
                    $"Button {expected.Button:X2} on layer {expected.Layer} has no safely restorable original assignment.");
            if (current.Block.AsSpan().SequenceEqual(expected.Block))
                continue;
            var original = new OnboardBackupBinding(expected.Button, expected.Layer, current.Block);
            changes.Add(new(() => WriteArchiveBinding(slot, expected), () => WriteArchiveBinding(slot, original, restoringOriginal: true)));
        }
        if (backup.Brightness is { } brightness)
        {
            foreach (var setting in brightness)
            {
                if (backup.Effects?.Any(effect => effect.Led == setting.Led) == true)
                    continue;
                string zone = ArchiveLightingZone(setting.Led);
                PlanArchiveValue(changes, setting.Value, $"onboard {zone} brightness",
                    () => TryReadArchiveBrightness(slot, setting.Led),
                    value => DeviceCommands.BrightnessRaw(ControlTransactionId, setting.Led, value, slot));
            }
        }
        if (backup.Effects is { } effects)
            foreach (var effect in effects)
                PlanArchiveEffect(changes, serial, slot, effect,
                    backup.Brightness?.FirstOrDefault(setting => setting.Led == effect.Led)?.Value);
        if (backup.Wheel?.SmartReel is bool smartReel)
            PlanArchiveValue(changes, smartReel ? (byte)1 : (byte)0, "onboard Smart-Reel",
                () => TryReadArchiveWheel(slot, Commands.GetSmartReel),
                value => DeviceCommands.SmartReel(ControlTransactionId, value == 1, slot));
        if (backup.Wheel?.Acceleration is bool acceleration)
            PlanArchiveValue(changes, acceleration ? (byte)1 : (byte)0, "onboard scroll acceleration",
                () => TryReadArchiveWheel(slot, Commands.GetScrollAcceleration),
                value => DeviceCommands.ScrollAcceleration(ControlTransactionId, value == 1, slot));
        return changes;
    }

    static string ArchiveLightingZone(byte led) => led switch
    {
        OnboardBackupBrightness.Scroll => "scroll wheel",
        OnboardBackupBrightness.Logo => "logo",
        OnboardBackupBrightness.Underglow => "underglow",
        _ => throw new ArgumentOutOfRangeException(nameof(led)),
    };

    void PlanArchiveEffect(List<ArchiveChange> changes, string serial, byte slot, OnboardBackupEffect effect,
        byte? brightness)
    {
        byte led = effect.Led;
        string zone = ArchiveLightingZone(led);
        byte[] originalEffect = ReadArchiveEffect(slot, led) ??
            throw new IOException($"The mouse did not return a verifiable original effect for the onboard {zone}.");
        // A firmware effect change may reset brightness even if the backup did not include it.
        byte originalBrightness = ReadArchiveValue(() => TryReadArchiveBrightness(slot, led)) ??
            throw new IOException($"The mouse did not return a verifiable original brightness for the onboard {zone}.");
        byte desiredBrightness = brightness ?? originalBrightness;
        bool effectChanges = !originalEffect.AsSpan().SequenceEqual(effect.Body);
        if (!effectChanges && desiredBrightness == originalBrightness)
            return;

        changes.Add(new(() => Write(effect.Body, desiredBrightness),
            () => Write(originalEffect, originalBrightness, restoringOriginal: true)));

        void Write(byte[] body, byte value, bool restoringOriginal = false)
        {
            bool effectVerified = true;
            if (effectChanges || restoringOriginal && ReadArchiveEffect(slot, led, body) == null)
            {
                RequireArchiveDestination(serial, slot);
                var report = RazerReport.Create(ControlTransactionId, 0x0F, 0x02, [slot, led, ..body]);
                if (!Send(report, $"onboard {zone} effect") && !restoringOriginal)
                    throw new IOException(LastProblem);
                effectVerified = ReadArchiveEffect(slot, led, body) != null;
                if (!effectVerified && !restoringOriginal)
                    throw new IOException($"The mouse did not confirm the onboard {zone} effect.");
            }

            if (TryReadArchiveBrightness(slot, led) != value)
            {
                RequireArchiveDestination(serial, slot);
                var report = DeviceCommands.BrightnessRaw(ControlTransactionId, led, value, slot);
                if (!Send(report, $"onboard {zone} brightness") && !restoringOriginal)
                    throw new IOException(LastProblem);
                if (!ReadArchiveValue(() => TryReadArchiveBrightness(slot, led), value).HasValue)
                    throw new IOException($"The mouse did not confirm the onboard {zone} brightness.");
                effectVerified = ReadArchiveEffect(slot, led, body) != null;
            }

            if (!effectVerified)
                throw new IOException($"The mouse did not confirm the onboard {zone} effect.");
        }
    }

    byte[]? ReadArchiveEffect(byte slot, byte led, byte[]? expected = null)
    {
        for (int attempt = 0; attempt < 3; attempt++)
        {
            if (attempt > 0)
                Thread.Sleep(25);
            if (Read(Commands.GetLightingEffect, slot, led) is not
                    { DataSize: 6 or 9 or 12, ProtocolType: 0, RemainingPackets: 0 } reply ||
                reply.Arguments[0] != slot || reply.Arguments[1] != led)
                continue;

            var body = reply.Arguments[2..reply.DataSize];
            // Static adds one RGB triple. The full-size response may pad the native body with zeros.
            int length = body[0] == 0x01 ? 7 : 4;
            if (body.Length != length && (reply.DataSize != 12 || body[length..].ContainsAnyExcept((byte)0)))
                continue;
            body = body[..length];
            if (OnboardBackupCodec.IsSupportedEffect(led, body) && (expected == null || body.SequenceEqual(expected)))
                return body.ToArray();
        }
        return null;
    }

    byte? TryReadArchiveBrightness(byte slot, byte led) =>
        Read(Commands.GetBrightness, slot, led) is { DataSize: 3 } reply &&
        reply.Arguments[0] == slot && reply.Arguments[1] == led ? reply.Arguments[2] : null;

    byte? TryReadArchiveWheel(byte slot, Commands.Command command) =>
        Read(command, slot) is { DataSize: 2 } reply &&
        reply.Arguments[0] == slot && reply.Arguments[1] is 0 or 1 ? reply.Arguments[1] : null;

    static byte? ReadArchiveValue(Func<byte?> read, byte? expected = null)
    {
        for (int attempt = 0; attempt < 3; attempt++)
        {
            if (attempt > 0)
                Thread.Sleep(25);
            if (read() is byte value && (expected is null || value == expected))
                return value;
        }
        return null;
    }

    void PlanArchiveValue(List<ArchiveChange> changes, byte desired, string setting, Func<byte?> read,
        Func<byte, RazerReport> report)
    {
        byte original = ReadArchiveValue(read) ??
            throw new IOException($"The mouse did not return a verifiable original value for {setting}.");
        if (desired == original)
            return;
        changes.Add(new(() => Write(desired), () => Write(original, restoringOriginal: true)));

        void Write(byte value, bool restoringOriginal = false)
        {
            if (!Send(report(value), setting) && !restoringOriginal)
                throw new IOException(LastProblem);
            if (!ReadArchiveValue(read, value).HasValue)
                throw new IOException($"The mouse did not confirm {setting}.");
        }
    }

    bool RollBackArchive(string serial, byte slot, Stack<ArchiveChange> attempted)
    {
        bool recovered = true;
        while (attempted.TryPop(out var change))
        {
            if (!MatchesArchiveDestination(serial, slot))
                return false;
            try
            {
                change.RollBack();
            }
            catch (IOException)
            {
                recovered = false;
            }
        }
        return MatchesArchiveDestination(serial, slot) && recovered;
    }

    PerformanceSettings ReadArchiveDpi(byte slot) =>
        TryReadArchiveDpi(slot) ?? throw new IOException($"Slot {slot} did not return a supported DPI stage table.");

    PerformanceSettings? TryReadArchiveDpi(byte slot)
    {
        var table = Read(Commands.GetDpiStages, slot);
        if (table == null || !ValidStageTable(table, slot))
            return null;
        var dpi = DeviceCommands.ReadStages(table, 0);
        if (dpi.Stages.Any(stage => stage.X is < 100 or > 50_000 || stage.Y is < 100 or > 50_000))
            return null;
        var canonical = DeviceCommands.DpiStages(ControlTransactionId, dpi, thirdValue: true, store: slot);
        int used = 3 + 7 * dpi.Stages.Count;
        return canonical.Arguments[..used].SequenceEqual(table.Arguments[..used]) ? dpi : null;
    }

    static bool SameArchiveDpi(PerformanceSettings first, PerformanceSettings second) =>
        first.ActiveStage == second.ActiveStage && first.Stages.SequenceEqual(second.Stages);

    void WriteArchiveDpi(byte slot, PerformanceSettings dpi, bool restoringOriginal = false)
    {
        var report = DeviceCommands.DpiStages(ControlTransactionId, dpi, thirdValue: true, store: slot);
        if (!Send(report, "the onboard DPI stages") && !restoringOriginal)
            throw new IOException(LastProblem);
        for (int attempt = 0; attempt < 3; attempt++)
        {
            if (attempt > 0)
                Thread.Sleep(25);
            if (TryReadArchiveDpi(slot) is { } current && SameArchiveDpi(dpi, current))
                return;
        }
        throw new IOException("The mouse did not confirm the onboard DPI stages.");
    }

    OnboardBinding ReadArchiveBinding(byte slot, byte button, byte layer)
    {
        for (int attempt = 0; attempt < 3; attempt++)
        {
            if (attempt > 0)
                Thread.Sleep(25);
            var reply = ReadReport(BasiliskOnboard.ReadBinding(ControlTransactionId, slot, button, layer));
            if (reply != null && BasiliskOnboard.ParseBinding(reply, slot, button, layer,
                    allowInherited: true, allowOpaque: true) is { } binding)
                return binding;
        }
        throw new IOException($"Slot {slot} did not return an assignment for button {button:X2} on layer {layer}.");
    }

    void WriteArchiveBinding(byte slot, OnboardBackupBinding binding, bool restoringOriginal = false)
    {
        var report = BasiliskOnboard.WriteBinding(ControlTransactionId, slot, binding.Button, binding.Layer, binding.Block);
        if (!Send(report, "the onboard button assignment") && !restoringOriginal)
            throw new IOException(LastProblem);
        ReadOnboardBinding(new OnboardRequest(OnboardOperation.ReadBinding, slot, binding.Button, binding.Layer),
            allowInherited: false, expected: binding.Block);
    }
}
