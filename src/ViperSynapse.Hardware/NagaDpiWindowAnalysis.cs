using ViperSynapse.Core.Devices;
using ViperSynapse.Core.Protocol;
using static ViperSynapse.Hardware.NagaDpiCapture;

namespace ViperSynapse.Hardware;

/// <summary>Recounts an offline capture using candidate mirror windows without opening a device.</summary>
internal static class NagaDpiWindowAnalysis
{
    [Flags]
    enum Presses { None = 0, Up = 1, Down = 2 }

    sealed class PressCounts
    {
        internal long Up { get; private set; }
        internal long Down { get; private set; }

        internal Presses Add(IReadOnlyList<Edge> edges)
        {
            Presses presses = Presses.None;
            foreach (var edge in edges)
            {
                if (!edge.Pressed)
                    continue;
                if (edge.Source == LegacyRemapControls.NagaDpiUp)
                {
                    Up++;
                    presses |= Presses.Up;
                }
                else
                {
                    Down++;
                    presses |= Presses.Down;
                }
            }
            return presses;
        }

        internal NagaDpiWindowResult Result(int? window) => new(window, Up, Down);
    }

    sealed record Candidate(int WindowMs, NagaDpiReportDecoder Decoder, PressCounts Counts);
    internal static NagaDpiWindowExport? Analyze(TextReader capture, IReadOnlyList<int> windows, TextWriter errors,
        bool collectDetails = true, int maxFrames = int.MaxValue, CancellationToken cancellationToken = default)
    {
        Candidate[] candidates = [];
        var recorded = new PressCounts();
        var differences = new List<NagaDpiChangedReport>();
        long frameNumber = 0, differenceCount = 0;
        int? recordedWindow = null;

        void AddDifference(Frame frame, ushort button, int windowMs, bool recordedPress)
        {
            differenceCount++;
            // Buffer only a bounded preview so malformed input produces no partial output.
            if (differences.Count < NagaDpiWindowExportCodec.DetailLimit)
                differences.Add(new(frameNumber, frame.Ticks, frame.Source!, frame.Report,
                    button == LegacyRemapControls.NagaDpiUp ? "dpi-up" : "dpi-down", windowMs, recordedPress, !recordedPress));
        }

        int result = ReadValidated(capture, "Naga Chroma capture", errors, header =>
        {
            var settings = GetCombinedSettings(header) ?? throw new InvalidDataException("Expected a combined capture made with --both.");
            recordedWindow = settings.WindowMs;
            candidates = windows.Distinct().Select(window =>
                new Candidate(window, new(true, settings.MouseLength, window), new())).ToArray();
        }, (frame, edges) =>
        {
            frameNumber++;
            var recordedPresses = recorded.Add(edges);
            bool mouse = frame.Source == MouseSource;
            byte[] report = Convert.FromHexString(frame.Report);
            foreach (var candidate in candidates)
            {
                candidate.Decoder.Read(mouse, report, frame.Ticks);
                var candidatePresses = candidate.Counts.Add(candidate.Decoder.Edges);
                if (!collectDetails)
                    continue;
                var changed = recordedPresses ^ candidatePresses;
                if ((changed & Presses.Up) != 0)
                    AddDifference(frame, LegacyRemapControls.NagaDpiUp, candidate.WindowMs, (recordedPresses & Presses.Up) != 0);
                if ((changed & Presses.Down) != 0)
                    AddDifference(frame, LegacyRemapControls.NagaDpiDown, candidate.WindowMs, (recordedPresses & Presses.Down) != 0);
            }
        }, maxFrames, cancellationToken);
        return result == 0
            ? new(NagaDpiWindowExportCodec.Version, recorded.Result(recordedWindow),
                candidates.Select(candidate => candidate.Counts.Result(candidate.WindowMs)).ToArray(),
                differenceCount, differenceCount > differences.Count, differences.ToArray())
            : null;
    }
}
