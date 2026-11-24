using Avalonia.Threading;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using ViperSynapse.App.Services;
using ViperSynapse.Core.Bindings;
using ViperSynapse.Core.Devices;
using ViperSynapse.Core.Input;

namespace ViperSynapse.App.ViewModels;

/// <summary>One row of the key list: the key's number and what it does now.</summary>
public sealed partial class KeyRowViewModel(DeviceKey key, bool bindingAvailable = true) : ObservableObject
{
    public DeviceKey Key { get; } = key;
    public string Number => Key.Group == "linux input" ? "" : Key.Number.ToString(System.Globalization.CultureInfo.InvariantCulture);
    public string ControlName => Key.Group == "linux input" && Key.Code != 0 ? BindingText.KeyLabel(Key.Code) : Key.Label;
    public string ControlDetails => Key.Label == ControlName ? Category : $"{Key.Label} · {Category}";

    /// <summary>Controls with no key of their own (the Tartarus's Hypershift button) are handled by the device.</summary>
    public bool Bindable => Key.Source != 0 && bindingAvailable;

    /// <summary>Set on the first key of each group: the list shows it as a heading (KEYPAD, OTHER, SIDE BUTTONS).</summary>
    public string? GroupTitle { get; set; }

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(ControlDetails))]
    public partial string Category { get; set; } = "";

    /// <summary>The key whose binding is open: its cell in the list is highlighted.</summary>
    [ObservableProperty]
    public partial bool IsSelected { get; set; }

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(AssignmentLabel))]
    public partial string Value { get; set; } = "";

    /// <summary>Changed from what the key does by itself: shown green, as Synapse does.</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(AssignmentLabel))]
    public partial bool Changed { get; set; }

    public string AssignmentLabel => !Changed && Value == ControlName ? "Default" : Value;
}

/// <summary>A keymap in the dropdown, with the colour Synapse gives it (the keypad's keymap light).</summary>
public sealed record KeymapItem(int Index, string Name)
{
    static readonly string[] Colors = ["#F2A33A", "#E5484D", "#44D62C", "#3D8BFD", "#2EC4D6", "#C04BD8", "#F2D43A", "#E6E6E6"];

    static readonly Avalonia.Media.IBrush[] Brushes = Colors.Select(c => (Avalonia.Media.IBrush)new Avalonia.Media.SolidColorBrush(Avalonia.Media.Color.Parse(c))).ToArray();

    // Computed, not stored: a record compares its fields, and a stored brush would make the same keymap from two
    // loads unequal, so the box would drop its selection each time the settings are read again.
    public Avalonia.Media.IBrush Color => Brushes[Index % Brushes.Length];

    public override string ToString() => Name;
}

public sealed record KeyGroup(string Title, IReadOnlyList<KeyRowViewModel> Keys);
public sealed record ProfileChoice(Guid Id, string Name);

public sealed record DeviceUnitChoice(string Port, string ProductId, string? Serial, string Label);
public sealed record DashboardUnitAttention(DeviceUnitChoice Unit, string Reason)
{
    public string HealthLabel => $"Health · {Unit.Label}";
}

/// <summary>A line of the key list: a group heading, or one key or two side by side (Synapse 4 runs a long group
/// down two columns).</summary>
public sealed record KeyListLine(string? Title, KeyRowViewModel? Left, KeyRowViewModel? Right);

/// <summary>A button the device's firmware handles itself (the keypad's Hypershift, keymap and profile buttons): listed
/// with what it does, not remappable.</summary>
public sealed record FirmwareButtonLine(string Category, string Value, string Where = "Keypad");

/// <summary>A device tab: its pages (Customize, Lighting, ...), profiles, keymaps and the key list.</summary>
public sealed partial class DeviceViewModel : TabViewModel
{
    readonly MainViewModel _main;
    DeviceSettings _settings = DeviceSettings.Create();
    bool _loading;
    bool _controlsReady;
    bool _detached;
    readonly DispatcherTimer? _stickPoll;
    readonly DispatcherTimer? _batteryPoll;
    long _settingsReadRequest;
    long _endpointRevision, _reloadSaveRevision;
    Task? _fullReload, _liveRead;
    IReadOnlyList<ushort> _lastInputControls = [];
    string? _lastInputControlsStatus;

    public DeviceViewModel(DeviceInfo info, MainViewModel main) :
        this(info, main, new OnboardEditorViewModel(main.Client.GetDevicesAsync, main.Client.ManageOnboardAsync,
            main.Client.BackupOnboardAsync, main.Client.RestoreOnboardAsync)) { }

    internal DeviceViewModel(DeviceInfo info, MainViewModel main, OnboardEditorViewModel onboard)
    {
        _main = main;
        Onboard = onboard;
        Info = info;
        ushort productId = Convert.ToUInt16(info.ProductId, 16);
        if (info.Connection == "bluetooth" && productId == DeviceCatalogue.BasiliskV4ProBluetooth)
            productId = DeviceCatalogue.BasiliskV4ProReceiver;
        Model = info.Known ? DeviceCatalogue.Default.Find(productId) : null;
        SetAttachedUnits([info]);
        RebuildControls();
        _controlsReady = true;
        RebuildFirmwareButtons();
        Lighting = new LightingViewModel(Model, l => PushProfile(p => p with { Lighting = l }));
        Lighting.ApplyToOthers += effect =>
        {
            foreach (var other in _main.Devices.Where(d => d != this && !d.IsBluetooth))
                other.ReceiveLighting(effect);
        };
        Performance = new PerformanceViewModel(Model, p => PushProfile(x => x with { Performance = p }));
        UpdateManagedDpiAvailability();
        Power = new PowerViewModel(p => PushProfile(x => x with { Power = p }));
        Calibration = new CalibrationViewModel(c => PushProfile(x => x with { Calibration = c }));
        ScrollWheel = new ScrollWheelViewModel(s => PushProfile(x => x with { Scroll = s }));
        SelectedPage = Pages[0];
        if (HasThumbstick)
        {
            _stickPoll = new DispatcherTimer(DispatcherPriority.Background) { Interval = TimeSpan.FromSeconds(5) };
            _stickPoll.Tick += (_, _) => KeepWatchingStick();
            _stickPoll.Start();
        }
        if (Model?.Has("battery") == true)
        {
            // The charge changes on its own: read it again every minute, battery only, so a page being edited is left be.
            _batteryPoll = new DispatcherTimer(DispatcherPriority.Background) { Interval = TimeSpan.FromMinutes(1) };
            _batteryPoll.Tick += (_, _) => _ = RefreshBatteryAsync();
            _batteryPoll.Start();
        }
        _ = LoadPhotoAsync();
    }

    async Task LoadPhotoAsync()
    {
        try
        {
            if (await DeviceImages.LoadAsync(Model).ConfigureAwait(false) is not { } photo)
                return;
            await Dispatcher.UIThread.InvokeAsync(() =>
            {
                if (_detached)
                    photo.Dispose();
                else
                    Photo = photo;
            });
        }
        catch (Exception e)
        {
            System.Diagnostics.Trace.TraceWarning("Could not load device photo: {0}", e.Message);
        }
    }

    internal const string OnboardPage = "ONBOARD";
    internal const string HealthPage = "HEALTH";
    internal DeviceHealth Health => DeviceHealth.From(Info, Model, UsingGenericControls);
    public OnboardEditorViewModel Onboard { get; }
    public bool HasOnboardPage => Model?.Has("onboard_profiles") == true && Info.Connection is ("wired" or "hyperspeed");
    public bool IsOnboardPage => HasOnboardPage && SelectedPage == OnboardPage;

    /// <summary>The model's photo from Razer's servers, once fetched; null until then or when there is none.</summary>
    [ObservableProperty]
    public partial Avalonia.Media.Imaging.Bitmap? Photo { get; set; }

    /// <summary>Customize shows the photo instead of the drawing (the drawing is the one you can click).</summary>
    [ObservableProperty]
    public partial bool ShowPhoto { get; set; }

    public bool ShowDrawing => !UsingGenericControls && (!ShowPhoto || Photo == null);

