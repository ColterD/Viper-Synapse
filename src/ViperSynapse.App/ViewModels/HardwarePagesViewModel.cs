using System.Collections.ObjectModel;
using Avalonia.Media;
using Avalonia.Threading;
using CommunityToolkit.Mvvm.ComponentModel;
using ViperSynapse.Core.Devices;
using ViperSynapse.Core.Lighting;
using ViperSynapse.Core.Protocol;

namespace ViperSynapse.App.ViewModels;

/// <summary>Coalesces a burst of edits (a slider being dragged) into one save, 300 ms after the last.</summary>
sealed class Debounce(Action action)
{
    /// <summary>The wait after the last edit; tests shorten it.</summary>
    internal static TimeSpan Delay { get; set; } = TimeSpan.FromMilliseconds(300);

    readonly DispatcherTimer _timer = new(DispatcherPriority.Background) { Interval = Delay };
    bool _wired;

    public void Poke()
    {
        if (!_wired)
        {
            _timer.Tick += (_, _) => { _timer.Stop(); action(); };
            _wired = true;
        }
        _timer.Stop();
        _timer.Start();
    }

    public bool IsPending => _timer.IsEnabled;

    public bool StopPending()
    {
        if (!_timer.IsEnabled)
            return false;
        _timer.Stop();
        return true;
    }
}

/// <summary>One LED of the plain grid editor.</summary>
public sealed record LedCell(int Row, int Column, IBrush Brush, string? Label = null,
    double Width = 44, IBrush? LabelBrush = null)
{
    public string AccessibleName => Label ?? $"LED row {Row + 1}, column {Column + 1}";
}

public sealed record EffectChoice(EffectKind Kind, string Label, IReadOnlyList<ColorMode> Modes)
{
    public override string ToString() => Label;
}

/// <summary>The LIGHTING page: brightness, the zone, the quick effect and its options.</summary>
public sealed partial class LightingViewModel : ObservableObject
{
    readonly DeviceModel? _model;
    readonly Action<LightingSettings> _save;
    readonly Debounce _debounce;
    Dictionary<string, ZoneLighting> _zones = [];
    bool _loading;

    public LightingViewModel(DeviceModel? model, Action<LightingSettings> save)
    {
        _model = model;
        _save = save;
        _debounce = new Debounce(Commit);
        Zones = model?.Zones.Keys.Order().ToList() is { Count: > 0 } zones ? zones : ["main"];
        SyncAll = Zones.Count > 1;
        SelectedZone = Zones[0];
    }

    public IReadOnlyList<string> Zones { get; }
    public bool HasZones => Zones.Count > 1;

    [ObservableProperty] public partial bool SyncAll { get; set; }
    [ObservableProperty] public partial string SelectedZone { get; set; }
    [ObservableProperty] public partial IReadOnlyList<EffectChoice> Effects { get; set; } = [];
    [ObservableProperty] public partial EffectChoice? SelectedEffect { get; set; }
    [ObservableProperty] public partial IReadOnlyList<ColorMode> ColorModes { get; set; } = [];
    [ObservableProperty] public partial ColorMode SelectedColorMode { get; set; }

    /// <summary>The colour mode box's side of <see cref="SelectedColorMode"/>. The box holds nothing for a moment when an
    /// effect brings its own modes; that empty pick is not a mode and is ignored (as a ColorMode it is a binding error,
    /// shown in red under the box).</summary>
    public ColorMode? ColorModeChoice
    {
        get => SelectedColorMode;
        set
        {
            if (value is { } mode)
                SelectedColorMode = mode;
        }
    }

    [ObservableProperty] public partial Color Color1 { get; set; } = Color.Parse("#44D62C");
    [ObservableProperty] public partial Color Color2 { get; set; } = Color.Parse("#0000FF");
    [ObservableProperty] public partial double Speed { get; set; } = 2;
    [ObservableProperty] public partial bool Reverse { get; set; }
    [ObservableProperty] public partial double Brightness { get; set; } = 100;

    /// <summary>Effects that always take two colours (iCUE's two-colour effects; Temperature's cool and hot).</summary>
    static bool TwoColours(EffectKind? kind) =>
        kind is EffectKind.Gradient or EffectKind.ColorShift or EffectKind.ColorWave or EffectKind.Marquee or EffectKind.Temperature;

