using System.Collections;
using System.Reflection;
using System.Text.Json;
using Microsoft.Extensions.Time.Testing;
using ViperSynapse.Core.Bindings;
using ViperSynapse.Core.Engine;
using ViperSynapse.Core.Input;
using ViperSynapse.Core.Lighting;
using ViperSynapse.Core.Protocol;
using ViperSynapse.Hardware;
using ViperSynapse.Hardware.Linux;
using ViperSynapse.Service;
using static ViperSynapse.Core.Devices.LegacyRemapControls;

namespace ViperSynapse.Tests;

public class NativeNagaIntegrationTests
{
    const string InputPath = "/dev/input/emulated-naga";
    const string MousePath = "/dev/hidraw-emulated-naga-mouse";
    const string VendorPath = "/dev/hidraw-emulated-naga-vendor";
    const string ProductKey = "0053";
    const ushort ThumbOne = 2;
    const ushort F1 = 59, F2 = 60, F3 = 61, F4 = 62;

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void Advertised_tilt_buttons_do_not_hide_wheel_only_presses(bool right)
    {
        using var device = new EmulatedNaga { HasHorizontalWheel = true, HasEvdevTiltKeys = true };
        device.Refresh();
        device.Bind((right ? NagaTiltRight : NagaTiltLeft, F1));
        device.Scroll(RelativeAxes.HWheel, right ? 1 : -1);
        Assert.Empty(device.Output.Keys);
        device.AdvanceTiltWindow();
        Assert.Equal<(ushort, int)>([(F1, 1), (F1, 0)], device.Output.Keys);
        Assert.Empty(device.Output.Wheel);
    }

    [Theory]
    [InlineData(1, true, 2)]
    [InlineData(1, false, 0)]
    [InlineData(0, false, 0)]
    [InlineData(2, true, 2)]
    public async Task First_press_after_failed_startup_steps_from_the_configured_index(int startup, bool up, int expected)
    {
        using var device = new EmulatedNaga();
        device.RefreshWithControl(MouseNode(), VendorNode());
        device.Firmware.RejectDpi = true;
        device.Configure(new PerformanceSettings([new(600, 600), new(1000, 1000), new(1400, 1400)], startup, 0) { ManagedDpiSteps = true });
        Assert.Null(device.Manager.ReadState(ProductKey)?.ManagedDpiStep);
        device.Firmware.RejectDpi = false;
        var updated = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        device.Manager.DevicesChanged += () => updated.TrySetResult();
        device.Readers[VendorPath].VendorReport(up ? (byte)0x20 : (byte)0x21);
        device.Readers[VendorPath].VendorReport();
        await updated.Task.WaitAsync(TimeSpan.FromSeconds(3), TestContext.Current.CancellationToken);
        Assert.Equal(expected, device.Manager.ReadState(ProductKey)?.ManagedDpiStep);
    }

    [Fact]
    public async Task A_firmware_change_after_confirmation_surfaces_on_the_next_read_without_a_write()
    {
        using var device = new EmulatedNaga();
        device.RefreshWithControl(MouseNode(), VendorNode());
        device.Configure(new PerformanceSettings([new(600, 650), new(1200, 1250)], 0, 0) { ManagedDpiSteps = true });
        var updated = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        device.Manager.DevicesChanged += () => updated.TrySetResult();
        device.Readers[VendorPath].VendorReport(0x20);
        device.Readers[VendorPath].VendorReport();
        await updated.Task.WaitAsync(TimeSpan.FromSeconds(3), TestContext.Current.CancellationToken);
        Assert.Null(device.Manager.ReadState(ProductKey)?.ManagedDpiProblem);
        device.Firmware.Current = new(3200, 3300);
        var state = device.Manager.ReadState(ProductKey)!;
        Assert.Equal(new DpiStage(3200, 3300), Assert.Single(state.Stages!));
        Assert.Equal(new DpiStage(1200, 1250), state.LastConfirmedManagedDpi);
        Assert.Contains("3200 × 3300", state.ManagedDpiProblem!);
        Assert.Equal(2, device.Firmware.Writes);
    }

    [Fact]
    public void Control_probe_retry_activates_the_saved_startup_step_and_reader_without_reconnect()
    {
        using var device = new EmulatedNaga { RejectControlOpen = true };
        device.RefreshWithControl(MouseNode(), VendorNode());
        device.Configure(new PerformanceSettings([new(600, 600), new(1200, 1200)], 1, 0) { ManagedDpiSteps = true });
        Assert.False(device.Info.ManagedDpiListening);
        Assert.Null(device.Manager.ReadState(ProductKey));
        Assert.Empty(device.Readers);
        device.RejectControlOpen = false;
        device.RetryControl(MouseNode(), VendorNode());
        Assert.True(device.Info.ManagedDpiListening);
        Assert.Contains(VendorPath, device.Readers.Keys);
        Assert.Equal(1, device.Manager.ReadState(ProductKey)?.ManagedDpiStep);
        Assert.Equal(new DpiStage(1200, 1200), device.Firmware.Current);
    }

