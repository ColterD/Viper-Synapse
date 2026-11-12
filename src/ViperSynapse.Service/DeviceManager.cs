using System.Globalization;
using System.Text.Json.Serialization;
using ViperSynapse.Core.Bindings;
using ViperSynapse.Core.Devices;
using ViperSynapse.Core.Engine;
using ViperSynapse.Core.Input;
using ViperSynapse.Hardware;
using ViperSynapse.Hardware.Linux;

namespace ViperSynapse.Service;

/// <summary>What the window is told about one connected device.</summary>
public sealed record DeviceInfo(
    string Key,
    string ProductId,
    string Name,
    string Type,
    string Port,
    bool Known,
    bool Verified,
    string? Serial,
    string? Firmware,
    string? ControlNode,
    IReadOnlyList<string> InputNodes,
    bool Remapping,
    string? Problem,
    IReadOnlyList<ushort>? InputControls = null,
    string? InputControlsStatus = null);

[JsonSourceGenerationOptions(PropertyNamingPolicy = JsonKnownNamingPolicy.CamelCase)]
[JsonSerializable(typeof(List<DeviceInfo>))]
internal sealed partial class DeviceInfoJson : JsonSerializerContext;

/// <summary>
/// Keeps track of the Razer devices plugged in and runs a remap session for each one whose active keymap
/// binds anything. A device with nothing bound is left alone (not grabbed), so it behaves exactly as it does
/// without the service. All state changes happen under one lock; device I/O happens outside it where it
/// can take time.
/// </summary>
public sealed class DeviceManager : IDisposable
{
    public const ushort RazerVendor = 0x1532;

    /// <summary>Model ids verified on real hardware; the rest of the catalogue is OpenRazer's word.</summary>
    static readonly HashSet<ushort> Verified = [0x02EB, 0x008D, 0x00E3];

    readonly SettingsStore _store;
    readonly IInputSink _output;
    readonly Lock _gate = new();
    readonly Dictionary<string, Connected> _devices = [];
    readonly VirtualGamepad _pad = new();
    MacroLibrary _macros;
    ShortcutList _shortcuts;

    public event Action? DevicesChanged;
    public event Action<string>? SettingsChanged;

    /// <summary>A watched thumbstick moved: device key, x and y from -1 to 1. At most 30 a second per device.</summary>
    public event Action<string, double, double>? StickMoved;

    /// <summary>The physical-path prefix the tests' stand-in devices claim. A running service ignores them, so a test
    /// never adds a device to the user's real settings.</summary>
    public const string TestPortPrefix = "usb-viper-test";

    readonly bool _includeTestDevices;

    public DeviceManager(SettingsStore store, IInputSink output, bool includeTestDevices = false)
    {
        _includeTestDevices = includeTestDevices;
        _store = store;
        // Every device session writes to the same virtual keyboard, so their modifier owners must share state.
        _output = output is SharedModifierSink ? output : new SharedModifierSink(output);
        _macros = store.LoadMacros();
        _shortcuts = store.LoadShortcuts();
    }

    sealed class Connected(string port, ushort productId, string name)
    {
        public string Port { get; } = port;
        public ushort ProductId { get; } = productId;
        public string Name { get; } = name;
        public string Key => ProductId.ToString("x4", CultureInfo.InvariantCulture);
        public DeviceModel? Model => DeviceCatalogue.Default.Find(ProductId);
        public string? ControlNode { get; set; }
        public DeviceIdentity? Identity { get; set; }
        public List<string> InputNodes { get; set; } = [];
        public IReadOnlyList<ushort> InputControls { get; set; } = [];
        public string InputControlsStatus { get; set; } = "unavailable";
        public bool InputFailed { get; set; }
        int _physicalHypershift;
        RemapEngine? _engine;
        public RemapEngine? Engine
        {
            get => _engine;
            set
            {
                _engine = value;
                if (value != null)
                    value.PhysicalHypershiftHeld = () => Volatile.Read(ref _physicalHypershift) != 0;
            }
        }
        public void SetPhysicalHypershift(bool held) => Volatile.Write(ref _physicalHypershift, held ? 1 : 0);
        DeviceSession? _session;
        public DeviceSession? Session { get => Volatile.Read(ref _session); set => Volatile.Write(ref _session, value); }
        public DeviceControl? Control { get; set; }
        public KeypadButtons? Buttons { get; set; }
        public bool ButtonsStarting { get; set; }
        public ThumbstickMode SessionStick { get; set; }
        public StickMonitor? Monitor { get; set; }
        public DateTime StickWatchedUntil { get; set; }
        public long StickLastSent { get; set; }
        public string? Problem { get; set; }
        public string? InputProblem { get; set; }
        public string? ButtonsProblem { get; set; }
        public string? CleanupProblem { get; set; }

