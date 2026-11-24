using Microsoft.Extensions.Time.Testing;
using ViperSynapse.Core.Bindings;
using ViperSynapse.Core.Engine;
using ViperSynapse.Core.Input;
using ViperSynapse.Hardware;
using ViperSynapse.Hardware.Linux;
using static ViperSynapse.Core.Devices.LegacyRemapControls;

namespace ViperSynapse.Tests;

public class EngineTests
{
    const ushort A = 30, B = 48, C = 46, Caps = 58, Tab = 15;

    sealed class Recorder : IInputSink, IEngineHost
    {
        readonly List<string> _events = [];
        public readonly Dictionary<Guid, Macro> Macros = [];
        public readonly List<string> Launched = [];
        public ShortcutList Shortcuts { get; set; } = ShortcutList.Empty;
        public Action<Exception>? OnReport { get; init; }
        public void Gamepad(GamepadControl control, bool down) { lock (_events) _events.Add($"pad:{control}:{(down ? 1 : 0)}"); }

        public void Key(ushort code, int value) { lock (_events) _events.Add($"{KeyCodes.Name(code)}:{value}"); }
        public void Relative(ushort axis, int value) { lock (_events) _events.Add($"REL{axis}:{value}"); }
        public void Sync() { }
        public void Launch(string command) { lock (_events) Launched.Add(command); }
        public void Open(string target) { lock (_events) Launched.Add("open " + target); }
        public void SwitchProfile(CycleDirection direction, Guid? profileId) { lock (_events) _events.Add($"profile:{direction}"); }
        public void SwitchKeymap(CycleDirection direction, int keymap) { lock (_events) _events.Add($"keymap:{direction}"); }
        public Macro? FindMacro(Guid id) => Macros.GetValueOrDefault(id);
        public void Report(Exception error)
        {
            if (OnReport is { } report)
                report(error);
            else
                throw error;
        }

        public string[] Events { get { lock (_events) return [.. _events]; } }
        public int Count(string e) => Events.Count(x => x == e);
        public void Clear() { lock (_events) _events.Clear(); }

        /// <summary>Background turbo and macro tasks continue on the thread pool after the fake clock moves.</summary>
        public void WaitFor(Func<bool> condition)
        {
            var deadline = DateTime.UtcNow + TimeSpan.FromSeconds(5);
            while (!condition())
            {
                if (DateTime.UtcNow > deadline)
                    throw new TimeoutException("Gave up; events: " + string.Join(' ', Events));
                Thread.Sleep(1);
            }
        }
    }

    sealed class FailOnShiftSink(Recorder rec) : IInputSink
    {
        public void Key(ushort code, int value)
        {
            if (code == KeyCodes.LeftShift && value == 1)
                throw new InvalidOperationException("simulated output failure");
            rec.Key(code, value);
        }
        public void Relative(ushort axis, int value) => rec.Relative(axis, value);
        public void Sync() => rec.Sync();
    }

    sealed class FailPrimaryAndCleanupSink(Recorder rec) : IInputSink
    {
        public bool FailPress { get; set; }
        public bool FailRelease { get; set; }
        public void Key(ushort code, int value)
        {
            if (code == C && value == 1 && FailPress)
                throw new IOException("primary press failure");
            if (code == C && value == 0 && FailRelease)
            {
                FailRelease = false;
                throw new IOException("primary release failure");
            }
            if (code == KeyCodes.LeftCtrl && value == 0)
                throw new IOException("cleanup failure");
            rec.Key(code, value);
        }
        public void Relative(ushort axis, int value) => rec.Relative(axis, value);
        public void Sync() => rec.Sync();
    }

    sealed class FailReleaseSink(Recorder rec) : IInputSink
    {
        public bool Fail { get; set; }
        public ushort FailCode { get; set; } = KeyCodes.LeftCtrl;
        public void Key(ushort code, int value)
        {
            if (Fail && code == FailCode && value == 0)
                throw new IOException("simulated modifier release failure");
            rec.Key(code, value);
        }
        public void Relative(ushort axis, int value) => rec.Relative(axis, value);
        public void Sync() => rec.Sync();
    }

    sealed class FailSyncSink(Recorder rec) : IInputSink
    {
        public bool Fail { get; set; }
        public readonly List<string> Timeline = [];
        public void Key(ushort code, int value)
        {
            Timeline.Add($"{KeyCodes.Name(code)}:{value}");
            rec.Key(code, value);
        }
        public void Relative(ushort axis, int value) => rec.Relative(axis, value);
        public void Sync()
        {
            if (Fail)
                throw new IOException("simulated sync failure");
            Timeline.Add("SYNC");
            rec.Sync();
        }
    }

    /// <summary>A fake clock that counts the timers made on it, so a test can wait until a delay is pending
    /// before moving time: moving it earlier would be lost.</summary>
    sealed class CountingTime : FakeTimeProvider
    {
        int _timers;
        public int Timers => Volatile.Read(ref _timers);

        public override ITimer CreateTimer(TimerCallback callback, object? state, TimeSpan dueTime, TimeSpan period)
        {
            // Counted once registered: moving time between the count and the registration would be lost.
            var timer = base.CreateTimer(callback, state, dueTime, period);
            Interlocked.Increment(ref _timers);
            return timer;
        }

        public void AdvanceWhenPending(int timers, TimeSpan by, Recorder rec)
        {
            rec.WaitFor(() => Timers >= timers);
            Advance(by);
        }
    }

    static (RemapEngine, Recorder, CountingTime) Make(Keymap keymap)
    {
        var rec = new Recorder();
        var time = new CountingTime();
        return (new RemapEngine(rec, rec, time) { Keymap = keymap }, rec, time);
    }

    [Fact]
    public void An_unbound_key_passes_through_with_its_repeats()
    {
        var (engine, rec, _) = Make(Keymap.Empty("k"));
        engine.Key(A, 1);
        engine.Key(A, 2);
        engine.Key(A, 0);
        Assert.Equal(["KEY_A:1", "KEY_A:2", "KEY_A:0"], rec.Events);
    }

    [Fact]
    public void A_keyboard_function_holds_its_modifiers_around_the_key()
    {
        var (engine, rec, _) = Make(Keymap.Empty("k").With(A, new KeyboardBinding(C, Modifiers.Ctrl | Modifiers.Shift)));
        engine.Key(A, 1);
        engine.Key(A, 2);
        engine.Key(A, 0);
        Assert.Equal(["KEY_LEFTCTRL:1", "KEY_LEFTSHIFT:1", "KEY_C:1", "KEY_C:0", "KEY_LEFTSHIFT:0", "KEY_LEFTCTRL:0"], rec.Events);
    }

    [Fact]
    public void A_failed_press_preserves_its_error_when_cleanup_also_fails()
    {
        var rec = new Recorder();
        var sink = new FailPrimaryAndCleanupSink(rec) { FailPress = true };
        var engine = new RemapEngine(sink, rec) { Keymap = Keymap.Empty("k").With(A, new KeyboardBinding(C, Modifiers.Ctrl)) };
        var error = Assert.Throws<IOException>(() => engine.Key(A, 1));
        Assert.Equal("primary press failure", error.Message);
        Assert.Equal("cleanup failure", Assert.IsType<IOException>(error.Data["ViperSynapse.CleanupFailure"]).Message);
    }

