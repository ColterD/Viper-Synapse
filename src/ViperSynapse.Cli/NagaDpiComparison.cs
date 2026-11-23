using ViperSynapse.Core.Devices;
using static ViperSynapse.Hardware.NagaDpiCapture;

namespace ViperSynapse.Cli;

internal static class NagaDpiComparison
{
    sealed record Press(string Report, Sample Before, Sample After, string? Release = null);

    sealed class Observations
    {
        readonly List<Press> _up = [];
        readonly List<Press> _down = [];

        public List<Press> For(ushort source) => source == LegacyRemapControls.NagaDpiUp ? _up : _down;
        public bool Empty => _up.Count == 0 && _down.Count == 0;

        public void AddFrame(Frame frame, IReadOnlyList<Edge> edges)
        {
            int sample = 0;
            foreach (var edge in edges)
            {
                var presses = For(edge.Source);
                if (edge.Pressed)
                    presses.Add(new(frame.Report, frame.Before, frame.After[sample++]));
                else
                    presses[^1] = presses[^1] with { Release = frame.Report };
            }
        }
    }

    internal static int Compare(string factoryPath, string remappedPath)
    {
        using var factory = File.OpenText(factoryPath);
        using var remapped = File.OpenText(remappedPath);
        return Compare(factory, remapped, Console.Out, Console.Error);
    }

    internal static int Compare(TextReader factory, TextReader remapped, TextWriter output, TextWriter errors)
    {
        var factoryPresses = new Observations();
        var remappedPresses = new Observations();
        if (ReadValidated(factory, "Factory capture", errors, null, factoryPresses.AddFrame) != 0 ||
            ReadValidated(remapped, "Remapped capture", errors, null, remappedPresses.AddFrame) != 0)
            return 1;

        if (factoryPresses.Empty && remappedPresses.Empty)
        {
            output.WriteLine("No DPI button presses were recorded in either capture.");
            return 0;
        }

        PrintButton(LegacyRemapControls.NagaDpiUp, factoryPresses, remappedPresses, output);
        PrintButton(LegacyRemapControls.NagaDpiDown, factoryPresses, remappedPresses, output);
        return 0;
    }

    static void PrintButton(ushort source, Observations factory, Observations remapped, TextWriter output)
    {
        var factoryPresses = factory.For(source);
        var remappedPresses = remapped.For(source);
        for (int i = 0; i < Math.Max(factoryPresses.Count, remappedPresses.Count); i++)
        {
            var factoryPress = i < factoryPresses.Count ? factoryPresses[i] : null;
            var remappedPress = i < remappedPresses.Count ? remappedPresses[i] : null;
            output.WriteLine(FormattableString.Invariant($"{ButtonName(source)} #{i + 1}"));
            PrintPress("Factory", factoryPress, output);
            PrintPress("Remapped", remappedPress, output);
            output.WriteLine($"  Reports: press={CompareReport(factoryPress?.Report, remappedPress?.Report)} " +
                $"release={CompareReport(factoryPress?.Release, remappedPress?.Release)}");
        }
    }

    static void PrintPress(string label, Press? press, TextWriter output)
    {
        if (press == null)
        {
            output.WriteLine($"  {label}: no recorded press");
            return;
        }

        string result = "DPI=unknown";
        if (press.Before.Dpi is { } before && press.After.Dpi is { } after)
        {
            int x = after.X - before.X, y = after.Y - before.Y;
            string change = x == 0 && y == 0 ? "unchanged" : "changed";
            result = FormattableString.Invariant($"DPI={change} (X {x:+0;-0;0}, Y {y:+0;-0;0})");
        }
        output.WriteLine($"  {label}: press={press.Report} release={press.Release ?? "unavailable"} " +
            $"before={Format(press.Before)} after={Format(press.After)} {result}");
    }

    static string CompareReport(string? factory, string? remapped)
    {
        if (factory == null || remapped == null)
            return "unavailable";
        return string.Equals(factory, remapped, StringComparison.Ordinal) ? "same" : "different";
    }
}