        public string? Problems
        {
            get
            {
                var problems = new[] { Problem, ButtonsProblem, InputProblem, CleanupProblem }
                    .Where(p => p != null).ToArray();
                return problems.Length == 0 ? null : string.Join(" ", problems);
            }
        }
    }

    public IReadOnlyList<DeviceInfo> Devices
    {
        get
        {
            lock (_gate)
                return _devices.Values.OrderBy(d => d.Port, StringComparer.Ordinal).Select(Describe).ToList();
        }
    }

    public string DevicesJson() => System.Text.Json.JsonSerializer.Serialize(Devices.ToList(), DeviceInfoJson.Default.ListDeviceInfo);

    static DeviceInfo Describe(Connected d) => new(
        d.Key,
        d.Key,
        d.Model?.Name ?? d.Name,
        d.Model?.Type ?? "unknown",
        d.Port,
        d.Model != null,
        Verified.Contains(d.ProductId),
        d.Identity?.Serial,
        d.Identity?.Firmware,
        d.ControlNode,
        d.InputNodes,
        d.Session != null,
        d.Problems,
        d.InputControls,
        d.InputControlsStatus);

    /// <summary>Looks at what is plugged in now: starts new devices, drops the ones that went away.</summary>
    public void Refresh()
    {
        var hidraw = HidrawNode.Enumerate().Where(n => n.VendorId == RazerVendor && n.PortPath.Length > 0 && Real(n.PortPath)).ToList();
        // Virtual devices (ours included) have no physical port; only real hardware is a device.
        var inputs = InputNode.Enumerate().Where(n => n.VendorId == RazerVendor && n.PortPath.Length > 0 && Real(n.PortPath)).ToList();
        var ports = hidraw.Select(n => (n.PortPath, n.ProductId, n.Name))
            .Concat(inputs.Select(n => (n.PortPath, n.ProductId, n.Name)))
            .GroupBy(p => p.PortPath)
            .ToDictionary(g => g.Key, g => g.First());

        bool changed = false;
        List<Connected> added = [];
        lock (_gate)
        {
            foreach (var gone in _devices.Keys.Where(port => !ports.ContainsKey(port)).ToList())
            {
                StopSession(_devices[gone]);
                _devices[gone].Control?.Dispose();
                _devices[gone].Buttons?.Dispose();
                _devices[gone].Monitor?.Dispose();
                _devices.Remove(gone);
                changed = true;
            }
            foreach (var (port, (_, productId, name)) in ports)
            {
                if (!_devices.TryGetValue(port, out var device))
                {
                    device = new Connected(port, productId, name);
                    _devices[port] = device;
                    added.Add(device);
                    changed = true;
                }
                var nodes = inputs.Where(n => n.PortPath == port).Select(n => n.DevicePath).ToList();
                if (!nodes.SequenceEqual(device.InputNodes))
                {
                    device.InputNodes = nodes;
                    // The device's nodes changed under a running session (a re-plug): start it again.
                    StopSession(device);
                    device.InputFailed = false;
                    changed = true;
                }
            }
        }

        // Capability discovery only opens nodes and queries their event bits; it never grabs or reads keys.
        // Run it outside the device lock and discard results if the nodes changed in the meantime.
        List<(Connected Device, string[] Nodes)> discover;
        lock (_gate)
            discover = _devices.Values.Where(d => d.Model?.Keys.Count is null or 0)
                .Select(d => (d, d.InputNodes.ToArray())).ToList();
        foreach (var (device, nodes) in discover)
        {
            var found = InputControlDiscovery.Read(nodes);
            lock (_gate)
            {
                if (!_devices.TryGetValue(device.Port, out var current) || current != device || !nodes.SequenceEqual(device.InputNodes))
                    continue;
                if (device.InputControlsStatus != found.Status || !device.InputControls.SequenceEqual(found.Controls))
                {
                    // A permission recovery is a meaningful reason to allow a retry.
                    if (found.Status == "available" && device.InputControlsStatus != "available")
                        device.InputFailed = false;
                    device.InputControls = found.Controls;
                    device.InputControlsStatus = found.Status;
                    changed = true;
                }
            }
        }

        var adopt = new List<(string Key, int Keymap)>();
        // Probing talks to the hardware; keep it out of the lock.
        foreach (var device in added)
        {
            var control = hidraw.FirstOrDefault(n => n.PortPath == device.Port && RazerProbe.HasControlReport(n));
            device.ControlNode = control?.DevicePath;
            if (control != null)
            {
                try
                {
                    var transport = new RazerTransport(HidrawDevice.Open(control.DevicePath));
                    device.Identity = RazerProbe.Identify(transport);
                    if (device.Identity == null)
                    {
                        transport.Dispose();
                        device.Problem = "The device did not answer on its control interface.";
                    }
                    else
                    {
                        // Kept open while the device is plugged in: lighting and sensitivity go through it.
                        device.Control = new DeviceControl(transport, device.Identity.TransactionId, device.Model);
                    }
                    // A keypad with keymaps of its own: the profile starts on the keymap the keypad is on.
                    if (device.Control?.ReadKeymap() is int keymap and >= 0)
                        adopt.Add((device.Key, keymap));
                }
                catch (Exception e) when (e is IOException or UnauthorizedAccessException)
                {
                    device.Problem = e is UnauthorizedAccessException
                        ? "No permission to talk to the device: install the Viper Synapse udev rule."
                        : e.Message;
                }
            }
        }

        foreach (var device in added.Where(d => d.Model?.Has("keypad_buttons") == true))
            StartButtons(device, hidraw.FirstOrDefault(n => n.PortPath == device.Port && n.Interface == 1));
        foreach (var (key, keymap) in adopt)
            FollowDeviceKeymap(key, keymap);

        List<string> keys;
        lock (_gate)
            keys = _devices.Values.Select(d => d.Key).Distinct().ToList();
        foreach (var key in keys)
            Apply(key, refreshing: true);
        if (changed)
            DevicesChanged?.Invoke();
    }

