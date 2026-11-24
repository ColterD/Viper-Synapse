using System.Runtime.ExceptionServices;
using ViperSynapse.Core.Bindings;
using ViperSynapse.Core.Devices;
using ViperSynapse.Core.Input;

namespace ViperSynapse.Core.Engine;

/// <summary>
/// Turns one device's key presses into what its keymap says. The device is grabbed, so every event comes
/// through here: unbound keys and pointer motion pass straight to the sink. A key's binding is fixed at
/// the moment it goes down, so letting go of Hypershift first still releases what the key started.
/// Called from the device's input thread only; turbo and macros run as tasks of their own.
/// </summary>
public sealed class RemapEngine
{
    readonly SharedModifierSink _sink;
    readonly IEngineHost _host;
    readonly TimeProvider _time;
    readonly MacroRunner _macros;
    readonly Dictionary<ushort, Binding> _held = [];
    readonly Dictionary<ushort, SharedModifierSink.Owner> _owners = [];
    readonly Dictionary<ushort, HashSet<Playback>> _running = [];
    readonly Dictionary<Guid, Playback> _toggled = [];
    readonly HashSet<Task> _backgroundTasks = [];
    // Where each sequence macro is up to, per key.
    readonly Dictionary<(ushort, Guid), int> _sequence = [];
    int _hypershiftHeld;

    public RemapEngine(IInputSink sink, IEngineHost host, TimeProvider? time = null)
    {
        _sink = sink as SharedModifierSink ?? new SharedModifierSink(sink);
        _host = host;
        _time = time ?? TimeProvider.System;
        _macros = new MacroRunner(_sink, host, _time);
    }

    public Keymap Keymap { get; set; } = Keymap.Empty("Keymap 1");

    /// <summary>What a control that sends no key of its own (a thumbstick direction) does when unbound: the device's
    /// default for it, W for the stick's up, say. Null leaves it doing nothing.</summary>
    public Func<ushort, Binding?>? Defaults { get; set; }

    public Action<ushort>? NagaDpiStep { get; set; }
    public bool NativeNagaTilt { get; set; }
    public bool NagaTiltReportsHorizontalWheel { get; set; }
    public bool NagaHorizontalHighResOnly { get; set; }
    public bool EvdevNagaTiltLeft { get; set; }
    public bool EvdevNagaTiltRight { get; set; }
    public IReadOnlySet<ushort>? NagaTiltShortcutSources { get; set; }

    public bool HasEvdevNagaTilt(ushort source) => source == LegacyRemapControls.NagaTiltLeft ? EvdevNagaTiltLeft : EvdevNagaTiltRight;

    public bool HasNagaTiltAction(ushort source) => Keymap.Resolve(source, HypershiftActive) is not DefaultBinding ||
        NagaTiltShortcutSources?.Contains(source) == true;

    public void NagaTiltDetent(ushort source)
    {
        if (Keymap.Resolve(source, HypershiftActive) is DefaultBinding && _host.Shortcuts.Completed(source, _held.ContainsKey) == null)
        {
            Scroll(RelativeAxes.HWheel, (source == LegacyRemapControls.NagaTiltLeft ? -1 : 1) * Math.Max(1, ScrollLines));
            return;
        }
        Key(source, 1);
        Key(source, 0);
    }

    /// <summary>Any key press stops running macros (Synapse's "Macro and Keystroke Interruption: any key").</summary>
    public MacroInterruption Interruption { get; set; } = MacroInterruption.DedicatedKey;

    /// <summary>How far one notch of the wheel scrolls, in notches (the Scroll Wheel card's scroll speed). Applies to the
    /// wheel where it is not rebound.</summary>
    public int ScrollLines { get; set; } = 1;

    bool _hypershiftButton;

    /// <summary>Current state of a device's separate firmware Hypershift button, if available.</summary>
    public Func<bool>? PhysicalHypershiftHeld { get; set; }

    /// <summary>The keypad's own Hypershift button, reported by its firmware rather than as a key.</summary>
    public void HypershiftButton(bool held)
    {
        if (held == _hypershiftButton)
            return;
        _hypershiftButton = held;
        _hypershiftHeld = Math.Max(0, _hypershiftHeld + (held ? 1 : -1));
    }

    public bool HypershiftActive => _hypershiftHeld > 0 || PhysicalHypershiftHeld?.Invoke() == true;

