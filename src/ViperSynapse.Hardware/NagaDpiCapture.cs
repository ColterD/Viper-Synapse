using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using ViperSynapse.Core.Devices;
using ViperSynapse.Core.Lighting;

namespace ViperSynapse.Hardware;

internal static class NagaDpiCapture
{
    const int FactoryVersion = 1;
    const int MouseVersion = 2;
    const int OverlapVersion = 3;
    const int FixedWindowVersion = 4;
    const int FixedWindowMs = 30;
    const int CombinedVersion = 5;
    const string CombinedSource = "both";
    internal const string MouseSource = "mouse-back-forward";
    internal const string FactorySource = "factory-report-04";
    const string Device = "1532:0053";
    const int MaxLineLength = 4096;

    internal sealed record Sample([property: JsonRequired] long Ticks, [property: JsonRequired] DpiStage? Dpi);
    internal sealed record Header(int Version, string Device, Sample Initial,
        [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] string? Source = null,
        [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] int? ReportLength = null,
        [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] int? MirrorWindowMs = null);
    internal sealed record Frame([property: JsonRequired] long Ticks, string Report, Sample Before, Sample[] After,
        [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] string? Source = null);
    internal readonly record struct Edge(ushort Source, bool Pressed);

    internal static void WriteHeader(TextWriter writer, Sample sample, int? mouseReportLength = null, bool both = false,
        int mirrorWindowMs = NagaDpiReportDecoder.DefaultMirrorWindowMs)
    {
        var header = both ? new Header(CombinedVersion, Device, sample, CombinedSource, mouseReportLength, mirrorWindowMs)
            : mouseReportLength.HasValue ? new Header(MouseVersion, Device, sample, MouseSource, mouseReportLength)
            : new Header(FactoryVersion, Device, sample);
        writer.WriteLine(JsonSerializer.Serialize(header, NagaDpiCaptureJson.Default.Header));
    }

    internal static void WriteFrame(TextWriter writer, Frame frame) =>
        writer.WriteLine(JsonSerializer.Serialize(frame, NagaDpiCaptureJson.Default.Frame));

    internal static int Replay(TextReader capture, TextWriter output, TextWriter errors) =>
        ReadValidated(capture, "Naga Chroma capture", errors,
            header => output.WriteLine($"Naga Chroma replay initial={Format(header.Initial)}" +
                (header.Source != null ? $" source={header.Source}" : "") +
                (header.MirrorWindowMs is int window ? FormattableString.Invariant($" mirror-window={window}ms") : "") + "."),
            (frame, edges) => PrintFrame(output, frame, edges));

    internal static int ReadValidated(TextReader capture, string label, TextWriter errors,
        Action<Header>? onHeader, Action<Frame, IReadOnlyList<Edge>> onFrame,
        int maxFrames = int.MaxValue, CancellationToken cancellationToken = default)
    {
        int lineNumber = 1;
        try
        {
            var header = JsonSerializer.Deserialize(ReadLine(capture) ?? "", NagaDpiCaptureJson.Default.Header);
            if (header is not { Device: Device } || !ValidSample(header.Initial) || GetDecoder(header) is not { } decoder)
                throw new InvalidDataException("Expected a supported Naga Chroma capture header and report format.");

            onHeader?.Invoke(header);
            long previousReport = header.Initial.Ticks;
            long previousBefore = header.Initial.Ticks;
            long previousAfter = header.Initial.Ticks;
            while (true)
            {
                cancellationToken.ThrowIfCancellationRequested();
                lineNumber++;
                if (ReadLine(capture) is not { } line)
                    break;
                if (lineNumber - 1 > maxFrames)
                    throw new InvalidDataException(FormattableString.Invariant($"Capture exceeds the {maxFrames:N0}-report limit."));
                var frame = JsonSerializer.Deserialize(line, NagaDpiCaptureJson.Default.Frame);
                if (frame is not { Report: not null, After: not null } || FrameInput(header, frame.Source) is not bool mouse ||
                    frame.Report.Length != decoder.ReportLength(mouse) * 2 ||
                    frame.Ticks < previousReport || !ValidSample(frame.Before) || frame.Before.Ticks < previousBefore ||
                    frame.Before.Ticks > frame.Ticks)
                    throw new InvalidDataException("Invalid DPI report frame or timestamp.");

                byte[] report = Convert.FromHexString(frame.Report);
                if (!decoder.Read(mouse, report, frame.Ticks) || frame.After.Length != decoder.Edges.Count(edge => edge.Pressed))
                    throw new InvalidDataException("The DPI edges decoded from the report do not match its samples.");
                // A queued report can predate the last feature reply, but the subsequent read cannot.
                long readingTime = Math.Max(frame.Ticks, previousAfter);
                foreach (var sample in frame.After)
                {
                    if (!ValidSample(sample) || sample.Ticks < readingTime)
                        throw new InvalidDataException("Invalid DPI reading or timestamp.");
                    readingTime = sample.Ticks;
                }

                onFrame(frame with { Report = Convert.ToHexString(report) }, decoder.Edges);
                previousReport = frame.Ticks;
                previousBefore = frame.Before.Ticks;
                if (frame.After.Length != 0)
                    previousAfter = readingTime;
            }
            return 0;
        }
        catch (Exception error) when (error is JsonException or InvalidDataException or FormatException)
        {
            errors.WriteLine($"{label} line {lineNumber}: {error.Message}");
            return 1;
        }
    }