    bool Real(string port) => _includeTestDevices || !port.StartsWith(TestPortPrefix, StringComparison.Ordinal);

    internal static bool ShortcutMatchesControls(DeviceModel? model, IReadOnlyList<ushort> controls, string status, IReadOnlyList<ushort> keys)
    {
        if (keys.Count == 0 || keys.Any(SourceKeys.IsWheel))
            return false;
        if (model?.Keys.Count > 0)
            return keys.All(k => model.Keys.Any(dk => dk.Source == k));
        return status == "available" && keys.All(controls.Contains);
    }

    public DeviceSettings GetSettings(string key) => _store.LoadDevice(key);

    /// <summary>The device's own sensitivity, polling rate and battery, read now. Null when no device of the model is
    /// plugged in or it has no control interface.</summary>
    public DeviceState? ReadState(string key)
    {
        DeviceControl? control;
        lock (_gate)
            control = _devices.Values.FirstOrDefault(d => d.Key == key && d.Control != null)?.Control;
        return control?.ReadState();
    }

    public void SetSettings(string key, DeviceSettings settings)
    {
        _store.SaveDevice(key, settings);
        Apply(key);
        SettingsChanged?.Invoke(key);
    }

    // Input threads hold the session lock while reading these replaced snapshots. Do not
    // take _gate here: session changes hold _gate while waiting for that same session lock.
    public MacroLibrary Macros => Volatile.Read(ref _macros);