    partial void OnShowPhotoChanged(bool value) => OnPropertyChanged(nameof(ShowDrawing));

    partial void OnPhotoChanged(Avalonia.Media.Imaging.Bitmap? value) => OnPropertyChanged(nameof(ShowDrawing));

    [ObservableProperty]
    public partial DeviceInfo Info { get; set; }

    IReadOnlyList<DeviceInfo> _attachedUnits = [];
    bool _replacingUnits;
    long _deviceReadRequest;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(UnitScope), nameof(DashboardConnection), nameof(DashboardAttentionSummary))]
    public partial IReadOnlyList<DeviceUnitChoice> UnitChoices { get; private set; } = [];

    [ObservableProperty] public partial DeviceUnitChoice? InspectedUnit { get; set; }

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(DashboardAttentionSummary))]
    public partial IReadOnlyList<DashboardUnitAttention> DashboardAttention { get; private set; } = [];

    public string DashboardAttentionSummary => DashboardAttention.Count switch
    {
        0 => "",
        1 when UnitChoices.Count == 1 => "Needs attention",
        1 => $"1 of {UnitChoices.Count} units needs attention",
        _ => $"{DashboardAttention.Count} of {UnitChoices.Count} units need attention",
    };

    public string UnitScope => UnitChoices.Count > 1
        ? $"Profile edits are shared by all {UnitChoices.Count} connected units."
        : "Profile edits are saved for this model.";

    internal bool SetAttachedUnits(IReadOnlyList<DeviceInfo> units)
    {
        var previous = Info;
        var next = units.FirstOrDefault(unit => unit.Port == previous.Port && unit.ProductId == previous.ProductId &&
            (previous.Serial == null || unit.Serial == previous.Serial));
        if (next == null && !string.IsNullOrEmpty(previous.Serial))
        {
            var matching = units.Where(unit => unit.Serial == previous.Serial).Take(2).ToArray();
            if (matching.Length == 1)
                next = matching[0];
        }
        next ??= units[0];

        _attachedUnits = units;
        var choices = units.Select(unit => new DeviceUnitChoice(unit.Port, unit.ProductId, unit.Serial,
            unit.Serial is { Length: > 0 } serial
                ? $"{DeviceHealth.ConnectionLabel(unit)} · {serial} · {unit.Port}"
                : $"{DeviceHealth.ConnectionLabel(unit)} · {unit.Port}")).ToArray();
        _replacingUnits = true;
        try
        {
            if (!UnitChoices.SequenceEqual(choices))
                UnitChoices = choices;
            InspectedUnit = UnitChoices.First(choice => choice.Port == next.Port && choice.ProductId == next.ProductId);
            var attention = new List<DashboardUnitAttention>();
            for (int i = 0; i < units.Count; i++)
                if (DeviceHealth.AttentionReason(units[i], Model) is { } reason)
                    attention.Add(new DashboardUnitAttention(UnitChoices[i], reason));
            if (!DashboardAttention.SequenceEqual(attention))
                DashboardAttention = attention;
        }
        finally
        {
            _replacingUnits = false;
        }
        Info = next;
        return !SameBatteryEndpoint(previous, next) || previous.ProductId != next.ProductId ||
            previous.Firmware != next.Firmware || previous.OnboardProfile != next.OnboardProfile;
    }

    partial void OnInspectedUnitChanged(DeviceUnitChoice? value)
    {
        if (_replacingUnits || value == null)
            return;
        var target = _attachedUnits.FirstOrDefault(unit => unit.Port == value.Port && unit.ProductId == value.ProductId && unit.Serial == value.Serial);
        if (target == null || ReferenceEquals(target, Info))
            return;
        Info = target;
        _ = RefreshLiveStateAsync();
    }

    public DeviceModel? Model { get; }
    public string Key => Info.Key;
    public override string Header => ShortName.ToUpperInvariant();
    public override string Title => (Model?.Name ?? Info.Name).ToUpperInvariant();
    public override TabIcon Icon => Model?.Type switch
    {
        "keypad" => TabIcon.Keypad,
        "mouse" => TabIcon.Mouse,
        "keyboard" => TabIcon.Keyboard,
        _ => TabIcon.Device,
    };

    /// <summary>The profile in use, shown in green on the dashboard card.</summary>
    public string ProfileName => SelectedProfile?.Name ?? "";

    partial void OnSelectedProfileChanged(ProfileChoice? oldValue, ProfileChoice? newValue) => OnPropertyChanged(nameof(ProfileName));

    /// <summary>The settings as last read from or sent to the service.</summary>
    public DeviceSettings CurrentSettings => _settings;

    /// <summary>Links a program to one of this device's profiles (taking it off the others), or to none.</summary>
    public void LinkProgram(string program, Guid? profileId)
    {
        if (!HasSoftwareSettings || _detached)
            return;
        var profiles = _settings.Profiles.Select(p =>
        {
            var programs = p.LinkedPrograms.Where(x => !string.Equals(x, program, StringComparison.OrdinalIgnoreCase)).ToList();
            if (p.Id == profileId)
                programs.Add(program);
            return p with { LinkedPrograms = programs };
        }).ToList();
        Push(_settings with { Profiles = profiles });
    }
    public string ShortName => (Model?.Name ?? Info.Name).Replace("Razer ", "", StringComparison.Ordinal);
    public bool IsKeypad => Model?.Type == "keypad";
    public bool IsBluetooth => Info.Connection == "bluetooth";

    public IReadOnlyList<KeyRowViewModel> Rows { get; private set; } = [];
    public IReadOnlyList<KeyGroup> Groups { get; private set; } = [];

    /// <summary>The key list's "All Buttons" box: everything, or one group.</summary>
    public IReadOnlyList<string> KeyFilters { get; private set; } = [];

    public bool UsingGenericControls => IsBluetooth || Model?.Keys.Count is null or 0;

    ushort? LegacyInputProduct => !IsBluetooth && Model is { } model && LegacyRemapControls.SupportsProduct(model.ProductId)
        ? model.ProductId : null;

    public string GenericControlsStatus => Info.InputControlsStatus switch
    {
        "available" when Info.NativeMacroKeys && LegacyInputProduct == LegacyRemapControls.BlackWidowChromaV2 =>
            "Viper reads M1–M5 directly while a binding or shortcut uses them.",
        "available" when LegacyInputProduct == LegacyRemapControls.BlackWidowChromaV2 &&
            !(Info.InputControls ?? []).Any(source => LegacyRemapControls.Describe(LegacyRemapControls.BlackWidowChromaV2, source) != null) =>
            "M keys appear when OpenRazer's driver mode exposes them to Linux as F13–F17.",
        "available" when (Info.InputControls?.Count ?? 0) == 0 => "This device advertises no compatible keyboard or mouse controls.",
        "available" when LegacyInputProduct.HasValue => "Remapping uses the Linux input controls this device advertises.",
        "available" => "Linux input controls advertised by this device; physical locations and firmware buttons are not identified.",
        "partial" => "Some input nodes could not be read. Remapping is unavailable until device access is fixed; this list may be incomplete.",
        "permission" => "Cannot read the device's input controls. Check device access permissions.",
        _ => "Device input controls are unavailable. Reconnect the device and check access permissions.",
    };

    void RebuildControls()
    {
        _lastInputControls = (Info.InputControls ?? []).ToArray();
        _lastInputControlsStatus = Info.InputControlsStatus;
        IReadOnlyList<DeviceKey> keys = UsingGenericControls ? [] : Model!.Keys;
        if (keys.Count == 0)
            keys = _lastInputControls.Distinct().Select(DescribeInput).OrderBy(key => key.Group == "linux input").ToList();
        Rows = keys.Select(k => new KeyRowViewModel(k, !UsingGenericControls || Info.InputControlsStatus == "available")).ToList();
        Groups = Rows.GroupBy(r => r.Key.Group).Select(g => new KeyGroup(GroupTitle(g.Key), g.ToList())).ToList();
        foreach (var group in Groups)
            group.Keys[0].GroupTitle = group.Title;
        KeyFilters = ["All Buttons", .. Groups.Select(g => FilterName(g.Title))];
        SelectedKeyFilter = KeyFilters[0];
        OnPropertyChanged(nameof(Rows));
        OnPropertyChanged(nameof(Groups));
        OnPropertyChanged(nameof(KeyFilters));
        OnPropertyChanged(nameof(KeyLines));
    }

    DeviceKey DescribeInput(ushort source)
    {
        if (LegacyInputProduct is ushort product && LegacyRemapControls.Describe(product, source) is { } named)
            return named;

        string label = source switch
        {
            SourceKeys.WheelUp => "REL_WHEEL_UP", SourceKeys.WheelDown => "REL_WHEEL_DOWN",
            SourceKeys.WheelLeft => "REL_HWHEEL_LEFT", SourceKeys.WheelRight => "REL_HWHEEL_RIGHT",
            _ => KeyCodes.Name(source),
        };
        return new DeviceKey(0, label, SourceKeys.IsWheel(source) ? (ushort)0 : source, 0, "linux input", source);
    }

    [ObservableProperty]
    public partial string SelectedKeyFilter { get; set; } = "";

    partial void OnSelectedKeyFilterChanged(string value) => OnPropertyChanged(nameof(KeyLines));

    static string FilterName(string upper) => upper == "DPI BUTTONS" ? "DPI Buttons" :
        System.Globalization.CultureInfo.InvariantCulture.TextInfo.ToTitleCase(upper.ToLowerInvariant());

    /// <summary>The key list as Synapse 4 lays it out: groups of more than six keys run down two columns (1-13 on the
    /// left, 14-27 on the right). Keys the firmware keeps for itself are left to <see cref="FirmwareButtons"/>.</summary>
    public IReadOnlyList<KeyListLine> KeyLines
    {
        get
        {
            var lines = new List<KeyListLine>();
            foreach (var group in Groups)
            {
                if (SelectedKeyFilter != KeyFilters[0] && SelectedKeyFilter != FilterName(group.Title))
                    continue;
                var keys = group.Keys.Where(k => k.Bindable || FirmwareButtons.Count == 0).ToList();
                if (keys.Count == 0)
                    continue;
                lines.Add(new KeyListLine(group.Title, null, null));
                // Synapse 4 runs a keypad's keys down two columns and a mouse's down one.
                if (keys.Count <= 6 || !IsKeypad)
                {
                    lines.AddRange(keys.Select(k => new KeyListLine(null, k, null)));
                    continue;
                }
                // An odd count leaves the extra key on the right, as Synapse puts 1-13 beside 14-27.
                int half = keys.Count / 2;
                for (int i = 0; i < keys.Count - half; i++)
                    lines.Add(new KeyListLine(null, i < half ? keys[i] : null, keys[half + i]));
            }
            return lines;
        }
    }

    public IReadOnlyList<FirmwareButtonLine> FirmwareButtons { get; private set; } = [];

    void RebuildFirmwareButtons()
    {
        if (UsingGenericControls)
            FirmwareButtons = [];
        else if (Model?.Has("keypad_buttons") == true)
            FirmwareButtons = [new("RAZER HYPERSHIFT", "Razer Hypershift"), new("SWITCH KEYMAP", "Cycle Keymap"), new("SWITCH PROFILE", "Cycle Profile")];
        else
            FirmwareButtons = Rows.Where(r => !r.Bindable)
                .Select(r => new FirmwareButtonLine(FirmwareCategory(r.Key.Label), r.Key.Label, "Mouse")).ToList();
        OnPropertyChanged(nameof(FirmwareButtons));
        OnPropertyChanged(nameof(HasFirmwareButtons));
        OnPropertyChanged(nameof(KeyLines));
    }

    public bool HasFirmwareButtons => FirmwareButtons.Count > 0;

    [RelayCommand]
    void SelectKey(KeyRowViewModel row) => SelectedKey = row;
    public IReadOnlyList<string> Pages => BuildPages();
    public LightingViewModel Lighting { get; }
    public PerformanceViewModel Performance { get; }
    public PowerViewModel Power { get; }
    public ScrollWheelViewModel ScrollWheel { get; }

    /// <summary>Every mouse gets the Scroll Wheel card (scroll speed); acceleration and Smart-Reel need the firmware's.</summary>
    public bool IsMouse => Model?.Type == "mouse";

    public bool HasScrollFirmware => !IsBluetooth && Model?.Has("scroll_wheel") == true;
    public CalibrationViewModel Calibration { get; }

    [ObservableProperty]
    public partial string SelectedPage { get; set; }

    [ObservableProperty]
    public partial IReadOnlyList<ProfileChoice> Profiles { get; private set; } = [];

    [ObservableProperty]
    public partial ProfileChoice? SelectedProfile { get; set; }

    /// <summary>Stable choices while the displayed index and name are unchanged.</summary>
    [ObservableProperty]
    public partial IReadOnlyList<KeymapItem> Keymaps { get; set; } = [];

    [ObservableProperty]
    public partial int SelectedKeymap { get; set; }

    /// <summary>What the keymap box binds to. A box whose items are replaced sends null back; that is ignored.</summary>
    public KeymapItem? SelectedKeymapItem
    {
        get => SelectedKeymap >= 0 && SelectedKeymap < Keymaps.Count ? Keymaps[SelectedKeymap] : null;
        set
        {
            if (value != null)
                SelectedKeymap = value.Index;
        }
    }

    /// <summary>Synapse 4's remap menu: the key list beside the device, shown or hidden by the button next to
    /// Standard and Hypershift.</summary>
    [ObservableProperty]
    public partial bool ShowKeyList { get; set; } = true;

    [RelayCommand]
    void ToggleKeyList() => ShowKeyList = !ShowKeyList;

    [ObservableProperty]
    public partial bool Hypershift { get; set; }

    /// <summary>The Standard half of the layer switch.</summary>
    public bool Standard
    {
        get => !Hypershift;
        set => Hypershift = !value;
    }

    public bool IsCustomize => SelectedPage == "CUSTOMIZE";

    // ---- Customize cards (Synapse 4: thumbstick, polling rate, macro interruption) -----------------------

    public bool HasThumbstick => Model?.Has("thumbstick") == true;

    /// <summary>Keypads show their polling rate on Customize; mice have it on Performance.</summary>
    public bool HasPollingCard => !IsBluetooth && Model?.Has("poll_rate") == true && Model?.Has("dpi") != true;

    [ObservableProperty] public partial ThumbstickMode StickMode { get; set; }
    [ObservableProperty] public partial double StickX { get; set; }
    [ObservableProperty] public partial double StickY { get; set; }
    [ObservableProperty] public partial MacroInterruption Interruption { get; set; }

    public bool StickAnalog { get => StickMode == ThumbstickMode.Analog; set { if (value) SetStick(ThumbstickMode.Analog); } }
    public bool StickFourWay { get => StickMode == ThumbstickMode.FourWay; set { if (value) SetStick(ThumbstickMode.FourWay); } }
    public bool StickEightWay { get => StickMode == ThumbstickMode.EightWay; set { if (value) SetStick(ThumbstickMode.EightWay); } }
    public string StickText => string.Create(System.Globalization.CultureInfo.InvariantCulture, $"X: {Percent(StickX)}%    Y: {Percent(-StickY)}%");

    // An int has no negative zero, which a formatted double would show as "-0".
    static int Percent(double v) => (int)Math.Round(v * 100);
    public string StickHint => StickMode switch
    {
        ThumbstickMode.FourWay => "Four directions, bound like keys (THUMBSTICK in the key list). Diagonals pick the stronger direction.",
        ThumbstickMode.EightWay => "Eight directions, bound like keys (THUMBSTICK in the key list). Diagonals press two directions.",
        _ => "The stick is the analog stick of the keypad's gamepad, for games that take a controller.",
    };

    public bool InterruptDedicated { get => Interruption == MacroInterruption.DedicatedKey; set { if (value) SetInterruption(MacroInterruption.DedicatedKey); } }
    public bool InterruptAnyKey { get => Interruption == MacroInterruption.AnyKey; set { if (value) SetInterruption(MacroInterruption.AnyKey); } }

    partial void OnStickModeChanged(ThumbstickMode value)
    {
        OnPropertyChanged(nameof(StickAnalog));
        OnPropertyChanged(nameof(StickFourWay));
        OnPropertyChanged(nameof(StickEightWay));
        OnPropertyChanged(nameof(StickHint));
    }

    partial void OnStickXChanged(double value) => OnPropertyChanged(nameof(StickText));
    partial void OnStickYChanged(double value) => OnPropertyChanged(nameof(StickText));

    partial void OnInterruptionChanged(MacroInterruption value)
    {
        OnPropertyChanged(nameof(InterruptDedicated));
        OnPropertyChanged(nameof(InterruptAnyKey));
    }

    void SetStick(ThumbstickMode mode)
    {
        if (mode != StickMode)
            PushProfile(p => p with { Thumbstick = mode });
    }

    void SetInterruption(MacroInterruption value)
    {
        if (value != Interruption)
            PushProfile(p => p with { Interruption = value });
    }

    /// <summary>Called on the UI thread when the service reports the stick.</summary>
    public void StickAt(double x, double y) => (StickX, StickY) = (x, y);

    /// <summary>While Customize is on screen, keep asking the service to report the stick.</summary>
    void KeepWatchingStick()
    {
        if (!_detached && HasThumbstick && _main.SelectedTab == this && IsCustomize)
            _ = WatchStickAsync();
    }

    async Task WatchStickAsync()
    {
        try { await _main.Client.WatchStickAsync(Key).ConfigureAwait(false); }
        catch (Exception e) when (e is Tmds.DBus.Protocol.DBusErrorReplyException or InvalidOperationException) { }
    }

    internal bool IsPolling => _stickPoll?.IsEnabled == true || _batteryPoll?.IsEnabled == true;

    internal void FlushPendingEdits()
    {
        var settings = MergePendingEdits(_settings);
        if (!ReferenceEquals(settings, _settings))
            Push(settings);
    }

    DeviceSettings MergePendingEdits(DeviceSettings settings)
    {
        // The pending controls still belong to the old profile, even when the incoming profile has changed.
        var lighting = Lighting.TakePendingSettings();
        var performance = Performance.TakePendingSettings();
        if (lighting is null && performance is null ||
            settings.Profiles.FirstOrDefault(p => p.Id == _settings.ActiveProfile) is not { } original)
            return settings;

        var revised = original with
        {
            Lighting = lighting ?? original.Lighting,
            Performance = performance ?? original.Performance,
        };
        return settings with { Profiles = settings.Profiles.Select(p => p.Id == revised.Id ? revised : p).ToArray() };
    }

    internal void Detach()
    {
        FlushPendingEdits();
        _detached = true;
        _stickPoll?.Stop();
        _batteryPoll?.Stop();
        InvalidateLiveReads();
        ++_settingsReadRequest;
    }

    partial void OnSelectedPageChanged(string value)
    {
        OnPropertyChanged(nameof(IsCustomize));
        OnPropertyChanged(nameof(IsOnboardPage));
        NotifySaveFeedback();
        KeepWatchingStick();
        RefreshOnboard();
        RefreshDpiOnPerformancePage();
    }

    /// <summary>The tab came to the front.</summary>
    public void Shown()
    {
        KeepWatchingStick();
        RefreshOnboard();
        RefreshDpiOnPerformancePage();
    }

    void RefreshDpiOnPerformancePage()
    {
        if (_controlsReady && !_detached && SelectedPage == "PERFORMANCE" && Performance.SupportsManagedDpiSteps)
            _ = RefreshLiveStateAsync();
    }

    internal void RefreshOnboard(IReadOnlyList<DeviceInfo>? snapshot = null)
    {
        if (!_detached && IsOnboardPage && Onboard.CanRefresh)
            _ = Onboard.RefreshTargetsAsync(snapshot);
    }

    [ObservableProperty]
    public partial KeyRowViewModel? SelectedKey { get; set; }

    [ObservableProperty]
    public partial BindingEditorViewModel? Editor { get; set; }

    sealed record BindingDraft(Guid ProfileId, int KeymapIndex, string KeymapName, bool Hypershift, ushort Source, Binding StartingBinding)
    {
        public Binding? SubmittedBinding { get; set; }
        public long Revision { get; set; }
    }

    BindingDraft? _bindingDraft;
    Action? _confirmProfileChange;
    bool _confirmingKeymapReset;

    [ObservableProperty]
    public partial string? ConfirmationMessage { get; set; }

    // ---- Battery: the charge readout in the header, on the Power page and on the dashboard card ------------------

    static readonly Avalonia.Media.IBrush BatteryGreen = new Avalonia.Media.SolidColorBrush(Avalonia.Media.Color.Parse("#44D62C"));
    static readonly Avalonia.Media.IBrush BatteryRed = new Avalonia.Media.SolidColorBrush(Avalonia.Media.Color.Parse("#E5484D"));

    public bool HasBattery => Model?.Has("battery") == true;
    long _batteryRequest;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(BatteryText), nameof(BatteryFill), nameof(BatteryKnown), nameof(BatteryBrush))]
    public partial int? BatteryPercent { get; set; }

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(BatteryText))]
    public partial bool Charging { get; set; }

    public bool BatteryKnown => HasBattery && BatteryPercent != null;

    public string BatteryText => BatteryPercent is int p ? $"{p} %" + (Charging ? "  ⚡" : "") : "";

    /// <summary>The width of the gauge's fill, out of 18.</summary>
    public double BatteryFill => Math.Clamp(BatteryPercent ?? 0, 0, 100) * 18 / 100.0;

    /// <summary>Green, red once the charge is at or below the low power level.</summary>
    public Avalonia.Media.IBrush BatteryBrush =>
        BatteryPercent is int p && p <= (_settings.Active.Power ?? _devicePower ?? Core.Lighting.PowerSettings.Default).LowBatteryPercent
            ? BatteryRed : BatteryGreen;

    void ShowBattery(int? percent, bool? charging)
    {
        Charging = charging == true;
        BatteryPercent = percent;
    }

    internal async Task RefreshBatteryAsync(Func<string, Task<int?>>? readBluetooth = null,
        Func<string, Task<DeviceState?>>? readUsb = null)
    {
        var capture = await Dispatcher.UIThread.InvokeAsync<(DeviceInfo Info, long Request)?>(() =>
            _detached || !HasBattery || (readUsb is null && readBluetooth is null && _liveRead is { IsCompleted: false })
                ? null : (Info, ++_batteryRequest));
        if (capture is not { } pending)
            return;
        var (info, request) = pending;
        if (info.Connection == "bluetooth")
        {
            await ReadBluetoothBatteryAsync(info.Port, request, readBluetooth ?? BluetoothBattery.ReadAsync).ConfigureAwait(false);
            return;
        }
        try
        {
            var read = readUsb == null ? _main.ReadLiveDeviceAsync(info) : readUsb(info.Key);
            if (await read.ConfigureAwait(false) is { } state && state.BatteryPercent != null)
                await Dispatcher.UIThread.InvokeAsync(() =>
                {
                    if (_batteryRequest == request)
                        ShowBattery(state.BatteryPercent, state.Charging);
                });
        }
        catch (Exception e) when (e is Tmds.DBus.Protocol.DBusErrorReplyException or InvalidOperationException or System.Text.Json.JsonException) { }
    }

    async Task ReadBluetoothBatteryAsync(string port, long request, Func<string, Task<int?>> read)
    {
        int? percent = await read(port).ConfigureAwait(false);
        await Dispatcher.UIThread.InvokeAsync(() =>
        {
            if (_batteryRequest == request)
                ShowBattery(percent, null);
        });
    }

    static bool SameBatteryEndpoint(DeviceInfo previous, DeviceInfo current) =>
        previous.Port == current.Port && previous.Connection == current.Connection &&
        previous.ControlNode == current.ControlNode && previous.Serial == current.Serial;

    public string DashboardConnection => UnitChoices.Count > 1
        ? $"{UnitChoices.Count} connected units"
        : DeviceHealth.ConnectionLabel(Info);

    string? DeviceProblem => string.IsNullOrWhiteSpace(Info.Problem) ? DeviceHealth.AttentionReason(Info, Model) : Info.Problem;
    public string Status => DeviceProblem ?? (Info.Remapping ? "Active" : "Ready");
    public bool HasDeviceProblem => DeviceProblem != null;
    public string Details => Info.Connection switch
    {
        "bluetooth" when Info.Known => "Bluetooth · Linux input",
        "wired" => ConnectionDetails("Wired USB"),
        "hyperspeed" => ConnectionDetails("HyperSpeed"),
        _ => $"{Info.Firmware}  ·  {(Info.Verified ? "Tested on this hardware" : Info.Known ? "From the OpenRazer catalogue, not yet tested here" : "Unknown model")}",
    };

    string ConnectionDetails(string connection)
    {
        var details = connection + " · " + (string.IsNullOrEmpty(Info.Firmware) ? "Firmware unavailable" : Info.Firmware);
        return Info.OnboardProfile is byte slot ? $"{details} · Onboard profile {slot}" : details;
    }

    partial void OnInfoChanged(DeviceInfo oldValue, DeviceInfo newValue)
    {
        NotifySaveFeedback();
        OnPropertyChanged(nameof(Status));
        OnPropertyChanged(nameof(HasDeviceProblem));
        OnPropertyChanged(nameof(Details));
        OnPropertyChanged(nameof(DashboardConnection));
        OnPropertyChanged(nameof(GenericControlsStatus));
        OnPropertyChanged(nameof(Health));
        if (!_controlsReady)
            return;
        UpdateManagedDpiAvailability();
        bool endpointChanged = !SameBatteryEndpoint(oldValue, newValue) || oldValue.ProductId != newValue.ProductId;
        if (endpointChanged)
        {
            Performance.ConfirmedDpiStep = null;
            Performance.ManagedDpiProblem = null;
            Performance.LastConfirmedManagedDpi = null;
            InvalidateLiveReads();
            ShowBattery(null, null);
            _deviceDefaults = null;
            _devicePower = null;
            _deviceScroll = null;
            _deviceWheelMode = null;
            _liftOff = -1;
        }
        bool bluetoothChanged = (oldValue.Connection == "bluetooth") != IsBluetooth;
        if (bluetoothChanged || UsingGenericControls &&
            (_lastInputControlsStatus != newValue.InputControlsStatus || !_lastInputControls.SequenceEqual(newValue.InputControls ?? [])))
        {
            SelectedKey = null;
            RebuildControls();
            RebuildFirmwareButtons();
            RefreshRows();
        }
        bool onboardChanged = (oldValue.Connection is "wired" or "hyperspeed") != HasOnboardPage &&
            Model?.Has("onboard_profiles") == true;
        if (bluetoothChanged || onboardChanged)
        {
            if (!Pages.Contains(SelectedPage))
                SelectedPage = Pages[0];
            OnPropertyChanged(nameof(UsingGenericControls));
            OnPropertyChanged(nameof(ShowDrawing));
            OnPropertyChanged(nameof(Pages));
            OnPropertyChanged(nameof(HasOnboardPage));
            OnPropertyChanged(nameof(IsOnboardPage));
            OnPropertyChanged(nameof(HasScrollFirmware));
            OnPropertyChanged(nameof(HasPollingCard));
        }
        if (endpointChanged && _loadedOnce)
            ApplyLiveDefaults();
        if (Performance.SupportsManagedDpiSteps && oldValue.ManagedDpiRevision != newValue.ManagedDpiRevision)
            _ = ReadDeviceAsync();
    }

    void UpdateManagedDpiAvailability() => Performance.ManagedDpiAvailable = Performance.SupportsManagedDpiSteps && Info.ManagedDpiListening;

    IReadOnlyList<string> BuildPages()
    {
        // Synapse 4's order: Customize, Performance, Lighting, Power, Calibration.
        var pages = new List<string> { "CUSTOMIZE" };
        if (!IsBluetooth)
        {
            // Keypads show their polling rate on Customize; Performance is the mouse page.
            if (Model?.Has("dpi") == true) pages.Add("PERFORMANCE");
            if (Model?.Zones.Count > 0 || IsKeypad) pages.Add("LIGHTING");
            if (Model?.Has("battery") == true) pages.Add("POWER");
            if (Model?.Has("lift_off") == true) pages.Add("CALIBRATION");
            if (HasOnboardPage) pages.Add(OnboardPage);
        }
        pages.Add(HealthPage);
        return pages;
    }

    static string FirmwareCategory(string label) => label switch
    {
        _ when label.Contains("Profile", StringComparison.Ordinal) => "SWITCH PROFILE",
        _ when label.Contains("Sensitivity", StringComparison.Ordinal) => "SENSITIVITY",
        _ when label.Contains("Hypershift", StringComparison.Ordinal) => "RAZER HYPERSHIFT",
        _ when label.Contains("Scroll", StringComparison.Ordinal) => "MOUSE",
        _ => "FIRMWARE",
    };

    static string GroupTitle(string group) => group switch
    {
        "main" => "KEYPAD",
        "other" => "OTHER",
        "mouse" => "MOUSE",
        "side" => "SIDE BUTTONS",
        "thumbstick" => "THUMBSTICK",
        _ => group.ToUpperInvariant(),
    };

    void InvalidateLiveReads()
    {
        ++_batteryRequest;
        ++_deviceReadRequest;
        ++_endpointRevision;
        _liveRead = null;
        _fullReload = null;
    }

    public async Task ReloadAsync()
    {
        var pending = await Dispatcher.UIThread.InvokeAsync<Task>(() =>
        {
            if (_detached)
                return Task.CompletedTask;
            long saveRevision = _main.DeviceSaveState(Key).Revision;
            if (_fullReload is { IsCompleted: false } && _reloadSaveRevision == saveRevision)
                return _fullReload;
            _reloadSaveRevision = saveRevision;
            return _fullReload = ReloadWithLiveStateAsync(_endpointRevision);
        });
        await pending.ConfigureAwait(false);
    }

    async Task ReloadWithLiveStateAsync(long endpointRevision)
    {
        if (!await ReloadSettingsAsync(_main.ReadDeviceSettingsAsync).ConfigureAwait(false))
            return;
        var pending = await Dispatcher.UIThread.InvokeAsync<Task>(() =>
            _endpointRevision == endpointRevision ? GetLiveRead() : Task.CompletedTask);
        await pending.ConfigureAwait(false);
    }

    async Task RefreshLiveStateAsync()
    {
        var pending = await Dispatcher.UIThread.InvokeAsync<Task>(GetLiveRead);
        await pending.ConfigureAwait(false);
    }

    Task GetLiveRead()
    {
        if (_detached)
            return Task.CompletedTask;
        return _liveRead is { IsCompleted: false } ? _liveRead : _liveRead = ReadDeviceAsync();
    }

    internal async Task<bool> ReloadSettingsAsync(Func<string, Task<DeviceSettings>> readSettings)
    {
        var capture = await Dispatcher.UIThread.InvokeAsync<(long Request, long Revision, Task Saved)?>(() =>
        {
            if (_detached)
                return null;
            var save = _main.DeviceSaveState(Key);
            return (++_settingsReadRequest, save.Revision, save.Completion);
        });
        if (capture is not { } pending)
            return false;

        bool Current() => !_detached && _settingsReadRequest == pending.Request &&
            _main.DeviceSaveState(Key).Revision == pending.Revision && _main.GetSaveFeedback(Key)?.Error == null;
        try
        {
            await pending.Saved.ConfigureAwait(false);
            if (!await Dispatcher.UIThread.InvokeAsync(Current))
                return true;

            var settings = await readSettings(Key).ConfigureAwait(false);
            await Dispatcher.UIThread.InvokeAsync(() =>
            {
                if (!Current())
                    return;
                var withEdits = settings.ActiveProfile == _settings.ActiveProfile ? settings : MergePendingEdits(settings);
                Load(withEdits, fromService: true);
                if (!ReferenceEquals(withEdits, settings))
                    _main.SaveDeviceSettings(Key, withEdits, settings);
            });
            return true;
        }
        catch (Exception e)
        {
            _main.Fail("Could not read the device settings", e, Current);
            return false;
        }
    }

    bool _loadedOnce;

    [ObservableProperty]
    public partial bool HasSoftwareSettings { get; private set; }

    Core.Lighting.ZoneEffect? _deferredLightingEffect;

    void ReceiveLighting(Core.Lighting.ZoneEffect effect)
    {
        if (HasSoftwareSettings)
            Lighting.Take(effect);
        else
            _deferredLightingEffect = effect;
    }

    void MarkSoftwareSettingsLoaded()
    {
        bool first = !HasSoftwareSettings;
        HasSoftwareSettings = true;
        if (_deferredLightingEffect is { } effect)
        {
            _deferredLightingEffect = null;
            Lighting.Take(effect);
        }
        if (first)
        {
            _main.Macros.KeymapsChanged();
            if (_main.SelectedTab == _main.LinkedGames)
                _main.LinkedGames.Refresh();
        }
    }

    /// <summary>A profile or keymap that changed from outside the window (a linked game, the keypad's own buttons)
    /// is announced on screen when the user wants that. Changes made here never arrive as a difference.</summary>
    void AnnounceOutsideChange(DeviceSettings settings)
    {
        if (!HasSoftwareSettings || !_main.Settings.Notifications)
            return;
        string? what = settings.ActiveProfile != _settings.ActiveProfile ? $"Profile: {settings.Active.Name}"
            : settings.ActiveKeymap != _settings.ActiveKeymap && IsKeypad ? $"Keymap: {settings.ActiveKeymapOrFirst.Name}"
            : null;
        if (what != null)
            _ = _main.Client.NotifyAsync(ShortName, what);
    }

    void Load(DeviceSettings settings, bool fromService = false, bool preservePendingEdits = false)
    {
        if (fromService)
            AnnounceOutsideChange(settings);
        bool keepDrafts = preservePendingEdits || _loadedOnce && _settings.ActiveProfile == settings.ActiveProfile;
        _loadedOnce = true;
        _loading = true;
        try
        {
            _settings = settings;
            UpdateDraftConflict();
            var profiles = settings.Profiles.Select(profile => new ProfileChoice(profile.Id, profile.Name)).ToArray();
            if (!Profiles.SequenceEqual(profiles))
                Profiles = profiles;
            SelectedProfile = Profiles.FirstOrDefault(profile => profile.Id == settings.Active.Id);
            var keymaps = settings.Active.Keymaps.Select((keymap, i) => new KeymapItem(i, keymap.Name)).ToArray();
            if (!Keymaps.SequenceEqual(keymaps))
            {
                Keymaps = keymaps;
                OnPropertyChanged(nameof(SelectedKeymapItem));
            }
            SelectedKeymap = Math.Clamp(settings.ActiveKeymap, 0, Math.Max(0, Keymaps.Count - 1));
            OnPropertyChanged(nameof(QuickRemapped));
            RefreshRows();
            StickMode = settings.Active.Thumbstick;
            Interruption = settings.Active.Interruption;
            if (!keepDrafts || !Lighting.HasPendingEdits)
                Lighting.Load(settings.Active.Lighting);
            ApplyLiveDefaults(keepDrafts);
        }
        finally
        {
            _loading = false;
        }
        if (fromService)
            MarkSoftwareSettingsLoaded();
        _main.Macros.KeymapsChanged();
    }

    void ApplyLiveDefaults(bool keepDrafts = true)
    {
        var profile = _settings.Active;
        if (!keepDrafts || !Performance.HasPendingEdits)
            Performance.Load(profile.Performance ?? _deviceDefaults ?? Core.Lighting.PerformanceSettings.Default);
        Power.Load(profile.Power ?? _devicePower ?? Core.Lighting.PowerSettings.Default);
        OnPropertyChanged(nameof(BatteryBrush));
        ScrollWheel.Load(profile.Scroll ?? _deviceScroll ?? Core.Lighting.ScrollSettings.Default, _deviceWheelMode);
        Calibration.Load(profile.Calibration ?? new Core.Lighting.CalibrationSettings(_liftOff));
    }

    Core.Lighting.PerformanceSettings? _deviceDefaults;
    int _liftOff = -1;
    Core.Lighting.PowerSettings? _devicePower;
    Core.Lighting.ScrollSettings? _deviceScroll;
    Core.Lighting.ScrollWheelMode? _deviceWheelMode;

    /// <summary>Reads what the device itself is set to, so Performance and Power show real values before the user
    /// changes anything.</summary>
    internal async Task ReadDeviceAsync(Func<DeviceInfo, Task<DeviceState?>>? readUsb = null)
    {
        var capture = await Dispatcher.UIThread.InvokeAsync<(DeviceInfo Info, long Battery, long State)?>(() =>
            _detached ? null : (Info, ++_batteryRequest, ++_deviceReadRequest));
        if (capture is not { } pending)
            return;
        var (info, batteryRequest, deviceRequest) = pending;
        bool Current() => !_detached && _deviceReadRequest == deviceRequest;
        try
        {
            if (info.Connection == "bluetooth")
            {
                if (HasBattery)
                    await ReadBluetoothBatteryAsync(info.Port, batteryRequest, BluetoothBattery.ReadAsync).ConfigureAwait(false);
                return;
            }
            var read = readUsb ?? _main.ReadLiveDeviceAsync;
            if (await read(info).ConfigureAwait(false) is not { } state)
                return;
            await Dispatcher.UIThread.InvokeAsync(() =>
            {
                if (!Current())
                    return;
                // A keypad reports a polling rate and no stages; its rate is still the device's own value.
                Performance.ConfirmedDpiStep = state.ManagedDpiStep;
                Performance.ManagedDpiProblem = state.ManagedDpiProblem;
                Performance.LastConfirmedManagedDpi = state.LastConfirmedManagedDpi;
                if (state.Stages is { Count: > 0 } || state.PollingRate > 0)
                    _deviceDefaults = new Core.Lighting.PerformanceSettings(state.Stages ?? [], state.ActiveStage, state.PollingRate);
                // A mouse asleep answers nothing: keep the last reading rather than blanking it.
                if (_batteryRequest == batteryRequest && state.BatteryPercent != null)
                    ShowBattery(state.BatteryPercent, state.Charging);
                _liftOff = state.LiftOffLevel;
                if (state.SleepSeconds > 0)
                    _devicePower = new Core.Lighting.PowerSettings(state.SleepSeconds, state.LowBatteryPercent > 0 ? state.LowBatteryPercent : Core.Lighting.PowerSettings.Default.LowBatteryPercent);
                if (state.ScrollAcceleration != null || state.SmartReel != null)
                    _deviceScroll = new Core.Lighting.ScrollSettings(1, state.ScrollAcceleration == true, state.SmartReel == true);
                _deviceWheelMode = state.WheelMode;
                if (_loadedOnce)
                    ApplyLiveDefaults();
                else
                    Load(_settings);
            });
        }
        catch (Exception e)
        {
            _main.Fail("Could not read the device", e, Current);
        }
    }

    internal async Task ActivateDpiStageAsync(StageRow row)
    {
        if (!Performance.ManagedDpiSteps)
        {
            Performance.Activate(row);
            return;
        }
        var unit = Info;
        var profile = _settings.Active.Id;
        bool Current() => !_detached && SameBatteryEndpoint(unit, Info) && unit.ProductId == Info.ProductId && _settings.Active.Id == profile;
        try
        {
            FlushPendingEdits();
            await _main.DeviceSaveState(Key).Completion.WaitAsync(TimeSpan.FromSeconds(5));
            int step = row.Number - 1;
            if (!Current() || !Performance.Stages.Contains(row) || _main.GetSaveFeedback(Key)?.Error != null ||
                _settings.Active.Performance is not { ManagedDpiSteps: true } performance || step >= performance.Stages.Count)
                return;
            await _main.Client.SelectManagedDpiStepAsync(unit, profile, step, performance.Stages[step]);
            if (Current())
                await ReadDeviceAsync();
        }
        catch (Exception error)
        {
            if (Current())
                Performance.ManagedDpiProblem = $"Could not select the DPI step. {error.Message}";
        }
    }

    void PushProfile(Func<Profile, Profile> change)
    {
        if (_loading)
            return;
        var profile = change(_settings.Active);
        Push(_settings with { Profiles = _settings.Profiles.Select(p => p.Id == profile.Id ? profile : p).ToList() });
    }

    Keymap CurrentKeymap => _settings.ActiveKeymapOrFirst;

    void RefreshRows()
    {
        foreach (var row in Rows)
        {
            var binding = CurrentKeymap.Resolve(row.Key.Source, Hypershift);
            if (!row.Bindable)
            {
                (row.Category, row.Value, row.Changed) = ("DEVICE FUNCTION", row.Key.Label, false);
                continue;
            }
            var (category, value) = BindingText.Describe(binding, row.Key.Code, id => _main.MacroLibrary.Find(id)?.Name);
            if (binding is DefaultBinding && row.Key.Code == 0)
                (category, value) = (SourceKeys.IsWheel(row.Key.Source) ? "MOUSE FUNCTION" : category, row.Key.Label);
            (row.Category, row.Value, row.Changed) = (category, value, binding is not DefaultBinding);
        }
    }

    partial void OnHypershiftChanged(bool value)
    {
        OnPropertyChanged(nameof(Standard));
        RefreshRows();
    }

    partial void OnSelectedKeyChanged(KeyRowViewModel? oldValue, KeyRowViewModel? newValue)
    {
        if (oldValue != null)
            oldValue.IsSelected = false;
        if (newValue != null)
            newValue.IsSelected = true;
        OnKeyChosen(newValue);
    }

    void OnKeyChosen(KeyRowViewModel? value)
    {
        if (value == null || !value.Bindable)
        {
            _bindingDraft = null;
            Editor = null;
            NotifySaveFeedback();
            return;
        }
        OpenEditor(value);
        NotifySaveFeedback();
    }

    void OpenEditor(KeyRowViewModel row)
    {
        var profile = _settings.Active;
        var keymap = CurrentKeymap;
        var binding = LayerBinding(keymap, Hypershift, row.Key.Source);
        var draft = new BindingDraft(profile.Id, ActiveKeymapIndex, keymap.Name, Hypershift, row.Key.Source, binding);
        _bindingDraft = draft;
        Editor = new BindingEditorViewModel(row, binding, _main.MacroLibrary, _settings.Profiles, Keymaps.Count,
            (_, value) => Save(draft, value), () =>
            {
                if (ReferenceEquals(_bindingDraft, draft))
                {
                    SelectedKey = null;
                    if (draft.Revision > 0)
                        _main.CancelBindingFailure(Key, draft.Revision);
                }
            }, target: $"{profile.Name} · {draft.KeymapName} · {(draft.Hypershift ? "Hypershift" : "Standard")}");
        UpdateDraftConflict();
    }

    partial void OnSelectedProfileChanged(ProfileChoice? value)
    {
        if (_loading || value == null || value.Id == _settings.ActiveProfile)
            return;
        Push(_settings with { ActiveProfile = value.Id, ActiveKeymap = 0 });
    }

    partial void OnSelectedKeymapChanged(int value)
    {
        OnPropertyChanged(nameof(SelectedKeymapItem));
        OnPropertyChanged(nameof(QuickRemapped));
        if (_loading || value < 0 || value == _settings.ActiveKeymap)
            return;
        Push(_settings with { ActiveKeymap = value });
    }

    void UpdateDraftConflict()
    {
        if (_bindingDraft is not { } draft || Editor is not { Conflict: null, IsSaving: false } editor)
            return;
        if (_main.GetSaveFeedback(Key)?.Error is SettingsConflictException || _main.GetBindingFailure(Key)?.Error is SettingsConflictException)
        {
            editor.Conflict = "The saved settings changed. Close this draft and choose Discard and reload… before editing the binding again.";
            return;
        }
        var profile = _settings.Profiles.FirstOrDefault(p => p.Id == draft.ProfileId);
        if (profile == null || draft.KeymapIndex >= profile.Keymaps.Count)
            editor.Conflict = "The original profile or keymap was removed. Close this draft to choose another target.";
        else
        {
            var current = profile.Keymaps[draft.KeymapIndex];
            var binding = LayerBinding(current, draft.Hypershift, draft.Source);
            if (draft.KeymapName != current.Name || binding != draft.StartingBinding && binding != draft.SubmittedBinding)
                editor.Conflict = "The original binding or keymap changed while this draft was open. Close and reopen the editor to review it.";
        }
    }

    static Binding LayerBinding(Keymap keymap, bool hypershift, ushort source) =>
        (hypershift ? keymap.Hypershift : keymap.Standard).GetValueOrDefault(source, DefaultBinding.Instance);

    static bool SameKeymap(Keymap first, Keymap second) => first.Name == second.Name &&
        SameBindings(first.Standard, second.Standard) && SameBindings(first.Hypershift, second.Hypershift);

    static bool SameBindings(IReadOnlyDictionary<ushort, Binding> first, IReadOnlyDictionary<ushort, Binding> second) =>
        first.Count == second.Count && first.All(item => second.TryGetValue(item.Key, out var binding) && binding == item.Value);

    void Save(BindingDraft draft, Binding binding)
    {
        if (!ReferenceEquals(_bindingDraft, draft))
            return;
        UpdateDraftConflict();
        if (Editor is not { Conflict: null, IsSaving: false } editor)
            return;

        // The queue and the displayed model share the same immutable results, so queued
        // edits derived before the binding was acknowledged can reuse its saved revision.
        var results = new System.Runtime.CompilerServices.ConditionalWeakTable<DeviceSettings, DeviceSettings>();
        DeviceSettings Apply(DeviceSettings current, bool requireOriginal)
        {
            var profile = current.Profiles.FirstOrDefault(p => p.Id == draft.ProfileId);
            if (profile == null || draft.KeymapIndex >= profile.Keymaps.Count || profile.Keymaps[draft.KeymapIndex].Name != draft.KeymapName)
            {
                if (requireOriginal)
                    throw new SettingsConflictException();
                return current;
            }
            var keymap = profile.Keymaps[draft.KeymapIndex];
            var previous = LayerBinding(keymap, draft.Hypershift, draft.Source);
            if (previous == binding)
                return current;
            if (previous != draft.StartingBinding)
            {
                if (requireOriginal)
                    throw new SettingsConflictException();
                return current;
            }
            return results.GetValue(current, snapshot =>
            {
                var assignments = new Dictionary<ushort, Binding>(draft.Hypershift ? keymap.Hypershift : keymap.Standard);
                if (binding is DefaultBinding)
                    assignments.Remove(draft.Source);
                else
                    assignments[draft.Source] = binding;
                var maps = profile.Keymaps.ToArray();
                maps[draft.KeymapIndex] = draft.Hypershift ? keymap with { Hypershift = assignments } : keymap with { Standard = assignments };
                return snapshot with { Profiles = snapshot.Profiles.Select(p => p.Id == profile.Id ? p with { Keymaps = maps } : p).ToArray() };
            });
        }

        draft.SubmittedBinding = binding;
        editor.IsSaving = true;
        editor.SaveError = null;
        var baseline = _settings;
        long previousRevision = draft.Revision;
        draft.Revision = _main.DeviceSaveState(Key).Revision + 1;
        _ = _main.SaveDeviceSettings(Key, Apply(baseline, false), baseline, Apply,
            error => CompleteDraftSave(editor, error, settings => Apply(settings, false)), previousRevision);
        NotifySaveFeedback();
    }

    bool OwnsBindingSave(long revision) => _bindingDraft?.Revision == revision && Editor != null;

    void CompleteDraftSave(BindingEditorViewModel editor, Exception? error,
        Func<DeviceSettings, DeviceSettings> apply)
    {
        NotifySaveFeedback();
        if (error == null)
            ApplySavedBinding(apply);
        if (!ReferenceEquals(Editor, editor))
            return;
        editor.IsSaving = false;
        if (error == null)
            SelectedKey = null;
        else if (error is SettingsConflictException)
            editor.Conflict = "The saved settings changed. Close this draft and choose Discard and reload… before editing the binding again.";
        else
            editor.SaveError = "Couldn't save this binding. Your draft is still open.";
    }

    internal void InitializeSavedSettings(DeviceSettings confirmedSettings)
    {
        if (_detached || HasSoftwareSettings)
            return;
        Load(confirmedSettings, preservePendingEdits: true);
        MarkSoftwareSettingsLoaded();
    }

    internal void ApplySavedBinding(Func<DeviceSettings, DeviceSettings> apply)
    {
        if (_detached)
            return;
        var saved = apply(_settings);
        if (!ReferenceEquals(saved, _settings))
            Load(saved);
        MarkSoftwareSettingsLoaded();
    }

    /// <summary>Binds a key in the Standard layer of the keymap in use (the MACRO tab's KEY BINDS assigns this way).</summary>
    public void Bind(DeviceKey key, Binding binding)
    {
        if (HasSoftwareSettings && !_detached)
            Bind([(key, binding)]);
    }

    void Bind(IEnumerable<(DeviceKey Key, Binding Binding)> changes)
    {
        var keymap = CurrentKeymap;
        var layer = new Dictionary<ushort, Binding>(keymap.Standard);
        foreach (var (key, binding) in changes)
        {
            if (binding is DefaultBinding) layer.Remove(key.Source);
            else layer[key.Source] = binding;
        }
        Push(ReplaceKeymap(keymap with { Standard = layer }));
        OnPropertyChanged(nameof(QuickRemapped));
    }

    // ---- Quick remapping: W, A, S, D work the virtual gamepad's left stick, Q and E its triggers ----------------

    static readonly (string Key, GamepadControl Control)[] QuickMap =
    [
        ("KEY_W", GamepadControl.LeftStickUp), ("KEY_A", GamepadControl.LeftStickLeft), ("KEY_S", GamepadControl.LeftStickDown),
        ("KEY_D", GamepadControl.LeftStickRight), ("KEY_Q", GamepadControl.LeftTrigger), ("KEY_E", GamepadControl.RightTrigger),
    ];

    /// <summary>The device's own keys that type W, A, S, D, Q and E (not the thumbstick's directions, which only
    /// default to them), with the gamepad control each stands in for.</summary>
    IEnumerable<(DeviceKey Key, GamepadControl Control)> QuickKeys =>
        QuickMap.SelectMany(m => Rows.Where(r => r.Bindable && r.Key.Source == r.Key.Code && KeyCodes.Name(r.Key.Code) == m.Key)
                                     .Select(r => (r.Key, m.Control)));

    public bool HasQuickRemapping => IsKeypad && QuickKeys.Any();

    /// <summary>The keymap in use has Quick remapping on: every one of those keys works the gamepad.</summary>
    public bool QuickRemapped => QuickKeys.Any() && QuickKeys.All(q =>
        CurrentKeymap.Standard.TryGetValue(q.Key.Source, out var b) && b == new GamepadBinding(q.Control));

    [RelayCommand]
    void QuickRemap() => Bind(QuickKeys.Select(q => (q.Key, (Binding)new GamepadBinding(q.Control))));

    [RelayCommand]
    void UndoQuickRemap() => Bind(QuickKeys.Where(q => CurrentKeymap.Standard.GetValueOrDefault(q.Key.Source) is GamepadBinding)
                                           .Select(q => (q.Key, (Binding)DefaultBinding.Instance)));

    int ActiveKeymapIndex => Math.Clamp(_settings.ActiveKeymap, 0, Math.Max(0, _settings.Active.Keymaps.Count - 1));

    DeviceSettings ReplaceKeymap(Keymap keymap) => ReplaceKeymap(_settings.Active, ActiveKeymapIndex, keymap);

    DeviceSettings ReplaceKeymap(Profile profile, int index, Keymap keymap)
    {
        var keymaps = profile.Keymaps.ToList();
        if (keymaps.Count == 0) keymaps.Add(keymap);
        else keymaps[index] = keymap;
        profile = profile with { Keymaps = keymaps };
        return _settings with { Profiles = _settings.Profiles.Select(p => p.Id == profile.Id ? profile : p).ToList() };
    }

    [RelayCommand]
    void AddProfile()
    {
        var profile = Profile.Create($"Profile {_settings.Profiles.Count + 1}");
        Push(_settings with { Profiles = [.. _settings.Profiles, profile], ActiveProfile = profile.Id, ActiveKeymap = 0 });
    }

    [RelayCommand]
    void DeleteProfile()
    {
        if (_settings.Profiles.Count <= 1)
            return;
        var target = _settings.Active;
        SetProfileConfirmation($"Delete the profile {target.Name}?", () =>
        {
            var rest = _settings.Profiles.Where(p => p.Id != target.Id).ToList();
            if (rest.Count == 0 || rest.Count == _settings.Profiles.Count)
                return;
            var next = _settings with { Profiles = rest };
            if (_settings.ActiveProfile == target.Id)
                next = next with { ActiveProfile = rest[0].Id, ActiveKeymap = 0 };
            Push(next);
        });
    }

    [RelayCommand]
    void AddKeymap()
    {
        var profile = _settings.Active;
        if (profile.Keymaps.Count >= Profile.MaxKeymaps)
            return;
        profile = profile with { Keymaps = [.. profile.Keymaps, Keymap.Empty($"Keymap {profile.Keymaps.Count + 1}")] };
        Push(_settings with
        {
            Profiles = _settings.Profiles.Select(p => p.Id == profile.Id ? profile : p).ToList(),
            ActiveKeymap = profile.Keymaps.Count - 1,
        });
    }

    bool CanResetKeymap => !_main.IsSavingBinding(Key);

    [RelayCommand(CanExecute = nameof(CanResetKeymap))]
    void ResetKeymap()
    {
        if (!CanResetKeymap)
            return;
        var target = _settings.Active;
        int index = ActiveKeymapIndex;
        var original = CurrentKeymap;
        SetProfileConfirmation($"Reset {original.Name} in {target.Name} to its defaults?", () =>
        {
            var profile = _settings.Profiles.FirstOrDefault(p => p.Id == target.Id);
            if (profile == null || index >= profile.Keymaps.Count || !SameKeymap(original, profile.Keymaps[index]))
            {
                _main.Fail("Could not reset the keymap", new InvalidOperationException("Its contents or profile changed. Select the keymap and try again."));
                return;
            }
            if (_bindingDraft is { } draft && draft.ProfileId == profile.Id && draft.KeymapIndex == index && Editor != null)
                Editor.Conflict = "The original keymap was reset. Close and reopen the editor to use its current bindings.";
            Push(ReplaceKeymap(profile, index, Keymap.Empty(original.Name)));
        }, requiresIdleBindingQueue: true);
    }

    void SetProfileConfirmation(string message, Action confirm, bool requiresIdleBindingQueue = false)
    {
        _confirmingKeymapReset = requiresIdleBindingQueue;
        _confirmProfileChange = confirm;
        ConfirmationMessage = message;
        ConfirmProfileChangeCommand.NotifyCanExecuteChanged();
    }

    bool CanConfirmProfileChange => !_confirmingKeymapReset || CanResetKeymap;

    [RelayCommand(CanExecute = nameof(CanConfirmProfileChange))]
    void ConfirmProfileChange()
    {
        if (!CanConfirmProfileChange)
            return;
        var confirm = _confirmProfileChange;
        CancelProfileChange();
        confirm?.Invoke();
    }

    [RelayCommand]
    void CancelProfileChange()
    {
        ConfirmationMessage = null;
        _confirmProfileChange = null;
        _confirmingKeymapReset = false;
        ConfirmProfileChangeCommand.NotifyCanExecuteChanged();
    }

    /// <summary>Shows the change at once and sends it to the service, which saves and applies it.</summary>
    Task<Exception?> Push(DeviceSettings settings)
    {
        var baseline = _settings;
        if (settings.ActiveProfile != _settings.ActiveProfile)
            settings = MergePendingEdits(settings);
        Load(settings);
        return _main.SaveDeviceSettings(Key, settings, baseline);
    }
}