    public bool ShowColor1 => SelectedEffect?.Kind is { } kind
        && kind is not (EffectKind.Off or EffectKind.Custom or EffectKind.Spectrum or EffectKind.Wave or EffectKind.Wheel or EffectKind.Fire or EffectKind.Watercolor)
        && (SelectedColorMode != ColorMode.Random || TwoColours(kind));
    public bool ShowColor2 => TwoColours(SelectedEffect?.Kind) || ShowColor1 && SelectedColorMode == ColorMode.Dual;
    public bool ShowModes => ColorModes.Count > 1;

    /// <summary>The service draws the effect on the selected zone (the firmware does not run it in this mode).</summary>
    bool Drawn => SelectedEffect is { } e && e.Kind is not (EffectKind.Off or EffectKind.Custom)
        && !EffectSupport.RunsOnDevice(new ZoneEffect(e.Kind, default, default, SelectedColorMode), NativeNames(SelectedZone));

    public bool ShowSpeed => SelectedEffect?.Kind is EffectKind.Reactive or EffectKind.Starlight
        || Drawn && SelectedEffect?.Kind is not (EffectKind.Static or EffectKind.Gradient or EffectKind.Temperature);
    public bool ShowDirection => SelectedEffect?.Kind is EffectKind.Wave or EffectKind.Wheel
        || Drawn && SelectedEffect?.Kind is EffectKind.ColorWave or EffectKind.Marquee or EffectKind.Gradient;

    /// <summary>A line under the effect where it needs saying how it works.</summary>
    public string? EffectHint => SelectedEffect?.Kind switch
    {
        EffectKind.Temperature => "Follows the CPU's temperature: the first colour at 40 °C, the second at 90 °C.",
        EffectKind.Reactive or EffectKind.Ripple when Drawn => "Responds to key presses while the app is remapping the device.",
        _ when Drawn => "The app draws this effect while it runs. The device shows the last frame after the app closes.",
        _ => null,
    };

    // ---- Per-key (Custom) ----------------------------------------------------------------------------

    bool UsesLegacyLighting => LegacyLightingCommands.SupportsProduct(_model?.ProductId);

    /// <summary>The device has an LED grid the custom effect can paint.</summary>
    public bool HasFrame => _model is { MatrixRows: > 0, MatrixColumns: > 0 } && _model.Has("custom_frame");

    /// <summary>Keys know their LEDs (the Tartarus): paint on the drawn device. Otherwise paint a plain LED grid.</summary>
    public bool PaintsKeys => _model?.Keys.Any(k => k.Leds.Count > 0) == true;

    public bool ShowFrameEditor => SelectedEffect?.Kind == EffectKind.Custom;
    public bool ShowBrightness => !ShowFrameEditor || _model?.Has("onboard_profiles") != true;
    public bool ShowKeyCanvas => ShowFrameEditor && PaintsKeys;
    public bool ShowLedGrid => ShowFrameEditor && !PaintsKeys;
    public bool ShowZoneSync => !UsesLegacyLighting || !ShowFrameEditor;
    public bool IsMouseLighting => _model?.Type == "mouse";

    CustomFrame _frame = CustomFrame.Filled(1, 1, Rgb.RazerGreen);
    bool _keepLegacyFrame;

    // OpenRGB's full keyboard map and BlackWidow Chroma V2 overlay, with ANSI legends.
    // Spare positions remain accessible by matrix coordinates for other physical layouts.
    static readonly string?[][] BlackWidowFrameLabels =
    [
        [null, "Esc", null, "F1", "F2", "F3", "F4", "F5", "F6", "F7", "F8", "F9", "F10", "F11", "F12", "Prt", "Scr", "Pause", null, null, "Logo", null],
        ["M1", "`", "1", "2", "3", "4", "5", "6", "7", "8", "9", "0", "-", "=", "Bksp", "Ins", "Home", "PgUp", "Num", "KP /", "KP *", "KP −"],
        ["M2", "Tab", "Q", "W", "E", "R", "T", "Y", "U", "I", "O", "P", "[", "]", "\\", "Del", "End", "PgDn", "KP 7", "KP 8", "KP 9", "KP +"],
        ["M3", "Caps", "A", "S", "D", "F", "G", "H", "J", "K", "L", ";", "'", null, "Enter", null, null, null, "KP 4", "KP 5", "KP 6", null],
        ["M4", "Shift", null, "Z", "X", "C", "V", "B", "N", "M", ",", ".", "/", null, "Shift", null, "↑", null, "KP 1", "KP 2", "KP 3", "KP ↵"],
        ["M5", "Ctrl", "Win", "Alt", null, null, null, "Space", null, null, null, "Alt", "Fn", "Menu", "Ctrl", "←", "↓", "→", null, "KP 0", "KP .", null],
    ];
    static readonly string[] NagaFrameLabels = ["Scroll Wheel", "Logo", "Thumb Grid"];