    [Fact]
    public async Task Explicit_remaps_replace_managed_stepping_for_both_advertised_evdev_dpi_codes()
    {
        using var device = new EmulatedNaga { HasEvdevDpi = true };
        device.RefreshWithControl(MouseNode());
        device.Configure(new PerformanceSettings([new(600, 600), new(1200, 1200), new(2400, 2400)], 1, 0) { ManagedDpiSteps = true });
        device.Bind((NagaDpiUp, F1), (NagaDpiDown, F2));
        device.Send(NagaDpiUp, 1);
        device.Send(NagaDpiUp, 0);
        device.Send(NagaDpiDown, 1);
        device.Send(NagaDpiDown, 0);
        await device.WaitForDpiQueue();
        Assert.Equal<(ushort, int)>([(F1, 1), (F1, 0), (F2, 1), (F2, 0)], device.Output.Keys);
        Assert.Equal(1, device.Manager.ReadState(ProductKey)?.ManagedDpiStep);
        Assert.Equal(1, device.Firmware.Writes);
    }

    [Fact]
    public void A_bound_tilt_does_not_pass_through_as_horizontal_scroll()
    {
        using var device = new EmulatedNaga();
        device.Refresh(MouseNode(), VendorNode());
        device.Bind((NagaTiltLeft, F1));
        var mouse = device.Readers[MousePath];
        device.Scroll(RelativeAxes.HWheel, -1);
        mouse.MouseReport(0x20);
        device.Scroll(RelativeAxes.HWheelHiRes, -120);
        mouse.MouseReport(0);
        mouse.MouseReport(0x40);
        device.Scroll(RelativeAxes.HWheel, 1);
        device.Scroll(RelativeAxes.HWheelHiRes, 120);
        mouse.MouseReport(0);
        Assert.Equal<(ushort, int)>([(F1, 1), (F1, 0)], device.Output.Keys);
        Assert.Equal<(ushort, int)>([(RelativeAxes.HWheel, 1), (RelativeAxes.HWheelHiRes, 120)], device.Output.Wheel);
    }

    [Fact]
    public async Task Managed_steps_use_the_vendor_press_and_correct_a_late_factory_change()
    {
        using var device = new EmulatedNaga();
        device.RefreshWithControl(MouseNode(), VendorNode());
        var performance = JsonSerializer.Deserialize<PerformanceSettings>("""
            {"Stages":[{"X":400,"Y":450},{"X":900,"Y":950},{"X":1800,"Y":1850}],"ActiveStage":1,"ManagedDpiSteps":true}
            """)!;
        device.Configure(performance);
        Assert.Equal(new DpiStage(900, 950), device.Firmware.Current);
        var vendor = Assert.Contains(VendorPath, device.Readers);
        device.Firmware.ChangeBeforeNextRead = new(3200, 3200);
        var updated = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        device.Manager.DevicesChanged += () => updated.TrySetResult();
        vendor.VendorReport(0x20);
        vendor.VendorReport();
        await updated.Task.WaitAsync(TimeSpan.FromSeconds(3), TestContext.Current.CancellationToken);
        Assert.Equal(new DpiStage(1800, 1850), device.Firmware.Current);
        Assert.Equal(3, device.Firmware.Writes);
        Assert.Equal(2, device.Manager.ReadState(ProductKey)?.ManagedDpiStep);
    }

    [Fact]
    public async Task Rapid_dpi_presses_stop_at_the_boundary_and_an_explicit_binding_wins()
    {
        using var device = new EmulatedNaga();
        device.RefreshWithControl(MouseNode(), VendorNode());
        device.Configure(new PerformanceSettings([new(700, 700), new(1100, 1100), new(1700, 1700)], 0, 0) { ManagedDpiSteps = true });
        var vendor = device.Readers[VendorPath];
        var completed = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        int applied = 0;
        void Changed()
        {
            if (Interlocked.Increment(ref applied) == 3)
                completed.TrySetResult();
        }
        device.Manager.DevicesChanged += Changed;
        for (int i = 0; i < 3; i++)
        {
            vendor.VendorReport(0x20);
            vendor.VendorReport();
        }
        await completed.Task.WaitAsync(TimeSpan.FromSeconds(3), TestContext.Current.CancellationToken);
        device.Manager.DevicesChanged -= Changed;
        Assert.Equal(new DpiStage(1700, 1700), device.Firmware.Current);
        Assert.Equal(4, device.Firmware.Writes);

        device.Bind((NagaDpiDown, F4));
        vendor.VendorReport(0x21);
        vendor.VendorReport();
        await device.WaitForDpiQueue();
        Assert.Equal<(ushort, int)>([(F4, 1), (F4, 0)], device.Output.Keys);
        Assert.Equal(2, device.Manager.ReadState(ProductKey)?.ManagedDpiStep);
        Assert.Equal(4, device.Firmware.Writes);
    }