    /// <summary>A key or button from the device: 1 down, 0 up, 2 auto-repeat.</summary>
    public void Key(ushort code, int value)
    {
        if (value == 2)
        {
            // Auto-repeat belongs to keys that pass through; a remapped key makes its own repeats.
            if (_held.TryGetValue(code, out var heldBinding) && heldBinding is DefaultBinding)
                Emit(code, 2);
            return;
        }
        if (value == 1)
        {
            if (_held.ContainsKey(code))
                return;
            if (Interruption == MacroInterruption.AnyKey)
                StopMacros();
            // A global shortcut this press completes beats the keymap; the keys held before it keep doing what
            // they started.
            var binding = _host.Shortcuts.Completed(code, _held.ContainsKey)?.Binding ?? Keymap.Resolve(code, HypershiftActive);
            if (binding is DefaultBinding && SourceKeys.IsStick(code))
                binding = Defaults?.Invoke(code) ?? new DisableBinding();
            if (binding is DefaultBinding && LegacyRemapControls.IsNagaTiltSource(code) &&
                (NativeNagaTilt || NagaTiltReportsHorizontalWheel && HasEvdevNagaTilt(code)))
                binding = NagaTiltReportsHorizontalWheel ? new DisableBinding() : new MouseBinding(
                    code == LegacyRemapControls.NagaTiltLeft ? MouseFunction.ScrollLeft : MouseFunction.ScrollRight);
            // The Naga's vendor DPI buttons have no default virtual input event.
            if (binding is DefaultBinding && LegacyRemapControls.IsNagaDpiSource(code))
            {
                NagaDpiStep?.Invoke(code);
                binding = new DisableBinding();
            }
            var owner = _sink.CreateOwner();
            _held[code] = binding;
            _owners[code] = owner;
            try { Press(code, binding, owner); }
            catch (Exception error)
            {
                _held.Remove(code);
                _owners.Remove(code);
                DisposeAfterFailure(owner, error);
                throw;
            }
        }
        else if (_held.Remove(code, out var binding))
        {
            var owner = _owners[code];
            _owners.Remove(code);
            try { Release(code, binding, owner); }
            catch (Exception error)
            {
                DisposeAfterFailure(owner, error);
                throw;
            }
            owner.Dispose();
        }
    }

    static void DisposeAfterFailure(SharedModifierSink.Owner owner, Exception failure)
    {
        try { owner.Dispose(); }
        catch (Exception cleanup) { failure.Data["ViperSynapse.CleanupFailure"] = cleanup; }
    }

    /// <summary>Wheel movement: each direction is a control of its own and can be rebound.</summary>
    public void Wheel(ushort axis, int value)
    {
        if (value == 0)
            return;
        bool horizontal = axis is RelativeAxes.HWheel or RelativeAxes.HWheelHiRes;
        bool hiRes = axis is RelativeAxes.WheelHiRes or RelativeAxes.HWheelHiRes;
        ushort source = horizontal
            ? value > 0 ? SourceKeys.WheelRight : SourceKeys.WheelLeft
            : value > 0 ? SourceKeys.WheelUp : SourceKeys.WheelDown;
        var binding = Keymap.Resolve(source, HypershiftActive);
        if (horizontal && (NativeNagaTilt || NagaTiltReportsHorizontalWheel))
        {
            ushort tilt = value > 0 ? LegacyRemapControls.NagaTiltRight : LegacyRemapControls.NagaTiltLeft;
            if (NativeNagaTilt || HasEvdevNagaTilt(tilt))
            {
                if (!NagaTiltReportsHorizontalWheel || Keymap.Resolve(tilt, HypershiftActive) is not DefaultBinding ||
                    NagaTiltShortcutSources?.Contains(tilt) == true)
                    return;
            }
            else if (Keymap.Resolve(tilt, HypershiftActive) is not DefaultBinding || _host.Shortcuts.Completed(tilt, _held.ContainsKey) != null)
            {
                if (!hiRes)
                    for (long i = 0; i < Math.Abs((long)value); i++)
                    {
                        Key(tilt, 1);
                        Key(tilt, 0);
                    }
                return;
            }
        }
        if (binding is DefaultBinding)
        {
            _sink.Relative(axis, value * Math.Max(1, ScrollLines));
            return;
        }
        // A bound wheel fires once per detent; the high-resolution twin of the same detent is dropped.
        if (hiRes)
            return;
        for (int i = 0; i < Math.Abs(value); i++)
        {
            var owner = _sink.CreateOwner();
            try
            {
                Press(source, binding, owner);
                Release(source, binding, owner);
            }
            catch (Exception error)
            {
                DisposeAfterFailure(owner, error);
                throw;
            }
            owner.Dispose();
        }
    }