    static NagaDpiReportDecoder? GetDecoder(Header header) => header switch
    {
        { Version: FactoryVersion, Source: null, ReportLength: null, MirrorWindowMs: null } => new(true, null),
        { Version: MouseVersion, Source: MouseSource, ReportLength: int length, MirrorWindowMs: null }
            when length is >= 1 and <= NagaChromaButtons.MaxMouseReportLength => new(false, length),
        _ when GetCombinedSettings(header) is { } settings => new(true, settings.MouseLength, settings.WindowMs),
        _ => null,
    };

    internal static (int MouseLength, int? WindowMs)? GetCombinedSettings(Header header) => header switch
    {
        { Version: OverlapVersion or FixedWindowVersion, Source: CombinedSource, ReportLength: int length, MirrorWindowMs: null }
            when length is >= 1 and <= NagaChromaButtons.MaxMouseReportLength =>
            (length, header.Version == FixedWindowVersion ? FixedWindowMs : null),
        { Version: CombinedVersion, Source: CombinedSource, ReportLength: int length, MirrorWindowMs: int window }
            when length is >= 1 and <= NagaChromaButtons.MaxMouseReportLength && NagaDpiReportDecoder.ValidMirrorWindow(window) => (length, window),
        _ => null,
    };

    static bool? FrameInput(Header header, string? source) => (header.Version, source) switch
    {
        (FactoryVersion, null) or (OverlapVersion or FixedWindowVersion or CombinedVersion, FactorySource) => false,
        (MouseVersion, null) or (OverlapVersion or FixedWindowVersion or CombinedVersion, MouseSource) => true,
        _ => null,
    };

    internal static void PrintFrame(TextWriter output, Frame frame, IReadOnlyList<Edge> edges)
    {
        string report = frame.Source == null ? $"report={frame.Report}" : $"report={frame.Report} input={frame.Source}";
        if (edges.Count == 0)
        {
            output.WriteLine($"{Stamp(frame.Ticks)} {report} edges=none");
            return;
        }
        int index = 0;
        foreach (var edge in edges)
        {
            string label = ButtonName(edge.Source);
            string prefix = $"{Stamp(frame.Ticks)} {label} {(edge.Pressed ? "press" : "release")} {report}";
            output.WriteLine(edge.Pressed ? $"{prefix} before={Format(frame.Before)} after={Format(frame.After[index++])}" : prefix);
        }
    }

    internal static string Format(Sample sample) =>
        $"{(sample.Dpi is { } dpi ? FormattableString.Invariant($"{dpi.X}x{dpi.Y}") : "unavailable")}@{Stamp(sample.Ticks)}";

    internal static string ButtonName(ushort source) => source == LegacyRemapControls.NagaDpiUp ? "DPI Up" : "DPI Down";

    internal static string Stamp(long ticks) => FormattableString.Invariant($"{TimeSpan.FromTicks(ticks).TotalMilliseconds:F3}ms");

    static bool ValidSample(Sample? sample) => sample != null && sample.Ticks >= 0 &&
        (sample.Dpi == null || NagaChromaSensitivity.IsValid(sample.Dpi.X, sample.Dpi.Y));

    static string? ReadLine(TextReader reader)
    {
        var line = new StringBuilder();
        for (int c = reader.Read(); c >= 0; c = reader.Read())
        {
            if (c == '\n')
                return line.ToString();
            if (line.Length == MaxLineLength)
                throw new InvalidDataException("Capture record exceeds the line length limit.");
            line.Append((char)c);
        }
        return line.Length == 0 ? null : line.ToString();
    }
}

[JsonSourceGenerationOptions(PropertyNamingPolicy = JsonKnownNamingPolicy.CamelCase)]
[JsonSerializable(typeof(NagaDpiCapture.Header))]
[JsonSerializable(typeof(NagaDpiCapture.Frame))]
internal sealed partial class NagaDpiCaptureJson : JsonSerializerContext;