    [Fact]
    public async Task Advertised_evdev_dpi_steps_and_reconnect_restores_the_saved_step()
    {
        using var device = new EmulatedNaga { HasEvdevDpi = true };
        device.RefreshWithControl(MouseNode());
        device.Configure(new PerformanceSettings([new(650, 650), new(1250, 1250)], 1, 0) { ManagedDpiSteps = true });
        Assert.Empty(device.Readers);
        var updated = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        device.Manager.DevicesChanged += () => updated.TrySetResult();
        device.Send(NagaDpiDown, 1);
        device.Send(NagaDpiDown, 0);
        await updated.Task.WaitAsync(TimeSpan.FromSeconds(3), TestContext.Current.CancellationToken);
        Assert.Equal(0, device.Manager.ReadState(ProductKey)?.ManagedDpiStep);
        var revisionBeforeReconnect = device.Info.ManagedDpiRevision;
        Assert.True(revisionBeforeReconnect > 0);
        device.Disconnect();
        device.RefreshWithControl(MouseNode());
        Assert.Equal(1, device.Manager.ReadState(ProductKey)?.ManagedDpiStep);
        Assert.True(device.Info.ManagedDpiRevision > revisionBeforeReconnect);
        Assert.Equal(new DpiStage(1250, 1250), device.Firmware.Current);
        device.SwitchProfile(Profile.Create("Precision") with
        {
            Performance = new PerformanceSettings([new(500, 600), new(1000, 1200)], 0, 0) { ManagedDpiSteps = true },
        });
        Assert.Equal(0, device.Manager.ReadState(ProductKey)?.ManagedDpiStep);
        Assert.Equal(new DpiStage(500, 600), device.Firmware.Current);
    }

    [Fact]
    public void Wheel_detents_remap_without_a_native_or_evdev_button_route()
    {
        using var device = new EmulatedNaga { HasHorizontalWheel = true };
        device.Refresh();
        device.Bind((NagaTiltRight, F2));
        Assert.Empty(device.Readers);
        device.Send(NagaTiltRight, 1);
        device.Scroll(RelativeAxes.HWheelHiRes, 120);
        device.Scroll(RelativeAxes.HWheel, 1);
        device.Send(NagaTiltRight, 0);
        device.Scroll(RelativeAxes.HWheel, -1);
        Assert.Equal<(ushort, int)>([(F2, 1), (F2, 0)], device.Output.Keys);
        Assert.Equal<(ushort, int)>([(RelativeAxes.HWheel, -1)], device.Output.Wheel);
    }

    [Theory]
    [InlineData(false, false)]
    [InlineData(false, true)]
    [InlineData(true, false)]
    [InlineData(true, true)]
    public void Evdev_tilt_matches_mirrors_in_either_order_and_keeps_independent_detents(bool right, bool wheelFirst)
    {
        using var device = new EmulatedNaga { HasHorizontalWheel = true, HasEvdevTiltKeys = true };
        device.Refresh();
        ushort source = right ? NagaTiltRight : NagaTiltLeft;
        int sign = right ? 1 : -1;
        device.Bind((source, F1));
        if (wheelFirst) device.Scroll(RelativeAxes.HWheel, sign);
        device.Send(source, 1);
        device.Send(source, 0);
        if (!wheelFirst) device.Scroll(RelativeAxes.HWheel, sign);
        device.Scroll(RelativeAxes.HWheelHiRes, sign * 120);
        device.Scroll(RelativeAxes.HWheel, sign * 2);
        device.AdvanceTiltWindow();
        Assert.Equal(3, device.Output.Keys.Count(key => key == (F1, 1)));
        Assert.Equal(3, device.Output.Keys.Count(key => key == (F1, 0)));
        Assert.Empty(device.Output.Wheel);
    }

    [Fact]
    public void A_deferred_tilt_output_failure_is_reported_to_the_input_session()
    {
        using var device = new EmulatedNaga { HasHorizontalWheel = true, HasEvdevTiltKeys = true };
        device.Refresh();
        device.Bind((NagaTiltRight, F1));
        var failure = new InvalidOperationException("Emulated output failure.");
        (string Path, Exception Error)? observed = null;
        Assert.IsType<DeviceSession>(device.Session).NodeLost += (path, error) => observed = (path, error);
        device.Scroll(RelativeAxes.HWheel, 1);
        device.Output.NextKeyFailure = failure;
        device.AdvanceTiltWindow();
        Assert.Equal(InputPath, observed?.Path);
        Assert.Same(failure, observed?.Error);
    }