    [ObservableProperty] public partial Color PaintColor { get; set; } = Color.Parse("#44D62C");

    /// <summary>Key number to the colour under it, for the drawing.</summary>
    [ObservableProperty] public partial IReadOnlyDictionary<int, Color>? KeyColors { get; set; }

    /// <summary>The LED grid for devices whose keys are not drawn.</summary>
    [ObservableProperty] public partial IReadOnlyList<LedCell> Cells { get; set; } = [];

    public int GridColumns => _frame.Columns;

    Rgb Paint => new(PaintColor.R, PaintColor.G, PaintColor.B);

    public void PaintKey(DeviceKey key)
    {
        if (key.Leds.Count == 0)
            return;
        SetFrame(_frame.With(key.Leds, Paint));
    }

    public void PaintCell(LedCell cell) => SetFrame(_frame.With([(cell.Row, cell.Column)], Paint));

    [CommunityToolkit.Mvvm.Input.RelayCommand]
    void FillAll() => SetFrame(CustomFrame.Filled(_frame.Rows, _frame.Columns, Paint));

    [CommunityToolkit.Mvvm.Input.RelayCommand]
    void AllOff() => SetFrame(CustomFrame.Filled(_frame.Rows, _frame.Columns, new Rgb(0, 0, 0)));

    void SetFrame(CustomFrame frame)
    {
        _frame = frame;
        ShowFrame();
        Changed();
    }

    void ShowFrame()
    {
        var colors = new Dictionary<int, Color>();
        foreach (var key in _model?.Keys ?? [])
            if (key.Leds.Count > 0)
            {
                var c = _frame[key.Leds[0].Row, key.Leds[0].Column];
                colors[key.Number] = Color.FromRgb(c.R, c.G, c.B);
            }
        KeyColors = colors;
        var cells = new List<LedCell>();
        for (int r = 0; r < _frame.Rows; r++)
            for (int c = 0; c < _frame.Columns; c++)
            {
                var rgb = _frame[r, c];
                string? label = _model?.ProductId switch
                {
                    0x0221 => BlackWidowFrameLabels[r][c],
                    0x0053 => NagaFrameLabels[c],
                    _ => null,
                };
                double width = _model?.ProductId switch { 0x0221 => 29, 0x0053 => 100, _ => 44 };
                var brush = new SolidColorBrush(Color.FromRgb(rgb.R, rgb.G, rgb.B));
                var labelBrush = rgb.R * 299 + rgb.G * 587 + rgb.B * 114 >= 128_000 ? Brushes.Black : Brushes.White;
                cells.Add(new LedCell(r, c, brush, label, width, labelBrush));
            }
        Cells = cells;
        OnPropertyChanged(nameof(GridColumns));
    }

    public static string ZoneLabel(string zone) => zone switch
    {
        "main" => "Device",
        "logo" => "Logo",
        "scroll" => "Scroll Wheel",
        "right" => "Side Grid",
        "underglow" => "Underglow",
        "left" => "Left Side",
        "backlight" => "Backlight",
        _ => zone,
    };

    readonly Dictionary<string, IReadOnlyList<EffectChoice>> _effects = [];

    /// <summary>The effects a zone takes, built once per zone: the effect boxes are bound to these lists, and a new list
    /// under a box resets it, which puts back the item it held and undoes the pick being made.</summary>
    IReadOnlyList<EffectChoice> EffectsFor(string zone) =>
        _effects.TryGetValue(zone, out var choices) ? choices : _effects[zone] = BuildEffects(zone);

    /// <summary>Synapse's quick effects, then iCUE's, as the box lists them.</summary>
    static readonly (EffectKind Kind, string Label)[] EffectLabels =
    [
        (EffectKind.Static, "Static"), (EffectKind.Breathing, "Breathing"), (EffectKind.Spectrum, "Spectrum Cycling"),
        (EffectKind.Wave, "Wave"), (EffectKind.Wheel, "Wheel"), (EffectKind.Reactive, "Reactive"), (EffectKind.Ripple, "Ripple"),
        (EffectKind.Starlight, "Starlight"), (EffectKind.Fire, "Fire"), (EffectKind.ColorShift, "Color Shift"),
        (EffectKind.ColorPulse, "Color Pulse"), (EffectKind.ColorWave, "Color Wave"), (EffectKind.Visor, "Visor"),
        (EffectKind.Rain, "Rain"), (EffectKind.Watercolor, "Watercolor"), (EffectKind.Marquee, "Marquee"),
        (EffectKind.Sequential, "Sequential"), (EffectKind.Strobing, "Strobing"), (EffectKind.Gradient, "Gradient"),
        (EffectKind.Heartbeat, "Heartbeat"), (EffectKind.Temperature, "Temperature"), (EffectKind.Off, "Off"),
    ];

