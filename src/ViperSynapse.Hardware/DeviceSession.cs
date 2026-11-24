using System.Runtime.ExceptionServices;
using ViperSynapse.Core.Bindings;
using ViperSynapse.Core.Devices;
using ViperSynapse.Core.Engine;
using ViperSynapse.Hardware.Linux;

namespace ViperSynapse.Hardware;

/// <summary>One input node of a physical device, as sysfs lists it.</summary>
public sealed record InputNode(string DevicePath, ushort VendorId, ushort ProductId, string Name, string Phys)
{
    public ushort Bus { get; init; } = HidrawNode.UsbBus;
    public string Unique { get; init; } = "";

    /// <summary>The USB port or Bluetooth peer address shared by this device's nodes.</summary>
    public string PortPath => HidrawNode.PhysicalPort(Bus, Phys, Unique);

    public static IReadOnlyList<InputNode> Enumerate(string sysRoot = "/sys/class/input")
    {
        var nodes = new List<InputNode>();
        if (!Directory.Exists(sysRoot))
            return nodes;
        foreach (var dir in Directory.EnumerateDirectories(sysRoot, "event*"))
        {
            string Read(string name)
            {
                try { return File.ReadAllText(Path.Combine(dir, "device", name)).Trim(); }
                catch (IOException) { return ""; }
                catch (UnauthorizedAccessException) { return ""; }
            }
            if (!ushort.TryParse(Read("id/vendor"), System.Globalization.NumberStyles.HexNumber, null, out var vendor)
                || !ushort.TryParse(Read("id/product"), System.Globalization.NumberStyles.HexNumber, null, out var product))
                continue;
            bool hasBus = ushort.TryParse(Read("id/bustype"), System.Globalization.NumberStyles.HexNumber, null, out var bus);
            nodes.Add(new InputNode("/dev/input/" + Path.GetFileName(dir), vendor, product, Read("name"), Read("phys"))
            {
                Bus = hasBus ? bus : HidrawNode.UsbBus,
                Unique = Read("uniq"),
            });
        }
        nodes.Sort((a, b) => string.CompareOrdinal(a.DevicePath, b.DevicePath));
        return nodes;
    }
}

/// <summary>
/// Takes over one physical device's keyboard and mouse nodes: grabs them, reads each on a thread of its
/// own and feeds the remap engine, whose output goes to the virtual device. Nodes that send neither keys
/// nor relative motion (the Tartarus's gamepad and analog nodes) are left alone. Disposing lets go of
/// everything; the kernel also drops the grabs if the process dies.
/// </summary>
public sealed class DeviceSession : IDisposable
{
    readonly List<EvdevDevice> _nodes = [];
    readonly List<Thread> _threads = [];
    readonly Lock _engineGate = new();
    static readonly TimeSpan TiltMirrorWindow = TimeSpan.FromMilliseconds(25);
    readonly TimeProvider _time;
    TiltRoute? _tiltLeft, _tiltRight;
    ITimer? _tiltTimer;
    string? _tiltWheelPath;

    sealed class TiltRoute(ushort source)
    {
        public ushort Source { get; } = source;
        public Queue<DetentBatch> Pending { get; } = [];
        public Queue<TiltButton> Buttons { get; } = [];
        public TiltButton? Held;
        public int Remainder;
    }

    sealed class TiltButton(long at)
    {
        public long At { get; } = at;
        public bool Released;
        public bool ConsumesWheel;
        public List<(long Count, bool Hypershift)> DeferredScroll { get; } = [];
    }

    sealed class DetentBatch(long at, long count, bool hypershift)
    {
        public long At { get; } = at;
        public long Count = count;
        public bool Hypershift { get; } = hypershift;
    }
    // Composite devices may advertise one Linux key code on several interfaces.
    readonly Dictionary<ushort, HashSet<string>> _keyNodes = [];
    IDisposable? _macroReader;
    IDisposable? _macroMode;
    sealed record NativeNagaReader(IHidrawButtonReader Reader, bool Tilt, bool Dpi);
    readonly Dictionary<string, NativeNagaReader> _nagaReaders = new(StringComparer.Ordinal);
    internal bool HasNativeNagaTilt { get; private set; }
    internal bool HasNativeNagaDpi { get; private set; }
    volatile bool _stopping;

    public RemapEngine Engine { get; }
    public IReadOnlyList<string> NodePaths => _nodes.Select(n => n.Path).ToList();
    internal bool HasNativeMacroKeys => _macroReader != null;