    public void SetMacros(MacroLibrary macros)
    {
        _store.SaveMacros(macros);
        lock (_gate)
            Volatile.Write(ref _macros, macros);
    }

    public ShortcutList Shortcuts => Volatile.Read(ref _shortcuts);

    /// <summary>New shortcuts can mean a device must now be grabbed (or need not be any more): every model is
    /// applied again.</summary>
    public void SetShortcuts(ShortcutList shortcuts)
    {
        _store.SaveShortcuts(shortcuts);
        List<string> keys;
        lock (_gate)
        {
            Volatile.Write(ref _shortcuts, shortcuts);
            keys = _devices.Values.Select(d => d.Key).Distinct().ToList();
        }
        foreach (var key in keys)
            Apply(key);
    }

    /// <summary>Brings every connected device of a model in line with its stored settings.</summary>
    public void Apply(string key) => Apply(key, refreshing: false);

    void Apply(string key, bool refreshing)
    {
        var settings = _store.LoadDevice(key);
        var keymap = settings.ActiveKeymapOrFirst;
        var profile = settings.Active;
        bool bindsAnything = keymap.Standard.Values.Concat(keymap.Hypershift.Values).Any(b => b is not DefaultBinding);
        bool changed = false;
        lock (_gate)
        {
            foreach (var device in _devices.Values.Where(d => d.Key == key))
            {
                // A failed reader must not be grabbed again on unrelated filesystem scans. Explicit
                // settings changes, changed nodes or recovered permissions permit another attempt.
                if (refreshing && device.InputFailed)
                    continue;
                if (!refreshing)
                    device.InputFailed = false;
                // A thumbstick in 4-way or 8-way mode types its directions even with nothing bound.
                var stick = device.Model?.Has("thumbstick") == true ? profile.Thumbstick : ThumbstickMode.Analog;
                // A global shortcut made of this device's keys needs its input too.
                bool shortcut = _shortcuts.Shortcuts.Any(s => ShortcutMatchesControls(device.Model, device.InputControls, device.InputControlsStatus, s.Keys));
                if (refreshing && device.Session != null && _shortcuts.Shortcuts.Count > 0 &&
                    (device.Model?.Keys.Count is null or 0) && device.InputControlsStatus != "available")
                    shortcut = true;
                // A faster wheel is the engine's work too.
                int scrollLines = Math.Clamp(profile.Scroll?.LinesPerNotch ?? 1, 1, Core.Lighting.ScrollSettings.MaxLinesPerNotch);
                if (!bindsAnything && !shortcut && scrollLines <= 1 && stick == ThumbstickMode.Analog)
                {
                    if (device.Session != null)
                    {
                        StopSession(device);
                        changed = true;
                    }
                    continue;
                }
                if (device.Session != null && device.SessionStick == stick)
                {
                    try
                    {
                        UpdateSession(device.Session, keymap, profile.Interruption, scrollLines, refreshing);
                        if (!refreshing && device.InputProblem?.StartsWith("Could not update the device's keys: ", StringComparison.Ordinal) == true)
                        {
                            bool recovered = false;
                            device.Session.WithEngine(engine => recovered = engine.TryConfirmOutputRecovery());
                            if (recovered)
                            {
                                device.InputProblem = null;
                                changed = true;
                            }
                        }
                    }
                    catch (Exception e) when (e is IOException or UnauthorizedAccessException or ObjectDisposedException or ThreadStateException)
                    {
                        device.InputProblem = "Could not update the device's keys: " + e.Message;
                        Console.Error.WriteLine($"{device.Key}: {device.InputProblem}");
                        changed = true;
                    }
                    continue;
                }
                // A new stick mode takes over (or gives back) a different set of nodes: start again.
                StopSession(device);
                if (device.InputNodes.Count == 0)
                {
                    device.InputProblem = "No input nodes are available. Reconnect the device or check its permissions.";
                    changed = true;
                    continue;
                }
                var model = device.Model;
                device.Engine = new RemapEngine(_output, new Host(this, key))
                {
                    Keymap = keymap,
                    Defaults = source => DefaultFor(model, source),
                    Interruption = profile.Interruption,
                    ScrollLines = scrollLines,
                };
                try
                {
                    device.Session = DeviceSession.Start(device.InputNodes, device.Engine, stick, (failed, path, error) =>
                        ThreadPool.QueueUserWorkItem(_ =>
                        {
                            if (!RetireFailedSession(device, failed, path, error))
                                return;
                            DevicesChanged?.Invoke();
                        }));
                    var watched = device;
                    device.Session.StickMoved += (x, y) => SendStick(watched, x, y);
                    device.Session.KeyPressed += source => watched.Control?.Press(source);
                    // The session now holds the stick; an analog-mode monitor would read nothing.
                    if (stick != ThumbstickMode.Analog)
                    {
                        device.Monitor?.Dispose();
                        device.Monitor = null;
                    }
                    device.SessionStick = stick;
                    if (IsInputSessionProblem(device.InputProblem))
                        device.InputProblem = null;
                }
                catch (Exception e) when (e is IOException or UnauthorizedAccessException)
                {
                    device.InputProblem = "Could not take over the device's keys: " + e.Message;
                }
                changed = true;
            }
        }
        // Lighting, sensitivity and power talk to the hardware: outside the lock.
        List<DeviceControl> controls;
        lock (_gate)
            controls = _devices.Values.Where(d => d.Key == key && d.Control != null).Select(d => d.Control!).ToList();
        foreach (var control in controls)
        {
            control.Apply(settings.Active, settings.ActiveKeymap);
            if (control.LastProblem != null)
                Console.Error.WriteLine($"{key}: {control.LastProblem}");
        }
        if (changed)
            DevicesChanged?.Invoke();
    }