    [Fact]
    public void High_resolution_only_wheel_counts_a_detent_after_120_units()
    {
        using var device = new EmulatedNaga { HasHighResOnly = true };
        device.Refresh();
        device.Bind((NagaTiltLeft, F1), (NagaTiltRight, F2));
        device.Scroll(RelativeAxes.HWheelHiRes, -60);
        device.Scroll(RelativeAxes.HWheelHiRes, 119);
        Assert.Empty(device.Output.Keys);
        device.Scroll(RelativeAxes.HWheelHiRes, -60);
        device.Scroll(RelativeAxes.HWheelHiRes, 1);
        Assert.Equal<(ushort, int)>([(F1, 1), (F1, 0), (F2, 1), (F2, 0)], device.Output.Keys);
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public void Tilt_button_edges_stay_held_for_a_shortcut_even_with_horizontal_scroll_mirrors(bool native)
    {
        using var device = new EmulatedNaga { HasHorizontalWheel = true, HasEvdevTiltKeys = !native };
        device.Refresh(native ? [MouseNode(), VendorNode()] : []);
        device.Manager.SetShortcuts(new ShortcutList([GlobalShortcut.Create([NagaTiltRight, KeyCodes.BtnLeft], new KeyboardBinding(F2))]));
        device.Scroll(RelativeAxes.HWheel, 1);
        if (native)
            device.Readers[MousePath].MouseReport(0x40);
        else
            device.Send(NagaTiltRight, 1);
        device.Scroll(RelativeAxes.HWheelHiRes, 120);
        device.Scroll(RelativeAxes.HWheel, 1);
        device.AdvanceTiltWindow();
        device.Send(KeyCodes.BtnLeft, 1);
        device.Send(KeyCodes.BtnLeft, 0);
        if (native)
            device.Readers[MousePath].MouseReport(0);
        else
            device.Send(NagaTiltRight, 0);
        Assert.Equal<(ushort, int)>([(F2, 1), (F2, 0)], device.Output.Keys);
        Assert.Empty(device.Output.Wheel);
        Assert.False(device.Info.NagaTiltDetentsOnly);
        Assert.Equal(!native, device.Info.NagaTiltUsesEvdev);
    }

    [Fact]
    public void A_shortcut_unavailable_on_the_mouse_does_not_consume_unassigned_tilt()
    {
        using var device = new EmulatedNaga { HasHorizontalWheel = true };
        device.Refresh(MouseNode(), VendorNode());
        device.Bind((NagaTiltLeft, F1));
        device.Manager.SetShortcuts(new ShortcutList([GlobalShortcut.Create([NagaTiltRight, 16], new KeyboardBinding(F2))]));
        var mouse = device.Readers[MousePath];
        mouse.MouseReport(0x40);
        device.Scroll(RelativeAxes.HWheel, 1);
        mouse.MouseReport(0);
        Assert.Empty(device.Output.Keys);
        Assert.Equal<(ushort, int)>([(RelativeAxes.HWheel, 1)], device.Output.Wheel);
    }

    [Fact]
    public async Task Dpi_buttons_follow_the_new_profile_even_if_releasing_the_old_keymap_fails()
    {
        using var device = new EmulatedNaga();
        device.RefreshWithControl(MouseNode(), VendorNode());
        device.Configure(new PerformanceSettings([new(700, 700), new(1100, 1100)], 0, 0) { ManagedDpiSteps = true });
        device.Send(KeyCodes.BtnLeft, 1);
        device.Output.RejectNextRelease = true;
        device.SwitchProfile(Profile.Create("Other DPI profile") with
        {
            Performance = new PerformanceSettings([new(800, 800), new(1600, 1600)], 0, 0) { ManagedDpiSteps = true },
        });
        Assert.Contains("Could not update the device's keys", device.Info.InputIssue!);
        var updated = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        device.Manager.DevicesChanged += () => updated.TrySetResult();
        device.Readers[VendorPath].VendorReport(0x20);
        device.Readers[VendorPath].VendorReport();
        await updated.Task.WaitAsync(TimeSpan.FromSeconds(3), TestContext.Current.CancellationToken);
        Assert.Equal(new DpiStage(1600, 1600), device.Firmware.Current);
    }

    [Fact]
    public async Task An_unreadable_dpi_reply_retains_confirmation_without_another_write()
    {
        using var device = new EmulatedNaga();
        device.RefreshWithControl(MouseNode(), VendorNode());
        device.Configure(new PerformanceSettings([new(700, 700), new(1100, 1100)], 0, 0) { ManagedDpiSteps = true });
        var updated = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        device.Manager.DevicesChanged += () => updated.TrySetResult();
        device.Firmware.UnreadableNextDpi = true;
        device.Readers[VendorPath].VendorReport(0x20);
        device.Readers[VendorPath].VendorReport();
        await updated.Task.WaitAsync(TimeSpan.FromSeconds(3), TestContext.Current.CancellationToken);
        Assert.Equal(2, device.Firmware.Writes);
        var state = device.Manager.ReadState(ProductKey)!;
        Assert.Equal(0, state.ManagedDpiStep);
        Assert.Contains("readable DPI", state.ManagedDpiProblem!);

        device.Firmware.RejectDpi = true;
        device.Configure(new PerformanceSettings([new(500, 500), new(900, 900)], 0, 0) { ManagedDpiSteps = true });
        state = device.Manager.ReadState(ProductKey)!;
        Assert.Null(state.ManagedDpiStep);
        Assert.Equal(new DpiStage(700, 700), state.LastConfirmedManagedDpi);
        Assert.NotNull(state.ManagedDpiProblem);
    }

    [Fact]
    public void Failed_listener_is_reported_separately_from_confirmed_dpi()
    {
        using var device = new EmulatedNaga { RejectNativeReader = true };
        device.RefreshWithControl(MouseNode(), VendorNode());
        device.Configure(new PerformanceSettings([new(700, 700)], 0, 0) { ManagedDpiSteps = true });
        Assert.False(device.Info.ManagedDpiListening);
        Assert.Contains("Emulated input permission", device.Info.InputIssue!);
        Assert.Equal(0, device.Manager.ReadState(ProductKey)?.ManagedDpiStep);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Failed_vendor_reader_keeps_thumb_and_tilt_and_uses_evdev_dpi_until_refresh(bool failOnStart)
    {
        using var device = new EmulatedNaga { HasEvdevDpi = true, RejectVendorReader = true, FailVendorOnStart = failOnStart };
        device.RefreshWithControl(MouseNode(), VendorNode());
        var macro = Macro.Create("Thumb", [new KeyStep(F3, true), new KeyStep(F3, false)]);
        device.Manager.SetMacros(new MacroLibrary([macro]));
        device.BindMacro(ThumbOne, macro, bindTilt: true);
        var mouse = device.Readers[MousePath];
        var session = device.Session;
        device.Configure(new PerformanceSettings([new(700, 700), new(1300, 1300)], 0, 0) { ManagedDpiSteps = true });
        Assert.Same(session, device.Session);
        Assert.False(mouse.Disposed);
        Assert.True(device.Info.ManagedDpiListening);
        Assert.Contains(VendorPath, device.Info.InputIssue!);
        if (failOnStart)
            Assert.True(device.Readers[VendorPath].Disposed);
        else
            Assert.DoesNotContain(VendorPath, device.Readers.Keys);

        mouse.MouseReport(0x20);
        mouse.MouseReport(0);
        device.Send(ThumbOne, 1);
        device.Send(ThumbOne, 0);
        device.Send(NagaDpiUp, 1);
        device.Send(NagaDpiUp, 0);
        await device.WaitForDpiQueue();
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(3));
        while (!device.Output.Keys.Contains((F3, 0)))
            await Task.Delay(10, timeout.Token);
        Assert.Equal<(ushort, int)>([(F1, 1), (F1, 0), (F3, 1), (F3, 0)], device.Output.Keys);
        Assert.Equal(1, device.Manager.ReadState(ProductKey)?.ManagedDpiStep);

        device.RejectVendorReader = false;
        device.RetryControl(MouseNode(), VendorNode());
        Assert.Same(session, device.Session);
        Assert.Same(mouse, device.Readers[MousePath]);
        Assert.Null(device.Info.InputIssue);
        device.Readers[VendorPath].VendorReport(0x21);
        device.Readers[VendorPath].VendorReport();
        await device.WaitForDpiQueue();
        Assert.Equal(0, device.Manager.ReadState(ProductKey)?.ManagedDpiStep);
    }

    [Theory]
    [InlineData(false, false)]
    [InlineData(true, false)]
    [InlineData(false, true)]
    [InlineData(true, true)]
    public async Task Confirmed_dpi_clears_only_the_managed_dpi_health_error(bool rejectPollingRate, bool useButton)
    {
        using var device = new EmulatedNaga();
        device.RefreshWithControl(MouseNode(), VendorNode());
        var performance = new PerformanceSettings([new(700, 700), new(1300, 1300)], 0, 1000) { ManagedDpiSteps = true };
        var profile = Profile.Create("Managed steps") with { Performance = performance };
        device.Firmware.RejectPollingRate = rejectPollingRate;
        device.Firmware.RejectDpi = true;
        device.SwitchProfile(profile);
        Assert.Contains("managed DPI step", device.Info.Problem!);
        if (rejectPollingRate)
            Assert.Contains("polling rate", device.Info.Problem!);

        device.Firmware.RejectDpi = false;
        if (useButton)
        {
            device.Readers[VendorPath].VendorReport(0x20);
            device.Readers[VendorPath].VendorReport();
            await device.WaitForDpiQueue();
        }
        else
        {
            var selected = device.Info;
            device.Manager.SelectManagedDpiStep(ProductKey, selected.Port, ProductKey, selected.Serial ?? "",
                profile.Id, 1, performance.Stages[1]);
        }

        Assert.Equal(1, device.Manager.ReadState(ProductKey)?.ManagedDpiStep);
        Assert.Null(device.Manager.ReadState(ProductKey)?.ManagedDpiProblem);
        if (rejectPollingRate)
        {
            Assert.Contains("polling rate", device.Info.Problem!);
            Assert.DoesNotContain("managed DPI step", device.Info.Problem!);
        }
        else
            Assert.Null(device.Info.Problem);
    }

    [Fact]
    public async Task Thumb_macros_play_the_same_before_and_after_managed_steps_and_tilt()
    {
        using var device = new EmulatedNaga();
        device.RefreshWithControl(MouseNode(), VendorNode());
        var macro = Macro.Create("Thumb", [new KeyStep(F3, true), new KeyStep(F3, false)]);
        device.Manager.SetMacros(new MacroLibrary([macro]));
        device.BindMacro(ThumbOne, macro);
        await PlayThumbAsync(1);
        device.Configure(new PerformanceSettings([new(700, 750), new(1300, 1350)], 0, 0) { ManagedDpiSteps = true });
        device.BindMacro(ThumbOne, macro, bindTilt: true);
        device.Readers[MousePath].MouseReport(0x20);
        await PlayThumbAsync(2);
        device.Readers[MousePath].MouseReport(0);
        Assert.Equal<(ushort, int)>([(F3, 1), (F3, 0), (F3, 1), (F3, 0)], device.Output.Keys.Where(e => e.Code == F3));

        async Task PlayThumbAsync(int plays)
        {
            device.Send(KeyCodes.BtnLeft, 1);
            device.Send(ThumbOne, 1);
            device.Send(ThumbOne, 0);
            using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(3));
            while (device.Output.Keys.Count(e => e == (F3, 0)) < plays)
                await Task.Delay(10, timeout.Token);
            Assert.Equal(plays - 1, device.Output.Keys.Count(e => e == (KeyCodes.BtnLeft, 0)));
            device.Send(KeyCodes.BtnLeft, 0);
        }
    }