    internal void AttachNativeMacroKeys(IDisposable reader, IDisposable mode)
    {
        lock (_engineGate)
        {
            ReleaseNativeAliases(LegacyRemapControls.IsMacroSource);
            Engine.Sync();
            (_macroReader, _macroMode) = (reader, mode);
        }
    }

    internal void DetachNativeMacroKeys()
    {
        IDisposable? reader = null, mode = null;
        try
        {
            lock (_engineGate)
            {
                (reader, mode) = (_macroReader, _macroMode);
                (_macroReader, _macroMode) = (null, null);
                if (reader != null)
                {
                    ReleaseNativeAliases(LegacyRemapControls.IsMacroSource);
                    Engine.Sync();
                }
            }
        }
        finally
        {
            try { reader?.Dispose(); }
            finally { mode?.Dispose(); }
        }
    }

    internal void DispatchNativeMacroKey(string path, ushort source, int value)
    {
        lock (_engineGate)
        {
            if (_stopping || _macroReader == null || !LegacyRemapControls.IsMacroSource(source))
                return;
            DispatchKey(path, source, value);
            Engine.Sync();
        }
    }

    internal bool OwnsNativeNagaReader(string path, IHidrawButtonReader reader)
    {
        lock (_engineGate)
            return _nagaReaders.TryGetValue(path, out var current) && ReferenceEquals(current.Reader, reader);
    }

    internal void AttachNativeNagaReader(string path, IHidrawButtonReader reader, bool tilt, bool dpi)
    {
        lock (_engineGate)
        {
            if (tilt && !HasNativeNagaTilt)
                ReleaseNativeAliases(LegacyRemapControls.IsNagaTiltSource);
            if (dpi && !HasNativeNagaDpi)
                ReleaseNativeAliases(LegacyRemapControls.IsNagaDpiSource);
            Engine.Sync();
            _nagaReaders.Add(path, new(reader, tilt, dpi));
            UpdateNativeNagaRoutes();
        }
    }

    internal bool UpdateNativeNagaReader(string path, bool tilt, bool dpi)
    {
        IHidrawButtonReader? retired = null;
        try
        {
            lock (_engineGate)
            {
                if (!_nagaReaders.TryGetValue(path, out var current))
                    return false;
                if (current.Tilt == tilt && current.Dpi == dpi)
                    return true;
                if (tilt || dpi)
                    _nagaReaders[path] = current with { Tilt = tilt, Dpi = dpi };
                else
                {
                    _nagaReaders.Remove(path);
                    retired = current.Reader;
                }

                bool hadTilt = HasNativeNagaTilt, hadDpi = HasNativeNagaDpi;
                UpdateNativeNagaRoutes();
                if (tilt && !hadTilt)
                    ReleaseNativeAliases(LegacyRemapControls.IsNagaTiltSource);
                else if (current.Tilt && !tilt)
                    ReleaseNativeAliases(LegacyRemapControls.IsNagaTiltSource, path);
                if (dpi && !hadDpi)
                    ReleaseNativeAliases(LegacyRemapControls.IsNagaDpiSource);
                else if (current.Dpi && !dpi)
                    ReleaseNativeAliases(LegacyRemapControls.IsNagaDpiSource, path);
                Engine.Sync();
                return true;
            }
        }
        finally
        {
            retired?.Dispose();
        }
    }

    void UpdateNativeNagaRoutes()
    {
        bool tilt = _nagaReaders.Values.Any(reader => reader.Tilt);
        if (tilt != HasNativeNagaTilt)
            ClearTiltRoutes();
        HasNativeNagaTilt = Engine.NativeNagaTilt = tilt;
        HasNativeNagaDpi = _nagaReaders.Values.Any(reader => reader.Dpi);
    }

    void ReleaseNativeAliases(Func<ushort, bool> matches, string? path = null)
    {
        foreach (ushort source in _keyNodes.Keys.Where(matches).ToArray())
        {
            if (path != null)
                DispatchKey(path, source, 0);
            else
            {
                _keyNodes.Remove(source);
                Engine.Key(source, 0);
            }
        }
    }

