using System.Globalization;
using Avalonia.Data.Converters;
using Avalonia.Input;
using Avalonia.Media;
using ViperSynapse.Core.Input;

namespace ViperSynapse.App.Views;

public static class Converters
{
    /// <summary>True when the bound enum's name equals the parameter: shows the flyout section for one function.</summary>
    public static readonly IValueConverter Is = new FuncValueConverter<object?, string?, bool>((value, parameter) =>
        value?.ToString() == parameter);

    public static readonly IValueConverter IsEmpty = new FuncValueConverter<int, bool>(count => count == 0);
    public static readonly IValueConverter HasItems = new FuncValueConverter<int, bool>(count => count > 0);

    public static readonly IValueConverter AvailableStatus = new FuncValueConverter<string, bool>(status => status == "Available");
    public static readonly IValueConverter WarningStatus = new FuncValueConverter<string, bool>(status => status is "Needs attention" or "Unconfirmed");
    public static readonly IValueConverter UnavailableStatus = new FuncValueConverter<string, bool>(status => status == "Unavailable");

    /// <summary>Keyboard and mouse functions offer Turbo.</summary>
    public static readonly IValueConverter IsTurbo = new FuncValueConverter<object?, bool>(value =>
        value?.ToString() is "Keyboard" or "Mouse");

    /// <summary>Full strength when true, faded when false: controls the device handles by itself.</summary>
    public static readonly IValueConverter Dim = new FuncValueConverter<bool, double>(on => on ? 1 : 0.5);

    public static readonly IValueConverter ZoneLabel = new FuncValueConverter<string?, string>(z => ViewModels.LightingViewModel.ZoneLabel(z ?? ""));

    public static readonly IValueConverter StickDirections = new FuncValueConverter<ViperSynapse.Core.Bindings.ThumbstickMode, int>(m => m switch
    {
        ViperSynapse.Core.Bindings.ThumbstickMode.FourWay => 4,
        ViperSynapse.Core.Bindings.ThumbstickMode.EightWay => 8,
        _ => 0,
    });

    // Filled paths use a 24-unit grid and cut-outs so the silhouettes remain legible at tab size.
    public static readonly IValueConverter TabIconGeometry = new FuncValueConverter<ViewModels.TabIcon, Geometry?>(icon =>
    {
        var path = icon switch
        {
            ViewModels.TabIcon.Devices => "F0 M3 2 H21 Q23 2 23 4 V16 Q23 18 21 18 H14 V20 H17 V22 H7 V20 H10 V18 H3 Q1 18 1 16 V4 Q1 2 3 2 Z M4 5 V15 H20 V5 Z M7 8 H10 V12 H7 Z M14 8 H17 V12 H14 Z",
            ViewModels.TabIcon.LinkedGames => "F0 M7 5 H17 C20 5 21 7 22 10 L23 16 C23.6 19 21 21 19 19 L16 16 H8 L5 19 C3 21 0.4 19 1 16 L2 10 C3 7 4 5 7 5 Z M6.5 8 V10 H4.5 V12 H6.5 V14 H8.5 V12 H10.5 V10 H8.5 V8 Z M15 10 A1.2 1.2 0 1 0 17.4 10 A1.2 1.2 0 1 0 15 10 Z M18 13 A1.2 1.2 0 1 0 20.4 13 A1.2 1.2 0 1 0 18 13 Z",
            ViewModels.TabIcon.Macro => "M11 2 H19 L14 9 H21 L9 23 L12 13 H5 Z M2 5 H7 V7 H2 Z M2 10 H5 V12 H2 Z M2 16 H7 V18 H2 Z",
            ViewModels.TabIcon.Settings => "F0 M10 1 H14 L14.7 4.3 L16.2 5 L19 3.2 L21.8 6 L20 8.8 L20.7 10.3 L24 11 V14 L20.7 14.7 L20 16.2 L21.8 19 L19 21.8 L16.2 20 L14.7 20.7 L14 24 H10 L9.3 20.7 L7.8 20 L5 21.8 L2.2 19 L4 16.2 L3.3 14.7 L0 14 V11 L3.3 10.3 L4 8.8 L2.2 6 L5 3.2 L7.8 5 L9.3 4.3 Z M12 8 A4.5 4.5 0 1 0 12 17 A4.5 4.5 0 1 0 12 8 Z",
            ViewModels.TabIcon.Mouse => "F0 M12 1 C7.6 1 4 4.6 4 9 V15 C4 19.4 7.6 23 12 23 C16.4 23 20 19.4 20 15 V9 C20 4.6 16.4 1 12 1 Z M11 4 H13 V10 H11 Z M6 12 H18 V14 H6 Z",
            ViewModels.TabIcon.Keyboard => "F0 M3 4 H21 Q23 4 23 6 V18 Q23 20 21 20 H3 Q1 20 1 18 V6 Q1 4 3 4 Z M4 7 V9 H7 V7 Z M9 7 V9 H11 V7 Z M13 7 V9 H15 V7 Z M17 7 V9 H20 V7 Z M4 11 V13 H7 V11 Z M9 11 V13 H11 V11 Z M13 11 V13 H15 V11 Z M17 11 V13 H20 V11 Z M6 15 V17 H18 V15 Z",
            ViewModels.TabIcon.Keypad => "F0 M5 1 H17 Q20 1 20 4 V12 L23 16 V20 Q23 22 21 22 H5 Q2 22 2 19 V4 Q2 1 5 1 Z M5 5 V8 H8 V5 Z M10 5 V8 H13 V5 Z M15 5 V8 H18 V5 Z M5 10 V13 H8 V10 Z M10 10 V13 H13 V10 Z M15 10 V13 H18 V10 Z M6 16 V19 H14 V16 Z",
            ViewModels.TabIcon.Device => "F0 M3 3 H21 Q23 3 23 5 V19 Q23 21 21 21 H3 Q1 21 1 19 V5 Q1 3 3 3 Z M5 7 V13 H19 V7 Z M5 16 V18 H7 V16 Z M9 16 V18 H11 V16 Z",
            _ => null,
        };
        return path is null ? null : StreamGeometry.Parse(path);
    });