    internal static void UpdateSession(DeviceSession session, Keymap keymap, MacroInterruption interruption, int scrollLines, bool refreshing)
    {
        // A device scan can be triggered by an unrelated udev permission change. Do not release held
        // keys or stop background actions when the existing session needs no new keymap.
        try
        {
            if (!refreshing)
                session.Apply(keymap);
        }
        finally
        {
            session.WithEngine(e => (e.Interruption, e.ScrollLines) = (interruption, scrollLines));
        }
    }

    static bool IsInputSessionProblem(string? problem) => problem != null &&
        (problem.StartsWith("Input stopped on ", StringComparison.Ordinal) ||
         problem.StartsWith("Could not take over the device's keys: ", StringComparison.Ordinal) ||
         problem.StartsWith("Could not update the device's keys: ", StringComparison.Ordinal) ||
         problem.StartsWith("No input nodes are available.", StringComparison.Ordinal));

    bool RetireFailedSession(Connected device, DeviceSession failed, string path, Exception error)
    {
        lock (_gate)
        {
            // A delayed notification must not stop a replacement session or a reconnected device.
            if (!_devices.TryGetValue(device.Port, out var current) || current != device || device.Session != failed)
                return false;
            StopSession(device);
            device.InputFailed = true;
            device.InputProblem = $"Input stopped on {path}: {error.Message}. Reconnect the device or change its settings to retry.";
            Console.Error.WriteLine($"{device.Key}: {device.InputProblem}");
            return true;
        }
    }