    /// <summary>The zone's catalogue effect names (static, breath_single, starlight_random, ...).</summary>
    IReadOnlyCollection<string> NativeNames(string zone) =>
        _model?.Zones.TryGetValue(zone, out var z) == true ? z.Effects : ["static", "spectrum", "none"];

    /// <summary>The effects a zone takes: the firmware's own (from the catalogue), and every other one the service draws
    /// for a known device, in the modes either can show.</summary>
    List<EffectChoice> BuildEffects(string zone)
    {
        var names = NativeNames(zone);
        var choices = new List<EffectChoice>();
        foreach (var (kind, label) in EffectLabels)
        {
            if (UsesLegacyLighting && !LegacyLightingCommands.SupportsEffect(kind))
                continue;
            var modes = EffectSupport.NativeModes(kind, names)
                .Union(!UsesLegacyLighting && _model != null && kind != EffectKind.Off ? EffectSupport.SoftwareModes(kind) : [])
                .Distinct().Order().ToList();
            if (modes.Count > 0)
                choices.Add(new EffectChoice(kind, label, modes));
        }
        if (HasFrame)
            choices.Add(new EffectChoice(EffectKind.Custom, _model?.Type == "mouse" ? "Custom (per LED)" : "Custom (per key)", [ColorMode.Single]));
        return choices;
    }

    public void Load(LightingSettings? settings)
    {
        _loading = true;
        try
        {
            _zones = settings?.Zones.ToDictionary(z => z.Key, z => z.Value) ?? [];
            if (UsesLegacyLighting && LegacyFrameSetting() is { } custom)
                foreach (var zone in Zones)
                    _zones[zone] = new ZoneLighting(custom.Effect, _zones.GetValueOrDefault(zone)?.Brightness ?? 100);
            LightingOn = settings?.Off != true;
            OffWhenDisplayOff = settings?.OffWhenDisplayOff == true;
            OffWhenIdle = settings?.OffWhenIdleMinutes > 0;
            IdleMinutes = settings?.OffWhenIdleMinutes > 0 ? settings.OffWhenIdleMinutes : 1;
            var initialColor = _model?.Has("onboard_profiles") == true && _zones.TryGetValue("underglow", out var underglow)
                ? underglow.Effect.Color1 : Rgb.RazerGreen;
            var frame = settings?.Frame;
            bool validFrame = frame != null && frame.Rows == _model?.MatrixRows && frame.Columns == _model?.MatrixColumns &&
                frame.Colors?.Count == frame.Rows * frame.Columns;
            _keepLegacyFrame = UsesLegacyLighting && validFrame;
            _frame = validFrame
                ? frame!
                : CustomFrame.Filled(Math.Max(1, _model?.MatrixRows ?? 1), Math.Max(1, _model?.MatrixColumns ?? 1), initialColor);
            ShowFrame();
            ShowZone(SelectedZone);
        }
        finally
        {
            _loading = false;
        }
    }

    void ShowZone(string zone)
    {
        bool was = _loading;
        _loading = true;
        try
        {
            Effects = EffectsFor(zone);
            var current = _zones.TryGetValue(zone, out var z) ? z : new ZoneLighting(ZoneEffect.Default);
            SelectedEffect = Effects.FirstOrDefault(e => e.Kind == current.Effect.Kind) ?? Effects.FirstOrDefault();
            ColorModes = SelectedEffect?.Modes ?? [];
            SelectedColorMode = ColorModes.Contains(current.Effect.Colors) ? current.Effect.Colors : ColorModes.FirstOrDefault();
            Color1 = Color.FromRgb(current.Effect.Color1.R, current.Effect.Color1.G, current.Effect.Color1.B);
            Color2 = Color.FromRgb(current.Effect.Color2.R, current.Effect.Color2.G, current.Effect.Color2.B);
            Speed = current.Effect.Speed;
            Reverse = current.Effect.Reverse;
            Brightness = current.Brightness;
            Notify();
        }
        finally
        {
            _loading = was;
        }
    }