    [Fact]
    public void Native_reports_remap_all_four_buttons_without_disturbing_holds_or_forwarding_mirrors()
    {
        using var device = new EmulatedNaga();
        device.Refresh(MouseNode(), VendorNode());

        Assert.Equal<ushort>([ThumbOne, KeyCodes.BtnLeft, NagaTiltRight, NagaTiltLeft, NagaDpiUp, NagaDpiDown],
            device.Info.InputControls!);
        Assert.Null(device.Info.ControlNode);
        Assert.False(device.Info.Remapping);
        Assert.Empty(device.Readers);

        device.Bind((NagaTiltLeft, F1), (NagaTiltRight, F2), (NagaDpiUp, F3), (NagaDpiDown, F4));
        var mouse = device.Readers[MousePath];
        var vendor = device.Readers[VendorPath];
        Assert.True(device.Info.Remapping);

        device.Send(ThumbOne, 1);
        device.Send(KeyCodes.BtnLeft, 1);
        mouse.MouseReport(0x21);
        device.Send(NagaTiltRight, 1); // The generic HID mouse may number the tilt aliases the other way around.
        vendor.VendorReport(0x22, 0x20);
        mouse.MouseReport(0x01);
        device.Send(NagaTiltRight, 0);
        Assert.Equal<(ushort, int)>([(ThumbOne, 1), (KeyCodes.BtnLeft, 1), (F1, 1), (F3, 1)], device.Output.Keys);

        vendor.VendorReport(0x20);
        vendor.VendorReport();
        vendor.VendorReport(0x23, 0x21);
        mouse.MouseReport(0x41);
        vendor.VendorReport();
        mouse.MouseReport(0x01);
        device.Send(KeyCodes.BtnLeft, 0);
        device.Send(ThumbOne, 0);

        Assert.Equal<(ushort, int)>([
            (ThumbOne, 1), (KeyCodes.BtnLeft, 1), (F1, 1), (F3, 1), (F1, 0), (F3, 0),
            (F4, 1), (F2, 1), (F4, 0), (F2, 0), (KeyCodes.BtnLeft, 0), (ThumbOne, 0),
        ], device.Output.Keys);

        device.Bind();
        Assert.False(device.Info.Remapping);
        Assert.True(mouse.Disposed);
        Assert.True(vendor.Disposed);
    }