    static void StopSession(Connected device)
    {
        var session = device.Session;
        device.Session = null;
        if (session == null)
            return;
        // A replacement engine cannot verify the old engine's cancelled background cleanup. Preserve
        // any unresolved update warning independently before a later start replaces the input status.
        if (device.InputProblem?.StartsWith("Could not update the device's keys: ", StringComparison.Ordinal) == true)
        {
            device.CleanupProblem = device.CleanupProblem == null ? device.InputProblem : $"{device.CleanupProblem} {device.InputProblem}";
            device.InputProblem = null;
        }
        try
        {
            session.Dispose();
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException or ObjectDisposedException or ThreadStateException)
        {
            string problem = "Could not release the device's keys: " + e.Message;
            device.CleanupProblem = device.CleanupProblem == null ? problem : $"{device.CleanupProblem} {problem}";
            Console.Error.WriteLine($"{device.Key}: {problem}");
        }
    }

    /// <summary>Listens to a keypad's own buttons: Hypershift held switches the engine's layer, the keymap button moves
    /// the profile to that keymap.</summary>
    void StartButtons(Connected device, HidrawNode? node) => StartButtonsUsing(device, node, KeypadButtons.Start);

    void StartButtonsUsing(Connected device, HidrawNode? node,
        Func<string, Action<bool>, Action<int>, Action<KeypadButtons>, KeypadButtons> start)
    {
        if (node == null)
            return;
        lock (_gate)
        {
            if (!_devices.TryGetValue(device.Port, out var current) || current != device || device.Buttons != null || device.ButtonsStarting)
                return;
            device.ButtonsStarting = true;
            device.SetPhysicalHypershift(false);
        }
        var published = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        KeypadButtons? buttons = null;
        bool listenerPublished = false;
        bool disposeAttempted = false;
        try
        {
            // The listener can be joined while _gate is held. Keep the firmware state independently of any
            // session, so reports during a session restart are also visible to the next engine. Register
            // handlers before the reader starts, so the first report or an immediate exit is not lost.
            buttons = start(node.DevicePath,
                device.SetPhysicalHypershift,
                keymap => _ = Task.Run(async () =>
                {
                    if (await published.Task.ConfigureAwait(false))
                        HandleKeymapReport(device, buttons!, keymap);
                }),
                stopped => _ = Task.Run(async () =>
                {
                    if (await published.Task.ConfigureAwait(false))
                        RetireStoppedButtons(device, stopped);
                }));
            bool accepted;
            lock (_gate)
            {
                device.ButtonsStarting = false;
                accepted = _devices.TryGetValue(device.Port, out var current) && current == device && device.Buttons == null;
                if (accepted)
                    device.Buttons = buttons;
            }
            listenerPublished = accepted;
            published.TrySetResult(accepted);
            if (!accepted)
            {
                disposeAttempted = true;
                buttons.Dispose(); // Never join an orphan listener while holding the manager lock.
            }
        }
        catch (Exception e)
        {
            published.TrySetResult(false);
            bool notify = false;
            lock (_gate)
            {
                if (!listenerPublished)
                    device.ButtonsStarting = false;
                if (e is IOException && _devices.TryGetValue(device.Port, out var current) && current == device)
                {
                    device.ButtonsProblem = "Cannot listen to the keypad's buttons: " + e.Message;
                    notify = true;
                }
            }
            if (!listenerPublished && buttons != null && !disposeAttempted)
            {
                try { buttons.Dispose(); }
                catch (Exception cleanup) { e.Data["KeypadButtonsCleanupFailure"] = cleanup; }
            }
            if (e is not IOException)
                throw;
            Console.Error.WriteLine($"{device.Key}: cannot listen to the keypad's buttons: {e.Message}");
            if (notify)
                DevicesChanged?.Invoke();
        }
    }

    void HandleKeymapReport(Connected device, KeypadButtons buttons, int keymap)
    {
        lock (_gate)
        {
            if (!_devices.TryGetValue(device.Port, out var current) || current != device || device.Buttons != buttons)
                return;
            device.Control?.KeymapChangedOnDevice(keymap - 1);
            FollowDeviceKeymap(device.Key, keymap - 1);
        }
    }

