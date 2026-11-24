using ViperSynapse.Core.Bindings;
using ViperSynapse.Core.Devices;
using ViperSynapse.Core.Engine;
using ViperSynapse.Core.Input;

namespace ViperSynapse.Hardware.Linux;

public sealed record InputCapabilities(IReadOnlyList<ushort> Keys, IReadOnlyList<ushort> Relative);
public sealed record DiscoveredControls(IReadOnlyList<ushort> Controls, string Status, bool HorizontalHighResOnly = false);

public static class InputControlDiscovery
{
    /// <summary>Offer supported virtual controls and model-specific sources handled without virtual key output.</summary>
    public static IReadOnlyList<ushort> Select(IEnumerable<InputCapabilities> nodes, ushort? wiredProduct = null)
    {
        var controls = new SortedSet<ushort>();
        var supported = KeyCodes.All.ToHashSet();
        foreach (var node in nodes)
        {
            if (node.Keys.Any(KeyCodes.IsJoystickButton))
                continue;
            foreach (var key in node.Keys.Where(k => KeyCodes.IsKeyboardOrMouse(k) && supported.Contains(k) ||
                wiredProduct == LegacyRemapControls.NagaChroma && LegacyRemapControls.IsNagaDpiSource(k)))
                controls.Add(key);
            if (node.Relative.Contains(RelativeAxes.Wheel))
            {
                controls.Add(SourceKeys.WheelUp);
                controls.Add(SourceKeys.WheelDown);
            }
            if (node.Relative.Contains(RelativeAxes.HWheel) ||
                wiredProduct == LegacyRemapControls.NagaChroma && node.Relative.Contains(RelativeAxes.HWheelHiRes))
            {
                controls.Add(SourceKeys.WheelLeft);
                controls.Add(SourceKeys.WheelRight);
            }
        }
        return controls.ToArray();
    }

    /// <summary>Read advertised event bits only; never grab or read an input event.</summary>
    public static DiscoveredControls Read(IEnumerable<string> paths, ushort? wiredProduct = null) => Read(paths, path =>
    {
        using var device = EvdevDevice.Open(path);
        return new InputCapabilities(device.Capabilities(EventTypes.Key, true), device.Capabilities(EventTypes.Rel, true));
    }, wiredProduct);

    /// <summary>The injected reader also permits testing inaccessible and disconnected input nodes.</summary>
    public static DiscoveredControls Read(IEnumerable<string> paths, Func<string, InputCapabilities> readNode, ushort? wiredProduct = null)
    {
        var nodes = new List<InputCapabilities>();
        bool denied = false, unavailable = false;
        foreach (string path in paths.Distinct(StringComparer.Ordinal))
        {
            try
            {
                nodes.Add(readNode(path));
            }
            catch (UnauthorizedAccessException) { denied = true; }
            catch (IOException) { unavailable = true; }
        }
        string status = nodes.Count > 0
            ? denied || unavailable ? "partial" : "available"
            : denied ? "permission" : "unavailable";
        var wheelNodes = nodes.Where(node => !node.Keys.Any(KeyCodes.IsJoystickButton)).ToArray();
        bool highResOnly = wiredProduct == LegacyRemapControls.NagaChroma &&
            wheelNodes.Any(node => node.Relative.Contains(RelativeAxes.HWheelHiRes)) &&
            !wheelNodes.Any(node => node.Relative.Contains(RelativeAxes.HWheel));
        return new DiscoveredControls(Select(nodes, wiredProduct), status, highResOnly);
    }
}