    /// <summary>Pointer motion and anything else relative passes through.</summary>
    public void Relative(ushort axis, int value) => _sink.Relative(axis, value);

    public void Sync() => _sink.Sync();

    /// <summary>Stops every turbo and macro and lets go of every key the engine holds down: the device was
    /// unplugged, the profile changed or the engine is shutting down.</summary>
    public void Reset()
    {
        Exception? failure = null;
        foreach (var (code, binding) in _held.ToList())
        {
            var owner = _owners[code];
            try { Release(code, binding, owner); }
            catch (Exception error) { failure ??= error; }
            try { owner.Dispose(); }
            catch (Exception error) { failure ??= error; }
        }
        _held.Clear();
        _owners.Clear();
        List<Playback> cancelled;
        lock (_running)
        {
            cancelled = [.. _running.Values.SelectMany(runs => runs), .. _toggled.Values];
            _running.Clear();
            _toggled.Clear();
        }
        CancelRuns(cancelled);
        _hypershiftHeld = 0;
        _hypershiftButton = false;
        // Retry releases already queued by previous resets. Background actions can still be finishing,
        // so callers must confirm their completion separately before declaring output recovery.
        if (failure == null)
        {
            try { _sink.RetryPending(); }
            catch (Exception error) { failure = error; }
        }
        if (failure != null)
            ExceptionDispatchInfo.Capture(failure).Throw();
    }

    /// <summary>Confirms that this engine's background cleanup has finished and retries queued output.</summary>
    public bool TryConfirmOutputRecovery()
    {
        // Call under the same serialization as Key and Reset, so no new engine work can start between
        // the snapshot and the retry. Never wait here: cleanup can depend on code outside the engine.
        lock (_running)
            if (_backgroundTasks.Any(task => !task.IsCompleted))
                return false;
        _sink.RetryPending();
        return true;
    }

    /// <summary>Stops every running and toggled macro (any-key interruption). Held turbo keys keep going.</summary>
    void StopMacros()
    {
        List<Playback> cancelled = [];
        lock (_running)
        {
            foreach (var (key, runs) in _running.ToList())
            {
                if (_held.TryGetValue(key, out var binding) && binding is not (MacroBinding or TextBinding))
                    continue;
                cancelled.AddRange(runs);
                _running.Remove(key);
            }
            cancelled.AddRange(_toggled.Values);
            _toggled.Clear();
        }
        CancelRuns(cancelled);
    }

    void Press(ushort code, Binding binding, IInputSink output)
    {
        switch (binding)
        {
            case DefaultBinding:
                Emit(output, code, 1);
                break;
            case KeyboardBinding k when k.Turbo:
                StartTurbo(code, k.TurboRate, owner => Tap(k, owner));
                break;
            case KeyboardBinding k:
                foreach (var m in ModifierCodes(k.Modifiers))
                    output.Key(m, 1);
                Emit(output, k.Key, 1);
                break;
            case MouseBinding m when m.Turbo || IsWheel(m.Function):
                if (m.Turbo) StartTurbo(code, m.TurboRate, owner => Click(m.Function, owner));
                else Click(m.Function, output);
                break;
            case MouseBinding { Function: MouseFunction.DoubleClick }:
                Click(MouseFunction.DoubleClick, output);
                break;
            case MouseBinding m:
                Emit(output, ButtonOf(m.Function), 1);
                break;
            case MacroBinding macro:
                StartMacro(code, macro);
                break;
            case HypershiftBinding:
                _hypershiftHeld++;
                break;
            case SwitchProfileBinding p:
                _host.SwitchProfile(p.Direction, p.ProfileId);
                break;
            case SwitchKeymapBinding k:
                _host.SwitchKeymap(k.Direction, k.Keymap);
                break;
            case LaunchBinding launch:
                _host.Launch(launch.Command);
                break;
            case TextBinding text:
                Run(code, ct => _macros.PlayAsync(TextTyper.Steps(text.Text).ToList<MacroStep>(), ct));
                break;
            case GamepadBinding pad:
                _host.Gamepad(pad.Control, true);
                break;
        }
    }

