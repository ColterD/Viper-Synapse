using ViperSynapse.Core.Bindings;
using ViperSynapse.Core.Engine;
using ViperSynapse.Core.Input;

namespace ViperSynapse.Hardware.Linux;

public sealed record InputCapabilities(IReadOnlyList<ushort> Keys, IReadOnlyList<ushort> Relative);
public sealed record DiscoveredControls(IReadOnlyList<ushort> Controls, string Status);

public static class InputControlDiscovery
{
    /// <summary>Only offer controls that the current session can grab and the virtual sink can emit.</summary>
    public static IReadOnlyList<ushort> Select(IEnumerable<InputCapabilities> nodes)
    {
        var controls = new SortedSet<ushort>();
        var supported = KeyCodes.All.ToHashSet();
        foreach (var node in nodes)
        {
            if (node.Keys.Any(KeyCodes.IsJoystickButton))
                continue;
            foreach (var key in node.Keys.Where(k => KeyCodes.IsKeyboardOrMouse(k) && supported.Contains(k)))
                controls.Add(key);
            if (node.Relative.Contains(RelativeAxes.Wheel))
            {
                controls.Add(SourceKeys.WheelUp);
                controls.Add(SourceKeys.WheelDown);
            }
            if (node.Relative.Contains(RelativeAxes.HWheel))
            {
                controls.Add(SourceKeys.WheelLeft);
                controls.Add(SourceKeys.WheelRight);
            }
        }
        return controls.ToArray();
    }

    /// <summary>Read advertised event bits only; never grab or read an input event.</summary>
    public static DiscoveredControls Read(IEnumerable<string> paths) => Read(paths, path =>
    {
        using var device = EvdevDevice.Open(path);
        return new InputCapabilities(device.Capabilities(EventTypes.Key, true), device.Capabilities(EventTypes.Rel, true));
    });

    /// <summary>The injected reader also permits testing inaccessible and disconnected input nodes.</summary>
    public static DiscoveredControls Read(IEnumerable<string> paths, Func<string, InputCapabilities> readNode)
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
        return new DiscoveredControls(Select(nodes), status);
    }
}