    [Fact]
    public void Ambiguous_vendor_endpoint_leaves_tilt_available_without_advertising_dpi()
    {
        using var device = new EmulatedNaga();
        device.Refresh(MouseNode(), VendorNode(), VendorNode(VendorPath + "-duplicate"));

        Assert.Equal<ushort>([ThumbOne, KeyCodes.BtnLeft, NagaTiltRight, NagaTiltLeft], device.Info.InputControls!);
        device.Bind((NagaDpiUp, F3));
        Assert.False(device.Info.Remapping);
        Assert.Empty(device.Readers);

        device.Bind((NagaTiltLeft, F1), (NagaDpiUp, F3));
        var mouse = Assert.Single(device.Readers).Value;
        mouse.MouseReport(0x20);
        mouse.MouseReport(0x00);
        Assert.Equal<(ushort, int)>([(F1, 1), (F1, 0)], device.Output.Keys);
    }

    [Fact]
    public async Task Losing_the_vendor_reader_releases_its_output_and_retires_the_session()
    {
        using var device = new EmulatedNaga();
        device.Refresh(MouseNode(), VendorNode());
        device.Bind((NagaDpiUp, F3));
        var vendor = Assert.Single(device.Readers).Value;
        Assert.False(device.Session!.HasNativeNagaTilt);

        vendor.VendorReport(0x22, 0x21, 0x20);
        Assert.Equal<(ushort, int)>([(F3, 1)], device.Output.Keys);

        var retired = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        device.Manager.DevicesChanged += () => retired.TrySetResult();
        vendor.Lose();
        await retired.Task.WaitAsync(TimeSpan.FromSeconds(5), TestContext.Current.CancellationToken);

        Assert.Equal<(ushort, int)>([(F3, 1), (F3, 0)], device.Output.Keys);
        Assert.True(vendor.Disposed);
        Assert.False(device.Info.Remapping);
        Assert.Contains(VendorPath, device.Info.Problem!);
    }

    static HidrawNode MouseNode() => new(MousePath, 0x1532, NagaChroma, 0, "Naga Chroma", "usb-native-naga/input0");
    static HidrawNode VendorNode(string path = VendorPath) => new(path, 0x1532, NagaChroma, 1, "Naga Chroma", "usb-native-naga/input1");

    sealed class EmulatedNaga : IDisposable
    {
        readonly DirectoryInfo _root = Directory.CreateTempSubdirectory("viper-native-naga-");
        readonly SettingsStore _store;
        public DeviceManager Manager { get; }
        public DeviceInfo Info => Assert.Single(Manager.Devices);
        public Output Output { get; } = new();
        public DeviceSession? Session { get; private set; }
        public Dictionary<string, ReportReader> Readers { get; } = [];
        public DpiFirmware Firmware { get; } = new();
        public bool HasHorizontalWheel { get; init; }
        public bool HasHighResOnly { get; init; }
        public bool HasEvdevTiltKeys { get; init; }
        public bool HasEvdevDpi { get; init; }
        public bool RejectNativeReader { get; init; }
        public bool RejectVendorReader { get; set; }
        public bool FailVendorOnStart { get; init; }
        public bool RejectControlOpen { get; set; }
        readonly FakeTimeProvider _clock = new();
        public void AdvanceTiltWindow() => _clock.Advance(TimeSpan.FromMilliseconds(25));