    void Release(ushort code, Binding binding, IInputSink output)
    {
        switch (binding)
        {
            case DefaultBinding:
                Emit(output, code, 0);
                break;
            case KeyboardBinding { Turbo: true } or MouseBinding { Turbo: true }:
                Stop(code);
                break;
            case KeyboardBinding k:
                Emit(output, k.Key, 0);
                foreach (var m in ModifierCodes(k.Modifiers).Reverse())
                    output.Key(m, 0);
                if (k.Modifiers != Modifiers.None)
                    _sink.Sync();
                break;
            case MouseBinding m when !IsWheel(m.Function) && m.Function != MouseFunction.DoubleClick:
                Emit(output, ButtonOf(m.Function), 0);
                break;
            case MacroBinding { Playback: MacroPlayback.WhileHeld }:
                Stop(code);
                break;
            case HypershiftBinding:
                _hypershiftHeld = Math.Max(0, _hypershiftHeld - 1);
                break;
            case GamepadBinding pad:
                _host.Gamepad(pad.Control, false);
                break;
        }
    }

    void StartMacro(ushort code, MacroBinding binding)
    {
        var macro = _host.FindMacro(binding.MacroId);
        if (macro == null)
            return;
        if (macro.Type == MacroType.Sequence)
        {
            // Each press plays the next action, round and round; the playback mode does not apply.
            var actions = macro.Actions();
            if (actions.Count == 0)
                return;
            var slot = (code, macro.Id);
            int next = _sequence.TryGetValue(slot, out var at) ? at % actions.Count : 0;
            _sequence[slot] = next + 1;
            Run(code, ct => _macros.PlayAsync(actions[next], ct));
            return;
        }
        if (binding.Playback == MacroPlayback.Toggle)
        {
            ToggleMacro(binding, macro);
            return;
        }
        // Pressing the key of a macro that is still playing starts nothing new.
        lock (_running)
            if (_running.ContainsKey(code))
                return;
        Run(code, ct => _macros.PlayAsync(macro, binding.Playback, binding.Times, ct));
    }

    void ToggleMacro(MacroBinding binding, Macro macro)
    {
        var id = binding.MacroId;
        Playback? running;
        Playback playback;
        lock (_running)
        {
            if (_toggled.Remove(id, out running))
                playback = running;
            else
                _toggled.Add(id, playback = new Playback());
        }
        if (running != null)
        {
            running.Cancel();
            return;
        }

        Launch(RunPlaybackAsync(playback, ct => _macros.PlayAsync(macro, binding.Playback, binding.Times, ct), () =>
        {
            lock (_running)
                if (_toggled.GetValueOrDefault(id) == playback)
                    _toggled.Remove(id);
        }));
    }

    void StartTurbo(ushort code, int rate, Action<IInputSink> tap)
    {
        var period = TimeSpan.FromSeconds(1.0 / Math.Clamp(rate, 1, 100));
        Run(code, async ct =>
        {
            var owner = _sink.CreateOwner();
            try
            {
                while (!ct.IsCancellationRequested)
                {
                    tap(owner);
                    try { await Task.Delay(period, _time, ct).ConfigureAwait(false); }
                    catch (OperationCanceledException) { break; }
                }
            }
            catch (Exception error)
            {
                DisposeAfterFailure(owner, error);
                throw;
            }
            owner.Dispose();
        });
    }

    /// <summary>Tracks each task started by a key. Text and sequence actions may overlap across presses,
    /// and each remains cancellable until it completes.</summary>
    void Run(ushort code, Func<CancellationToken, Task> work)
    {
        var playback = new Playback();
        lock (_running)
        {
            if (!_running.TryGetValue(code, out var runs))
                _running.Add(code, runs = []);
            runs.Add(playback);
        }
        Launch(Task.Run(() => RunPlaybackAsync(playback, work, () =>
        {
            lock (_running)
                if (_running.TryGetValue(code, out var runs) && runs.Remove(playback) && runs.Count == 0)
                    _running.Remove(code);
        })));
    }

    static async Task RunPlaybackAsync(Playback playback, Func<CancellationToken, Task> work, Action unregister)
    {
        try { await work(playback.Token).ConfigureAwait(false); }
        finally
        {
            unregister();
            await playback.CompleteAsync().ConfigureAwait(false);
        }
    }

