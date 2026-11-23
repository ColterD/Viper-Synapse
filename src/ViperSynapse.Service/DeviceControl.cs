using System.Text.Json;
using System.Text.Json.Serialization;
using ViperSynapse.Core.Bindings;
using ViperSynapse.Core.Devices;
using ViperSynapse.Core.Lighting;
using ViperSynapse.Core.Protocol;
using ViperSynapse.Hardware;

namespace ViperSynapse.Service;

/// <summary>What the Performance and Power pages show before the user changes anything: the device's own values.</summary>
public sealed record DeviceState(
    IReadOnlyList<DpiStage>? Stages,
    int ActiveStage,
    int PollingRate,
    int? BatteryPercent,
    bool? Charging,
    int LiftOffLevel = -1,
    int SleepSeconds = 0,
    int LowBatteryPercent = 0,
    bool? ScrollAcceleration = null,
    bool? SmartReel = null,
    byte? OnboardProfile = null,
    ScrollWheelMode? WheelMode = null);

[JsonSourceGenerationOptions(PropertyNamingPolicy = JsonKnownNamingPolicy.CamelCase)]
[JsonSerializable(typeof(DeviceState))]
internal sealed partial class DeviceStateJson : JsonSerializerContext;

/// <summary>
/// One device's control interface, kept open while it is plugged in: sends a profile's lighting, sensitivity and
/// power settings, and reads the device's state. A setting is sent only when it differs from what was sent last,
/// so saving a key binding does not restart the lighting animation. Failures are reported, never thrown at the
/// caller: a device that refuses one command keeps the rest.
/// </summary>
public sealed partial class DeviceControl(RazerTransport transport, byte transactionId, DeviceModel? model,
    byte? previousOnboardProfile = null) : IDisposable
{
    [Flags]
    enum SlotSettings { Lighting = 1, Dpi = 2, Scroll = 4, All = Lighting | Dpi | Scroll }

    readonly Lock _gate = new();
    string? _sentLighting, _sentPerformance, _sentPower, _sentCalibration, _sentScroll;
    int _sentPollingRate;
    ScrollWheelMode? _sentWheelMode;
    byte? _onboardProfile;
    byte? _lastKnownProfile = previousOnboardProfile;
    // Settings already live on a proven reattached mouse. Wait for a category edit before overwriting them.
    SlotSettings _protectedSettings = previousOnboardProfile.HasValue ? SlotSettings.All : 0;
    Profile? _lastRequestedProfile;
    PerformanceSettings? _dpiEditBaseline;
    int _failedWrites;
    int _acknowledgedWrites;
    int _readbackConfirmations;
    bool _disposed;
    bool HasOnboardProfiles => model?.Has("onboard_profiles") == true;
    bool IsNagaChroma => model?.ProductId == LegacyRemapControls.NagaChroma;
    // OpenRazer uses FF for both live DPI commands on the Naga Chroma, independent of the identity probe.
    byte DpiTransactionId => IsNagaChroma ? NagaChromaSensitivity.TransactionId : transactionId;
    byte ControlTransactionId => transactionId;
    public byte? OnboardProfile { get { lock (_gate) return _onboardProfile; } }
    int _sentKeymap = -1;

    public string? LastProblem { get; private set; }

    internal UnitApplyOutcome ApplyWithResult(string port, string productId, string? serial, Profile profile, int keymap, bool refreshing)
    {
        lock (_gate)
        {
            if (_disposed)
                return new(port, productId, serial, UnitApplyStatus.Disconnected);
            int acknowledged = _acknowledgedWrites, confirmed = _readbackConfirmations;
            Apply(profile, keymap, refreshing);
            acknowledged = _acknowledgedWrites - acknowledged;
            confirmed = _readbackConfirmations - confirmed;
            var status = LastProblem != null ? UnitApplyStatus.Failed : acknowledged > 0 ? UnitApplyStatus.Acknowledged
                : confirmed > 0 ? UnitApplyStatus.ReadbackConfirmed : UnitApplyStatus.NoFirmwareChanges;
            return new(port, productId, serial, status, LastProblem, acknowledged, confirmed);
        }
    }

    /// <param name="profile">The profile to bring the device in line with.</param>
    /// <param name="keymap">The keymap in use (0-based): a keypad with keymaps of its own is switched to it.</param>
    /// <param name="refreshing">Whether this is a background refresh, which leaves a newly selected firmware slot alone.</param>
    public void Apply(Profile profile, int keymap = 0, bool refreshing = false)
    {
        lock (_gate)
        {
            if (_disposed)
                return;
            LastProblem = null;
            byte? store = HasOnboardProfiles ? ReadOnboardProfile() : DeviceCommands.VarStore;
            if (HasOnboardProfiles)
            {
                if (!refreshing && _lastRequestedProfile is { } last)
                {
                    UnprotectOnChange(SlotSettings.Lighting, last.Lighting, profile.Lighting);
                    UnprotectOnChange(SlotSettings.Dpi, last.Performance is { } lastDpi ? DpiOnly(lastDpi) : null,
                        profile.Performance is { } nextDpi ? DpiOnly(nextDpi) : null);
                    UnprotectOnChange(SlotSettings.Scroll, last.Scroll is { } lastScroll ? ScrollToggles(lastScroll) : null,
                        profile.Scroll is { } nextScroll ? ScrollToggles(nextScroll) : null);
                    if (last.Id != profile.Id || last.Performance?.Stages.Count != profile.Performance?.Stages.Count)
                        _dpiEditBaseline = null;
                    else if (last.Performance is { Stages.Count: > 0 } previous && profile.Performance is { } next &&
                        (previous.ActiveStage != next.ActiveStage || !previous.Stages.SequenceEqual(next.Stages)))
                        _dpiEditBaseline ??= previous;
                }
                _lastRequestedProfile = profile;
            }
            if (model?.Has("keymaps") == true && keymap != _sentKeymap &&
                Send(DeviceCommands.SelectKeymap(transactionId, keymap + 1), "keymap"))
                _sentKeymap = keymap;
            if (profile.Lighting is { IsEmpty: false } lighting && store is byte lightingSlot && SlotCanChange(SlotSettings.Lighting))
            {
                _lighting = lighting;
                ApplyChanged(ref _sentLighting, lighting, () => SendLighting(lighting, lightingSlot));
            }
            if (profile.Performance is { } performance)
            {
                if (store is byte dpiSlot && model?.Has("dpi") == true && SlotCanChange(SlotSettings.Dpi))
                    ApplyChanged(ref _sentPerformance, DpiOnly(performance), () => SendPerformance(performance, dpiSlot));
                if (performance.PollingRate > 0 && performance.PollingRate != _sentPollingRate && model?.Has("poll_rate") == true)
                {
                    bool hyper = model.PollRates.Any(r => r > 1000);
                    if (Send(hyper ? DeviceCommands.HyperPollingRate(transactionId, performance.PollingRate) :
                        DeviceCommands.PollingRate(transactionId, performance.PollingRate), "polling rate"))
                        _sentPollingRate = performance.PollingRate;
                }
            }
            if (profile.Power is { } power && model?.Has("idle_time") == true)
                ApplyChanged(ref _sentPower, power, () => SendPower(power));
            if (profile.Calibration is { } calibration && model?.Has("lift_off") == true)
                ApplyChanged(ref _sentCalibration, calibration, () => SendCalibration(calibration));
            // Only the firmware's part: the scroll speed is the remap engine's.
            if (profile.Scroll is { } scroll && model?.Has("scroll_wheel") == true)
            {
                if (store is byte scrollSlot && SlotCanChange(SlotSettings.Scroll))
                    ApplyChanged(ref _sentScroll, ScrollToggles(scroll), () =>
                    {
                        Send(DeviceCommands.ScrollAcceleration(transactionId, scroll.Acceleration, scrollSlot), "scroll acceleration");
                        Send(DeviceCommands.SmartReel(transactionId, scroll.SmartReel, scrollSlot), "Smart-Reel");
                    });
                if (HasOnboardProfiles && scroll.WheelMode is { } mode && Enum.IsDefined(mode) && mode != _sentWheelMode &&
                    Send(DeviceCommands.SetScrollWheelMode(transactionId, mode), "scroll wheel mode") &&
                    Confirm(ReadWheelMode() == mode, "scroll wheel mode"))
                    _sentWheelMode = mode;
                if (scroll.WheelMode is null)
                    _sentWheelMode = null;
            }
        }
    }

    static PerformanceSettings DpiOnly(PerformanceSettings performance) => performance with { PollingRate = 0 };
    static ScrollSettings ScrollToggles(ScrollSettings scroll) => scroll with { LinesPerNotch = 1, WheelMode = null };

    bool SlotCanChange(SlotSettings settings) => (_protectedSettings & settings) == 0;

    void UnprotectOnChange<T>(SlotSettings settings, T? previous, T? next) where T : class
    {
        if (!SlotCanChange(settings) &&
            JsonSerializer.Serialize(previous, ControlJson.Options) != JsonSerializer.Serialize(next, ControlJson.Options))
            _protectedSettings &= ~settings;
    }

    void ApplyChanged<T>(ref string? sent, T value, Action apply)
    {
        var now = JsonSerializer.Serialize(value, ControlJson.Options);
        if (now == sent)
            return;
        int failures = _failedWrites;
        apply();
        sent = failures == _failedWrites ? now : null;
    }

    byte? ReadOnboardProfile()
    {
        var report = Read(Commands.GetActiveOnboardProfile);
        byte? slot = report is { DataSize: >= 1 } && report.Arguments[0] is >= 1 and <= 5 ? report.Arguments[0] : null;
        if (slot != _onboardProfile)
        {
            _software.Stop();
            _lighting = null;
            _sentLighting = _sentPerformance = _sentScroll = null;
            _onboardProfile = slot;
        }
        if (slot is byte activeSlot)
        {
            if (_lastKnownProfile is byte previousSlot && previousSlot != activeSlot)
            {
                _protectedSettings = SlotSettings.All;
                _dpiEditBaseline = null;
            }
            _lastKnownProfile = activeSlot;
        }
        if (slot == null)
            LastProblem ??= "The active onboard profile is unavailable.";
        return slot;
    }

    internal byte? ObserveOnboardProfile()
    {
        lock (_gate)
        {
            if (_disposed || !HasOnboardProfiles)
                return null;
            string? previousProblem = LastProblem;
            try
            {
                return ReadOnboardProfile();
            }
            finally
            {
                // An observation must not replace the outcome of a user's last apply.
                LastProblem = previousProblem;
            }
        }
    }

    void SendLighting(LightingSettings lighting, byte store)
    {
        string type = model?.Type ?? "keyboard";
        bool frameSent = false;
        // Stopped before anything goes out, so a last drawn frame cannot land on top of a firmware effect.
        _software.Stop();
        if (model is { } legacy && LegacyLightingCommands.SupportsProduct(legacy.ProductId))
        {
            SendLegacyLighting(legacy.ProductId, lighting);
            return;
        }
        if (HasOnboardProfiles && lighting.Zones.Values.Any(z => z.Effect.Kind == EffectKind.Custom))
        {
            SendMouseFrame(lighting, lighting.Off || _dimmed);
            return;
        }
        var drawn = lighting.Zones.Where(z => _software.Draws(z.Key, z.Value.Effect)).ToDictionary(z => z.Key, z => z.Value.Effect);
        foreach (var (zone, settings) in lighting.Zones)
        {
            byte led = DeviceCommands.LedId(zone, type);
            if (drawn.ContainsKey(zone))
            {
                // Drawn by the service (below); only its brightness goes to the device.
            }
            else if (settings.Effect.Kind == EffectKind.Custom)
            {
                // One frame covers the whole device: send it, then switch to it, once whatever zones ask for it.
                if (frameSent || lighting.Frame == null)
                    continue;
                foreach (var row in DeviceCommands.CustomFrame(transactionId, lighting.Frame))
                    Send(row, "per-key colours");
                Send(DeviceCommands.CustomFrameEffect(transactionId), "per-key effect");
                frameSent = true;
            }
            else
            {
                SendLightingSetting(slot => DeviceCommands.Effect(transactionId, led, settings.Effect, slot), $"{zone} effect", store);
            }
            if (model == null || !model.Zones.TryGetValue(zone, out var known) || known.Brightness)
                SendLightingSetting(slot => DeviceCommands.Brightness(transactionId, led,
                    lighting.Off || (_dimmed && !HasOnboardProfiles) ? 0 : settings.Brightness, slot), $"{zone} brightness", store);
        }
        _software.Paused = lighting.Off || _dimmed;
        _software.Show(drawn, type);
    }

    void SendLegacyLighting(ushort productId, LightingSettings lighting)
    {
        if (HasLegacyCustomEffect(productId, lighting))
        {
            if (SendLegacyFrame(productId, lighting))
                foreach (var (zone, settings) in lighting.Zones)
                    if (LegacyLightingCommands.Led(productId, zone) is byte led)
                        SendLegacyBrightness(led, zone, settings.Brightness);
            return;
        }

        foreach (var (zone, settings) in lighting.Zones)
        {
            if (LegacyLightingCommands.Led(productId, zone) is not byte led)
                continue;
            if (LegacyLightingCommands.SupportsEffect(settings.Effect.Kind))
                Send(LegacyLightingCommands.Effect(productId, led, settings.Effect), $"{zone} effect");
            else
                Confirm(false, $"{zone} effect on this device");
            SendLegacyBrightness(led, zone, lighting.Off || _dimmed ? 0 : settings.Brightness);
        }
    }

    static bool HasLegacyCustomEffect(ushort productId, LightingSettings lighting) =>
        lighting.Zones.Any(zone => zone.Value.Effect.Kind == EffectKind.Custom &&
            LegacyLightingCommands.Led(productId, zone.Key).HasValue);

    bool SendLegacyFrame(ushort productId, LightingSettings lighting)
    {
        if (lighting.Frame is not { } desired || !LegacyLightingCommands.MatchesFrame(productId, desired))
            return Confirm(false, "custom lighting frame dimensions");

        if (lighting.Off || _dimmed)
            desired = CustomFrame.Filled(desired.Rows, desired.Columns, default);
        // OpenRazer uploads every row before it activates the volatile custom effect.
        foreach (var report in LegacyLightingCommands.FrameRows(productId, desired))
            if (!Send(report, "custom lighting frame row"))
                return false;
        return Send(LegacyLightingCommands.ActivateFrame(productId), "custom lighting effect");
    }

    void SendLegacyBrightness(byte led, string zone, int percent)
    {
        byte value = DeviceCommands.BrightnessValue(percent);
        if (Send(LegacyLightingCommands.Brightness(led, value), $"{zone} brightness"))
            Confirm(ReadReport(LegacyLightingCommands.ReadBrightness(led)) is { DataSize: 3 } reply &&
                reply.Arguments[2] == value, $"{zone} brightness");
    }

    void SendMouseFrame(LightingSettings lighting, bool dark)
    {
        if (lighting.Frame is not { Rows: 1, Columns: 7, Colors.Count: 7 } frame)
        {
            Confirm(false, "seven-pixel lighting frame");
            return;
        }
        if (dark)
            frame = CustomFrame.Filled(1, 7, default);
        if (Send(DeviceCommands.CustomFrameRow(transactionId, 0, 0, 6, frame.Colors), "lighting frame"))
            Send(DeviceCommands.CustomFrameEffect(transactionId), "lighting frame effect");
    }

    readonly SoftwareLighting _software = new(transport, transactionId, model);
    internal string? SoftwareLightingProblem => _software.Problem;
    internal event Action? SoftwareLightingProblemChanged
    {
        add => _software.ProblemChanged += value;
        remove => _software.ProblemChanged -= value;
    }

    /// <summary>A key or button went down (its source code): for the drawn Reactive and Ripple effects. Any thread.</summary>
    public void Press(ushort source) => _software.Press(source);

    bool _dimmed;
    LightingSettings? _lighting;

    /// <summary>The profile's switch-off rules, for the idle watcher.</summary>
    public LightingSettings? Lighting
    {
        get { lock (_gate) return _lighting; }
    }

    /// <summary>Darkens the device (the screen went off, the user went idle) or brings its brightness back, without
    /// touching the effects.</summary>
    public void SetDimmed(bool dimmed)
    {
        lock (_gate)
        {
            if (_disposed || dimmed == _dimmed || _lighting is not { } lighting)
                return;
            _dimmed = dimmed;
            _software.Paused = lighting.Off || dimmed;
            if (model is { } legacy && LegacyLightingCommands.SupportsProduct(legacy.ProductId))
            {
                int failures = _failedWrites;
                if (HasLegacyCustomEffect(legacy.ProductId, lighting))
                    SendLegacyFrame(legacy.ProductId, lighting);
                else
                    foreach (var (zone, settings) in lighting.Zones)
                        if (LegacyLightingCommands.Led(legacy.ProductId, zone) is byte led)
                            SendLegacyBrightness(led, zone, lighting.Off || dimmed ? 0 : settings.Brightness);
                if (failures != _failedWrites)
                    _sentLighting = null;
                return;
            }
            if (HasOnboardProfiles && lighting.Zones.Values.Any(z => z.Effect.Kind == EffectKind.Custom))
            {
                SendMouseFrame(lighting, lighting.Off || dimmed);
                return;
            }
            string type = model?.Type ?? "keyboard";
            foreach (var (zone, settings) in lighting.Zones)
                if (model == null || !model.Zones.TryGetValue(zone, out var known) || known.Brightness)
                {
                    RazerReport Brightness(byte slot) => DeviceCommands.Brightness(transactionId,
                        DeviceCommands.LedId(zone, type), lighting.Off || dimmed ? 0 : settings.Brightness, slot);
                    if (HasOnboardProfiles)
                        Send(Brightness(DeviceCommands.NoStore), $"{zone} brightness");
                    else
                        SendLightingSetting(Brightness, $"{zone} brightness", DeviceCommands.VarStore);
                }
        }
    }

    /// <summary>The keymap the keypad is on now (0-based), read from it; -1 when it does not say.</summary>
    public int ReadKeymap()
    {
        lock (_gate)
        {
            try
            {
                if (ReadRaw(0x02, 0xB7) is { } answer && answer.Arguments[0] is >= 1 and <= 8)
                    return _sentKeymap = answer.Arguments[0] - 1;
            }
            catch (IOException) { }
            return -1;
        }
    }

    /// <summary>The keymap the device is on, as the keypad reports it moving (its keymap button).</summary>
    public void KeymapChangedOnDevice(int keymap)
    {
        lock (_gate)
            _sentKeymap = keymap;
    }

    void SendPerformance(PerformanceSettings p, byte store)
    {
        // No stage above what the sensor can do, whatever the settings file says.
        if (model?.DpiMax is int max && max > 0)
            p = p with { Stages = p.Stages.Select(s => new DpiStage(Math.Min(s.X, max), Math.Min(s.Y, max))).ToList() };
        if (p.Stages.Count > 0 && model?.Has("dpi") == true)
        {
            if (model?.Has("dpi_stages") == true)
            {
                if (_dpiEditBaseline is { } baseline)
                {
                    var merged = MergeDpiEdit(p, baseline, store);
                    if (merged == null)
                        return;
                    p = merged;
                }
                var request = DeviceCommands.DpiStages(transactionId, p, model.Has("dpi_stage_third"), store);
                if (Send(request, "sensitivity stages") && HasOnboardProfiles)
                {
                    int used = 3 + 7 * request.Arguments[2];
                    var readback = Read(Commands.GetDpiStages, store);
                    if (Confirm(readback != null && ValidStageTable(readback, store) && readback.DataSize >= used &&
                        readback.Arguments[..used].SequenceEqual(request.Arguments[..used]), "sensitivity stages"))
                        _dpiEditBaseline = null;
                }
            }
            if (!HasOnboardProfiles)
            {
                var active = p.Stages[Math.Clamp(p.ActiveStage, 0, p.Stages.Count - 1)];
                Send(DeviceCommands.Dpi(DpiTransactionId, active.X, active.Y), "sensitivity");
            }
        }
    }

    PerformanceSettings? MergeDpiEdit(PerformanceSettings requested, PerformanceSettings baseline, byte slot)
    {
        var report = Read(Commands.GetDpiStages, slot);
        if (report == null || !ValidStageTable(report, slot))
        {
            Confirm(false, "current sensitivity stages");
            return null;
        }
        var current = DeviceCommands.ReadStages(report, requested.PollingRate);
        var stages = current.Stages.ToArray();
        int active = requested.ActiveStage == baseline.ActiveStage ? current.ActiveStage : requested.ActiveStage;
        if (active < 0 || active >= stages.Length)
        {
            Confirm(false, "selected sensitivity stage");
            return null;
        }
        for (int i = 0; i < requested.Stages.Count; i++)
        {
            var before = baseline.Stages[i];
            var after = requested.Stages[i];
            if (before == after)
                continue;
            if (i >= stages.Length)
            {
                Confirm(false, "edited sensitivity stage");
                return null;
            }
            stages[i] = new DpiStage(after.X == before.X ? stages[i].X : after.X,
                after.Y == before.Y ? stages[i].Y : after.Y);
        }
        return current with { Stages = stages, ActiveStage = active };
    }

    void SendCalibration(CalibrationSettings c)
    {
        Send(DeviceCommands.CalibrationMode(transactionId, false), "calibration mode");
        if (c.LiftOffLevel >= 0)
        {
            Send(DeviceCommands.SmartTracking(transactionId, c.LiftOffLevel), "lift-off distance");
            return;
        }
        // Back to the sensor's own mode 0. The Naga answers this write with Failure yet applies it, so the result is
        // judged by reading the mode back, not by the answer.
        try
        {
            transport.Send(DeviceCommands.TrackingModeDefault(transactionId));
            if (ReadRaw(0x0B, 0x8B, 0x00, 0x04) is { } mode && mode.Arguments[2] != 0x00)
                Confirm(false, "default tracking mode");
        }
        catch (IOException e)
        {
            LastProblem = $"Could not reset the sensor: {e.Message}";
            _failedWrites++;
        }
    }

    void SendPower(PowerSettings p)
    {
        Send(DeviceCommands.IdleTime(transactionId, p.SleepSeconds), "sleep timer");
        if (model?.Has("low_battery_threshold") == true)
            Send(DeviceCommands.LowBatteryThreshold(transactionId, p.LowBatteryPercent), "low battery threshold");
    }

    /// <summary>Lighting to the device's storage, then to its live slot. The Tartarus V2 Pro keeps the two apart and shows
    /// the live one: an effect stored alone is saved but not shown while the per-key frame (sent live) is up, and the
    /// keys stay on the frame. Mice with one slot (the Naga, the Basilisk) answer both alike.</summary>
    void SendLightingSetting(Func<byte, RazerReport> build, string what, byte store)
    {
        Send(build(store), what);
        if (!HasOnboardProfiles)
            Send(build(DeviceCommands.NoStore), what);
    }

    bool Send(RazerReport request, string what)
    {
        try
        {
            var answer = transport.Send(request);
            if (Acknowledges(request, answer))
            {
                _acknowledgedWrites++;
                return true;
            }
            LastProblem = $"The device did not acknowledge {what} ({answer.Status}).";
        }
        catch (IOException e)
        {
            LastProblem = $"Could not send the {what}: {e.Message}";
        }
        _failedWrites++;
        return false;
    }

    bool Confirm(bool confirmed, string what)
    {
        if (confirmed)
            _readbackConfirmations++;
        else
        {
            LastProblem ??= $"The device did not confirm the requested {what}.";
            _failedWrites++;
        }
        return confirmed;
    }

    /// <summary>The device's sensitivity, polling rate and battery as it reports them now.</summary>
    public DeviceState ReadState()
    {
        lock (_gate)
        {
            IReadOnlyList<DpiStage>? stages = null;
            int active = 0, rate = 0;
            int? battery = null;
            bool? charging = null;
            int liftOff = -1, sleep = 0, lowBattery = 0;
            bool? acceleration = null, smartReel = null;
            byte? store = null;
            ScrollWheelMode? wheelMode = null;
            if (_disposed)
                return new DeviceState(null, 0, 0, null, null);
            try
            {
                store = HasOnboardProfiles ? ReadOnboardProfile() : DeviceCommands.VarStore;
                if (model?.Has("poll_rate") == true)
                {
                    if (model.PollRates.Any(r => r > 1000))
                    {
                        if (ReadReport(DeviceCommands.GetHyperPollingRate(transactionId)) is { DataSize: >= 2 } hyper)
                            rate = DeviceCommands.ReadHyperPollingRate(hyper);
                    }
                    else if (Read(Commands.GetPollingRate) is { } poll)
                    {
                        rate = DeviceCommands.ReadPollingRate(poll);
                    }
                }
                if (model?.Has("dpi_stages") == true && store is byte dpiSlot &&
                    (HasOnboardProfiles ? Read(Commands.GetDpiStages, dpiSlot) : Read(Commands.GetDpiStages)) is { } table)
                {
                    if (!HasOnboardProfiles || ValidStageTable(table, dpiSlot))
                    {
                        var read = DeviceCommands.ReadStages(table, rate);
                        (stages, active) = (read.Stages, read.ActiveStage);
                    }
                    else
                        LastProblem ??= "The device returned an invalid sensitivity table.";
                }
                else if (IsNagaChroma)
                {
                    if (ReadReport(NagaChromaSensitivity.ReadRequest()) is { } dpi && NagaChromaSensitivity.Decode(dpi) is { } current)
                        stages = [current];
                }
                else if (!HasOnboardProfiles && model?.Has("dpi") == true && Read(Commands.GetDpi) is { } dpi)
                {
                    var a = dpi.Arguments;
                    stages = [new DpiStage((a[1] << 8) | a[2], (a[3] << 8) | a[4])];
                }
                if (model?.Has("battery") == true && Read(Commands.GetBattery) is { DataSize: >= 2 } level)
                    battery = DeviceCommands.ReadBatteryPercent(level);
                if (model?.Has("charging") == true && Read(Commands.GetCharging) is { DataSize: >= 2 } state)
                    charging = state.Arguments[1] != 0;
                if (model?.Has("lift_off") == true && ReadRaw(0x0B, 0x8B, 0x00, 0x04) is { } tracking)
                    liftOff = DeviceCommands.ReadLiftOffLevel(tracking);
                if (model?.Has("idle_time") == true && Read(Commands.GetIdleTime) is { DataSize: >= 2 } idle)
                    sleep = (idle.Arguments[0] << 8) | idle.Arguments[1];
                if (model?.Has("low_battery_threshold") == true && Read(Commands.GetLowBatteryThreshold) is { DataSize: >= 1 } low)
                    lowBattery = (int)Math.Round(low.Arguments[0] / 2.55);
                if (model?.Has("scroll_wheel") == true && store is byte scrollSlot)
                {
                    if (Read(Commands.GetScrollAcceleration, scrollSlot) is { DataSize: >= 2 } accel)
                        acceleration = accel.Arguments[1] != 0;
                    if (Read(Commands.GetSmartReel, scrollSlot) is { DataSize: >= 2 } reel)
                        smartReel = reel.Arguments[1] != 0;
                }
                if (HasOnboardProfiles)
                    wheelMode = ReadWheelMode();
            }
            catch (IOException e)
            {
                LastProblem = e.Message;
            }
            return new DeviceState(stages, active, rate, battery, charging, liftOff, sleep, lowBattery,
                acceleration, smartReel, HasOnboardProfiles ? store : null, wheelMode);
        }
    }

    RazerReport? ReadRaw(byte commandClass, byte id, params byte[] arguments)
    {
        return ReadReport(RazerReport.Create(transactionId, commandClass, id, arguments));
    }

    RazerReport? Read(Commands.Command command, params byte[] arguments) =>
        ReadReport(Commands.Request(transactionId, command, arguments));

    ScrollWheelMode? ReadWheelMode()
    {
        var answer = Read(Commands.GetScrollWheelMode);
        return answer is { DataSize: >= 2 } && answer.Arguments[1] <= (byte)ScrollWheelMode.PrecisionTactile
            ? (ScrollWheelMode)answer.Arguments[1] : null;
    }

    RazerReport? ReadReport(RazerReport request)
    {
        try
        {
            var answer = transport.Send(request);
            return Acknowledges(request, answer) ? answer : null;
        }
        catch (IOException e)
        {
            LastProblem ??= e.Message;
            return null;
        }
    }

    static bool ValidStageTable(RazerReport table, byte slot)
    {
        var data = table.Arguments;
        int count = data[2];
        return data[0] == slot && count is >= 1 and <= PerformanceSettings.MaxStages &&
            data[1] >= 1 && data[1] <= count && table.DataSize >= 3 + count * 7;
    }

    bool Acknowledges(RazerReport request, RazerReport answer) => RazerProbe.Accepts(request, answer, HasOnboardProfiles);

    public void Dispose()
    {
        lock (_gate)
        {
            if (_disposed)
                return;
            _disposed = true;
            FinishBlackWidowMode();
            _software.Dispose();
            transport.Dispose();
        }
    }
}

static class ControlJson
{
    public static readonly JsonSerializerOptions Options = new() { TypeInfoResolver = ControlJsonContext.Default };
}

[JsonSerializable(typeof(LightingSettings))]
[JsonSerializable(typeof(PerformanceSettings))]
[JsonSerializable(typeof(ScrollSettings))]
[JsonSerializable(typeof(PowerSettings))]
[JsonSerializable(typeof(CalibrationSettings))]
internal sealed partial class ControlJsonContext : JsonSerializerContext;