    [Fact]
    public void A_failed_release_preserves_its_error_when_cleanup_also_fails()
    {
        var rec = new Recorder();
        var sink = new FailPrimaryAndCleanupSink(rec);
        var engine = new RemapEngine(sink, rec) { Keymap = Keymap.Empty("k").With(A, new KeyboardBinding(C, Modifiers.Ctrl)) };
        engine.Key(A, 1);
        sink.FailRelease = true;
        var error = Assert.Throws<IOException>(() => engine.Key(A, 0));
        Assert.Equal("primary release failure", error.Message);
        Assert.Equal("cleanup failure", Assert.IsType<IOException>(error.Data["ViperSynapse.CleanupFailure"]).Message);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void A_shared_modifier_stays_down_until_the_last_binding_releases_it(bool releaseSecondFirst)
    {
        var keymap = Keymap.Empty("k")
            .With(A, new KeyboardBinding(C, Modifiers.Ctrl))
            .With(B, new KeyboardBinding(Tab, Modifiers.Ctrl));
        var (engine, rec, _) = Make(keymap);

        engine.Key(A, 1);
        engine.Key(B, 1);
        engine.Key(releaseSecondFirst ? B : A, 0);

        Assert.DoesNotContain("KEY_LEFTCTRL:0", rec.Events);
        engine.Key(releaseSecondFirst ? A : B, 0);
        Assert.Equal(1, rec.Count("KEY_LEFTCTRL:0"));
    }

    [Fact]
    public void A_modifier_used_as_both_key_and_chord_modifier_is_released()
    {
        var (engine, rec, _) = Make(Keymap.Empty("k").With(A, new KeyboardBinding(KeyCodes.LeftCtrl, Modifiers.Ctrl)));
        engine.Key(A, 1);
        engine.Key(A, 0);
        Assert.Equal(1, rec.Count("KEY_LEFTCTRL:0"));
    }

    [Fact]
    public void A_passed_through_modifier_stays_down_after_a_chord_releases_it()
    {
        var (engine, rec, _) = Make(Keymap.Empty("k").With(A, new KeyboardBinding(C, Modifiers.Ctrl)));
        engine.Key(KeyCodes.LeftCtrl, 1);
        engine.Key(A, 1);
        engine.Key(A, 0);
        Assert.DoesNotContain("KEY_LEFTCTRL:0", rec.Events);
        engine.Key(KeyCodes.LeftCtrl, 0);
        Assert.Equal(1, rec.Count("KEY_LEFTCTRL:0"));
    }

    [Fact]
    public void Reset_releases_a_shared_modifier_only_once()
    {
        var keymap = Keymap.Empty("k")
            .With(A, new KeyboardBinding(C, Modifiers.Ctrl))
            .With(B, new KeyboardBinding(Tab, Modifiers.Ctrl | Modifiers.Shift));
        var (engine, rec, _) = Make(keymap);
        engine.Key(A, 1);
        engine.Key(B, 1);
        engine.Reset();
        Assert.Equal(1, rec.Count("KEY_LEFTCTRL:0"));
        Assert.Equal(1, rec.Count("KEY_LEFTSHIFT:0"));
    }

    [Fact]
    public void A_new_chord_reasserts_a_modifier_after_a_macro_uses_it()
    {
        var macro = Macro.Create("ctrl", [new KeyStep(KeyCodes.LeftCtrl, true), new KeyStep(KeyCodes.LeftCtrl, false), new KeyStep(1, true), new KeyStep(1, false)]);
        var keymap = Keymap.Empty("k")
            .With(A, new KeyboardBinding(C, Modifiers.Ctrl))
            .With(B, new MacroBinding(macro.Id))
            .With(Tab, new KeyboardBinding(Caps, Modifiers.Ctrl));
        var (engine, rec, _) = Make(keymap);
        rec.Macros[macro.Id] = macro;
        engine.Key(A, 1);
        engine.Key(B, 1);
        rec.WaitFor(() => rec.Count("KEY_ESC:0") == 1);
        Assert.DoesNotContain("KEY_LEFTCTRL:0", rec.Events);
        engine.Key(Tab, 1);
        Assert.Equal(3, rec.Count("KEY_LEFTCTRL:1"));
        engine.Reset();
    }

    [Fact]
    public void A_macro_cannot_release_a_modifier_held_by_another_binding()
    {
        var macro = Macro.Create("tap ctrl", [new KeyStep(KeyCodes.LeftCtrl, true), new KeyStep(KeyCodes.LeftCtrl, false), new KeyStep(Tab, true), new KeyStep(Tab, false)]);
        var keymap = Keymap.Empty("k").With(A, new KeyboardBinding(C, Modifiers.Ctrl)).With(B, new MacroBinding(macro.Id));
        var (engine, rec, _) = Make(keymap);
        rec.Macros[macro.Id] = macro;
        engine.Key(A, 1);
        engine.Key(B, 1);
        rec.WaitFor(() => rec.Count("KEY_TAB:0") == 1);
        Assert.DoesNotContain("KEY_LEFTCTRL:0", rec.Events);
        engine.Key(A, 0);
        Assert.Equal(1, rec.Count("KEY_LEFTCTRL:0"));
    }

    [Fact]
    public void A_turbo_chord_cannot_release_a_modifier_held_by_another_binding()
    {
        var keymap = Keymap.Empty("k").With(A, new KeyboardBinding(C, Modifiers.Ctrl))
            .With(B, new KeyboardBinding(Tab, Modifiers.Ctrl, Turbo: true));
        var (engine, rec, _) = Make(keymap);
        engine.Key(A, 1);
        engine.Key(B, 1);
        rec.WaitFor(() => rec.Count("KEY_TAB:0") == 1);
        Assert.DoesNotContain("KEY_LEFTCTRL:0", rec.Events);
        engine.Key(B, 0);
        engine.Key(A, 0);
        Assert.Equal(1, rec.Count("KEY_LEFTCTRL:0"));
    }

    [Fact]
    public void Devices_sharing_an_output_keep_each_others_modifiers_held()
    {
        var rec = new Recorder();
        var output = new SharedModifierSink(rec);
        var first = new RemapEngine(output, rec) { Keymap = Keymap.Empty("first").With(A, new KeyboardBinding(C, Modifiers.Ctrl)) };
        var second = new RemapEngine(output, rec) { Keymap = Keymap.Empty("second").With(B, new KeyboardBinding(Tab, Modifiers.Ctrl)) };
        first.Key(A, 1);
        second.Key(B, 1);
        first.Reset();
        Assert.DoesNotContain("KEY_LEFTCTRL:0", rec.Events);
        second.Key(B, 0);
        Assert.Equal(1, rec.Count("KEY_LEFTCTRL:0"));
    }

    [Fact]
    public void A_failed_overlapping_press_does_not_release_another_devices_key()
    {
        var rec = new Recorder();
        var sink = new FailSyncSink(rec);
        var output = new SharedModifierSink(sink);
        var first = new RemapEngine(output, rec);
        var second = new RemapEngine(output, rec);
        first.Key(B, 1);
        sink.Fail = true;
        Assert.Throws<IOException>(() => second.Key(B, 1));
        sink.Fail = false;
        rec.Clear();
        first.Key(B, 2);
        Assert.DoesNotContain("KEY_B:0", rec.Events);
        first.Key(B, 0);
        Assert.Equal(["KEY_B:2", "KEY_B:0"], rec.Events);
    }

    [Fact]
    public void Two_devices_sharing_a_key_release_it_only_after_both_let_go()
    {
        var rec = new Recorder();
        var output = new SharedModifierSink(rec);
        var first = new RemapEngine(output, rec);
        var second = new RemapEngine(output, rec);
        first.Key(B, 1);
        second.Key(B, 1);
        first.Key(B, 0);
        Assert.DoesNotContain("KEY_B:0", rec.Events);
        second.Key(B, 0);
        Assert.Equal(1, rec.Count("KEY_B:0"));
    }

    [Fact]
    public void A_macro_cannot_release_a_key_held_by_another_device()
    {
        var rec = new Recorder();
        var output = new SharedModifierSink(rec);
        var macro = Macro.Create("overlap", [new KeyStep(B, false), new KeyStep(B, true), new KeyStep(B, false),
            new KeyStep(Tab, true), new KeyStep(Tab, false)]);
        rec.Macros[macro.Id] = macro;
        var first = new RemapEngine(output, rec);
        var second = new RemapEngine(output, rec) { Keymap = Keymap.Empty("second").With(A, new MacroBinding(macro.Id)) };
        first.Key(B, 1);
        second.Key(A, 1);
        rec.WaitFor(() => rec.Count("KEY_TAB:0") == 1);
        Assert.DoesNotContain("KEY_B:0", rec.Events);
        first.Key(B, 0);
        Assert.Equal(1, rec.Count("KEY_B:0"));
    }

    [Fact]
    public void Repeated_passed_through_modifiers_do_not_add_extra_holds()
    {
        var (engine, rec, _) = Make(Keymap.Empty("k"));
        engine.Key(KeyCodes.LeftCtrl, 1);
        engine.Key(KeyCodes.LeftCtrl, 1);
        engine.Key(KeyCodes.LeftCtrl, 2);
        engine.Key(KeyCodes.LeftCtrl, 0);
        engine.Key(KeyCodes.LeftCtrl, 0);
        Assert.Equal(["KEY_LEFTCTRL:1", "KEY_LEFTCTRL:2", "KEY_LEFTCTRL:0"], rec.Events);
    }

    [Fact]
    public void A_cancelled_macro_releases_only_its_own_modifier()
    {
        var macro = Macro.Create("hold", [new KeyStep(KeyCodes.LeftCtrl, true), new KeyStep(Tab, true), new DelayStep(10000)]);
        var keymap = Keymap.Empty("k").With(A, new KeyboardBinding(C, Modifiers.Ctrl))
            .With(B, new MacroBinding(macro.Id, MacroPlayback.WhileHeld));
        var (engine, rec, _) = Make(keymap);
        rec.Macros[macro.Id] = macro;
        engine.Key(B, 1);
        rec.WaitFor(() => rec.Count("KEY_TAB:1") == 1);
        engine.Key(A, 1);
        engine.Key(B, 0);
        rec.WaitFor(() => rec.Count("KEY_TAB:0") == 1);
        Assert.DoesNotContain("KEY_LEFTCTRL:0", rec.Events);
        engine.Key(A, 0);
        Assert.Equal(1, rec.Count("KEY_LEFTCTRL:0"));
    }

    [Fact]
    public void Unbalanced_macro_modifier_events_do_not_steal_another_hold()
    {
        var macro = Macro.Create("bad ctrl", [new KeyStep(KeyCodes.LeftCtrl, false), new KeyStep(KeyCodes.LeftCtrl, true),
            new KeyStep(KeyCodes.LeftCtrl, true), new KeyStep(KeyCodes.LeftCtrl, false), new KeyStep(KeyCodes.LeftCtrl, false),
            new KeyStep(Tab, true), new KeyStep(Tab, false)]);
        var keymap = Keymap.Empty("k").With(A, new KeyboardBinding(C, Modifiers.Ctrl)).With(B, new MacroBinding(macro.Id));
        var (engine, rec, _) = Make(keymap);
        rec.Macros[macro.Id] = macro;
        engine.Key(A, 1);
        engine.Key(B, 1);
        rec.WaitFor(() => rec.Count("KEY_TAB:0") == 1);
        Assert.DoesNotContain("KEY_LEFTCTRL:0", rec.Events);
        engine.Key(A, 0);
        Assert.Equal(1, rec.Count("KEY_LEFTCTRL:0"));
    }

    [Fact]
    public void A_nested_macro_cannot_release_its_callers_modifier()
    {
        var inner = Macro.Create("inner", [new KeyStep(KeyCodes.LeftCtrl, true), new KeyStep(KeyCodes.LeftCtrl, false)]);
        var outer = Macro.Create("outer", [new KeyStep(KeyCodes.LeftCtrl, true), new MacroCallStep(inner.Id),
            new KeyStep(Tab, true), new KeyStep(Tab, false), new KeyStep(KeyCodes.LeftCtrl, false)]);
        var (engine, rec, _) = Make(Keymap.Empty("k").With(A, new MacroBinding(outer.Id)));
        rec.Macros[inner.Id] = inner;
        rec.Macros[outer.Id] = outer;
        engine.Key(A, 1);
        rec.WaitFor(() => rec.Count("KEY_LEFTCTRL:0") == 1);
        var events = rec.Events;
        Assert.True(Array.IndexOf(events, "KEY_LEFTCTRL:0") > Array.IndexOf(events, "KEY_TAB:0"));
    }

    [Fact]
    public void A_failed_chord_press_releases_modifiers_already_acquired()
    {
        var rec = new Recorder();
        var engine = new RemapEngine(new FailOnShiftSink(rec), rec)
        {
            Keymap = Keymap.Empty("k").With(A, new KeyboardBinding(C, Modifiers.Ctrl | Modifiers.Shift)),
        };
        Assert.Throws<InvalidOperationException>(() => engine.Key(A, 1));
        Assert.Equal(["KEY_LEFTCTRL:1", "KEY_LEFTCTRL:0"], rec.Events);
        engine.Reset();
        Assert.Equal(1, rec.Count("KEY_LEFTCTRL:0"));
    }

    [Fact]
    public void Failed_modifier_release_does_not_leak_ownership_after_output_recovers()
    {
        var rec = new Recorder();
        var sink = new FailReleaseSink(rec);
        var engine = new RemapEngine(sink, rec) { Keymap = Keymap.Empty("k").With(A, new KeyboardBinding(C, Modifiers.Ctrl)) };
        engine.Key(A, 1);
        sink.Fail = true;
        Assert.Throws<IOException>(() => engine.Key(A, 0));
        sink.Fail = false;
        rec.Clear();
        engine.Key(A, 1);
        engine.Key(A, 0);
        Assert.Equal(["KEY_LEFTCTRL:0", "KEY_LEFTCTRL:1", "KEY_C:1", "KEY_C:0", "KEY_LEFTCTRL:0"], rec.Events);
    }

    [Fact]
    public void Failed_modifier_release_is_retried_before_a_later_unmodified_key()
    {
        var rec = new Recorder();
        var sink = new FailReleaseSink(rec);
        var engine = new RemapEngine(sink, rec) { Keymap = Keymap.Empty("k").With(A, new KeyboardBinding(C, Modifiers.Ctrl)) };
        engine.Key(A, 1);
        sink.Fail = true;
        Assert.Throws<IOException>(() => engine.Key(A, 0));
        sink.Fail = false;
        rec.Clear();
        engine.Key(B, 1);
        Assert.Equal(["KEY_LEFTCTRL:0", "KEY_B:1"], rec.Events);
        engine.Key(B, 0);
    }

    [Fact]
    public void Pending_modifier_release_blocks_later_input_until_it_succeeds()
    {
        var rec = new Recorder();
        var sink = new FailReleaseSink(rec);
        var engine = new RemapEngine(sink, rec) { Keymap = Keymap.Empty("k").With(A, new KeyboardBinding(C, Modifiers.Ctrl)) };
        engine.Key(A, 1);
        sink.Fail = true;
        Assert.Throws<IOException>(() => engine.Key(A, 0));
        Assert.Throws<IOException>(() => engine.Key(B, 1));
        Assert.DoesNotContain("KEY_B:1", rec.Events);
        sink.Fail = false;
        rec.Clear();
        engine.Key(B, 1);
        Assert.Equal(["KEY_LEFTCTRL:0", "KEY_B:1"], rec.Events);
        engine.Key(B, 0);
    }

    [Fact]
    public void A_key_released_while_another_release_is_pending_is_not_lost()
    {
        var rec = new Recorder();
        var sink = new FailReleaseSink(rec);
        var engine = new RemapEngine(sink, rec) { Keymap = Keymap.Empty("k").With(A, new KeyboardBinding(C, Modifiers.Ctrl)) };
        engine.Key(B, 1);
        engine.Key(A, 1);
        sink.Fail = true;
        Assert.Throws<IOException>(() => engine.Key(A, 0));
        Assert.Throws<IOException>(() => engine.Key(B, 0));
        sink.Fail = false;
        rec.Clear();
        engine.Key(Tab, 1);
        Assert.Equal(["KEY_LEFTCTRL:0", "KEY_B:0", "KEY_TAB:1"], rec.Events);
        engine.Key(Tab, 0);
    }

    [Fact]
    public async Task A_cancelled_macro_queues_every_held_key_when_output_is_blocked()
    {
        var rec = new Recorder();
        var sink = new FailReleaseSink(rec);
        var shared = new SharedModifierSink(sink);
        var runner = new MacroRunner(shared, rec, new CountingTime());
        using var cancel = new CancellationTokenSource();
        var play = runner.PlayAsync([new KeyStep(B, true), new KeyStep(C, true), new DelayStep(10000)], cancel.Token);
        rec.WaitFor(() => rec.Count("KEY_C:1") == 1);
        using var other = shared.CreateOwner();
        other.Key(KeyCodes.LeftCtrl, 1);
        sink.Fail = true;
        Assert.Throws<IOException>(() => other.Key(KeyCodes.LeftCtrl, 0));
        Assert.Throws<IOException>(() => other.Dispose());
        cancel.Cancel();
        await Assert.ThrowsAsync<IOException>(() => play);
        sink.Fail = false;
        rec.Clear();
        using var next = shared.CreateOwner();
        next.Key(Tab, 1);
        var events = rec.Events;
        Assert.Equal("KEY_LEFTCTRL:0", events[0]);
        Assert.Contains("KEY_B:0", events);
        Assert.Contains("KEY_C:0", events);
        Assert.True(Array.IndexOf(events, "KEY_B:0") < Array.IndexOf(events, "KEY_TAB:1"));
        Assert.True(Array.IndexOf(events, "KEY_C:0") < Array.IndexOf(events, "KEY_TAB:1"));
    }

    [Fact]
    public void A_failed_press_sync_releases_the_written_key_before_later_input()
    {
        var rec = new Recorder();
        var sink = new FailSyncSink(rec) { Fail = true };
        var engine = new RemapEngine(sink, rec);
        Assert.Throws<IOException>(() => engine.Key(B, 1));
        sink.Fail = false;
        rec.Clear();
        engine.Key(C, 1);
        Assert.Equal(["KEY_B:0", "KEY_C:1"], rec.Events);
        engine.Key(C, 0);
    }

    [Fact]
    public async Task A_failed_macro_press_sync_releases_the_written_key_before_later_input()
    {
        var rec = new Recorder();
        var sink = new FailSyncSink(rec) { Fail = true };
        var shared = new SharedModifierSink(sink);
        var runner = new MacroRunner(shared, rec, new CountingTime());
        await Assert.ThrowsAsync<IOException>(() => runner.PlayAsync([new KeyStep(B, true)], CancellationToken.None));
        sink.Fail = false;
        rec.Clear();
        using var next = shared.CreateOwner();
        next.Key(C, 1);
        Assert.Equal(["KEY_B:0", "KEY_C:1"], rec.Events);
    }

    [Fact]
    public void Failed_sync_does_not_leak_ownership_after_output_recovers()
    {
        var rec = new Recorder();
        var sink = new FailSyncSink(rec);
        var engine = new RemapEngine(sink, rec) { Keymap = Keymap.Empty("k").With(A, new KeyboardBinding(C, Modifiers.Ctrl)) };
        engine.Key(A, 1);
        sink.Fail = true;
        Assert.Throws<IOException>(() => engine.Key(A, 0));
        sink.Fail = false;
        rec.Clear();
        engine.Key(A, 1);
        engine.Key(A, 0);
        Assert.Equal(["KEY_LEFTCTRL:0", "KEY_LEFTCTRL:1", "KEY_C:1", "KEY_C:0", "KEY_LEFTCTRL:0"], rec.Events);
    }

    [Fact]
    public void Failed_sync_is_retried_before_a_later_input_event()
    {
        var rec = new Recorder();
        var sink = new FailSyncSink(rec);
        var engine = new RemapEngine(sink, rec) { Keymap = Keymap.Empty("k").With(A, new KeyboardBinding(C, Modifiers.Ctrl)) };
        engine.Key(A, 1);
        sink.Fail = true;
        Assert.Throws<IOException>(() => engine.Key(A, 0));
        sink.Fail = false;
        sink.Timeline.Clear();
        engine.Key(B, 1);
        Assert.Equal(["SYNC", "KEY_LEFTCTRL:0", "SYNC", "KEY_B:1", "SYNC"], sink.Timeline);
        engine.Key(B, 0);
    }

    [Fact]
    public void Failed_cleanup_does_not_release_or_leak_another_devices_modifier()
    {
        var rec = new Recorder();
        var sink = new FailReleaseSink(rec) { FailCode = KeyCodes.LeftShift };
        var shared = new SharedModifierSink(sink);
        var first = new RemapEngine(shared, rec) { Keymap = Keymap.Empty("first").With(A, new KeyboardBinding(C, Modifiers.Ctrl | Modifiers.Shift)) };
        var second = new RemapEngine(shared, rec) { Keymap = Keymap.Empty("second").With(B, new KeyboardBinding(Tab, Modifiers.Ctrl)) };
        first.Key(A, 1);
        second.Key(B, 1);
        sink.Fail = true;
        Assert.Throws<IOException>(() => first.Key(A, 0));
        Assert.DoesNotContain("KEY_LEFTCTRL:0", rec.Events);
        sink.Fail = false;
        second.Key(B, 0);
        Assert.Equal(1, rec.Count("KEY_LEFTCTRL:0"));
    }

    [Fact]
    public void Reset_queues_other_releases_when_one_release_fails()
    {
        var rec = new Recorder();
        var sink = new FailReleaseSink(rec);
        var keymap = Keymap.Empty("k").With(A, new KeyboardBinding(C, Modifiers.Ctrl))
            .With(B, new KeyboardBinding(Tab, Modifiers.Shift));
        var engine = new RemapEngine(sink, rec) { Keymap = keymap };
        engine.Key(A, 1);
        engine.Key(B, 1);
        sink.Fail = true;
        Assert.Throws<IOException>(() => engine.Reset());
        Assert.DoesNotContain("KEY_LEFTSHIFT:0", rec.Events);
        sink.Fail = false;
        rec.Clear();
        engine.Key(A, 1);
        engine.Key(A, 0);
        Assert.Equal(["KEY_LEFTCTRL:0", "KEY_TAB:0", "KEY_LEFTSHIFT:0", "KEY_LEFTCTRL:1", "KEY_C:1", "KEY_C:0", "KEY_LEFTCTRL:0"], rec.Events);
    }

    [Fact]
    public void A_disabled_key_sends_nothing()
    {
        var (engine, rec, _) = Make(Keymap.Empty("k").With(A, new DisableBinding()));
        engine.Key(A, 1);
        engine.Key(A, 0);
        Assert.Empty(rec.Events);
    }

    [Fact]
    public void Hypershift_switches_layers_and_a_key_releases_what_it_started()
    {
        var keymap = Keymap.Empty("k")
            .With(Caps, new HypershiftBinding())
            .With(A, new KeyboardBinding(B))
            .With(A, new KeyboardBinding(C), hypershift: true);
        var (engine, rec, _) = Make(keymap);

        engine.Key(A, 1); engine.Key(A, 0);
        engine.Key(Caps, 1);
        engine.Key(A, 1);
        engine.Key(Caps, 0);   // Hypershift let go before the key
        engine.Key(A, 0);
        engine.Key(Tab, 1); engine.Key(Tab, 0);

        Assert.Equal(["KEY_B:1", "KEY_B:0", "KEY_C:1", "KEY_C:0", "KEY_TAB:1", "KEY_TAB:0"], rec.Events);
    }

    [Fact]
    public void A_key_missing_from_hypershift_keeps_its_standard_binding()
    {
        var keymap = Keymap.Empty("k").With(Caps, new HypershiftBinding()).With(A, new KeyboardBinding(B));
        var (engine, rec, _) = Make(keymap);
        engine.Key(Caps, 1);
        engine.Key(A, 1); engine.Key(A, 0);
        Assert.Equal(["KEY_B:1", "KEY_B:0"], rec.Events);
    }

    [Theory]
    [InlineData(false, false)]
    [InlineData(false, true)]
    [InlineData(true, false)]
    [InlineData(true, true)]
    public void An_incomplete_tilt_shortcut_preserves_the_mirrored_horizontal_wheel(bool wheelFirst, bool bindWheel)
    {
        var keymap = bindWheel ? Keymap.Empty("k").With(SourceKeys.WheelRight, new KeyboardBinding(C)) : Keymap.Empty("k");
        var (engine, rec, time) = Make(keymap);
        engine.NagaTiltReportsHorizontalWheel = engine.EvdevNagaTiltRight = true;
        engine.NagaTiltShortcutSources = new HashSet<ushort> { NagaTiltRight };
        rec.Shortcuts = new ShortcutList([GlobalShortcut.Create([NagaTiltRight, A], new KeyboardBinding(B))]);
        using var session = new DeviceSession(engine, ThumbstickMode.Analog, time);

        if (wheelFirst)
        {
            SendTiltInput(session, EventTypes.Rel, RelativeAxes.HWheelHiRes, 120);
            SendTiltInput(session, EventTypes.Rel, RelativeAxes.HWheel, 1);
        }
        SendTiltInput(session, EventTypes.Key, NagaTiltRight, 1);
        SendTiltInput(session, EventTypes.Key, NagaTiltRight, 0);
        if (!wheelFirst)
        {
            SendTiltInput(session, EventTypes.Rel, RelativeAxes.HWheelHiRes, 120);
            SendTiltInput(session, EventTypes.Rel, RelativeAxes.HWheel, 1);
        }
        time.Advance(TimeSpan.FromMilliseconds(25));

        if (bindWheel)
            Assert.Equal(["KEY_C:1", "KEY_C:0"], rec.Events);
        else
        {
            Assert.Equal(2, rec.Events.Length);
            Assert.Contains("REL6:1", rec.Events);
            Assert.Contains("REL12:120", rec.Events);
        }
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void A_completed_tilt_shortcut_owns_its_hold_and_suppresses_the_mirrored_wheel(bool wheelFirst)
    {
        var (engine, rec, time) = Make(Keymap.Empty("k").With(A, new DisableBinding()));
        engine.NagaTiltReportsHorizontalWheel = engine.EvdevNagaTiltRight = true;
        engine.NagaTiltShortcutSources = new HashSet<ushort> { NagaTiltRight };
        rec.Shortcuts = new ShortcutList([GlobalShortcut.Create([NagaTiltRight, A], new KeyboardBinding(B))]);
        using var session = new DeviceSession(engine, ThumbstickMode.Analog, time);

        SendTiltInput(session, EventTypes.Key, A, 1);
        if (wheelFirst)
            SendTiltInput(session, EventTypes.Rel, RelativeAxes.HWheel, 1);
        SendTiltInput(session, EventTypes.Key, NagaTiltRight, 1);
        if (!wheelFirst)
            SendTiltInput(session, EventTypes.Rel, RelativeAxes.HWheel, 1);
        time.Advance(TimeSpan.FromMilliseconds(25));
        Assert.Equal(["KEY_B:1"], rec.Events);

        SendTiltInput(session, EventTypes.Key, NagaTiltRight, 0);
        SendTiltInput(session, EventTypes.Key, A, 0);
        Assert.Equal(["KEY_B:1", "KEY_B:0"], rec.Events);
    }

    [Theory]
    [InlineData(false, true, "KEY_B")]
    [InlineData(true, false, "KEY_C")]
    public void A_deferred_tilt_detent_uses_the_layer_active_when_it_arrives(bool shiftedAtArrival, bool shiftedAtExpiry, string key)
    {
        var keymap = Keymap.Empty("k").With(NagaTiltRight, new KeyboardBinding(B))
            .With(NagaTiltRight, new KeyboardBinding(C), hypershift: true);
        var (engine, rec, time) = Make(keymap);
        engine.NagaTiltReportsHorizontalWheel = engine.EvdevNagaTiltRight = true;
        using var session = new DeviceSession(engine, ThumbstickMode.Analog, time);

        engine.HypershiftButton(shiftedAtArrival);
        SendTiltInput(session, EventTypes.Rel, RelativeAxes.HWheel, 1);
        Assert.Empty(rec.Events);
        engine.HypershiftButton(shiftedAtExpiry);
        time.Advance(TimeSpan.FromMilliseconds(25));

        Assert.Equal([$"{key}:1", $"{key}:0"], rec.Events);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void A_horizontal_wheel_binding_counts_only_the_available_detent_stream(bool highResOnly)
    {
        var (engine, rec, time) = Make(Keymap.Empty("k").With(SourceKeys.WheelRight, new KeyboardBinding(B))
            .With(SourceKeys.WheelLeft, new KeyboardBinding(C)));
        engine.NagaTiltReportsHorizontalWheel = engine.EvdevNagaTiltRight = engine.EvdevNagaTiltLeft = true;
        engine.NagaHorizontalHighResOnly = highResOnly;
        using var session = new DeviceSession(engine, ThumbstickMode.Analog, time);

        SendTiltInput(session, EventTypes.Rel, RelativeAxes.HWheelHiRes, 119);
        SendTiltInput(session, EventTypes.Rel, RelativeAxes.HWheelHiRes, -60);
        Assert.Empty(rec.Events);
        SendTiltInput(session, EventTypes.Rel, RelativeAxes.HWheelHiRes, 1);
        SendTiltInput(session, EventTypes.Rel, RelativeAxes.HWheelHiRes, -60);
        if (highResOnly)
            Assert.Equal(["KEY_B:1", "KEY_B:0", "KEY_C:1", "KEY_C:0"], rec.Events);
        else
            Assert.Empty(rec.Events);
        SendTiltInput(session, EventTypes.Rel, RelativeAxes.HWheel, 1);
        SendTiltInput(session, EventTypes.Rel, RelativeAxes.HWheel, -1);
        Assert.Equal(["KEY_B:1", "KEY_B:0", "KEY_C:1", "KEY_C:0"], rec.Events);
    }

    static void SendTiltInput(DeviceSession session, ushort type, ushort code, int value) =>
        session.Dispatch("/dev/input/emulated-naga", new InputEvent { Type = type, Code = code, Value = value });

    [Fact]
    public void Turbo_repeats_at_its_rate_and_stops_on_release()
    {
        var (engine, rec, time) = Make(Keymap.Empty("k").With(A, new KeyboardBinding(B, Turbo: true, TurboRate: 10)));
        engine.Key(A, 1);
        rec.WaitFor(() => rec.Count("KEY_B:1") == 1);
        for (int i = 2; i <= 10; i++)
        {
            time.AdvanceWhenPending(i - 1, TimeSpan.FromMilliseconds(100), rec);
            int expected = i;
            rec.WaitFor(() => rec.Count("KEY_B:1") == expected);
        }
        engine.Key(A, 0);
        time.Advance(TimeSpan.FromSeconds(1));
        Thread.Sleep(50);
        Assert.Equal(10, rec.Count("KEY_B:1"));
        Assert.Equal(10, rec.Count("KEY_B:0"));
    }

    [Fact]
    public void A_macro_plays_its_steps_with_their_delays()
    {
        var macro = Macro.Create("m", [new KeyStep(B, true), new KeyStep(B, false), new DelayStep(500), new KeyStep(C, true), new KeyStep(C, false)]);
        var (engine, rec, time) = Make(Keymap.Empty("k").With(A, new MacroBinding(macro.Id)));
        rec.Macros[macro.Id] = macro;

        engine.Key(A, 1);
        engine.Key(A, 0);
        time.AdvanceWhenPending(1, TimeSpan.FromMilliseconds(499), rec);
        Thread.Sleep(30);
        Assert.Equal(0, rec.Count("KEY_C:1"));
        time.Advance(TimeSpan.FromMilliseconds(1));
        rec.WaitFor(() => rec.Count("KEY_C:0") == 1);
        Assert.Equal(["KEY_B:1", "KEY_B:0", "KEY_C:1", "KEY_C:0"], rec.Events);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void Retriggered_sequence_actions_are_all_cancelled(bool interruptWithKey)
    {
        // Sequence delays separate actions. The nested calls supply delays during playback.
        var first = Macro.Create("first", [
            new KeyStep(B, true), new DelayStep(100), new KeyStep(C, true), new KeyStep(C, false), new KeyStep(B, false),
        ]);
        var second = Macro.Create("second", [new DelayStep(100), new KeyStep(Tab, true), new KeyStep(Tab, false)]);
        var sequence = Macro.Create("sequence", [
            new MacroCallStep(first.Id), new DelayStep(1), new MacroCallStep(second.Id),
        ]) with { Type = MacroType.Sequence };
        var (engine, rec, time) = Make(Keymap.Empty("k").With(A, new MacroBinding(sequence.Id)).With(Caps, new DisableBinding()));
        foreach (var macro in new[] { first, second, sequence })
            rec.Macros[macro.Id] = macro;

        engine.Key(A, 1);
        engine.Key(A, 0);
        rec.WaitFor(() => time.Timers >= 1);
        engine.Key(A, 1);
        engine.Key(A, 0);
        rec.WaitFor(() => time.Timers >= 2);

        if (interruptWithKey)
        {
            engine.Interruption = MacroInterruption.AnyKey;
            engine.Key(Caps, 1);
            engine.Key(Caps, 0);
        }
        else
        {
            engine.Reset();
        }

        time.Advance(TimeSpan.FromSeconds(1));
        rec.WaitFor(engine.TryConfirmOutputRecovery);
        Assert.Equal(["KEY_B:1", "KEY_B:0"], rec.Events);
    }

    [Fact]
    public void A_while_held_macro_stops_on_release_and_lets_go_of_its_keys()
    {
        var macro = Macro.Create("hold", [new KeyStep(B, true), new DelayStep(100), new KeyStep(B, false), new DelayStep(100)]);
        var (engine, rec, time) = Make(Keymap.Empty("k").With(A, new MacroBinding(macro.Id, MacroPlayback.WhileHeld)));
        rec.Macros[macro.Id] = macro;

        engine.Key(A, 1);
        time.AdvanceWhenPending(1, TimeSpan.FromMilliseconds(100), rec);
        time.AdvanceWhenPending(2, TimeSpan.FromMilliseconds(100), rec);
        rec.WaitFor(() => rec.Count("KEY_B:1") == 2);   // second pass, B down again
        engine.Key(A, 0);
        rec.WaitFor(() => rec.Count("KEY_B:0") == 2);   // the stuck B is let go
        time.Advance(TimeSpan.FromSeconds(2));
        Thread.Sleep(50);
        Assert.Equal(2, rec.Count("KEY_B:1"));
    }

    [Fact]
    public void A_toggle_macro_starts_on_one_press_and_stops_on_the_next()
    {
        var macro = Macro.Create("toggle", [new KeyStep(B, true), new KeyStep(B, false), new DelayStep(100)]);
        var (engine, rec, time) = Make(Keymap.Empty("k").With(A, new MacroBinding(macro.Id, MacroPlayback.Toggle)));
        rec.Macros[macro.Id] = macro;

        engine.Key(A, 1); engine.Key(A, 0);
        time.AdvanceWhenPending(1, TimeSpan.FromMilliseconds(100), rec);
        rec.WaitFor(() => rec.Count("KEY_B:1") == 2);
        rec.WaitFor(() => time.Timers >= 2);
        engine.Key(A, 1); engine.Key(A, 0);
        Thread.Sleep(30);
        time.Advance(TimeSpan.FromSeconds(1));
        Thread.Sleep(50);
        Assert.Equal(2, rec.Count("KEY_B:1"));
    }

    [Fact]
    public void A_failed_toggle_restarts_on_the_next_press()
    {
        Exception? reported = null;
        var rec = new Recorder { OnReport = error => Volatile.Write(ref reported, error) };
        var time = new CountingTime();
        var sink = new FailPrimaryAndCleanupSink(rec) { FailPress = true };
        var macro = Macro.Create("toggle", [new KeyStep(C, true), new KeyStep(C, false), new DelayStep(100)]);
        rec.Macros[macro.Id] = macro;
        var engine = new RemapEngine(sink, rec, time)
        {
            Keymap = Keymap.Empty("k").With(A, new MacroBinding(macro.Id, MacroPlayback.Toggle))
        };

        engine.Key(A, 1); engine.Key(A, 0);
        rec.WaitFor(() => Volatile.Read(ref reported) != null && engine.TryConfirmOutputRecovery());
        Assert.IsType<IOException>(reported);
        sink.FailPress = false;

        engine.Key(A, 1); engine.Key(A, 0);
        try
        {
            Assert.Equal(["KEY_C:1", "KEY_C:0"], rec.Events);
        }
        finally
        {
            engine.Reset();
            rec.WaitFor(engine.TryConfirmOutputRecovery);
        }
    }

    [Fact]
    public void A_macro_played_a_set_number_of_times_ends_by_itself()
    {
        var macro = Macro.Create("three", [new KeyStep(B, true), new KeyStep(B, false)]);
        var (engine, rec, time) = Make(Keymap.Empty("k").With(A, new MacroBinding(macro.Id, MacroPlayback.Times, 3)));
        rec.Macros[macro.Id] = macro;
        engine.Key(A, 1); engine.Key(A, 0);
        time.AdvanceWhenPending(1, TimeSpan.FromMilliseconds(1), rec);
        time.AdvanceWhenPending(2, TimeSpan.FromMilliseconds(1), rec);
        rec.WaitFor(() => rec.Count("KEY_B:0") == 3);
        Thread.Sleep(30);
        time.Advance(TimeSpan.FromSeconds(1));
        Thread.Sleep(30);
        Assert.Equal(3, rec.Count("KEY_B:1"));
    }

    [Fact]
    public void A_bound_wheel_fires_once_per_detent()
    {
        var (engine, rec, _) = Make(Keymap.Empty("k").With(SourceKeys.WheelUp, new KeyboardBinding(Tab)));
        engine.Wheel(RelativeAxes.Wheel, 1);
        engine.Wheel(RelativeAxes.WheelHiRes, 120);
        engine.Wheel(RelativeAxes.Wheel, -1);
        engine.Wheel(RelativeAxes.WheelHiRes, -120);
        Assert.Equal(["KEY_TAB:1", "KEY_TAB:0", "REL8:-1", "REL11:-120"], rec.Events);
    }

    [Fact]
    public void Switches_and_launches_go_to_the_host()
    {
        var keymap = Keymap.Empty("k")
            .With(A, new SwitchProfileBinding(CycleDirection.Next))
            .With(B, new SwitchKeymapBinding(CycleDirection.Previous))
            .With(C, new LaunchBinding("kcalc"));
        var (engine, rec, _) = Make(keymap);
        foreach (var k in new[] { A, B, C }) { engine.Key(k, 1); engine.Key(k, 0); }
        Assert.Equal(["profile:Next", "keymap:Previous"], rec.Events);
        Assert.Equal(["kcalc"], rec.Launched);
    }

    [Fact]
    public void Reset_lets_go_of_every_held_key()
    {
        var (engine, rec, _) = Make(Keymap.Empty("k").With(A, new KeyboardBinding(C, Modifiers.Ctrl)));
        engine.Key(A, 1);
        engine.Key(B, 1);
        engine.Reset();
        Assert.Contains("KEY_C:0", rec.Events);
        Assert.Contains("KEY_LEFTCTRL:0", rec.Events);
        Assert.Contains("KEY_B:0", rec.Events);
    }

    [Fact]
    public void Text_is_typed_with_shift_where_a_us_keyboard_needs_it()
    {
        var steps = TextTyper.Steps("Hi!").Select(s => $"{KeyCodes.Name(s.Code)}:{(s.Down ? 1 : 0)}");
        Assert.Equal(["KEY_LEFTSHIFT:1", "KEY_H:1", "KEY_H:0", "KEY_LEFTSHIFT:0", "KEY_I:1", "KEY_I:0",
            "KEY_LEFTSHIFT:1", "KEY_1:1", "KEY_1:0", "KEY_LEFTSHIFT:0"], steps);
    }

    [Fact]
    public void Scroll_speed_multiplies_an_unbound_wheel_and_leaves_a_bound_one()
    {
        var (engine, rec, _) = Make(Keymap.Empty("k").With(SourceKeys.WheelDown, new KeyboardBinding(A)));
        engine.ScrollLines = 3;
        engine.Wheel(RelativeAxes.Wheel, 1);
        engine.Wheel(RelativeAxes.WheelHiRes, 120);
        engine.Wheel(RelativeAxes.Wheel, -1);
        // Up is unbound: three notches' worth, in both resolutions. Down is bound: one key press per notch.
        Assert.Equal(["REL8:3", "REL11:360", "KEY_A:1", "KEY_A:0"], rec.Events);
    }

    [Fact]
    public void A_global_shortcut_beats_the_keymap_and_the_held_key_keeps_its_own_binding()
    {
        var (engine, rec, _) = Make(Keymap.Empty("k").With(B, new KeyboardBinding(C)));
        rec.Shortcuts = new ShortcutList([GlobalShortcut.Create([Tab, B], new LaunchBinding("tool"))]);
        engine.Key(Tab, 1);
        engine.Key(B, 1);
        engine.Key(B, 0);
        engine.Key(Tab, 0);
        // Tab is unbound, so it passed through; B ran the shortcut instead of typing C.
        Assert.Equal(["KEY_TAB:1", "KEY_TAB:0"], rec.Events);
        Assert.Equal(["tool"], rec.Launched);

        // B alone is its keymap binding again.
        rec.Clear();
        engine.Key(B, 1);
        engine.Key(B, 0);
        Assert.Equal(["KEY_C:1", "KEY_C:0"], rec.Events);
        Assert.Single(rec.Launched);
    }

    [Fact]
    public void The_shortcut_with_the_most_keys_wins()
    {
        var (engine, rec, _) = Make(Keymap.Empty("k"));
        rec.Shortcuts = new ShortcutList([
            GlobalShortcut.Create([Tab, A], new LaunchBinding("two")),
            GlobalShortcut.Create([Caps, Tab, A], new LaunchBinding("three")),
        ]);
        engine.Key(Caps, 1);
        engine.Key(Tab, 1);
        engine.Key(A, 1);
        engine.Key(A, 0);
        Assert.Equal(["three"], rec.Launched);
    }
}

public class MacroFeatureTests
{
    sealed class Host : IInputSink, IEngineHost
    {
        public readonly List<string> Events = [];
        public readonly Dictionary<Guid, Macro> Macros = [];
        public void Key(ushort code, int value) { lock (Events) Events.Add($"{code}:{value}"); }
        public void Relative(ushort axis, int value) { }
        public void Sync() { }
        public void Launch(string command) { lock (Events) Events.Add("run " + command); }
        public void Open(string target) { lock (Events) Events.Add("open " + target); }
        public void SwitchProfile(CycleDirection direction, Guid? profileId) { }
        public void SwitchKeymap(CycleDirection direction, int keymap) { }
        public Macro? FindMacro(Guid id) => Macros.GetValueOrDefault(id);
        public void Gamepad(GamepadControl control, bool down) { }
        public ShortcutList Shortcuts => ShortcutList.Empty;
        public void Report(Exception error) => throw error;
    }

    static (MacroRunner, Host) Runner()
    {
        var host = new Host();
        return (new MacroRunner(host, host, new Microsoft.Extensions.Time.Testing.FakeTimeProvider()), host);
    }

    [Fact]
    public async Task Loops_repeat_their_steps_and_nest()
    {
        var (runner, host) = Runner();
        await runner.PlayAsync([new LoopStartStep(2), new KeyStep(1, true), new LoopStartStep(3), new KeyStep(2, true), new LoopEndStep(), new LoopEndStep(), new KeyStep(9, true)],
            TestContext.Current.CancellationToken);
        Assert.Equal(["1:1", "2:1", "2:1", "2:1", "1:1", "2:1", "2:1", "2:1", "9:1"], host.Events.Take(9));
        // Keys the macro left down are let go at the end, in no particular order.
        Assert.Equal(["1:0", "2:0", "9:0"], host.Events.Skip(9).Order());
    }

    [Fact]
    public async Task A_macro_plays_another_and_a_macro_calling_itself_stops()
    {
        var (runner, host) = Runner();
        var inner = Macro.Create("inner", [new KeyStep(5, true), new KeyStep(5, false)]);
        var self = Macro.Create("self", []);
        self = self with { Steps = [new KeyStep(7, true), new KeyStep(7, false), new MacroCallStep(self.Id)] };
        host.Macros[inner.Id] = inner;
        host.Macros[self.Id] = self;
        await runner.PlayAsync([new MacroCallStep(inner.Id), new LaunchStep("https://example.org")], TestContext.Current.CancellationToken);
        Assert.Equal(["5:1", "5:0", "open https://example.org"], host.Events);
        host.Events.Clear();
        await runner.PlayAsync(self.Steps, TestContext.Current.CancellationToken);
        Assert.Equal(2 * (MacroRunner.MaxNesting + 1), host.Events.Count);
    }

    [Fact]
    public void A_sequence_macro_splits_into_actions_at_its_delays_and_duration_counts_loops()
    {
        var macro = Macro.Create("seq", [new KeyStep(1, true), new KeyStep(1, false), new DelayStep(100), new KeyStep(2, true), new KeyStep(2, false),
            new RandomDelayStep(100, 300), new LoopStartStep(3), new DelayStep(10), new LoopEndStep()]);
        Assert.Equal(2, macro.Actions().Count);
        Assert.Equal(TimeSpan.FromMilliseconds(100 + 200 + 30), macro.Duration);
    }

    [Fact]
    public void MacroDuration_Fixed_delay_multiplication_does_not_overflow_int()
    {
        var macro = Macro.Create("fixed overflow", [new LoopStartStep(2), new DelayStep(1_500_000_000), new LoopEndStep()]);

        Assert.Equal(TimeSpan.FromMilliseconds(3_000_000_000L), macro.Duration);
    }

    [Fact]
    public void MacroDuration_Random_delay_endpoints_do_not_overflow_int_before_averaging()
    {
        var macro = Macro.Create("random overflow", [new RandomDelayStep(2_000_000_000, 2_100_000_000)]);

        Assert.Equal(TimeSpan.FromMilliseconds(2_050_000_000L), macro.Duration);
    }

    [Fact]
    public void MacroDuration_Nested_loop_multipliers_do_not_overflow_int()
    {
        var macro = Macro.Create("loop overflow", [new LoopStartStep(50_000), new LoopStartStep(50_000), new DelayStep(1),
            new LoopEndStep(), new LoopEndStep()]);

        Assert.Equal(TimeSpan.FromMilliseconds(2_500_000_000L), macro.Duration);
    }

    [Fact]
    public void MacroDuration_Zero_delays_stay_zero_in_deeply_nested_loops()
    {
        var macro = Macro.Create("zero delay", [
            .. Enumerable.Repeat<MacroStep>(new LoopStartStep(int.MaxValue), 36),
            new DelayStep(0), new RandomDelayStep(0, 0),
            .. Enumerable.Repeat<MacroStep>(new LoopEndStep(), 36),
        ]);

        Assert.Equal(TimeSpan.Zero, macro.Duration);
    }

    [Fact]
    public void Each_press_of_a_sequence_macro_plays_the_next_action()
    {
        var host = new Host();
        var macro = Macro.Create("seq", [new KeyStep(1, true), new KeyStep(1, false), new DelayStep(50), new KeyStep(2, true), new KeyStep(2, false)]) with { Type = MacroType.Sequence };
        host.Macros[macro.Id] = macro;
        var engine = new RemapEngine(host, host) { Keymap = Keymap.Empty("k").With(30, new MacroBinding(macro.Id)) };
        for (int i = 0; i < 3; i++)
        {
            engine.Key(30, 1);
            engine.Key(30, 0);
            var deadline = DateTime.UtcNow.AddSeconds(5);
            while (host.Events.Count < (i + 1) * 2 && DateTime.UtcNow < deadline)
                Thread.Sleep(2);
        }
        Assert.Equal(["1:1", "1:0", "2:1", "2:0", "1:1", "1:0"], host.Events);
    }

    [Fact]
    public void Randomized_recording_puts_a_random_delay_between_every_event()
    {
        var steps = MacroRecording.ToSteps([new(TimeSpan.Zero, 30, 1), new(TimeSpan.FromMilliseconds(80), 30, 0)], RecordDelayMode.Randomized, randomMinMs: 20, randomMaxMs: 60);
        Assert.Equal<MacroStep>([new KeyStep(30, true), new RandomDelayStep(20, 60), new KeyStep(30, false)], steps);
    }
}