    void Notify()
    {
        OnPropertyChanged(nameof(EffectTab));
        OnPropertyChanged(nameof(ShowQuick));
        OnPropertyChanged(nameof(ColorModeChoice));
        OnPropertyChanged(nameof(ShowFrameEditor));
        OnPropertyChanged(nameof(ShowBrightness));
        OnPropertyChanged(nameof(ShowKeyCanvas));
        OnPropertyChanged(nameof(ShowLedGrid));
        OnPropertyChanged(nameof(ShowZoneSync));
        OnPropertyChanged(nameof(ShowColor1));
        OnPropertyChanged(nameof(ShowColor2));
        OnPropertyChanged(nameof(ShowModes));
        OnPropertyChanged(nameof(ShowSpeed));
        OnPropertyChanged(nameof(ShowDirection));
        OnPropertyChanged(nameof(EffectHint));
    }

    partial void OnSelectedZoneChanged(string value)
    {
        if (!_loading)
            ShowZone(value);
    }

    partial void OnSelectedEffectChanged(EffectChoice? value)
    {
        if (value != null && !_loading)
        {
            _loading = true;
            ColorModes = value.Modes;
            if (!ColorModes.Contains(SelectedColorMode))
                SelectedColorMode = ColorModes.FirstOrDefault();
            _loading = false;
        }
        Notify();
        Changed();
    }

    partial void OnSelectedColorModeChanged(ColorMode value) { Notify(); Changed(); }
    partial void OnColor1Changed(Color value) => Changed();
    partial void OnColor2Changed(Color value) => Changed();
    partial void OnSpeedChanged(double value) => Changed();
    partial void OnReverseChanged(bool value) => Changed();
    partial void OnBrightnessChanged(double value) => Changed();
    partial void OnSyncAllChanged(bool value) => Changed();

    void Changed()
    {
        if (_loading || SelectedEffect == null)
            return;
        var effect = new ZoneEffect(SelectedEffect.Kind, new Rgb(Color1.R, Color1.G, Color1.B), new Rgb(Color2.R, Color2.G, Color2.B),
            SelectedColorMode, (int)Speed, Reverse);
        var lighting = new ZoneLighting(effect, (int)Brightness);
        bool legacyFrame = UsesLegacyLighting && (effect.Kind == EffectKind.Custom || LegacyFrameSetting() != null);
        bool globalFrame = legacyFrame || _model?.Has("onboard_profiles") == true &&
            (effect.Kind == EffectKind.Custom || _zones.Values.Any(z => z.Effect.Kind == EffectKind.Custom));
        _keepLegacyFrame |= legacyFrame;
        foreach (var zone in SyncAll || globalFrame ? Zones : [SelectedZone])
        {
            // A synced effect a zone cannot show (wave on a logo) leaves that zone as it was.
            if (zone == SelectedZone || EffectsFor(zone).Any(e => e.Kind == effect.Kind))
                _zones[zone] = legacyFrame && zone != SelectedZone
                    ? lighting with { Brightness = _zones.GetValueOrDefault(zone)?.Brightness ?? 100 }
                    : lighting;
        }
        _debounce.Poke();
    }

    void Commit() => _save(Current());

    internal bool HasPendingEdits => _debounce.IsPending;

    internal LightingSettings? TakePendingSettings() => _debounce.StopPending() ? Current() : null;

    ZoneLighting? LegacyFrameSetting() => Zones.Select(zone => _zones.GetValueOrDefault(zone))
        .FirstOrDefault(setting => setting?.Effect.Kind == EffectKind.Custom);

    LightingSettings Current() => new(new Dictionary<string, ZoneLighting>(_zones),
        _keepLegacyFrame || _zones.Values.Any(z => z.Effect.Kind == EffectKind.Custom) ? _frame : null,
        Off: !LightingOn, OffWhenDisplayOff: OffWhenDisplayOff, OffWhenIdleMinutes: OffWhenIdle ? (int)IdleMinutes : 0);

    // ---- Synapse 4 cards: the Brightness switch, Switch off lighting, Quick and Advanced effects ---------

    [ObservableProperty] public partial bool LightingOn { get; set; } = true;
    [ObservableProperty] public partial bool OffWhenDisplayOff { get; set; }
    [ObservableProperty] public partial bool OffWhenIdle { get; set; }
    [ObservableProperty] public partial double IdleMinutes { get; set; } = 1;

