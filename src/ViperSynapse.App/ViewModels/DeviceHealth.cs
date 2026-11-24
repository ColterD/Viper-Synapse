using ViperSynapse.App.Services;
using ViperSynapse.Core.Devices;

namespace ViperSynapse.App.ViewModels;

internal sealed record DeviceHealthFeature(string Name, string Status, string Reason, string Verification);

internal sealed record DeviceHealth(string Model, string Connection, string ControlStatus, string InputStatus,
    string? Issue, IReadOnlyList<DeviceHealthFeature> Features)
{
    const string Available = "Available";
    const string Unavailable = "Unavailable";

    internal static DeviceHealth From(DeviceInfo info, DeviceModel? model, bool genericControls)
    {
        bool bluetoothLimited = info.Connection == "bluetooth";
        string connection = ConnectionLabel(info);
        var control = ControlAccess(info, bluetoothLimited);
        var input = InputAccess(info, genericControls);
        bool basilisk = model?.ProductId is DeviceCatalogue.BasiliskV4ProWired or DeviceCatalogue.BasiliskV4ProReceiver or DeviceCatalogue.BasiliskV4ProBluetooth;
        bool receiver = basilisk && info.Connection == "hyperspeed";
        string? Emulated(string scope) => basilisk ? $"Emulated: {scope}." : null;
        bool onboard = model?.Has("onboard_profiles") == true;
        var onboardFeature = Firmware("Onboard profiles and firmware buttons", onboard, Emulated("slot management, assignments and backup/restore"));
        if (onboard && !bluetoothLimited && info.Connection is not ("wired" or "hyperspeed"))
            onboardFeature = onboardFeature with { Status = Unavailable, Reason = "Onboard management requires wired USB or HyperSpeed." };
        else if (onboard && control.Status == Available && info.OnboardProfile == null)
            onboardFeature = onboardFeature with { Status = "Unconfirmed", Reason = "Viper has not confirmed the active onboard profile." };
        DeviceHealthFeature[] macroKeys = model?.ProductId == LegacyRemapControls.BlackWidowChromaV2
            ? [info.NativeMacroKeys && info.InputIssue == null
                ? new("M1–M5", Available, "Uses separate native reports for macro keys and ordinary keys.", "Report routing tested in emulation.")
                : new("M1–M5", info.InputControlsStatus == "available" && info.InputControls?.Any(LegacyRemapControls.IsMacroSource) == true
                    ? "Driver-dependent" : Unavailable,
                    "The native macro reader is unavailable. Viper can use F13–F17 if the driver exposes them, but cannot recover held keys the driver releases early.",
                    "Physical driver behavior unverified.")]
            : [];
        DeviceHealthFeature[] detentTilt = info.NagaTiltDetentsOnly
            ? [new("Tilt wheel", "Detents only", "At least one direction reports wheel detents. Held actions and shortcuts that start by holding tilt require button press and release events.",
                "Event routing tested in emulation.")]
            : info.NagaTiltUsesEvdev
            ? [new("Tilt wheel", "Driver-dependent", "Viper can act on wheel detents. Holding tilt for an action or shortcut requires the driver to send button press and release events.",
                "Event routing tested in emulation.")]
            : [];

        return new(info.Name, connection, control.Status, input.Status, info.Problem,
        [
            new("Software remapping", input.Status, input.Reason,
                Emulated("Linux input routing on this connection") ?? "Feature-specific verification is not recorded."),
            .. macroKeys,
            .. detentTilt,
            Firmware(model?.Has("dpi_stages") == true ? "DPI stages" : "DPI sensitivity", model?.Has("dpi") == true,
                receiver ? "Hardware: DPI table read. Stage changes tested in emulation." : Emulated("DPI stage updates")),
            Firmware("Polling rate", model?.Has("poll_rate") == true,
                receiver ? "Hardware: polling rate read." : Emulated("polling rate read")),
            Firmware("Lighting", model?.Zones.Count > 0 || model?.Type == "keypad",
                receiver ? "Hardware: quick-effect zones. Custom frames and slot settings tested in emulation." : Emulated("lighting reports")),
            Firmware("Power settings", model?.Has("idle_time") == true, Emulated("power settings read")),
            Firmware("Lift-off calibration", model?.Has("lift_off") == true),
            Firmware("Firmware wheel settings", model?.Has("scroll_wheel") == true,
                receiver ? "Hardware: Smart-Reel and acceleration toggles. Wheel mode tested in emulation." : Emulated("wheel mode and toggles")),
            onboardFeature,
        ]);

        DeviceHealthFeature Firmware(string name, bool supported, string? verification = null)
        {
            if (supported)
                return new(name, control.Status, control.Reason, bluetoothLimited
                    ? "Unavailable on this connection."
                    : verification ?? "Feature-specific verification is not recorded.");

            return new(name, "Not supported", model == null
                ? "Viper has no compatibility information for this model."
                : "Viper does not support this feature on this model.", "");
        }
    }

    internal static string ConnectionLabel(DeviceInfo info) => info.Connection switch
    {
        "wired" => "Wired USB",
        "hyperspeed" => "HyperSpeed",
        "bluetooth" => "Bluetooth",
        _ => info.Transport switch { "usb" => "USB", "bluetooth" => "Bluetooth", _ => "Unknown" },
    };

    internal static string? AttentionReason(DeviceInfo info, DeviceModel? model)
    {
        bool bluetooth = info.Connection == "bluetooth";
        var input = InputAccess(info, bluetooth || model?.Keys.Count is null or 0);
        if (input.Status is "Unavailable" or "Needs attention")
            return input.Reason;
        if (!string.IsNullOrWhiteSpace(info.Problem))
            return info.Problem;
        if (!string.IsNullOrWhiteSpace(info.ControlIssue))
            return info.ControlIssue;
        if (model != null)
        {
            var control = ControlAccess(info, bluetooth);
            if (control.Status != Available)
                return control.Reason;
        }
        return null;
    }

    static (string Status, string Reason) ControlAccess(DeviceInfo info, bool bluetoothLimited)
    {
        if (bluetoothLimited)
            return (Unavailable, "Viper cannot configure this device over Bluetooth. Use wired USB or HyperSpeed.");
        if (info.ControlReady == true)
            return (Available, "Control interface responded.");
        if (info.ControlIssue is { Length: > 0 } issue)
            return (Unavailable, issue);
        if (info.ControlNode == null)
            return (Unavailable, "No compatible control interface was detected.");

        return info.ControlReady == false
            ? (Unavailable, "Viper could not establish communication with the control interface.")
            : ("Unconfirmed", "The service has not reported whether the control interface responded.");
    }

    static (string Status, string Reason) InputAccess(DeviceInfo info, bool genericControls)
    {
        if (info.InputNodes.Count == 0)
            return (Unavailable, "No Linux input interface was detected for this connection.");
        if (info.InputIssue is { Length: > 0 } issue)
            return ("Needs attention", issue);
        if (info.Remapping)
            return (Available, "A remapping session is active.");
        if (!genericControls)
            return ("Detected", "Linux input interfaces were found. Viper checks access when remapping starts.");

        return info.InputControlsStatus switch
        {
            "available" when info.InputControls?.Count > 0 => (Available, "Linux reports controls Viper can remap."),
            "available" => (Unavailable, "Linux reports no compatible controls to remap."),
            "partial" => (Unavailable, "Viper could read only some Linux input interfaces. Check device access permissions."),
            "permission" => (Unavailable, "Viper lacks permission to read the Linux input interfaces."),
            "unavailable" => (Unavailable, "Viper could not read the Linux input interfaces."),
            _ => ("Unconfirmed", "The service has not reported whether the Linux input interfaces are readable."),
        };
    }
}