        public EmulatedNaga()
        {
            _store = new SettingsStore(_root.FullName);
            Manager = new DeviceManager(_store, Output)
            {
                FindNativeNagaNode = node => NagaChromaButtons.FindReportNode(node, _root.FullName),
                OpenNativeNagaButtons = (node, onKey, onLost) =>
                {
                    bool rejectVendor = RejectVendorReader && node.Path == VendorPath;
                    if (RejectNativeReader || rejectVendor && !FailVendorOnStart)
                        throw new UnauthorizedAccessException("Emulated input permission failure.");
                    var reader = new ReportReader(node, onKey, onLost, failOnStart: rejectVendor);
                    Readers[node.Path] = reader;
                    return reader;
                },
                StartInputSession = (paths, engine, mode, _) =>
                {
                    Assert.Equal(InputPath, Assert.Single(paths));
                    return Session = new DeviceSession(engine, mode, _clock);
                },
            };
        }

        public void Refresh(params HidrawNode[] nodes) => Manager.Refresh(WithDescriptors(nodes),
            [new InputNode(InputPath, 0x1532, NagaChroma, "Naga Chroma", "usb-native-naga/input0")],
            _ => false, _ => throw new InvalidOperationException("Native Naga bindings require no feature transport."),
            paths => InputControlDiscovery.Read(paths, _ => new InputCapabilities(
                HasEvdevTiltKeys ? [ThumbOne, KeyCodes.BtnLeft, NagaTiltLeft, NagaTiltRight] : [ThumbOne, KeyCodes.BtnLeft],
                HasHighResOnly ? [RelativeAxes.HWheelHiRes] : HasHorizontalWheel ? [RelativeAxes.HWheel, RelativeAxes.HWheelHiRes] : []), NagaChroma));

        public void RefreshWithControl(params HidrawNode[] nodes) => RefreshControl(nodes, force: false);
        public void RetryControl(params HidrawNode[] nodes) => RefreshControl(nodes, force: true);

        void RefreshControl(HidrawNode[] nodes, bool force) => Manager.Refresh(WithDescriptors(nodes),
            [new InputNode(InputPath, 0x1532, NagaChroma, "Naga Chroma", "usb-native-naga/input0")],
            node => node.DevicePath == MousePath, _ => RejectControlOpen ? throw new UnauthorizedAccessException("Emulated control access failure.") :
                new RazerTransport(Firmware) { AnswerDelay = TimeSpan.Zero },
            paths => InputControlDiscovery.Read(paths, _ => new InputCapabilities(
                HasEvdevDpi ? [ThumbOne, KeyCodes.BtnLeft, NagaDpiUp, NagaDpiDown] : [ThumbOne, KeyCodes.BtnLeft], []), NagaChroma), force);

        public void Disconnect() => Manager.Refresh([], [], _ => false, _ => throw new InvalidOperationException(),
            _ => new DiscoveredControls([], "unavailable"));

        HidrawNode[] WithDescriptors(HidrawNode[] nodes)
        {
            foreach (var node in nodes)
            {
                var device = Directory.CreateDirectory(Path.Combine(_root.FullName, Path.GetFileName(node.DevicePath), "device"));
                var driver = Path.Combine(device.FullName, "driver");
                File.WriteAllBytes(Path.Combine(device.FullName, "report_descriptor"), Convert.FromHexString(
                    node.Interface == 0 ? "750195088102750895078106" : "85047508950F8100"));
                if (new DirectoryInfo(driver).LinkTarget == null)
                    Directory.CreateSymbolicLink(driver, "../../drivers/hid-generic");
            }
            return nodes;
        }

        public void BindMacro(ushort source, Macro macro, bool bindTilt = false)
        {
            var settings = _store.LoadDevice(ProductKey);
            var keymap = settings.ActiveKeymapOrFirst.With(source, new MacroBinding(macro.Id));
            if (bindTilt)
                keymap = keymap.With(NagaTiltLeft, new KeyboardBinding(F1));
            Manager.SetSettings(ProductKey, settings with { Profiles = [settings.Active with { Keymaps = [keymap] }] });
        }

        public void Configure(PerformanceSettings performance)
        {
            var current = _store.LoadDevice(ProductKey);
            Manager.SetSettings(ProductKey, current with { Profiles = [current.Active with { Performance = performance }] });
        }

        public void SwitchProfile(Profile profile)
        {
            var current = _store.LoadDevice(ProductKey);
            Manager.SetSettings(ProductKey, current with { Profiles = [.. current.Profiles, profile], ActiveProfile = profile.Id, ActiveKeymap = 0 });
        }

        public void Bind(params (ushort Source, ushort Target)[] bindings)
        {
            var settings = _store.LoadDevice(ProductKey);
            var keymap = Keymap.Empty(settings.ActiveKeymapOrFirst.Name);
            foreach (var (source, target) in bindings)
                keymap = keymap.With(source, new KeyboardBinding(target));
            _store.SaveDevice(ProductKey, settings with { Profiles = [settings.Active with { Keymaps = [keymap] }] });
            Manager.Apply(ProductKey);
        }