    internal void DispatchNativeNagaKey(string path, IHidrawButtonReader reader, ushort source, int value)
    {
        lock (_engineGate)
        {
            if (_stopping || !_nagaReaders.TryGetValue(path, out var current) || !ReferenceEquals(current.Reader, reader))
                return;
            bool routesTilt = current.Tilt && LegacyRemapControls.IsNagaTiltSource(source);
            bool routesDpi = current.Dpi && LegacyRemapControls.IsNagaDpiSource(source);
            if (!routesTilt && !routesDpi)
                return;
            DispatchKey(path, source, value);
            Engine.Sync();
        }
    }

    readonly StickDirections _stick;
    readonly HashSet<string> _stickNodes = [];
    double _stickX, _stickY;

    /// <summary>The stick moved (4-way and 8-way mode): x and y from -1 to 1. Raised on the reader thread.</summary>
    public event Action<double, double>? StickMoved;

    /// <summary>A key or button of the device went down (its code, before any remapping). Raised on a reader thread, for
    /// the lighting effects that answer key presses.</summary>
    public event Action<ushort>? KeyPressed;

    /// <summary>Raised on a reader thread when a node goes away (unplugged) or fails.</summary>
    public event Action<string, Exception>? NodeLost;

    internal DeviceSession(RemapEngine engine, ThumbstickMode stick, TimeProvider? time = null)
    {
        Engine = engine;
        _time = time ?? TimeProvider.System;
        _stick = new StickDirections(stick);
    }

    /// <param name="nodePaths">The device's input nodes.</param>
    /// <param name="engine">Where its keys go.</param>
    /// <param name="stick">Analog leaves the keypad's gamepad node to the system; 4-way and 8-way take it over and
    /// turn the stick into direction presses.</param>
    /// <param name="onNodeLost">Optional failure handler registered before any reader thread starts.</param>
    public static DeviceSession Start(IEnumerable<string> nodePaths, RemapEngine engine, ThumbstickMode stick = ThumbstickMode.Analog,
        Action<DeviceSession, string, Exception>? onNodeLost = null)
    {
        var session = new DeviceSession(engine, stick);
        // Register before readers start: a node can fail before Start returns to the caller.
        if (onNodeLost != null)
            session.NodeLost += (path, error) => onNodeLost(session, path, error);
        try
        {
            foreach (var path in nodePaths)
            {
                var node = EvdevDevice.Open(path);
                // Own the descriptor immediately: capability queries and grabs can fail too.
                session._nodes.Add(node);
                var keys = node.Capabilities(EventTypes.Key, true);
                bool gamepad = keys.Any(Core.Input.KeyCodes.IsJoystickButton);
                if (gamepad && stick != ThumbstickMode.Analog && node.Capabilities(EventTypes.Abs, true).Count > 0)
                {
                    GrabIfIdle(node);
                    session._stickNodes.Add(node.Path);
                    continue;
                }
                // Otherwise gamepad and joystick nodes (the Tartarus V2 Pro's analog mode) carry axes the engine does
                // not pass on: grabbing one would freeze its sticks. They are left to the system.
                if (keys.Count == 0 && node.Capabilities(EventTypes.Rel, true).Count == 0 || gamepad)
                {
                    node.Dispose();
                    session._nodes.Remove(node);
                    continue;
                }
                GrabIfIdle(node);
            }
            foreach (var node in session._nodes)
            {
                var thread = new Thread(() => session.Pump(node)) { IsBackground = true, Name = "input " + node.Path };
                session._threads.Add(thread);
                thread.Start();
            }
            return session;
        }
        catch (Exception error)
        {
            try { session.Dispose(); }
            catch (Exception cleanup) { error.Data["DeviceSessionCleanupFailure"] = cleanup; }
            throw;
        }
    }

    /// <summary>Every node is taken (a node held down when the session started is taken once it is let go).</summary>
    public bool AllGrabbed => _nodes.All(n => n.IsGrabbed);

    /// <summary>Changes the keymap. Keys held across the change are let go first, so none stays down.</summary>
    public void Apply(Keymap keymap)
    {
        lock (_engineGate)
        {
            ClearTiltRoutes();
            Exception? failure = null;
            foreach (var (source, _) in _stick.Release())
            {
                try { Engine.Key(source, 0); }
                catch (Exception error) { failure ??= error; }
            }
            try { Engine.Reset(); }
            catch (Exception error) { failure ??= error; }
            _keyNodes.Clear();
            Engine.Keymap = keymap;
            if (failure != null)
                ExceptionDispatchInfo.Capture(failure).Throw();
        }
    }