    public static IReadOnlyList<string> EffectTabs { get; } = ["Quick Effects", "Advanced Effects"];

    /// <summary>Advanced is Synapse's Chroma Studio; here it is the per-key frame.</summary>
    public string EffectTab
    {
        get => SelectedEffect?.Kind == EffectKind.Custom ? EffectTabs[1] : EffectTabs[0];
        set
        {
            if (value == EffectTab)
                return;
            SelectedEffect = value == EffectTabs[1]
                ? Effects.FirstOrDefault(e => e.Kind == EffectKind.Custom)
                : Effects.FirstOrDefault(e => e.Kind == EffectKind.Spectrum) ?? Effects.FirstOrDefault(e => e.Kind != EffectKind.Custom);
        }
    }

    public bool ShowQuick => EffectTab == EffectTabs[0];

    /// <summary>The quick effects, without the per-key one (that is the Advanced side). Made only when the zone's effects
    /// change, never on a read, for the reason given on <see cref="EffectsFor"/>.</summary>
    [ObservableProperty] public partial IReadOnlyList<EffectChoice> QuickEffects { get; private set; } = [];

    partial void OnEffectsChanged(IReadOnlyList<EffectChoice> value) => QuickEffects = value.Where(e => e.Kind != EffectKind.Custom).ToList();

    partial void OnLightingOnChanged(bool value) => Changed();
    partial void OnOffWhenDisplayOffChanged(bool value) => Changed();
    partial void OnOffWhenIdleChanged(bool value) => Changed();
    partial void OnIdleMinutesChanged(double value) => Changed();

    /// <summary>Synapse's "Apply to other Chroma-enabled devices": the chosen quick effect goes to every other device
    /// that can show it.</summary>
    public event Action<ZoneEffect>? ApplyToOthers;

    [CommunityToolkit.Mvvm.Input.RelayCommand]
    void ApplyToOtherDevices()
    {
        if (SelectedEffect is { Kind: not EffectKind.Custom } effect)
            ApplyToOthers?.Invoke(new ZoneEffect(effect.Kind, new Rgb(Color1.R, Color1.G, Color1.B), new Rgb(Color2.R, Color2.G, Color2.B), SelectedColorMode, (int)Speed, Reverse));
    }

    /// <summary>Takes a quick effect from another device: every zone that can show it.</summary>
    public void Take(ZoneEffect effect)
    {
        foreach (var zone in Zones)
            if (EffectsFor(zone).Any(e => e.Kind == effect.Kind))
                _zones[zone] = new ZoneLighting(effect, _zones.TryGetValue(zone, out var z) ? z.Brightness : 100);
        Load(Current());
        _debounce.Poke();
    }
}

/// <summary>One row of the sensitivity stages.</summary>
public sealed partial class StageRow(int number, PerformanceViewModel owner) : ObservableObject
{
    public int Number { get; } = number;
    public int DpiMax => owner.DpiMax;
    public int DpiStep => owner.DpiStep;
    public bool SeparateXY => owner.SeparateXY;

    internal void SeparateXYChanged() => OnPropertyChanged(nameof(SeparateXY));
    [ObservableProperty] public partial double X { get; set; }
    [ObservableProperty] public partial double Y { get; set; }
    [ObservableProperty] public partial bool Active { get; set; }

    partial void OnXChanged(double value) => owner.StageChanged(this, x: true);
    partial void OnYChanged(double value) => owner.StageChanged(this, x: false);
}

/// <summary>The PERFORMANCE page: sensitivity stages (X and Y tied unless untied) and the polling rate.</summary>
public sealed partial class PerformanceViewModel : ObservableObject
{
    readonly Action<PerformanceSettings> _save;
    readonly Debounce _debounce;
    bool _loading;

    public PerformanceViewModel(DeviceModel? model, Action<PerformanceSettings> save)
    {
        _save = save;
        _debounce = new Debounce(Commit);
        DpiMax = model?.DpiMax > 0 ? model.DpiMax : 20000;
        DpiStep = model?.Has("onboard_profiles") == true ? 1 : 50;
        PollRates = model?.PollRates.Count > 0 ? model.PollRates : [125, 500, 1000];
        HasStages = model?.Has("dpi_stages") == true;
    }

    public int DpiMax { get; }
    public int DpiStep { get; }
    public bool HasStages { get; }
    public IReadOnlyList<int> PollRates { get; }
    public ObservableCollection<StageRow> Stages { get; } = [];