        public void Send(ushort source, int value) => Session!.Dispatch(InputPath,
            new InputEvent { Type = EventTypes.Key, Code = source, Value = value });

        public Task WaitForDpiQueue()
        {
            // The input dispatch enqueues synchronously. Wait for any work it scheduled before asserting no write.
            var devices = (IDictionary)typeof(DeviceManager).GetField("_devices", BindingFlags.Instance | BindingFlags.NonPublic)!
                .GetValue(Manager)!;
            var device = devices[Info.Port]!;
            var queue = (Task)device.GetType().GetProperty("DpiQueue")!.GetValue(device)!;
            return queue.WaitAsync(TimeSpan.FromSeconds(3), TestContext.Current.CancellationToken);
        }

        public void Scroll(ushort axis, int value) => Session!.Dispatch(InputPath,
            new InputEvent { Type = EventTypes.Rel, Code = axis, Value = value });

        public void Dispose()
        {
            try { Manager.Dispose(); }
            finally { _root.Delete(recursive: true); }
        }
    }

    sealed class ReportReader(NagaReportNode node, Action<ushort, int> onKey, Action<Exception> onLost, bool failOnStart) : IHidrawButtonReader
    {
        readonly NagaChromaButtons.Decoder _decoder = new(node, onKey);
        bool _started;
        public bool Disposed { get; private set; }

        public void Start()
        {
            if (failOnStart)
                throw new ThreadStateException("Emulated input reader startup failure.");
            _started = true;
        }
        public void Dispose() => Disposed = true;
        public void Lose() => onLost(new IOException("Emulated hidraw disconnect."));

        public void MouseReport(byte buttons) => Read([buttons, 0, 0, 0, 0, 0, 0, 0]);

        public void VendorReport(params byte[] usages)
        {
            var report = new byte[16];
            report[0] = 0x04;
            usages.CopyTo(report, 1);
            Read(report);
        }

        void Read(ReadOnlySpan<byte> report)
        {
            Assert.True(_started);
            Assert.False(Disposed);
            _decoder.Read(report);
        }
    }

    sealed class Output : IInputSink
    {
        readonly List<(ushort Code, int Value)> _keys = [];
        public bool RejectNextRelease { get; set; }
        public Exception? NextKeyFailure { get; set; }
        public (ushort Code, int Value)[] Keys { get { lock (_keys) return _keys.ToArray(); } }
        public void Key(ushort code, int value)
        {
            lock (_keys)
            {
                if (NextKeyFailure is { } failure)
                {
                    NextKeyFailure = null;
                    throw failure;
                }
                if (value == 0 && RejectNextRelease)
                {
                    RejectNextRelease = false;
                    throw new IOException("Emulated release failure.");
                }
                _keys.Add((code, value));
            }
        }
        public List<(ushort Axis, int Value)> Wheel { get; } = [];
        public void Relative(ushort axis, int value) => Wheel.Add((axis, value));
        public void Sync() { }
    }

    sealed class DpiFirmware : IHidFeatureDevice
    {
        RazerReport? _request;
        public string Path => MousePath;
        public DpiStage Current { get; set; } = new(800, 800);
        public DpiStage? ChangeBeforeNextRead { get; set; }
        public bool UnreadableNextDpi { get; set; }
        public bool RejectDpi { get; set; }
        public bool RejectPollingRate { get; set; }
        public int Writes { get; private set; }

        public void SendFeature(ReadOnlySpan<byte> report) => _request = RazerReport.Parse(report[1..]);

        public int ReceiveFeature(Span<byte> report)
        {
            var request = _request!;
            var data = request.Arguments[..request.DataSize].ToArray();
            var status = ReportStatus.Successful;
            if ((request.CommandClass, request.CommandId) == (0x04, 0x05))
            {
                if (RejectDpi)
                    status = ReportStatus.Failure;
                else
                    Current = new((data[1] << 8) | data[2], (data[3] << 8) | data[4]);
                Writes++;
            }
            else if ((request.CommandClass, request.CommandId) == (0x04, 0x85))
            {
                if (ChangeBeforeNextRead is { } factory)
                {
                    Current = factory;
                    ChangeBeforeNextRead = null;
                }
                data = [0, (byte)(Current.X >> 8), (byte)Current.X, (byte)(Current.Y >> 8), (byte)Current.Y, 0, 0];
                if (UnreadableNextDpi)
                {
                    data = [0];
                    UnreadableNextDpi = false;
                }
            }
            else if ((request.CommandClass, request.CommandId) == (0x00, 0x82))
                data = "NAGATEST"u8.ToArray();
            else if ((request.CommandClass, request.CommandId) == (0x00, 0x81))
                data = [1, 0];
            else if ((request.CommandClass, request.CommandId) == (0x00, 0x05) && RejectPollingRate)
                status = ReportStatus.Failure;
            RazerReport.Create(request.TransactionId, request.CommandClass, request.CommandId, data).Bytes.CopyTo(report[1..]);
            report[1] = (byte)status;
            return report.Length;
        }

        public void Dispose() { }
    }
}
