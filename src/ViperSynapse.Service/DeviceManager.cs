using System.Collections.Concurrent;
using System.Globalization;
using System.Text;
using System.Text.Json.Serialization;
using ViperSynapse.Core.Bindings;
using ViperSynapse.Core.Devices;
using ViperSynapse.Core.Engine;
using ViperSynapse.Core.Input;
using ViperSynapse.Core.Lighting;
using ViperSynapse.Core.Protocol;
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
    string? InputControlsStatus = null,
    string? Connection = null,
    byte? OnboardProfile = null, bool NativeMacroKeys = false,
    string? Transport = null, bool? ControlReady = null, string? ControlIssue = null, string? InputIssue = null,
    bool? VirtualOutputReady = null, long ManagedDpiRevision = 0, bool ManagedDpiListening = false, bool NagaTiltDetentsOnly = false,
    bool NagaTiltUsesEvdev = false);

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
    const string BasiliskSettingsKey = "00e3";

    /// <summary>Model ids verified on real hardware; the rest of the catalogue is OpenRazer's word.</summary>
    static readonly HashSet<ushort> Verified = [0x02EB, 0x008D, 0x00E3];

    readonly SettingsStore _store;
    IInputSink? _output;
    readonly Func<IInputSink>? _createOutput;
    IDisposable? _ownedOutput;
    string? _outputIssue;
    readonly Lock _gate = new();
    readonly Lock _profileGate = new();
    readonly Lock _shortcutGate = new();
    readonly ConcurrentDictionary<string, Lock> _applyGates = new(StringComparer.Ordinal);
    readonly Lock _refreshGate = new();
    readonly Dictionary<string, Connected> _devices = [];
    readonly Dictionary<string, byte> _basiliskSlots = new(StringComparer.Ordinal);
    readonly VirtualGamepad _pad = new();
    MacroLibrary _macros;
    ShortcutList _shortcuts;
    bool _disposed;

    internal Func<IEnumerable<string>, RemapEngine, ThumbstickMode, Action<DeviceSession, string, Exception>, DeviceSession>
        StartInputSession { get; init; } = DeviceSession.Start;
    internal Func<HidrawNode, bool> IsNativeMacroNode { get; init; } = node => BlackWidowMacroKeys.IsNativeReportNode(node);
    internal Func<string, Action<ushort, int>, Action<Exception>, IHidrawButtonReader> OpenNativeMacroKeys { get; init; } =
        BlackWidowMacroKeys.Open;
    internal Func<HidrawNode, NagaReportNode?> FindNativeNagaNode { get; init; } = node => NagaChromaButtons.FindReportNode(node);
    internal Func<NagaReportNode, Action<ushort, int>, Action<Exception>, IHidrawButtonReader> OpenNativeNagaButtons { get; init; } =
        NagaChromaButtons.Open;
    internal Func<IReadOnlyList<string>, Action<double, double>, IDisposable?> OpenStickMonitor { get; init; } = OpenAnalogStick;
    internal int StickWatchMilliseconds { get; init; } = 15_000;
    internal TimeProvider InputTimeProvider { get; init; } = TimeProvider.System;

    public event Action? DevicesChanged;
    public event Action<string>? SettingsChanged;

    /// <summary>A watched thumbstick moved: device key, x and y from -1 to 1. At most 30 a second per device.</summary>
    public event Action<string, double, double>? StickMoved;

    /// <summary>The physical-path prefix the tests' stand-in devices claim. A running service ignores them, so a test
    /// never adds a device to the user's real settings.</summary>
    public const string TestPortPrefix = "usb-viper-test";

    readonly bool _includeTestDevices;

    public DeviceManager(SettingsStore store, IInputSink output, bool includeTestDevices = false)
        : this(store, includeTestDevices) => _output = ShareModifiers(output);

    internal DeviceManager(SettingsStore store, Func<IInputSink> createOutput)
        : this(store, includeTestDevices: false)
    {
        _createOutput = createOutput;
        RetryInputOutput();
    }

    DeviceManager(SettingsStore store, bool includeTestDevices)
    {
        _includeTestDevices = includeTestDevices;
        _store = store;
        _macros = store.LoadMacros();
        _shortcuts = store.LoadShortcuts();
    }

    // Sessions write to the same virtual keyboard and share modifier ownership.
    static IInputSink ShareModifiers(IInputSink output) => output is SharedModifierSink ? output : new SharedModifierSink(output);

    internal bool RetryInputOutput()
    {
        lock (_refreshGate)
        {
            lock (_gate)
            {
                if (_disposed)
                    return false;
                if (_output != null)
                    return true;
            }

            IInputSink output;
            try
            {
                output = _createOutput!();
            }
            catch (Exception e) when (e is IOException or UnauthorizedAccessException)
            {
                const string help = "Virtual input (/dev/uinput) is unavailable. Load the uinput module and check the Viper udev rule.";
                lock (_gate)
                    _outputIssue = help + " " + e.Message;
                Console.Error.WriteLine(_outputIssue);
                return false;
            }

            string[] keys;
            lock (_gate)
            {
                _ownedOutput = output as IDisposable;
                _output = ShareModifiers(output);
                _outputIssue = null;
                keys = _devices.Values.Where(d => !d.Suppressed).Select(d => d.Key).Distinct().ToArray();
            }
            foreach (var key in keys)
                Apply(key);
            if (keys.Length > 0)
                DevicesChanged?.Invoke();
            return true;
        }
    }

    sealed record NagaDpiContext(RemapEngine Engine, DeviceControl Control, Guid Profile, PerformanceSettings Performance);

    sealed class Connected(string port, ushort productId, string name)
    {
        public string Port { get; } = port;
        public ushort ProductId { get; } = productId;
        public string Name { get; } = name;
        public ushort Bus { get; init; } = HidrawNode.UsbBus;
        public bool IsBasiliskUsb => Bus == HidrawNode.UsbBus &&
            ProductId is DeviceCatalogue.BasiliskV4ProWired or DeviceCatalogue.BasiliskV4ProReceiver;
        public bool IsBluetooth => Bus == HidrawNode.BluetoothBus;
        public bool IsBasiliskBluetooth => IsBluetooth && ProductId == DeviceCatalogue.BasiliskV4ProBluetooth;
        public bool BluetoothBasiliskId => IsBluetooth && ProductId is DeviceCatalogue.BasiliskV4ProWired or
            DeviceCatalogue.BasiliskV4ProReceiver or DeviceCatalogue.BasiliskV4ProBluetooth;
        public string ProductKey => ProductId.ToString("x4", CultureInfo.InvariantCulture);
        public string Key => IsBasiliskUsb || IsBasiliskBluetooth ? BasiliskSettingsKey : ProductKey;
        public DeviceModel? Model => DeviceCatalogue.Default.Find(ProductId, IsBluetooth);
        public string? Connection => IsBasiliskBluetooth ? "bluetooth" : IsBasiliskUsb
            ? ProductId == DeviceCatalogue.BasiliskV4ProWired ? "wired" : "hyperspeed"
            : null;
        public string? ControlNode { get; set; }
        public HidrawNode? ControlCandidate { get; set; }
        public long NextControlProbe { get; set; }
        public DeviceIdentity? Identity { get; set; }
        public string? StableSerial { get; set; }
        public byte? OnboardProfile { get; set; }
        public RazerTransport? ControlTransport { get; set; }
        public bool Suppressed { get; set; }
        public List<string> InputNodes { get; set; } = [];
        public IReadOnlyList<ushort> EvdevControls { get; set; } = [];
        public bool HorizontalHighResOnly { get; set; }
        public string? NativeMacroPath { get; set; }
        public string? NativeMacroProblem { get; set; }
        public IReadOnlyList<NagaReportNode> NativeNagaNodes { get; set; } = [];
        public bool NagaHasHorizontalWheel => Bus == HidrawNode.UsbBus && ProductId == LegacyRemapControls.NagaChroma &&
            EvdevControls.Contains(SourceKeys.WheelLeft) && EvdevControls.Contains(SourceKeys.WheelRight);
        public Lock DpiQueueGate { get; } = new();
        public Task DpiQueue { get; set; } = Task.CompletedTask;
        public NagaDpiContext? DpiContext { get; set; }
        public long ManagedDpiRevision { get; set; }
        public bool CanReadNativeMacros => NativeMacroPath != null && Control != null && InputControlsStatus == "available";
        public bool CanReadNativeNagaTilt => NativeNagaNodes.Count > 0 && InputControlsStatus == "available";
        public bool CanReadNativeNagaDpi => CanReadNativeNagaTilt && NativeNagaNodes.Any(node => node.Kind == NagaReportKind.Dpi);
        public bool CanReadNagaDpi => Bus == HidrawNode.UsbBus && ProductId == LegacyRemapControls.NagaChroma &&
            InputControlsStatus == "available" && (CanReadNativeNagaDpi ||
                EvdevControls.Contains(LegacyRemapControls.NagaDpiUp) && EvdevControls.Contains(LegacyRemapControls.NagaDpiDown));
        public IReadOnlyList<ushort> InputControls
        {
            get
            {
                if (CanReadNativeMacros && NativeMacroProblem == null)
                    return EvdevControls.Union(Enumerable.Range(LegacyRemapControls.FirstMacroSource, 5)
                        .Select(source => (ushort)source)).Order().ToArray();
                if (!CanReadNativeNagaTilt && !NagaHasHorizontalWheel)
                    return EvdevControls;
                IEnumerable<ushort> controls = EvdevControls.Union([LegacyRemapControls.NagaTiltLeft, LegacyRemapControls.NagaTiltRight]);
                if (CanReadNativeNagaDpi)
                    controls = controls.Union([LegacyRemapControls.NagaDpiUp, LegacyRemapControls.NagaDpiDown]);
                return controls.Order().ToArray();
            }
        }
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
        public HidrawNode? ButtonsCandidate { get; set; }
        public bool ButtonsStarting { get; set; }
        public ThumbstickMode SessionStick { get; set; }
        public StickWatch StickWatch { get; } = new();
        public string? Problem { get; set; }
        public string? ControlProblem { get; set; }
        public string? InputProblem { get; set; }
        public string? ButtonsProblem { get; set; }
        public string? CleanupProblem { get; set; }

        public string? Problems
        {
            get
            {
                var problems = new[] { Problem, ControlProblem, Control?.SoftwareLightingProblem, ButtonsProblem, InputProblem, NativeMacroProblem, CleanupProblem }
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
                return _devices.Values.Where(d => !d.Suppressed)
                    .OrderBy(d => d.Key == BasiliskSettingsKey && d.Control == null)
                    .ThenBy(d => d.Port, StringComparer.Ordinal).Select(Describe).ToList();
        }
    }

    public string DevicesJson() => System.Text.Json.JsonSerializer.Serialize(Devices.ToList(), DeviceInfoJson.Default.ListDeviceInfo);

    DeviceInfo Describe(Connected d) => new(
        d.Key,
        d.ProductKey,
        d.Model?.Name ?? d.Name,
        d.Model?.Type ?? "unknown",
        d.Port,
        d.Model != null,
        !d.IsBluetooth && Verified.Contains(d.ProductId),
        d.Identity?.Serial,
        d.Identity?.Firmware,
        d.ControlNode,
        d.InputNodes,
        d.Session != null,
        (d.Problems, _outputIssue) switch
        {
            ({ } problems, { } outputIssue) => problems + " " + outputIssue,
            var (problems, outputIssue) => problems ?? outputIssue,
        },
        d.InputControls,
        d.InputControlsStatus,
        d.Connection,
        d.OnboardProfile,
        d.CanReadNativeMacros && d.NativeMacroProblem == null,
        d.Bus switch { HidrawNode.UsbBus => "usb", HidrawNode.BluetoothBus => "bluetooth", _ => null },
        d.Control != null,
        d.Control?.SoftwareLightingProblem ?? d.Problem,
        _outputIssue ?? d.InputProblem ?? d.NativeMacroProblem,
        _output != null,
        d.ManagedDpiRevision,
        !d.InputFailed && d.Session != null && d.DpiContext is { } dpi && d.Engine == dpi.Engine && d.Control == dpi.Control,
        d.NagaHasHorizontalWheel && !d.CanReadNativeNagaTilt && (!d.EvdevControls.Contains(LegacyRemapControls.NagaTiltLeft) ||
            !d.EvdevControls.Contains(LegacyRemapControls.NagaTiltRight)),
        d.NagaHasHorizontalWheel && !d.CanReadNativeNagaTilt);

    /// <summary>Looks at what is plugged in now: starts new devices, drops the ones that went away.</summary>
    public void Refresh() => Refresh(forceControlRetry: false);

    internal void Refresh(bool forceControlRetry)
    {
        lock (_refreshGate)
        {
            if (!_disposed)
                RefreshCore(HidrawNode.Enumerate(), InputNode.Enumerate(), node => RazerProbe.HasControlReport(node),
                    OpenControl, null, forceControlRetry);
        }
    }

    internal void Refresh(IEnumerable<HidrawNode> hidraw, IEnumerable<InputNode> inputs,
        Func<HidrawNode, bool> hasControlReport, Func<string, RazerTransport> open,
        Func<IEnumerable<string>, DiscoveredControls>? readControls = null, bool forceControlRetry = false)
    {
        lock (_refreshGate)
        {
            if (!_disposed)
                RefreshCore(hidraw, inputs, hasControlReport, open, readControls, forceControlRetry);
        }
    }

    static RazerTransport OpenControl(string path) => new(HidrawDevice.Open(path));

    internal void RefreshReceivers() =>
        RefreshReceivers(() => HidrawNode.Enumerate(), node => RazerProbe.HasControlReport(node), OpenControl);

    internal void RefreshReceivers(Func<IReadOnlyList<HidrawNode>> enumerate,
        Func<HidrawNode, bool> hasControlReport, Func<string, RazerTransport> open)
    {
        lock (_refreshGate)
        {
            if (_disposed)
                return;
            Connected[] receivers;
            lock (_gate)
                receivers = _devices.Values.Where(d => d.IsBasiliskUsb &&
                    d.ProductId == DeviceCatalogue.BasiliskV4ProReceiver && !d.Suppressed).ToArray();
            if (receivers.Length == 0)
                return;
            var nodes = enumerate();
            bool changed = false;
            foreach (var receiver in receivers)
                changed |= ReconcileControl(receiver, nodes, hasControlReport, open, [], forceRetry: false);
            if (!changed)
                return;
            lock (_gate)
                SelectBasiliskEndpoints();
            Apply(BasiliskSettingsKey, refreshing: true);
            DevicesChanged?.Invoke();
        }
    }

    void RefreshCore(IEnumerable<HidrawNode> rawNodes, IEnumerable<InputNode> inputNodes,
        Func<HidrawNode, bool> hasControlReport, Func<string, RazerTransport> open,
        Func<IEnumerable<string>, DiscoveredControls>? readControls, bool forceControlRetry)
    {
        var hidraw = rawNodes.Where(n => n.VendorId == RazerVendor && n.PortPath.Length > 0 && Real(n.PortPath)).ToList();
        // Virtual devices (ours included) have no physical port; only real hardware is a device.
        var inputs = inputNodes.Where(n => n.VendorId == RazerVendor && n.PortPath.Length > 0 && Real(n.PortPath)).ToList();
        var ports = hidraw.Select(n => (n.PortPath, n.ProductId, n.Name, n.Bus))
            .Concat(inputs.Select(n => (n.PortPath, n.ProductId, n.Name, n.Bus)))
            .GroupBy(p => p.PortPath)
            .ToDictionary(g => g.Key, g => g.First());
        var macroNodes = hidraw
            .Where(n => n.Bus == HidrawNode.UsbBus && n.ProductId == LegacyRemapControls.BlackWidowChromaV2 && n.Interface == 1 &&
                IsNativeMacroNode(n))
            .GroupBy(n => n.PortPath)
            .Where(group => group.Count() == 1)
            .ToDictionary(group => group.Key, group => group.Single().DevicePath);
        var nagaNodes = hidraw
            .Where(n => n.Bus == HidrawNode.UsbBus && n.ProductId == LegacyRemapControls.NagaChroma)
            .Select(n => (n.PortPath, Report: FindNativeNagaNode(n)))
            .Where(n => n.Report != null)
            .GroupBy(n => n.PortPath)
            .ToDictionary(group => group.Key, group => group.Select(n => n.Report!).GroupBy(n => n.Kind)
                .Where(reports => reports.Count() == 1).OrderBy(reports => reports.Key).Select(reports => reports.Single()).ToArray());

        bool changed = false;
        HashSet<string> affectedModels = [];
        lock (_gate)
        {
            foreach (var gone in _devices.Keys.Where(port => !ports.TryGetValue(port, out var found) ||
                found.ProductId != _devices[port].ProductId || found.Bus != _devices[port].Bus).ToList())
            {
                affectedModels.Add(_devices[gone].Key);
                StopSession(_devices[gone]);
                _devices[gone].Control?.Dispose();
                _devices[gone].Buttons?.Dispose();
                _devices[gone].StickWatch.Dispose();
                _devices.Remove(gone);
                changed = true;
            }
            foreach (var (port, (_, productId, name, bus)) in ports)
            {
                if (!_devices.TryGetValue(port, out var device))
                {
                    device = new Connected(port, productId, name) { Bus = bus };
                    _devices[port] = device;
                    affectedModels.Add(device.Key);
                    changed = true;
                }
                var nodes = inputs.Where(n => n.PortPath == port &&
                    (!(device.IsBasiliskUsb || device.IsBasiliskBluetooth || productId == LegacyRemapControls.NagaChroma) ||
                        n.ProductId == productId && n.Bus == bus))
                    .Select(n => n.DevicePath).ToList();
                string? macroPath = productId == LegacyRemapControls.BlackWidowChromaV2 && bus == HidrawNode.UsbBus
                    ? macroNodes.GetValueOrDefault(port) : null;
                IReadOnlyList<NagaReportNode> nagaReports = productId == LegacyRemapControls.NagaChroma && bus == HidrawNode.UsbBus
                    ? nagaNodes.GetValueOrDefault(port) ?? [] : [];
                if (!nodes.SequenceEqual(device.InputNodes) || device.NativeMacroPath != macroPath ||
                    !nagaReports.SequenceEqual(device.NativeNagaNodes))
                {
                    if (!nodes.SequenceEqual(device.InputNodes))
                        device.StickWatch.RetireReader();
                    device.InputNodes = nodes;
                    device.NativeMacroPath = macroPath;
                    device.NativeNagaNodes = nagaReports;
                    // The device's nodes changed under a running session (a re-plug): start it again.
                    StopSession(device);
                    device.InputFailed = false;
                    device.NativeMacroProblem = null;
                    affectedModels.Add(device.Key);
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
            var found = readControls?.Invoke(nodes) ?? InputControlDiscovery.Read(nodes,
                device.Bus == HidrawNode.UsbBus ? device.ProductId : null);
            lock (_gate)
            {
                if (!_devices.TryGetValue(device.Port, out var current) || current != device || !nodes.SequenceEqual(device.InputNodes))
                    continue;
                if (device.InputControlsStatus != found.Status || device.HorizontalHighResOnly != found.HorizontalHighResOnly ||
                    !device.EvdevControls.SequenceEqual(found.Controls))
                {
                    // A permission recovery is a meaningful reason to allow a retry.
                    if (found.Status == "available" && device.InputControlsStatus != "available")
                        device.InputFailed = false;
                    device.EvdevControls = found.Controls;
                    device.InputControlsStatus = found.Status;
                    device.HorizontalHighResOnly = found.HorizontalHighResOnly;
                    affectedModels.Add(device.Key);
                    changed = true;
                }
            }
        }

        var adopt = new List<(string Key, int Keymap)>();
        // Probing talks to the hardware; keep it out of the lock.
        List<Connected> probes;
        lock (_gate)
            probes = _devices.Values.Where(d => !d.BluetoothBasiliskId).ToList();
        foreach (var device in probes)
        {
            lock (_gate)
            {
                if (forceControlRetry && device.NativeMacroProblem != null)
                {
                    device.NativeMacroProblem = null;
                    affectedModels.Add(device.Key);
                    changed = true;
                }
            }
            if (ReconcileControl(device, hidraw, hasControlReport, open, adopt, forceControlRetry))
            {
                affectedModels.Add(device.Key);
                changed = true;
            }
        }

        lock (_gate)
        {
            if (SelectBasiliskEndpoints())
            {
                affectedModels.Add(BasiliskSettingsKey);
                changed = true;
            }
        }

        foreach (var device in probes.Where(d => d.Model?.Has("keypad_buttons") == true))
            changed |= ReconcileButtons(device, hidraw.FirstOrDefault(n => n.PortPath == device.Port && n.ProductId == device.ProductId &&
                n.Bus == device.Bus && n.Interface == 1), forceControlRetry);
        foreach (var (key, keymap) in adopt)
            FollowDeviceKeymap(key, keymap);

        foreach (var key in affectedModels)
            Apply(key, refreshing: true);
        if (!affectedModels.Contains(BasiliskSettingsKey))
        {
            // Device-side slot selection can change without a new endpoint. An explicit or
            // udev refresh observes it without replaying settings across healthy units.
            foreach (var device in probes.Where(d => d.IsBasiliskUsb && !d.Suppressed))
                changed |= ObserveOnboardSlot(device);
        }
        if (changed)
            DevicesChanged?.Invoke();
    }

    bool ObserveOnboardSlot(Connected device)
    {
        if (device.Control is not { } control)
            return false;
        byte? slot = control.ObserveOnboardProfile();
        lock (_gate)
        {
            if (device.Control != control || device.OnboardProfile == slot)
                return false;
            device.OnboardProfile = slot;
            if (slot is byte active && device.StableSerial is { } serial)
                _basiliskSlots[serial] = active;
            return true;
        }
    }

    bool ReconcileControl(Connected device, IEnumerable<HidrawNode> nodes, Func<HidrawNode, bool> hasControlReport,
        Func<string, RazerTransport> open, List<(string Key, int Keymap)> adopt, bool forceRetry)
    {
        var candidate = nodes.FirstOrDefault(n => n.PortPath == device.Port && n.ProductId == device.ProductId &&
            n.Bus == device.Bus && (!device.IsBasiliskUsb || n.Interface == 3) && hasControlReport(n));
        bool replaced = candidate != device.ControlCandidate;
        if (!replaced)
        {
            if (candidate == null)
                return false;
            if (device.ControlTransport is { } transport)
            {
                if (!device.IsBasiliskUsb || StillResponding(transport, device.Identity!))
                    return false;
            }
            else if (!forceRetry && !device.IsBasiliskUsb && Environment.TickCount64 < device.NextControlProbe)
                return false;
        }

        DeviceControl? retired;
        lock (_gate)
        {
            if (device.NativeMacroPath != null && replaced)
            {
                StopSession(device);
                device.InputFailed = false;
                device.NativeMacroProblem = null;
            }
            retired = device.Control;
            device.Control = null;
            device.ControlTransport = null;
        }
        retired?.Dispose();
        return ProbeControl(device, candidate, open, adopt, replaced);
    }

    bool ProbeControl(Connected device, HidrawNode? candidate, Func<string, RazerTransport> open,
        List<(string Key, int Keymap)> adopt, bool replaced)
    {
        var path = candidate?.DevicePath;
        RazerTransport? transport = null;
        DeviceIdentity? identity = null;
        DeviceControl? control = null;
        string? problem = null;
        if (path != null)
        {
            try
            {
                transport = open(path);
                identity = RazerProbe.Identify(transport, strict: device.IsBasiliskUsb);
                if (identity == null)
                    problem = "The device did not answer on its control interface.";
                else
                {
                    byte? previousSlot;
                    lock (_gate)
                        previousSlot = device.IsBasiliskUsb && _basiliskSlots.TryGetValue(identity.Serial, out byte slot) ? slot : null;
                    control = new DeviceControl(transport, identity.TransactionId, device.Model, previousSlot);
                    var endpoint = control;
                    control.SoftwareLightingProblemChanged += () => ThreadPool.QueueUserWorkItem(_ =>
                    {
                        lock (_gate)
                        {
                            if (!_devices.TryGetValue(device.Port, out var current) || current != device ||
                                !ReferenceEquals(device.Control, endpoint) || device.Suppressed)
                                return;
                        }
                        DevicesChanged?.Invoke();
                    });
                }
            }
            catch (Exception e) when (e is IOException or UnauthorizedAccessException)
            {
                problem = e is UnauthorizedAccessException
                    ? "No permission to talk to the device: install the Viper Synapse udev rule."
                    : e.Message;
            }
        }
        if (control == null)
            transport?.Dispose();
        if (!device.IsBasiliskUsb && control?.ReadKeymap() is int keymap and >= 0)
            adopt.Add((device.Key, keymap));
        lock (_gate)
        {
            bool changed = replaced || device.Identity != identity || device.Problem != problem ||
                device.ControlProblem != null || device.OnboardProfile != null;
            // Keep an already-proven pair associated when a node stops responding. A new serial or
            // a replaced node invalidates that match.
            if (identity != null || replaced)
                device.StableSerial = identity?.Serial;
            device.ControlCandidate = candidate;
            device.NextControlProbe = Environment.TickCount64 + 2_000;
            device.ControlNode = path;
            device.Identity = identity;
            device.Control = control;
            device.ControlTransport = control == null ? null : transport;
            device.Problem = problem;
            device.ControlProblem = null;
            device.OnboardProfile = null;
            return changed;
        }
    }

    static bool StillResponding(RazerTransport transport, DeviceIdentity identity)
    {
        try
        {
            var request = Commands.Request(identity.TransactionId, Commands.GetSerial);
            var reply = transport.Send(request);
            return RazerProbe.Accepts(request, reply, strict: true) &&
                Encoding.ASCII.GetString(reply.Arguments[..Math.Min((int)reply.DataSize, 22)])
                    .TrimEnd('\0', ' ') == identity.Serial;
        }
        catch (IOException)
        {
            return false;
        }
    }

    bool SelectBasiliskEndpoints()
    {
        var candidates = _devices.Values
            .Where(d => d.IsBasiliskUsb && d.StableSerial is { Length: > 0 } serial &&
                !string.IsNullOrWhiteSpace(serial) && !serial.All(c => c == '0'))
            .GroupBy(d => d.StableSerial!, StringComparer.Ordinal)
            .Where(group => group.Count() == 2);
        var inactiveEndpoints = new HashSet<Connected>();
        foreach (var pair in candidates)
        {
            var wired = pair.FirstOrDefault(d => d.ProductId == DeviceCatalogue.BasiliskV4ProWired);
            var receiver = pair.FirstOrDefault(d => d.ProductId == DeviceCatalogue.BasiliskV4ProReceiver);
            if (wired == null || receiver == null)
                continue;
            bool preferWired = wired.Control != null || receiver.Control == null && wired.InputNodes.Count > 0;
            inactiveEndpoints.Add(preferWired ? receiver : wired);
        }
        bool changed = false;
        foreach (var device in _devices.Values.Where(d => d.IsBasiliskUsb))
        {
            bool suppressed = inactiveEndpoints.Contains(device);
            if (device.Suppressed != suppressed)
            {
                device.Suppressed = suppressed;
                StopSession(device);
                device.InputFailed = false;
                changed = true;
            }
            if (suppressed && device.Control != null)
            {
                device.Control.Dispose();
                device.Control = null;
                device.ControlTransport = null;
            }
        }
        return changed;
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
        if (key != BasiliskSettingsKey)
            return ReadStateCore(key);
        lock (_refreshGate)
            return ReadStateCore(key);
    }

    DeviceState? ReadStateCore(string key)
    {
        DeviceControl? control;
        lock (_gate)
            control = _devices.Values.Where(d => d.Key == key && !d.Suppressed && d.Control != null)
                .OrderBy(d => d.Port, StringComparer.Ordinal).FirstOrDefault()?.Control;
        return control?.ReadState();
    }

    internal DeviceState? ReadUnitState(string key, string port, string productId, string serial)
    {
        lock (_refreshGate)
        {
            DeviceControl? control;
            lock (_gate)
            {
                control = _devices.TryGetValue(port, out var device) && !device.Suppressed &&
                    device.Key == key && device.ProductKey == productId && (device.Identity?.Serial ?? "") == serial
                    ? device.Control : null;
            }
            return control?.ReadState();
        }
    }

    internal OnboardResult ManageOnboard(OnboardRequest request) =>
        WithOnboardDevice(request.Operation is OnboardOperation.List or OnboardOperation.ReadBinding
            ? request.Serial : RequireOnboardSerial(request.Serial), (control, serial) =>
        {
            var result = control.ManageOnboard(serial, request);
            bool namesChanged = request.Operation is OnboardOperation.Rename or OnboardOperation.Delete or OnboardOperation.Create;
            var names = _store.OnboardNames(serial, result.Slots, namesChanged ? request.Slot : null, request.Name?.Trim());
            return result with { Names = names };
        });

    internal OnboardBackup BackupOnboard(OnboardArchiveRequest request)
    {
        if (request.Backup != null)
            throw new InvalidDataException("A backup request must not include an archive.");
        return WithOnboardDevice(RequireOnboardSerial(request.Serial), (control, serial) => control.BackupOnboard(serial, request.Slot));
    }

    internal OnboardRestoreResult RestoreOnboard(OnboardArchiveRequest request)
    {
        var backup = request.Backup ?? throw new InvalidDataException("Select a valid onboard backup to restore.");
        return WithOnboardDevice(RequireOnboardSerial(request.Serial),
            (control, serial) => control.RestoreOnboard(serial, request.Slot, backup));
    }

    static string RequireOnboardSerial(string? serial) =>
        serial is { } known && ValidOnboardSerial(known) ? known : throw new InvalidDataException("Select a mouse by serial.");

    T WithOnboardDevice<T>(string? requestedSerial, Func<DeviceControl, string, T> execute)
    {
        if (requestedSerial != null && !ValidOnboardSerial(requestedSerial))
            throw new InvalidDataException("Invalid mouse serial.");

        bool changed = false;
        try
        {
            lock (_refreshGate)
            {
                Connected[] candidates;
                lock (_gate)
                    candidates = _devices.Values.Where(device => device.IsBasiliskUsb && !device.Suppressed &&
                        device.Control != null && device.ControlTransport != null && device.Identity is { } replyIdentity &&
                        ValidOnboardSerial(replyIdentity.Serial) &&
                        (requestedSerial == null || replyIdentity.Serial == requestedSerial)).ToArray();
                if (candidates.Length != 1)
                    throw new InvalidDataException(candidates.Length == 0
                        ? "No matching Basilisk V4 Pro is available over wired USB or HyperSpeed."
                        : "More than one Basilisk V4 Pro matches. Use its unique serial.");

                var device = candidates[0];
                var identity = device.Identity!;
                var control = device.Control!;
                if (!StillResponding(device.ControlTransport!, identity))
                    throw new IOException("The selected mouse's identity could not be confirmed.");

                try
                {
                    return execute(control, identity.Serial);
                }
                finally
                {
                    byte? slot = control.OnboardProfile;
                    lock (_gate)
                    {
                        changed = slot != device.OnboardProfile;
                        device.OnboardProfile = slot;
                        if (slot is byte active)
                            _basiliskSlots[identity.Serial] = active;
                    }
                }
            }
        }
        finally
        {
            if (changed)
                DevicesChanged?.Invoke();
        }
    }

    static bool ValidOnboardSerial(string serial) =>
        !string.IsNullOrWhiteSpace(serial) && serial.Length <= 22 && !serial.Any(char.IsControl);

    public void SetSettings(string key, DeviceSettings settings) => CommitSettings(key, null, settings);

    internal SettingsSnapshot SettingsSnapshot(string key) => _store.DeviceSnapshot(key);

    internal SettingsCommitResult CommitSettings(string key, string? revision, DeviceSettings settings)
    {
        lock (ApplyGate(key))
        {
            var saved = _store.CommitDevice(key, revision, settings);
            return saved.Saved ? saved with { Units = ApplySettingsChange(key) } : saved;
        }
    }

    void UpdateSettings(string key, Func<DeviceSettings, DeviceSettings> update)
    {
        lock (ApplyGate(key))
            if (_store.UpdateDevice(key, update))
                ApplySettingsChange(key);
    }

    IReadOnlyList<UnitApplyOutcome> ApplySettingsChange(string key)
    {
        var outcomes = Apply(key, refreshing: false);
        SettingsChanged?.Invoke(key);
        return outcomes;
    }

    // Input threads hold the session lock while reading these replaced snapshots. Do not
    // take _gate here: session changes hold _gate while waiting for that same session lock.
    public MacroLibrary Macros => Volatile.Read(ref _macros);

    public void SetMacros(MacroLibrary macros) => CommitMacros(null, macros);

    internal SettingsSnapshot MacrosSnapshot() => _store.MacrosSnapshot();

    internal SettingsCommitResult CommitMacros(string? revision, MacroLibrary macros)
    {
        lock (_profileGate)
        {
            var saved = _store.CommitMacros(revision, macros);
            if (saved.Saved)
                Volatile.Write(ref _macros, macros);
            return saved;
        }
    }

    internal string ExportProfile(string key, Guid? id)
    {
        lock (_profileGate)
        {
            var settings = GetSettings(key);
            return PortableProfile.Export(key, settings, Macros, id ?? settings.Active.Id).Write();
        }
    }

    internal void ImportProfile(string key, PortableProfile archive, bool replace, bool allowLaunch)
    {
        lock (ApplyGate(key))
        {
            lock (_profileGate)
                Volatile.Write(ref _macros, _store.ImportProfile(key, archive, replace, allowLaunch));
            ApplySettingsChange(key);
        }
    }

    public ShortcutList Shortcuts => Volatile.Read(ref _shortcuts);

    /// <summary>New shortcuts can mean a device must now be grabbed (or need not be any more): every model is
    /// applied again.</summary>
    public void SetShortcuts(ShortcutList shortcuts) => CommitShortcuts(null, shortcuts);

    internal SettingsSnapshot ShortcutsSnapshot() => _store.ShortcutsSnapshot();

    internal SettingsCommitResult CommitShortcuts(string? revision, ShortcutList shortcuts)
    {
        SettingsCommitResult saved;
        lock (_shortcutGate)
        {
            saved = _store.CommitShortcuts(revision, shortcuts);
            if (!saved.Saved)
                return saved;
            Volatile.Write(ref _shortcuts, shortcuts);
        }
        List<string> keys;
        lock (_gate)
            keys = _devices.Values.Select(d => d.Key).Distinct().ToList();
        foreach (var key in keys)
            Apply(key);
        return saved;
    }

    /// <summary>Brings every connected device of a model in line with its stored settings.</summary>
    public void Apply(string key) => Apply(key, refreshing: false);

    Lock ApplyGate(string key) => key == BasiliskSettingsKey ? _refreshGate : _applyGates.GetOrAdd(key, static _ => new());

    IReadOnlyList<UnitApplyOutcome> Apply(string key, bool refreshing)
    {
        lock (ApplyGate(key))
            return _disposed ? [] : ApplyCore(key, refreshing);
    }

    IReadOnlyList<UnitApplyOutcome> ApplyCore(string key, bool refreshing)
    {
        var settings = _store.LoadDevice(key);
        var keymap = settings.ActiveKeymapOrFirst;
        var profile = settings.Active;
        bool bindsAnything = keymap.Standard.Values.Concat(keymap.Hypershift.Values).Any(b => b is not DefaultBinding);
        bool changed = false;
        lock (_gate)
        {
            var output = _output;
            foreach (var device in _devices.Values.Where(d => d.Key == key && !d.Suppressed))
            {
                if (output == null)
                    continue;
                // A failed reader must not be grabbed again on unrelated filesystem scans. Explicit
                // settings changes, changed nodes or recovered permissions permit another attempt.
                if (refreshing && device.InputFailed)
                    continue;
                if (!refreshing)
                {
                    device.InputFailed = false;
                    if (device.NativeMacroProblem != null)
                    {
                        device.NativeMacroProblem = null;
                        changed = true;
                    }
                }
                // A thumbstick in 4-way or 8-way mode types its directions even with nothing bound.
                var stick = device.Model?.Has("thumbstick") == true ? profile.Thumbstick : ThumbstickMode.Analog;
                var inputControls = device.InputControls;
                // A global shortcut made of this device's keys needs its input too.
                bool shortcut = _shortcuts.Shortcuts.Any(s => ShortcutMatchesControls(device.Model, inputControls, device.InputControlsStatus, s.Keys));
                if (refreshing && device.Session != null && _shortcuts.Shortcuts.Count > 0 &&
                    (device.Model?.Keys.Count is null or 0) && device.InputControlsStatus != "available")
                    shortcut = true;
                // A faster wheel is the engine's work too.
                int scrollLines = Math.Clamp(profile.Scroll?.LinesPerNotch ?? 1, 1, Core.Lighting.ScrollSettings.MaxLinesPerNotch);
                bool bindingsAvailable = bindsAnything;
                if (device.IsBasiliskBluetooth || device.Bus == HidrawNode.UsbBus && LegacyRemapControls.SupportsProduct(device.ProductId))
                {
                    bindingsAvailable = device.InputControlsStatus == "available" &&
                        keymap.Standard.Concat(keymap.Hypershift)
                            .Any(b => b.Value is not DefaultBinding && inputControls.Contains(b.Key));
                    if (!inputControls.Contains(SourceKeys.WheelUp) && !inputControls.Contains(SourceKeys.WheelDown))
                        scrollLines = 1;
                }
                bool managedDpi = device.CanReadNagaDpi && device.Control != null && profile.Performance is { ManagedDpiSteps: true };
                if (!managedDpi && !bindingsAvailable && !shortcut && scrollLines <= 1 && stick == ThumbstickMode.Analog)
                {
                    if (device.Session != null)
                    {
                        StopSession(device);
                        changed = true;
                    }
                    continue;
                }
                bool UsesControl(Func<ushort, bool> matches) => keymap.Standard.Concat(keymap.Hypershift)
                    .Any(binding => matches(binding.Key) && binding.Value is not DefaultBinding) ||
                    _shortcuts.Shortcuts.Any(s => s.Keys.Any(matches) &&
                        ShortcutMatchesControls(device.Model, inputControls, device.InputControlsStatus, s.Keys));
                bool nativeMacros = device.CanReadNativeMacros && device.NativeMacroProblem == null && UsesControl(LegacyRemapControls.IsMacroSource);
                bool nativeNagaTilt = device.CanReadNativeNagaTilt && UsesControl(LegacyRemapControls.IsNagaTiltSource);
                bool nativeNagaDpi = device.CanReadNativeNagaDpi && (managedDpi || UsesControl(LegacyRemapControls.IsNagaDpiSource));
                if (device.Session is { } session && device.SessionStick == stick && session.HasNativeMacroKeys == nativeMacros &&
                    session.HasNativeNagaTilt == nativeNagaTilt && session.HasNativeNagaDpi == nativeNagaDpi)
                {
                    try
                    {
                        session.WithEngine(engine =>
                        {
                            try { UpdateSession(session, keymap, profile.Interruption, scrollLines, refreshing); }
                            finally { ConfigureNagaInput(engine, device, profile, managedDpi, nativeNagaTilt); }
                        });
                        if (!refreshing && device.InputProblem?.StartsWith("Could not update the device's keys: ", StringComparison.Ordinal) == true)
                        {
                            bool recovered = false;
                            session.WithEngine(engine => recovered = engine.TryConfirmOutputRecovery());
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
                device.Engine = new RemapEngine(output, new Host(this, key), InputTimeProvider)
                {
                    Keymap = keymap,
                    Defaults = source => DefaultFor(model, source),
                    Interruption = profile.Interruption,
                    ScrollLines = scrollLines,
                };
                ConfigureNagaInput(device.Engine, device, profile, managedDpi, nativeNagaTilt);
                try
                {
                    device.Session = StartInputSession(device.InputNodes, device.Engine, stick, (failed, path, error) =>
                        ThreadPool.QueueUserWorkItem(_ =>
                        {
                            if (!RetireFailedSession(device, failed, path, error))
                                return;
                            DevicesChanged?.Invoke();
                        }));
                    var watched = device;
                    if (nativeMacros)
                    {
                        try { StartNativeMacroSession(device); }
                        catch (Exception error) when (error is IOException or UnauthorizedAccessException or ObjectDisposedException or ThreadStateException)
                        {
                            device.NativeMacroProblem = $"Could not start native macro keys: {error.Message}";
                            try { device.Session.DetachNativeMacroKeys(); }
                            catch (Exception cleanup) when (cleanup is IOException or UnauthorizedAccessException or ObjectDisposedException or ThreadStateException)
                            {
                                device.NativeMacroProblem += $" Could not clean up native macro mode: {cleanup.Message}";
                            }
                        }
                    }
                    if (nativeNagaTilt || nativeNagaDpi)
                        StartNativeNagaSession(device, nativeNagaTilt, nativeNagaDpi);
                    device.Session.StickMoved += (x, y) => SendStick(watched, x, y);
                    device.Session.KeyPressed += source => watched.Control?.Press(source);
                    // The session now holds the stick; an analog-mode monitor would read nothing.
                    if (stick != ThumbstickMode.Analog)
                        device.StickWatch.RetireReader();
                    device.SessionStick = stick;
                    if (IsInputSessionProblem(device.InputProblem))
                        device.InputProblem = null;
                }
                catch (Exception e) when (e is IOException or UnauthorizedAccessException or ThreadStateException)
                {
                    StopSession(device);
                    device.InputProblem = "Could not take over the device's keys: " + e.Message;
                    if (nativeMacros || nativeNagaTilt || nativeNagaDpi)
                        device.InputFailed = true;
                }
                changed = true;
            }
        }
        // Lighting, sensitivity and power talk to the hardware: outside the lock.
        List<(Connected Device, DeviceControl? Control, string? Serial)> controls;
        lock (_gate)
            controls = _devices.Values.Where(d => d.Key == key && !d.Suppressed)
                .Select(d => (d, d.Control, d.Identity?.Serial)).ToList();
        var outcomes = new List<UnitApplyOutcome>(controls.Count);
        foreach (var (device, control, serial) in controls)
        {
            if (control == null)
            {
                outcomes.Add(new(device.Port, device.ProductKey, serial, UnitApplyStatus.Unavailable,
                    device.Problem ?? "Firmware control is unavailable on this connection."));
                continue;
            }
            var outcome = control.ApplyWithResult(device.Port, device.ProductKey, serial, settings.Active, settings.ActiveKeymap, refreshing);
            changed |= PublishApplyOutcome(device, control, outcome);
            outcomes.Add(outcome);
        }
        if (changed)
            DevicesChanged?.Invoke();
        return outcomes;
    }

    bool PublishApplyOutcome(Connected device, DeviceControl control, UnitApplyOutcome outcome)
    {
        if (outcome.Detail != null)
            Console.Error.WriteLine($"{device.Key}: {outcome.Detail}");
        var slot = device.IsBasiliskUsb ? control.OnboardProfile : null;
        lock (_gate)
        {
            if (device.Control != control || device.ControlProblem == outcome.Detail && device.OnboardProfile == slot &&
                device.ManagedDpiRevision == control.ManagedDpiRevision)
                return false;
            device.ControlProblem = outcome.Detail;
            device.OnboardProfile = slot;
            device.ManagedDpiRevision = control.ManagedDpiRevision;
            if (slot is byte active && device.StableSerial is { } serial)
                _basiliskSlots[serial] = active;
            return true;
        }
    }

    void ConfigureNagaInput(RemapEngine engine, Connected device, Profile profile, bool managedDpi, bool nativeTilt)
    {
        if (device.Bus != HidrawNode.UsbBus || device.ProductId != LegacyRemapControls.NagaChroma)
            return;
        engine.NativeNagaTilt = nativeTilt;
        engine.NagaTiltReportsHorizontalWheel = device.NagaHasHorizontalWheel;
        engine.NagaHorizontalHighResOnly = device.HorizontalHighResOnly;
        engine.EvdevNagaTiltLeft = device.EvdevControls.Contains(LegacyRemapControls.NagaTiltLeft);
        engine.EvdevNagaTiltRight = device.EvdevControls.Contains(LegacyRemapControls.NagaTiltRight);
        var controls = device.InputControls;
        engine.NagaTiltShortcutSources = _shortcuts.Shortcuts
            .Where(shortcut => ShortcutMatchesControls(device.Model, controls, device.InputControlsStatus, shortcut.Keys))
            .SelectMany(shortcut => shortcut.Keys.Where(LegacyRemapControls.IsNagaTiltSource)).ToHashSet();
        if (!managedDpi || device.Control is not { } control || profile.Performance is not { } performance)
        {
            device.DpiContext = null;
            engine.NagaDpiStep = null;
            return;
        }
        var context = device.DpiContext;
        if (context == null || context.Engine != engine || context.Control != control || context.Profile != profile.Id ||
            !DeviceControl.SameDpiSteps(context.Performance, performance))
            device.DpiContext = context = new(engine, control, profile.Id, performance);
        engine.NagaDpiStep = source => QueueNagaDpiStep(device, context, source == LegacyRemapControls.NagaDpiUp ? 1 : -1);
    }

    void QueueNagaDpiStep(Connected device, NagaDpiContext context, int direction)
    {
        lock (device.DpiQueueGate)
        {
            var previous = device.DpiQueue;
            device.DpiQueue = Task.Run(async () =>
            {
                try { await previous.ConfigureAwait(false); }
                catch (Exception error) { Console.Error.WriteLine(error); }
                lock (ApplyGate(device.Key))
                {
                    lock (_gate)
                        if (_disposed || _devices.GetValueOrDefault(device.Port) != device || device.Engine != context.Engine ||
                            device.Control != context.Control || !ReferenceEquals(device.DpiContext, context))
                            return;
                    if (!context.Control.StepManagedDpi(context.Profile, context.Performance, direction))
                        return;
                    lock (_gate)
                    {
                        if (device.Control != context.Control)
                            return;
                        device.ManagedDpiRevision = context.Control.ManagedDpiRevision;
                    }
                }
                DevicesChanged?.Invoke();
            });
        }
    }

    internal DeviceState SelectManagedDpiStep(string key, string port, string product, string serial, Guid profile, int step, DpiStage expected)
    {
        DeviceState state;
        lock (_refreshGate)
        lock (ApplyGate(key))
        {
            Connected? device;
            lock (_gate)
                device = _devices.GetValueOrDefault(port);
            if (_disposed || device == null || device.Suppressed || device.Key != key || device.ProductKey != product ||
                (device.Identity?.Serial ?? "") != serial || device.Control is not { } control)
                throw new InvalidOperationException("The selected mouse is no longer available.");
            if (!control.SelectManagedDpi(profile, step, expected))
                throw new InvalidOperationException("The DPI profile changed. Review the steps before selecting one again.");
            lock (_gate)
                device.ManagedDpiRevision = control.ManagedDpiRevision;
            state = control.ReadState();
        }
        DevicesChanged?.Invoke();
        return state;
    }

    internal UnitApplyOutcome RetryUnitApply(string key, string port, string productId, string serial)
    {
        lock (_refreshGate)
        lock (ApplyGate(key))
        {
            Connected? device;
            lock (_gate)
                device = _devices.GetValueOrDefault(port);
            if (_disposed || device == null || device.Suppressed || device.Key != key || device.ProductKey != productId ||
                device.Identity?.Serial != serial)
                return new(port, productId, serial, UnitApplyStatus.Disconnected, "The original device is no longer connected.");
            if (device.Control is not { } control || device.ControlTransport is not { } transport)
                return new(port, productId, serial, UnitApplyStatus.Unavailable, "Firmware control is unavailable.");
            if (string.IsNullOrWhiteSpace(serial) || !StillResponding(transport, device.Identity!))
                return new(port, productId, serial, UnitApplyStatus.Unavailable, "The original device's identity could not be verified.");
            var settings = _store.LoadDevice(key);
            var outcome = control.ApplyWithResult(port, productId, serial, settings.Active, settings.ActiveKeymap, refreshing: false);
            if (PublishApplyOutcome(device, control, outcome))
                DevicesChanged?.Invoke();
            return outcome;
        }
    }

    void StartNativeMacroSession(Connected device)
    {
        var session = device.Session!;
        string path = device.NativeMacroPath!;
        var reader = OpenNativeMacroKeys(path, (source, value) => session.DispatchNativeMacroKey(path, source, value),
            error => ThreadPool.QueueUserWorkItem(_ =>
            {
                if (RetireFailedSession(device, session, path, error, nativeMacro: true))
                    DevicesChanged?.Invoke();
            }));
        IDisposable? mode = null;
        try
        {
            mode = device.Control!.EnableNativeMacroKeys();
            session.AttachNativeMacroKeys(reader, mode);
        }
        catch
        {
            try { reader.Dispose(); }
            finally { mode?.Dispose(); }
            throw;
        }
        // Reports generated by the mode change stay queued until the session owns the reader.
        reader.Start();
    }

    void StartNativeNagaSession(Connected device, bool tilt, bool dpi)
    {
        var session = device.Session!;
        foreach (var node in device.NativeNagaNodes)
        {
            bool nodeDpi = dpi && node.Kind == NagaReportKind.Dpi;
            if (!tilt && !nodeDpi)
                continue;
            var reader = OpenNativeNagaButtons(node,
                (source, value) => session.DispatchNativeNagaKey(node.Path, source, value, tilt, nodeDpi),
                error => ThreadPool.QueueUserWorkItem(_ =>
                {
                    if (RetireFailedSession(device, session, node.Path, error))
                        DevicesChanged?.Invoke();
                }));
            try { session.AttachNativeNagaReader(reader, tilt, nodeDpi); }
            catch
            {
                reader.Dispose();
                throw;
            }
            reader.Start();
        }
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

    bool RetireFailedSession(Connected device, DeviceSession failed, string path, Exception error, bool nativeMacro = false)
    {
        lock (_gate)
        {
            // A delayed notification must not stop a replacement session or a reconnected device.
            if (!_devices.TryGetValue(device.Port, out var current) || current != device || device.Session != failed ||
                nativeMacro && !failed.HasNativeMacroKeys)
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

    bool ReconcileButtons(Connected device, HidrawNode? candidate, bool forceRetry)
    {
        KeypadButtons? retired;
        lock (_gate)
        {
            if (device.ButtonsStarting || device.ButtonsCandidate == candidate &&
                (!forceRetry || candidate == null || device.Buttons != null))
                return false;
            device.ButtonsCandidate = candidate;
            retired = device.Buttons;
            device.Buttons = null;
            device.ButtonsProblem = null;
        }
        retired?.Dispose();
        device.SetPhysicalHypershift(false);
        StartButtons(device, candidate);
        return true;
    }

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
                if ((e is IOException or UnauthorizedAccessException) &&
                    _devices.TryGetValue(device.Port, out var current) && current == device)
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
            if (e is not (IOException or UnauthorizedAccessException))
                throw;
            Console.Error.WriteLine($"{device.Key}: cannot listen to the keypad's buttons: {e.Message}");
            if (notify)
                DevicesChanged?.Invoke();
        }
    }

    void HandleKeymapReport(Connected device, KeypadButtons buttons, int keymap)
    {
        lock (ApplyGate(device.Key))
        {
            bool changed;
            lock (_gate)
            {
                if (!_devices.TryGetValue(device.Port, out var current) || current != device || device.Buttons != buttons)
                    return;
                device.Control?.KeymapChangedOnDevice(keymap - 1);
                changed = _store.UpdateDevice(device.Key, settings => AdoptKeymap(settings, keymap - 1));
            }
            if (changed)
                ApplySettingsChange(device.Key);
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
        List<(Connected Device, IReadOnlyList<string> Paths, long Generation)> readers = [];
        lock (_gate)
        {
            foreach (var device in _devices.Values.Where(d => d.Key == key && d.Model?.Has("thumbstick") == true))
            {
                bool needReader = device.InputNodes.Count > 0 && (device.Session == null || device.SessionStick == ThumbstickMode.Analog);
                if (device.StickWatch.Renew(StickWatchMilliseconds, needReader) is long generation)
                    readers.Add((device, device.InputNodes, generation));
            }
        }
        foreach (var (device, paths, generation) in readers)
        {
            IDisposable? monitor = null;
            try
            {
                monitor = OpenStickMonitor(paths, (x, y) => SendStick(device, x, y, generation));
            }
            catch (Exception e) when (e is IOException or UnauthorizedAccessException)
            {
                Console.Error.WriteLine($"{key}: cannot follow the thumbstick: {e.Message}");
            }
            finally
            {
                device.StickWatch.Complete(generation, monitor);
            }
        }
    }

    static IDisposable? OpenAnalogStick(IReadOnlyList<string> paths, Action<double, double> moved)
    {
        var node = paths.FirstOrDefault(IsGamepad);
        if (node == null)
            return null;
        var monitor = StickMonitor.Start(node);
        monitor.Moved += moved;
        return monitor;
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

    void SendStick(Connected device, double x, double y, long? readerGeneration = null)
    {
        if (device.StickWatch.ShouldReport(readerGeneration))
            StickMoved?.Invoke(device.Key, x, y);
    }

    /// <summary>The screen went off or on, or the idle time moved: each device whose profile asks for it goes dark or
    /// comes back.</summary>
    public void Idle(bool screenOff, TimeSpan idle)
    {
        List<DeviceControl> controls;
        lock (_refreshGate)
        lock (_gate)
            controls = _devices.Values.Where(d => !d.Suppressed && d.Control != null).Select(d => d.Control!).ToList();
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
    void FollowDeviceKeymap(string key, int keymap) => UpdateSettings(key, settings => AdoptKeymap(settings, keymap));

    static DeviceSettings AdoptKeymap(DeviceSettings settings, int keymap)
    {
        if (settings.ActiveKeymap == keymap && settings.Active.Keymaps.Count > keymap)
            return settings;
        var profile = settings.Active;
        var keymaps = profile.Keymaps.ToList();
        while (keymaps.Count <= keymap)
            keymaps.Add(Keymap.Empty($"Keymap {keymaps.Count + 1}"));
        profile = profile with { Keymaps = keymaps };
        return settings with
        {
            Profiles = settings.Profiles.Select(p => p.Id == profile.Id ? profile : p).ToList(),
            ActiveKeymap = keymap,
        };
    }

    // Per device model: the profile the user chose, while a linked program's profile stands in for it.
    readonly Lock _focusGate = new();
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
            UpdateSettings(key, settings =>
            {
                var linked = settings.Profiles.FirstOrDefault(p => p.LinkedPrograms.Any(program.Matches));
                Guid? target;
                lock (_focusGate)
                {
                    if (linked != null)
                    {
                        _chosenProfile.TryAdd(key, settings.ActiveProfile);
                        target = linked.Id;
                    }
                    else
                        target = _chosenProfile.Remove(key, out var chosen) ? chosen : null;
                }
                return target is Guid id && id != settings.ActiveProfile && settings.Profiles.Any(p => p.Id == id)
                    ? settings with { ActiveProfile = id, ActiveKeymap = 0 } : settings;
            });
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
            ThreadPool.QueueUserWorkItem(_ => manager.UpdateSettings(key, settings => settings.SwitchProfile(direction, profileId)));

        public void SwitchKeymap(CycleDirection direction, int keymap) =>
            ThreadPool.QueueUserWorkItem(_ => manager.UpdateSettings(key, settings => settings.SwitchKeymap(direction, keymap)));

        public Macro? FindMacro(Guid id) => manager.Macros.Find(id);

        public ShortcutList Shortcuts => manager.Shortcuts;

        public void Gamepad(GamepadControl control, bool down) => manager._pad.Set(control, down);

        public void Report(Exception error) => Console.Error.WriteLine($"{key}: {error.Message}");
    }

    public void Dispose()
    {
        lock (_refreshGate)
        lock (_gate)
        {
            if (_disposed)
                return;
            _disposed = true;
            foreach (var device in _devices.Values)
            {
                StopSession(device);
                device.Control?.Dispose();
                device.Buttons?.Dispose();
                device.StickWatch.Dispose();
            }
            _devices.Clear();
        }
        _pad.Dispose();
        _ownedOutput?.Dispose();
    }
}