    void Stop(ushort code)
    {
        HashSet<Playback>? runs;
        lock (_running)
            _running.Remove(code, out runs);
        if (runs != null)
            CancelRuns(runs);
    }

    static void CancelRuns(IEnumerable<Playback> runs)
    {
        foreach (var playback in runs)
            playback.Cancel();
    }

    sealed class Playback
    {
        readonly CancellationTokenSource _source = new();
        readonly Lock _gate = new();
        Task? _cancellation;
        bool _finished;

        public CancellationToken Token => _source.Token;

        public void Cancel()
        {
            // CancelAsync sets the cancellation flag immediately. Callbacks can run off the input thread.
            lock (_gate)
                if (!_finished)
                    _cancellation ??= _source.CancelAsync();
        }

        public async ValueTask CompleteAsync()
        {
            Task? cancellation;
            lock (_gate)
            {
                _finished = true;
                cancellation = _cancellation;
            }
            try
            {
                if (cancellation != null)
                    await cancellation.ConfigureAwait(false);
            }
            finally { _source.Dispose(); }
        }
    }

    void Launch(Task task)
    {
        lock (_running)
            _backgroundTasks.Add(task);
        _ = task.ContinueWith(t =>
        {
            lock (_running)
                _backgroundTasks.Remove(t);
            if (t.IsFaulted)
                _host.Report(t.Exception!.GetBaseException());
        });
    }

    void Tap(KeyboardBinding k, IInputSink output)
    {
        foreach (var m in ModifierCodes(k.Modifiers))
            output.Key(m, 1);
        Emit(output, k.Key, 1);
        Emit(output, k.Key, 0);
        foreach (var m in ModifierCodes(k.Modifiers).Reverse())
            output.Key(m, 0);
        output.Sync();
    }

    void Click(MouseFunction function, IInputSink output)
    {
        switch (function)
        {
            case MouseFunction.ScrollUp: Scroll(RelativeAxes.Wheel, 1); break;
            case MouseFunction.ScrollDown: Scroll(RelativeAxes.Wheel, -1); break;
            case MouseFunction.ScrollRight: Scroll(RelativeAxes.HWheel, 1); break;
            case MouseFunction.ScrollLeft: Scroll(RelativeAxes.HWheel, -1); break;
            case MouseFunction.DoubleClick:
                for (int i = 0; i < 2; i++) { Emit(output, KeyCodes.BtnLeft, 1); Emit(output, KeyCodes.BtnLeft, 0); }
                break;
            default:
                Emit(output, ButtonOf(function), 1);
                Emit(output, ButtonOf(function), 0);
                break;
        }
    }

    void Scroll(ushort axis, int clicks)
    {
        _sink.Relative(axis, clicks);
        _sink.Relative(axis == RelativeAxes.Wheel ? RelativeAxes.WheelHiRes : RelativeAxes.HWheelHiRes, clicks * 120);
        _sink.Sync();
    }

    void Emit(ushort code, int value)
    {
        _sink.Key(code, value);
        _sink.Sync();
    }

    static void Emit(IInputSink output, ushort code, int value)
    {
        output.Key(code, value);
        output.Sync();
    }

    static bool IsWheel(MouseFunction f) => f is MouseFunction.ScrollUp or MouseFunction.ScrollDown or MouseFunction.ScrollLeft or MouseFunction.ScrollRight;

    static ushort ButtonOf(MouseFunction f) => f switch
    {
        MouseFunction.LeftClick => KeyCodes.BtnLeft,
        MouseFunction.RightClick => KeyCodes.BtnRight,
        MouseFunction.ScrollClick => KeyCodes.BtnMiddle,
        MouseFunction.Back => KeyCodes.BtnSide,
        MouseFunction.Forward => KeyCodes.BtnExtra,
        _ => KeyCodes.BtnLeft,
    };

    static IEnumerable<ushort> ModifierCodes(Modifiers m)
    {
        if (m.HasFlag(Modifiers.Ctrl)) yield return KeyCodes.LeftCtrl;
        if (m.HasFlag(Modifiers.Shift)) yield return KeyCodes.LeftShift;
        if (m.HasFlag(Modifiers.Alt)) yield return KeyCodes.LeftAlt;
        if (m.HasFlag(Modifiers.Super)) yield return KeyCodes.LeftMeta;
    }
}
