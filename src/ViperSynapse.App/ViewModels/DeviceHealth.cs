using ViperSynapse.App.Services;
using ViperSynapse.Core.Devices;

namespace ViperSynapse.App.ViewModels;

internal sealed record DeviceHealthFeature(string Name, string Status, string Reason);

internal sealed record DeviceHealth(string Model, string Connection, string ControlStatus, string InputStatus,
    string? Issue, IReadOnlyList<DeviceHealthFeature> Features)
{
    const string Available = "Available";
    const string Unavailable = "Unavailable";

    internal static DeviceHealth From(DeviceInfo info, DeviceModel? model, bool genericControls)
    {
        bool bluetoothLimited = info.Connection == "bluetooth";
        string connection = info.Connection switch
        {
            "wired" => "Wired USB",
            "hyperspeed" => "HyperSpeed",
            "bluetooth" => "Bluetooth",
            _ => info.Transport switch { "usb" => "USB", "bluetooth" => "Bluetooth", _ => "Unknown" },
        };
        var control = ControlAccess(info, bluetoothLimited);
        var input = InputAccess(info, genericControls);
        bool onboard = model?.Has("onboard_profiles") == true;
        var onboardFeature = Firmware("Onboard profiles and firmware buttons", onboard);
        if (onboard && !bluetoothLimited && info.Connection is not ("wired" or "hyperspeed"))
            onboardFeature = new(onboardFeature.Name, Unavailable, "Onboard management requires a supported wired USB or HyperSpeed connection.");
        else if (onboard && control.Status == Available && info.OnboardProfile == null)
            onboardFeature = new(onboardFeature.Name, "Unconfirmed", "Viper has not confirmed the active onboard profile.");

        return new(info.Name, connection, control.Status, input.Status, info.Problem,
        [
            new("Software remapping", input.Status, input.Reason),
            Firmware(model?.Has("dpi_stages") == true ? "DPI stages" : "DPI sensitivity", model?.Has("dpi") == true),
            Firmware("Polling rate", model?.Has("poll_rate") == true),
            Firmware("Lighting", model?.Zones.Count > 0 || model?.Type == "keypad"),
            Firmware("Power settings", model?.Has("idle_time") == true),
            Firmware("Lift-off calibration", model?.Has("lift_off") == true),
            Firmware("Firmware wheel settings", model?.Has("scroll_wheel") == true),
            onboardFeature,
        ]);

        DeviceHealthFeature Firmware(string name, bool supported)
        {
            if (supported)
                return control.Status == Available && !info.Verified
                    ? new(name, "Unconfirmed", "Control interface responded. Hardware verification for this connection is pending.")
                    : new(name, control.Status, control.Reason);

            return new(name, "Not supported", model == null
                ? "Viper has no compatibility information for this model."
                : "Viper does not support this feature on this model.");
        }
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