    /// <summary>Runs something against the engine between input events.</summary>
    public void WithEngine(Action<RemapEngine> action)
    {
        lock (_engineGate)
            if (!_stopping)
                action(Engine);
    }

    /// <summary>Takes the node now unless one of its keys or buttons is down. Grabbing mid-press splits the press
    /// from its release: the desktop saw the press from the real device, the release would come from the virtual
    /// one, and the desktop would hold the button forever (a slider dragged by the click that turned this on stuck
    /// to the pointer). A held node is taken by its own thread once everything is let go.</summary>
    static void GrabIfIdle(EvdevDevice node)
    {
        if (!node.AnyKeyDown())
            node.Grab();
    }

    /// <summary>Waits for every key and button of the node to be let go, then grabs it. A press that slips in between
    /// the check and the grab is caught by checking again after: the grab is let go and the wait goes on. Whatever
    /// queued before the grab already reached the desktop, so it is thrown away rather than played twice.</summary>
    void GrabWhenIdle(EvdevDevice node, Span<InputEvent> events)
    {
        while (!_stopping && !node.IsGrabbed)
        {
            if (node.AnyKeyDown())
            {
                Thread.Sleep(10);
                continue;
            }
            node.Grab();
            if (node.AnyKeyDown())
            {
                node.Release();
                continue;
            }
            while (node.Read(events, TimeSpan.Zero) > 0)
            {
            }
        }
    }

    void Pump(EvdevDevice node)
    {
        var events = new InputEvent[64];
        try
        {
            GrabWhenIdle(node, events);
            while (!_stopping)
            {
                int n = node.Read(events, TimeSpan.FromMilliseconds(100));
                bool stick = _stickNodes.Contains(node.Path);
                lock (_engineGate)
                {
                    // A read can finish just as Dispose starts; do not emit after its final reset.
                    if (_stopping)
                        break;
                    for (int i = 0; i < n; i++)
                    {
                        if (stick)
                            DispatchStick(events[i]);
                        else
                            Dispatch(node.Path, events[i]);
                    }
                }
            }
        }
        catch (Exception e) when (e is IOException or ObjectDisposedException)
        {
            if (!_stopping)
                NodeLost?.Invoke(node.Path, e);
        }
    }

    /// <summary>The keypad's gamepad node in 4-way or 8-way mode: the stick (reported as the right stick, -32768 to
    /// 32767) becomes direction presses and its click a control of its own; everything else on the node is dropped.</summary>
    void DispatchStick(in InputEvent e)
    {
        const ushort AbsX = 0, AbsY = 1, AbsRx = 3, AbsRy = 4, BtnThumbL = 0x13D, BtnThumbR = 0x13E;
        switch (e.Type)
        {
            case EventTypes.Abs when e.Code is AbsRx or AbsX:
                _stickX = Math.Clamp(e.Value / 32767.0, -1, 1);
                break;
            case EventTypes.Abs when e.Code is AbsRy or AbsY:
                _stickY = Math.Clamp(e.Value / 32767.0, -1, 1);
                break;
            case EventTypes.Key when e.Code is BtnThumbL or BtnThumbR && e.Value != 2:
                Engine.Key(SourceKeys.StickClick, e.Value);
                Engine.Sync();
                break;
            case EventTypes.Syn when e.Code == EventTypes.SynReport:
                StickMoved?.Invoke(_stickX, _stickY);
                foreach (var (source, pressed) in _stick.Move(_stickX, _stickY))
                    Engine.Key(source, pressed ? 1 : 0);
                Engine.Sync();
                break;
        }
    }

    internal void Dispatch(string nodePath, in InputEvent e)
    {
        switch (e.Type)
        {
            case EventTypes.Key when (_macroReader == null || !LegacyRemapControls.IsMacroSource(e.Code)) &&
                (!LegacyRemapControls.IsNagaTiltSource(e.Code) || !HasNativeNagaTilt &&
                    (!Engine.NagaTiltReportsHorizontalWheel || Engine.HasEvdevNagaTilt(e.Code))) &&
                (!HasNativeNagaDpi || !LegacyRemapControls.IsNagaDpiSource(e.Code)):
                if (e.Value == 1 && Engine.NagaTiltReportsHorizontalWheel && LegacyRemapControls.IsNagaTiltSource(e.Code) &&
                    !_keyNodes.ContainsKey(e.Code))
                    ObserveTiltButton(e.Code);
                DispatchKey(nodePath, e.Code, e.Value);
                break;
            case EventTypes.Rel when e.Code is RelativeAxes.Wheel or RelativeAxes.HWheel or RelativeAxes.WheelHiRes or RelativeAxes.HWheelHiRes:
                if (!RouteTiltWheel(nodePath, e.Code, e.Value))
                    Engine.Wheel(e.Code, e.Value);
                break;
            case EventTypes.Rel:
                Engine.Relative(e.Code, e.Value);
                break;
            case EventTypes.Syn when e.Code == EventTypes.SynReport:
                Engine.Sync();
                break;
        }
    }