    void RetireStoppedButtons(Connected device, KeypadButtons buttons)
    {
        lock (_gate)
        {
            if (!_devices.TryGetValue(device.Port, out var current) || current != device || device.Buttons != buttons)
                return;
            device.Buttons = null;
            // Do not allow a replacement until this listener's final callback has finished.
            device.ButtonsStarting = true;
            device.SetPhysicalHypershift(false);
        }
        // The callback was queued by the listener itself; joining it on this worker is safe.
        buttons.Dispose();
        bool notify = false;
        lock (_gate)
        {
            device.ButtonsStarting = false;
            if (_devices.TryGetValue(device.Port, out var current) && current == device)
            {
                device.ButtonsProblem = "The keypad button listener stopped. Reconnect the device to retry.";
                notify = true;
            }
        }
        if (notify)
            DevicesChanged?.Invoke();
    }

    /// <summary>The window follows the thumbstick for the next 15 seconds (it asks again while the page is open). In
    /// analog mode the gamepad node is read without taking it; in 4-way and 8-way mode the session reports it.</summary>
    public void WatchStick(string key)
    {
        List<Connected> devices;
        lock (_gate)
            devices = _devices.Values.Where(d => d.Key == key && d.Model?.Has("thumbstick") == true).ToList();
        foreach (var device in devices)
        {
            device.StickWatchedUntil = DateTime.UtcNow.AddSeconds(15);
            if (device.Monitor != null || device.Session != null && device.SessionStick != ThumbstickMode.Analog)
                continue;
            var node = device.InputNodes.FirstOrDefault(IsGamepad);
            if (node == null)
                continue;
            try
            {
                var monitor = StickMonitor.Start(node);
                monitor.Moved += (x, y) => SendStick(device, x, y);
                device.Monitor = monitor;
            }
            catch (Exception e) when (e is IOException or UnauthorizedAccessException)
            {
                Console.Error.WriteLine($"{key}: cannot follow the thumbstick: {e.Message}");
            }
        }
    }

    static bool IsGamepad(string path)
    {
        try
        {
            using var node = EvdevDevice.Open(path);
            return node.Capabilities(EventTypes.Key).Any(Core.Input.KeyCodes.IsJoystickButton) && node.Capabilities(EventTypes.Abs).Count > 0;
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
            return false;
        }
    }

    void SendStick(Connected device, double x, double y)
    {
        if (DateTime.UtcNow > device.StickWatchedUntil)
        {
            // Nobody is looking any more: an analog-mode monitor goes away (off its own thread).
            if (device.Monitor is { } idle)
            {
                device.Monitor = null;
                ThreadPool.QueueUserWorkItem(_ => idle.Dispose());
            }
            return;
        }
        long now = Environment.TickCount64;
        if (now - device.StickLastSent < 33)
            return;
        device.StickLastSent = now;
        StickMoved?.Invoke(device.Key, x, y);
    }

    /// <summary>The screen went off or on, or the idle time moved: each device whose profile asks for it goes dark or
    /// comes back.</summary>
    public void Idle(bool screenOff, TimeSpan idle)
    {
        List<DeviceControl> controls;
        lock (_gate)
            controls = _devices.Values.Where(d => d.Control != null).Select(d => d.Control!).ToList();
        foreach (var control in controls)
        {
            if (control.Lighting is not { } rules)
                continue;
            bool dark = rules.OffWhenDisplayOff && screenOff
                || rules.OffWhenIdleMinutes > 0 && idle >= TimeSpan.FromMinutes(rules.OffWhenIdleMinutes);
            control.SetDimmed(dark);
        }
    }

    /// <summary>What a control with no key of its own does by default: the key the catalogue names for it (W for the
    /// thumbstick's up).</summary>
    static Binding? DefaultFor(DeviceModel? model, ushort source) =>
        model?.Keys.FirstOrDefault(k => k.Source == source && k.Code != 0 && k.Code != source) is { } key
            ? new KeyboardBinding(key.Code)
            : null;

