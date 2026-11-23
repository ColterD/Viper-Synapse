using System.Text.Json;
using System.Text.Json.Serialization;

namespace ViperSynapse.Core.Protocol;

public sealed record NagaDpiWindowExport(
    [property: JsonRequired] int Version,
    [property: JsonRequired] NagaDpiWindowResult Recorded,
    [property: JsonRequired] NagaDpiWindowResult[] Windows,
    [property: JsonRequired] long ChangedReportCount,
    [property: JsonRequired] bool DetailsTruncated,
    [property: JsonRequired] NagaDpiChangedReport[] ChangedReports);

public sealed record NagaDpiWindowResult(
    [property: JsonRequired] int? WindowMs,
    [property: JsonRequired] long DpiUp,
    [property: JsonRequired] long DpiDown);

public sealed record NagaDpiChangedReport(
    [property: JsonRequired] long FrameNumber,
    [property: JsonRequired] long Ticks,
    [property: JsonRequired] string Source,
    [property: JsonRequired] string Report,
    [property: JsonRequired] string Button,
    [property: JsonRequired] int WindowMs,
    [property: JsonRequired] bool RecordedPress,
    [property: JsonRequired] bool CandidatePress);

public static class NagaDpiWindowExportCodec
{
    public const int Version = 1;
    public const int MaxWindowMs = 1000;
    public const int DetailLimit = 1000;
    public const int MaxJsonBytes = 1024 * 1024;

    public static string Write(NagaDpiWindowExport export) =>
        JsonSerializer.Serialize(export, NagaDpiWindowExportJson.Default.NagaDpiWindowExport);

    public static async Task<NagaDpiWindowExport> ReadAsync(Stream stream, CancellationToken cancellationToken = default)
    {
        var buffer = new byte[MaxJsonBytes + 1];
        int length = await stream.ReadAtLeastAsync(buffer, buffer.Length, false, cancellationToken)
            .ConfigureAwait(false);
        if (length > MaxJsonBytes)
            throw new InvalidDataException("The DPI window comparison export exceeds 1 MiB.");
        return Read(buffer.AsSpan(0, length));
    }

    static NagaDpiWindowExport Read(ReadOnlySpan<byte> json)
    {
        if (json.StartsWith("\uFEFF"u8))
            json = json[3..];
        var export = JsonSerializer.Deserialize(json, NagaDpiWindowExportJson.Default.NagaDpiWindowExport);
        if (export is not { Version: Version })
            throw new InvalidDataException("Expected a version 1 Naga Chroma DPI window comparison export.");

        if (!ValidCounts(export.Recorded) || export.Windows is not { Length: > 0 and <= MaxWindowMs + 1 })
            throw new InvalidDataException("The DPI window comparison has invalid press counts.");

        var windows = new HashSet<int>();
        foreach (var window in export.Windows)
        {
            if (!ValidCounts(window) || window.WindowMs is not int windowMs || !windows.Add(windowMs))
                throw new InvalidDataException("The DPI window comparison has an invalid or duplicate candidate window.");
        }

        if (export.ChangedReports == null || export.ChangedReportCount < 0 ||
            export.ChangedReports.Length != Math.Min(export.ChangedReportCount, DetailLimit) ||
            export.DetailsTruncated != (export.ChangedReportCount > DetailLimit))
            throw new InvalidDataException("The DPI window comparison has inconsistent report totals.");

        foreach (var change in export.ChangedReports)
        {
            if (!ValidReport(change) || !windows.Contains(change.WindowMs))
                throw new InvalidDataException("The DPI window comparison contains an invalid changed report.");
        }
        return export;
    }

    static bool ValidCounts(NagaDpiWindowResult? result) => result is { DpiUp: >= 0, DpiDown: >= 0 } &&
        result.WindowMs is null or (>= 0 and <= MaxWindowMs);

    static bool ValidReport(NagaDpiChangedReport? change)
    {
        if (change is not { FrameNumber: > 0, Ticks: >= 0, Button: "dpi-up" or "dpi-down", Report: not null } ||
            change.RecordedPress == change.CandidatePress || !change.Report.All(Uri.IsHexDigit))
            return false;
        return change.Source switch
        {
            "factory-report-04" => change.Report.Length == 32 && change.Report.StartsWith("04", StringComparison.Ordinal),
            "mouse-back-forward" => change.Report.Length is >= 2 and <= 16 && change.Report.Length % 2 == 0,
            _ => false,
        };
    }
}

[JsonSourceGenerationOptions(PropertyNamingPolicy = JsonKnownNamingPolicy.CamelCase, WriteIndented = true)]
[JsonSerializable(typeof(NagaDpiWindowExport))]
internal sealed partial class NagaDpiWindowExportJson : JsonSerializerContext;