    TiltRoute Tilt(ushort source) => source == LegacyRemapControls.NagaTiltLeft
        ? _tiltLeft ??= new(source) : _tiltRight ??= new(source);

    void ClearTiltRoutes()
    {
        _tiltLeft = _tiltRight = null;
        _tiltWheelPath = null;
        _tiltTimer?.Change(Timeout.InfiniteTimeSpan, Timeout.InfiniteTimeSpan);
    }

    bool RouteTiltWheel(string path, ushort axis, int value)
    {
        if (HasNativeNagaTilt || !Engine.NagaTiltReportsHorizontalWheel || value == 0 ||
            axis is not (RelativeAxes.HWheel or RelativeAxes.HWheelHiRes))
            return false;
        ushort source = value < 0 ? LegacyRemapControls.NagaTiltLeft : LegacyRemapControls.NagaTiltRight;
        bool hypershift = Engine.HypershiftActive;
        var route = value < 0 ? _tiltLeft : _tiltRight;
        long now = _time.GetTimestamp();
        DrainTilt(route, now);
        if (!Engine.CanRemapNagaTilt(source, hypershift) && route?.Held == null && route?.Buttons.Count is not > 0)
            return false;
        // The standard event owns the detent when both resolutions are exposed.
        if ((axis == RelativeAxes.HWheelHiRes) != Engine.NagaHorizontalHighResOnly)
            return true;

        route ??= Tilt(source);
        long count = Math.Abs((long)value);
        if (Engine.NagaHorizontalHighResOnly)
        {
            count += route.Remainder;
            route.Remainder = (int)(count % 120);
            count /= 120;
        }
        if (count == 0)
            return true;
        if (!Engine.HasEvdevNagaTilt(source))
        {
            EmitTiltDetents(source, count, hypershift);
            return true;
        }

        while (count > 0 && route.Buttons.TryDequeue(out var button))
        {
            MatchTiltWheel(route, button, 1, hypershift);
            count--;
        }
        if (count > 0)
        {
            if (route.Held is { } held)
                MatchTiltWheel(route, held, count, hypershift);
            else if (!_keyNodes.ContainsKey(source))
            {
                route.Pending.Enqueue(new(now, count, hypershift));
                _tiltWheelPath = path;
                ScheduleTiltTimer(now);
            }
        }
        return true;
    }

    void ObserveTiltButton(ushort source)
    {
        var route = Tilt(source);
        long now = _time.GetTimestamp();
        DrainTilt(route, now);
        if (route.Pending.Count == 0 && !Engine.CanRemapNagaTilt(source, Engine.HypershiftActive))
            return;
        var button = new TiltButton(now);
        route.Held = button;
        if (route.Pending.TryPeek(out var wheel))
        {
            MatchTiltWheel(route, button, 1, wheel.Hypershift);
            if (--wheel.Count == 0)
                route.Pending.Dequeue();
        }
        else
            route.Buttons.Enqueue(button);
    }

    void MatchTiltWheel(TiltRoute route, TiltButton button, long count, bool hypershift)
    {
        if (button.Released)
        {
            if (!button.ConsumesWheel)
                EmitTiltScroll(route.Source, count, hypershift);
        }
        else if (!Engine.HeldNagaTiltConsumesWheel(route.Source))
        {
            // A later key can complete the shortcut while this tilt is still held.
            button.DeferredScroll.Add((count, hypershift));
        }
    }

    void DrainTilt(TiltRoute? route, long now)
    {
        if (route == null)
            return;
        while (route.Buttons.TryPeek(out var button) && _time.GetElapsedTime(button.At, now) >= TiltMirrorWindow)
            route.Buttons.Dequeue();
        while (route.Pending.TryPeek(out var wheel) && _time.GetElapsedTime(wheel.At, now) >= TiltMirrorWindow)
        {
            route.Pending.Dequeue();
            EmitTiltDetents(route.Source, wheel.Count, wheel.Hypershift);
        }
    }