    /// <summary>The keypad moved to another keymap (its keymap button, or what it was on when it was plugged in): the
    /// profile follows, gaining empty keymaps up to that one if it has fewer.</summary>
    void FollowDeviceKeymap(string key, int keymap)
    {
        var settings = _store.LoadDevice(key);
        if (settings.ActiveKeymap == keymap && settings.Active.Keymaps.Count > keymap)
            return;
        var profile = settings.Active;
        var keymaps = profile.Keymaps.ToList();
        while (keymaps.Count <= keymap)
            keymaps.Add(Keymap.Empty($"Keymap {keymaps.Count + 1}"));
        profile = profile with { Keymaps = keymaps };
        SetSettings(key, settings with
        {
            Profiles = settings.Profiles.Select(p => p.Id == profile.Id ? profile : p).ToList(),
            ActiveKeymap = keymap,
        });
    }

    // Per device model: the profile the user chose, while a linked program's profile stands in for it.
    readonly Dictionary<string, Guid> _chosenProfile = [];

    /// <summary>A program came to the front: switch each device to the profile linked to it, or back to the one the
    /// user had chosen when the front program is linked to none.</summary>
    public void Focused(FocusedProgram program)
    {
        List<string> keys;
        lock (_gate)
            keys = _devices.Values.Select(d => d.Key).Distinct().ToList();
        foreach (var key in keys)
        {
            var settings = _store.LoadDevice(key);
            var linked = settings.Profiles.FirstOrDefault(p => p.LinkedPrograms.Any(program.Matches));
            Guid? target;
            lock (_gate)
            {
                if (linked != null)
                {
                    _chosenProfile.TryAdd(key, settings.ActiveProfile);
                    target = linked.Id;
                }
                else
                {
                    target = _chosenProfile.Remove(key, out var chosen) ? chosen : null;
                }
            }
            if (target is Guid id && id != settings.ActiveProfile && settings.Profiles.Any(p => p.Id == id))
                SetSettings(key, settings with { ActiveProfile = id, ActiveKeymap = 0 });
        }
    }

    /// <summary>What a device's engine asks of the service.</summary>
    sealed class Host(DeviceManager manager, string key) : IEngineHost
    {
        public void Launch(string command)
        {
            try { CommandLine.Start(command); }
            catch (Exception e) when (e is System.ComponentModel.Win32Exception or InvalidOperationException)
            {
                Console.Error.WriteLine($"Could not start \"{command}\": {e.Message}");
            }
        }

        // Switching changes the keymap the engine runs, which must not happen on the engine's own input thread
        // while it is mid-event; the pool takes it.
        public void Open(string target)
        {
            try { CommandLine.Open(target); }
            catch (Exception e) when (e is System.ComponentModel.Win32Exception or InvalidOperationException)
            {
                Console.Error.WriteLine($"Could not open \"{target}\": {e.Message}");
            }
        }

        public void SwitchProfile(CycleDirection direction, Guid? profileId) =>
            ThreadPool.QueueUserWorkItem(_ => manager.SetSettings(key, manager.GetSettings(key).SwitchProfile(direction, profileId)));

        public void SwitchKeymap(CycleDirection direction, int keymap) =>
            ThreadPool.QueueUserWorkItem(_ => manager.SetSettings(key, manager.GetSettings(key).SwitchKeymap(direction, keymap)));

        public Macro? FindMacro(Guid id) => manager.Macros.Find(id);

        public ShortcutList Shortcuts => manager.Shortcuts;

        public void Gamepad(GamepadControl control, bool down) => manager._pad.Set(control, down);

        public void Report(Exception error) => Console.Error.WriteLine($"{key}: {error.Message}");
    }

    public void Dispose()
    {
        lock (_gate)
        {
            foreach (var device in _devices.Values)
            {
                StopSession(device);
                device.Control?.Dispose();
                device.Buttons?.Dispose();
                device.Monitor?.Dispose();
            }
            _devices.Clear();
        }
        _pad.Dispose();
    }
}