    [ObservableProperty] public partial double StageCount { get; set; } = 5;
    [ObservableProperty] public partial bool SeparateXY { get; set; }
    [ObservableProperty] public partial int PollingRate { get; set; }
    [ObservableProperty] public partial int ActiveStage { get; set; }

    public void Load(PerformanceSettings settings)
    {
        _loading = true;
        try
        {
            while (Stages.Count > settings.Stages.Count)
                Stages.RemoveAt(Stages.Count - 1);
            for (int i = 0; i < settings.Stages.Count; i++)
            {
                if (i == Stages.Count)
                    Stages.Add(new StageRow(i + 1, this));
                var row = Stages[i];
                (row.X, row.Y, row.Active) = (settings.Stages[i].X, settings.Stages[i].Y, i == settings.ActiveStage);
            }
            StageCount = settings.Stages.Count;
            ActiveStage = settings.ActiveStage;
            SeparateXY = settings.Stages.Any(s => s.X != s.Y);
            // 0 = not known: nothing is shown chosen rather than a guess.
            PollingRate = settings.PollingRate;
            // The count often stays the same across a load, which raises nothing; the switch must still re-read it.
            OnPropertyChanged(nameof(StagesEnabled));
        }
        finally
        {
            _loading = false;
        }
    }

    internal void StageChanged(StageRow row, bool x)
    {
        if (_loading)
            return;
        if (!SeparateXY)
        {
            _loading = true;
            if (x) row.Y = row.X; else row.X = row.Y;
            _loading = false;
        }
        _debounce.Poke();
    }

    public void Activate(StageRow row)
    {
        foreach (var s in Stages)
            s.Active = s == row;
        ActiveStage = row.Number - 1;
    }

    partial void OnSeparateXYChanged(bool value)
    {
        foreach (var s in Stages)
            s.SeparateXYChanged();
        if (!_loading)
            _debounce.Poke();
    }

    partial void OnActiveStageChanged(int value) { if (!_loading) _debounce.Poke(); }
    partial void OnPollingRateChanged(int value) { if (!_loading) _debounce.Poke(); }

    /// <summary>Synapse's "Sensitivity Stages" switch: off keeps one sensitivity (the active stage), on brings the
    /// stages back as they were.</summary>
    public bool StagesEnabled
    {
        get => Stages.Count > 1;
        set
        {
            if (value == StagesEnabled)
                return;
            if (value)
            {
                StageCount = Math.Max(2, _stagesBefore);
            }
            else
            {
                _stagesBefore = Stages.Count;
                var keep = Stages[Math.Clamp(ActiveStage, 0, Stages.Count - 1)];
                Stages.Clear();
                Stages.Add(new StageRow(1, this) { X = keep.X, Y = keep.Y, Active = true });
                ActiveStage = 0;
                StageCount = 1;
                _debounce.Poke();
            }
            OnPropertyChanged();
        }
    }

    int _stagesBefore = PerformanceSettings.MaxStages;

    [CommunityToolkit.Mvvm.Input.RelayCommand]
    void AddStage() => StageCount = Math.Min(PerformanceSettings.MaxStages, Stages.Count + 1);

    [CommunityToolkit.Mvvm.Input.RelayCommand]
    void RemoveStage() => StageCount = Math.Max(1, Stages.Count - 1);

    partial void OnStageCountChanged(double value)
    {
        OnPropertyChanged(nameof(StagesEnabled));
        if (_loading)
            return;
        int count = Math.Clamp((int)value, 1, PerformanceSettings.MaxStages);
        while (Stages.Count < count)
        {
            int dpi = Math.Min(DpiMax, (int)(Stages.LastOrDefault()?.X ?? 400) * 2);
            Stages.Add(new StageRow(Stages.Count + 1, this) { X = dpi, Y = dpi });
        }
        while (Stages.Count > count)
            Stages.RemoveAt(Stages.Count - 1);
        if (ActiveStage >= count)
            Activate(Stages[^1]);
        _debounce.Poke();
    }

    void Commit() => _save(Current());

    internal bool HasPendingEdits => _debounce.IsPending;

    internal PerformanceSettings? TakePendingSettings() => _debounce.StopPending() ? Current() : null;

    PerformanceSettings Current()
    {
        var stages = Stages.Select(s => new DpiStage(Round(s.X), Round(SeparateXY ? s.Y : s.X))).ToList();
        return new PerformanceSettings(stages, Math.Clamp(ActiveStage, 0, Math.Max(0, stages.Count - 1)), PollingRate);
    }

