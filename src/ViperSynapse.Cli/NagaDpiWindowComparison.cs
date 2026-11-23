using System.Globalization;
using ViperSynapse.Core.Protocol;
using ViperSynapse.Hardware;
using static ViperSynapse.Hardware.NagaDpiCapture;

namespace ViperSynapse.Cli;

internal static class NagaDpiWindowComparison
{
    internal static int Run(string path, string[] values)
    {
        bool details = false, json = false;
        var windows = new List<int>(values.Length);
        foreach (string value in values)
        {
            if (value == "--details" && !details)
            {
                details = true;
                continue;
            }
            if (value == "--json" && !json)
            {
                json = true;
                continue;
            }
            if (!int.TryParse(value, NumberStyles.None, CultureInfo.InvariantCulture, out int window) ||
                !NagaDpiReportDecoder.ValidMirrorWindow(window))
            {
                PrintUsage(Console.Error);
                return 64;
            }
            windows.Add(window);
        }
        if (windows.Count == 0)
        {
            PrintUsage(Console.Error);
            return 64;
        }
        using var capture = File.OpenText(path);
        return Compare(capture, windows, Console.Out, Console.Error, details, json);
    }

    internal static int Compare(TextReader capture, IReadOnlyList<int> windows, TextWriter output, TextWriter errors,
        bool details = false, bool json = false)
    {
        if (windows.Count == 0 || windows.Any(window => !NagaDpiReportDecoder.ValidMirrorWindow(window)))
        {
            PrintUsage(errors);
            return 64;
        }

        var result = NagaDpiWindowAnalysis.Analyze(capture, windows, errors, collectDetails: details || json);
        if (result == null)
            return 1;
        if (json)
        {
            output.WriteLine(NagaDpiWindowExportCodec.Write(result));
            return 0;
        }

        string recordedRule = result.Recorded.WindowMs is int savedWindow ? FormattableString.Invariant($"{savedWindow} ms") : "overlap only";
        output.WriteLine($"Recorded ({recordedRule}): {FormatCounts(result.Recorded)}");
        foreach (var window in result.Windows)
            output.WriteLine(FormattableString.Invariant($"{window.WindowMs} ms: {FormatCounts(window)}"));
        if (details)
            PrintDetails(output, result.ChangedReports, result.ChangedReportCount);
        return 0;
    }

    static string FormatCounts(NagaDpiWindowResult counts) =>
        FormattableString.Invariant($"DPI Up={counts.DpiUp} DPI Down={counts.DpiDown}");

    static void PrintDetails(TextWriter output, IReadOnlyList<NagaDpiChangedReport> differences, long count)
    {
        if (count == 0)
        {
            output.WriteLine("No press classifications changed.");
            return;
        }

        output.WriteLine("Reports with changed press classifications");
        foreach (var difference in differences)
        {
            string recorded = difference.RecordedPress ? "press" : "no-press";
            string candidate = difference.RecordedPress ? "no-press" : "press";
            string button = difference.Button == "dpi-up" ? "DPI Up" : "DPI Down";
            output.WriteLine(FormattableString.Invariant(
                $"frame={difference.FrameNumber} {Stamp(difference.Ticks)} input={difference.Source} report={difference.Report} {button} window={difference.WindowMs}ms recorded={recorded} candidate={candidate}"));
        }
        if (count > differences.Count)
            output.WriteLine(FormattableString.Invariant($"Showing the first {differences.Count} of {count} changed classifications."));
    }

    static void PrintUsage(TextWriter errors) =>
        errors.WriteLine($"Usage: viper-cli naga-dpi-windows FILE MS [MS...] [--details] [--json] (whole numbers 0–{NagaDpiReportDecoder.MaxMirrorWindowMs}).");
}