    void EmitTiltDetents(ushort source, long count, bool hypershift)
    {
        for (long i = 0; i < count; i++)
            Engine.NagaTiltDetent(source, hypershift);
    }

    void EmitTiltScroll(ushort source, long count, bool hypershift)
    {
        for (long i = 0; i < count; i++)
            Engine.NagaTiltWheelFallback(source, hypershift);
    }

    void ScheduleTiltTimer(long now)
    {
        long? next = _tiltLeft?.Pending.TryPeek(out var left) == true ? left.At : null;
        if (_tiltRight?.Pending.TryPeek(out var right) == true)
            next = next is long at ? Math.Min(at, right.At) : right.At;
        if (next == null)
            return;
        _tiltTimer ??= _time.CreateTimer(_ => FinishTiltWindow(), null, Timeout.InfiniteTimeSpan, Timeout.InfiniteTimeSpan);
        var remaining = TiltMirrorWindow - _time.GetElapsedTime(next.Value, now);
        _tiltTimer.Change(remaining > TimeSpan.Zero ? remaining : TimeSpan.Zero, Timeout.InfiniteTimeSpan);
    }

    void FinishTiltWindow()
    {
        string? path = null;
        try
        {
            lock (_engineGate)
            {
                if (_stopping || _tiltWheelPath == null)
                    return;
                path = _tiltWheelPath;
                long now = _time.GetTimestamp();
                DrainTilt(_tiltLeft, now);
                DrainTilt(_tiltRight, now);
                Engine.Sync();
                ScheduleTiltTimer(now);
            }
        }
        // Timer callbacks have no caller to receive an input failure.
#pragma warning disable CA1031
        catch (Exception error)
#pragma warning restore CA1031
        {
            if (!_stopping && path != null)
                NodeLost?.Invoke(path, error);
        }
    }

    void DispatchKey(string nodePath, ushort code, int value)
    {
        _keyNodes.TryGetValue(code, out var nodes);
        switch (value)
        {
            case 1 when nodes == null:
                _keyNodes.Add(code, [nodePath]);
                KeyPressed?.Invoke(code);
                Engine.Key(code, 1);
                break;
            case 1:
                nodes!.Add(nodePath);
                break;
            case 0 when nodes != null && nodes.Remove(nodePath) && nodes.Count == 0:
                _keyNodes.Remove(code);
                ReleaseKey(code);
                break;
            case 2 when nodes?.Contains(nodePath) == true:
                Engine.Key(code, 2);
                break;
        }
    }

    void ReleaseKey(ushort code)
    {
        var route = code switch
        {
            LegacyRemapControls.NagaTiltLeft => _tiltLeft,
            LegacyRemapControls.NagaTiltRight => _tiltRight,
            _ => null,
        };
        var button = route?.Held;
        if (route != null && button != null)
        {
            route.Held = null;
            button.Released = true;
            button.ConsumesWheel = Engine.HeldNagaTiltConsumesWheel(code);
        }
        Engine.Key(code, 0);
        if (button is { ConsumesWheel: false })
            foreach (var (count, hypershift) in button.DeferredScroll)
                EmitTiltScroll(code, count, hypershift);
    }

    public void Dispose()
    {
        _stopping = true;
        Exception? failure = null;
        try
        {
            lock (_engineGate)
            {
                _tiltTimer?.Dispose();
                Engine.Reset();
            }
        }
        catch (Exception error) { failure = error; }
        try { _macroReader?.Dispose(); }
        catch (Exception error) { failure ??= error; }
        try { _macroMode?.Dispose(); }
        catch (Exception error) { failure ??= error; }
        foreach (var reader in _nagaReaders.Values)
        {
            try { reader.Reader.Dispose(); }
            catch (Exception error) { failure ??= error; }
        }
        // Readers wake from poll within 100 ms and see the flag; only then are the nodes closed, so no
        // read can land on a descriptor number the process has already reused.
        foreach (var thread in _threads)
        {
            try { thread.Join(); }
            catch (Exception error) { failure ??= error; }
        }
        foreach (var node in _nodes)
        {
            try { node.Dispose(); }
            catch (Exception error) { failure ??= error; }
        }
        if (failure != null)
            ExceptionDispatchInfo.Capture(failure).Throw();
    }
}