    int Round(double dpi) => Math.Clamp((int)Math.Round(dpi / DpiStep) * DpiStep, 100, DpiMax);
}

/// <summary>The POWER page: battery, sleep timer, low-power warning.</summary>
public sealed partial class PowerViewModel(Action<PowerSettings> save) : ObservableObject
{
    bool _loading;

    [ObservableProperty] public partial double SleepMinutes { get; set; } = 5;
    [ObservableProperty] public partial double LowBattery { get; set; } = 15;
    public void Load(PowerSettings settings)
    {
        _loading = true;
        SleepMinutes = settings.SleepSeconds / 60.0;
        LowBattery = settings.LowBatteryPercent;
        _loading = false;
    }

    partial void OnSleepMinutesChanged(double value) => Changed();
    partial void OnLowBatteryChanged(double value) => Changed();

    void Changed()
    {
        if (!_loading)
            save(new PowerSettings((int)(SleepMinutes * 60), (int)LowBattery));
    }
}

/// <summary>The Scroll Wheel card on a mouse's Customize page: scroll speed (lines per notch, done in software, so any
/// mouse has it) and, where the firmware has them, scroll acceleration and Smart-Reel.</summary>
public sealed partial class ScrollWheelViewModel(Action<ScrollSettings> save) : ObservableObject
{
    bool _loading;

    public sealed record WheelModeOption(string Label, ScrollWheelMode? Mode);

    public IReadOnlyList<WheelModeOption> WheelModes { get; } =
        [new("Use device setting", null), new("Tactile", ScrollWheelMode.Tactile), new("Free-spin", ScrollWheelMode.FreeSpin),
            new("Precision Tactile", ScrollWheelMode.PrecisionTactile)];
    [ObservableProperty] public partial double LinesPerNotch { get; set; } = 1;
    [ObservableProperty] public partial bool Acceleration { get; set; }
    [ObservableProperty] public partial bool SmartReel { get; set; }
    [ObservableProperty] public partial WheelModeOption? WheelMode { get; set; }
    [ObservableProperty, NotifyPropertyChangedFor(nameof(HasReportedWheelMode))]
    public partial string? ReportedWheelMode { get; private set; }
    public bool HasReportedWheelMode => ReportedWheelMode != null;

    public void Load(ScrollSettings settings, ScrollWheelMode? reportedMode = null)
    {
        _loading = true;
        LinesPerNotch = settings.LinesPerNotch;
        Acceleration = settings.Acceleration;
        SmartReel = settings.SmartReel;
        WheelMode = WheelModes.FirstOrDefault(option => option.Mode == settings.WheelMode) ?? WheelModes[0];
        ReportedWheelMode = reportedMode == null ? null :
            WheelModes.FirstOrDefault(option => option.Mode == reportedMode)?.Label;
        _loading = false;
    }

    partial void OnLinesPerNotchChanged(double value) => Changed();
    partial void OnAccelerationChanged(bool value) => Changed();
    partial void OnSmartReelChanged(bool value) => Changed();
    partial void OnWheelModeChanged(WheelModeOption? value)
    {
        if (value != null)
            Changed();
    }

    void Changed()
    {
        if (!_loading)
            save(new ScrollSettings(Math.Clamp((int)Math.Round(LinesPerNotch), 1, ScrollSettings.MaxLinesPerNotch),
                Acceleration, SmartReel, WheelMode?.Mode));
    }
}

/// <summary>The CALIBRATION page: Smart Tracking on or off (off leaves the sensor at its own default) and the tracking
/// distance LOW / MEDIUM / HIGH (1, 2, 3 mm).</summary>
public sealed partial class CalibrationViewModel(Action<CalibrationSettings> save) : ObservableObject
{
    bool _loading;

    [ObservableProperty] public partial bool SmartTracking { get; set; }

    /// <summary>0 LOW, 1 MEDIUM, 2 HIGH.</summary>
    [ObservableProperty] public partial double Distance { get; set; } = 1;

    public void Load(CalibrationSettings settings)
    {
        _loading = true;
        SmartTracking = settings.LiftOffLevel >= 0;
        Distance = settings.LiftOffLevel >= 0 ? settings.LiftOffLevel : 1;
        _loading = false;
    }

    partial void OnSmartTrackingChanged(bool value) => Save();
    partial void OnDistanceChanged(double value) => Save();

    void Save()
    {
        if (!_loading)
            save(new CalibrationSettings(SmartTracking ? (int)Math.Round(Distance) : -1));
    }
}