    public static readonly IValueConverter Equal = new FuncValueConverter<object?, object?, bool>((value, parameter) =>
        Equals(value?.ToString(), parameter?.ToString()));
}

/// <summary>Physical keys as Avalonia reports them, as Linux key codes: what the "press a key" box records.</summary>
public static class PhysicalKeys
{
    public static ushort ToLinux(PhysicalKey key)
    {
        string name = key switch
        {
            >= PhysicalKey.A and <= PhysicalKey.Z => $"KEY_{key}",
            >= PhysicalKey.Digit0 and <= PhysicalKey.Digit9 => "KEY_" + key.ToString()[5..],
            >= PhysicalKey.F1 and <= PhysicalKey.F24 => $"KEY_{key}",
            >= PhysicalKey.NumPad0 and <= PhysicalKey.NumPad9 => "KEY_KP" + key.ToString()[6..],
            PhysicalKey.Escape => "KEY_ESC",
            PhysicalKey.Backquote => "KEY_GRAVE",
            PhysicalKey.Minus => "KEY_MINUS",
            PhysicalKey.Equal => "KEY_EQUAL",
            PhysicalKey.Backspace => "KEY_BACKSPACE",
            PhysicalKey.Tab => "KEY_TAB",
            PhysicalKey.BracketLeft => "KEY_LEFTBRACE",
            PhysicalKey.BracketRight => "KEY_RIGHTBRACE",
            PhysicalKey.Backslash => "KEY_BACKSLASH",
            PhysicalKey.CapsLock => "KEY_CAPSLOCK",
            PhysicalKey.Semicolon => "KEY_SEMICOLON",
            PhysicalKey.Quote => "KEY_APOSTROPHE",
            PhysicalKey.Enter => "KEY_ENTER",
            PhysicalKey.ShiftLeft => "KEY_LEFTSHIFT",
            PhysicalKey.ShiftRight => "KEY_RIGHTSHIFT",
            PhysicalKey.Comma => "KEY_COMMA",
            PhysicalKey.Period => "KEY_DOT",
            PhysicalKey.Slash => "KEY_SLASH",
            PhysicalKey.ControlLeft => "KEY_LEFTCTRL",
            PhysicalKey.ControlRight => "KEY_RIGHTCTRL",
            PhysicalKey.AltLeft => "KEY_LEFTALT",
            PhysicalKey.AltRight => "KEY_RIGHTALT",
            PhysicalKey.MetaLeft => "KEY_LEFTMETA",
            PhysicalKey.MetaRight => "KEY_RIGHTMETA",
            PhysicalKey.Space => "KEY_SPACE",
            PhysicalKey.ContextMenu => "KEY_COMPOSE",
            PhysicalKey.Insert => "KEY_INSERT",
            PhysicalKey.Delete => "KEY_DELETE",
            PhysicalKey.Home => "KEY_HOME",
            PhysicalKey.End => "KEY_END",
            PhysicalKey.PageUp => "KEY_PAGEUP",
            PhysicalKey.PageDown => "KEY_PAGEDOWN",
            PhysicalKey.ArrowUp => "KEY_UP",
            PhysicalKey.ArrowDown => "KEY_DOWN",
            PhysicalKey.ArrowLeft => "KEY_LEFT",
            PhysicalKey.ArrowRight => "KEY_RIGHT",
            PhysicalKey.PrintScreen => "KEY_SYSRQ",
            PhysicalKey.ScrollLock => "KEY_SCROLLLOCK",
            PhysicalKey.Pause => "KEY_PAUSE",
            PhysicalKey.NumLock => "KEY_NUMLOCK",
            PhysicalKey.NumPadDivide => "KEY_KPSLASH",
            PhysicalKey.NumPadMultiply => "KEY_KPASTERISK",
            PhysicalKey.NumPadSubtract => "KEY_KPMINUS",
            PhysicalKey.NumPadAdd => "KEY_KPPLUS",
            PhysicalKey.NumPadEnter => "KEY_KPENTER",
            PhysicalKey.NumPadDecimal => "KEY_KPDOT",
            PhysicalKey.IntlBackslash => "KEY_102ND",
            _ => "",
        };
        return KeyCodes.TryParse(name.ToUpper(CultureInfo.InvariantCulture), out var code) ? code : (ushort)0;
    }
}
