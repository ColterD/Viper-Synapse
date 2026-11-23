using System.Text.Json;
using ViperSynapse.Cli;
using ViperSynapse.Hardware;

namespace ViperSynapse.Tests;

public sealed class NagaDpiWindowComparisonTests
{
    [Fact]
    public void Recounts_the_same_reports_at_different_windows_in_requested_order()
    {
        using var capture = new StringReader(CombinedCapture());
        using var output = new StringWriter();
        using var errors = new StringWriter();

        Assert.Equal(0, NagaDpiWindowComparison.Compare(capture, [60, 0, 19, 20, 60], output, errors));

        Assert.Equal([
            "Recorded (30 ms): DPI Up=1 DPI Down=0",
            "60 ms: DPI Up=1 DPI Down=0",
            "0 ms: DPI Up=2 DPI Down=0",
            "19 ms: DPI Up=2 DPI Down=0",
            "20 ms: DPI Up=1 DPI Down=0",
        ], output.ToString().Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries));
        Assert.Empty(errors.ToString());
    }

    [Fact]
    public void Details_identify_changed_reports_when_the_press_counts_match()
    {
        using var capture = new StringReader(EqualCountsCapture());
        using var output = new StringWriter();
        using var errors = new StringWriter();

        Assert.Equal(0, NagaDpiWindowComparison.Compare(capture, [10, 30], output, errors, details: true));

        var lines = output.ToString().Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        Assert.Contains("Recorded (30 ms): DPI Up=2 DPI Down=0", lines);
        Assert.Contains("10 ms: DPI Up=2 DPI Down=0", lines);
        Assert.Contains("30 ms: DPI Up=2 DPI Down=0", lines);
        Assert.Equal([
            "frame=3 40.000ms input=mouse-back-forward report=10000000 DPI Up window=10ms recorded=no-press candidate=press",
            "frame=5 55.000ms input=factory-report-04 report=04200000000000000000000000000000 DPI Up window=10ms recorded=press candidate=no-press",
        ], lines.Where(line => line.StartsWith("frame=", StringComparison.Ordinal)));
        Assert.Empty(errors.ToString());
    }

    [Fact]
    public void Json_exports_counts_and_report_changes_even_when_totals_match()
    {
        using var capture = new StringReader(EqualCountsCapture());
        using var output = new StringWriter();
        using var errors = new StringWriter();

        Assert.Equal(0, NagaDpiWindowComparison.Compare(capture, [10, 30, 10], output, errors, json: true));

        using var document = JsonDocument.Parse(output.ToString());
        var result = document.RootElement;
        Assert.Equal(1, result.GetProperty("version").GetInt32());
        AssertCounts(result.GetProperty("recorded"), 30, 2, 0);
        Assert.Collection(result.GetProperty("windows").EnumerateArray(),
            window => AssertCounts(window, 10, 2, 0),
            window => AssertCounts(window, 30, 2, 0));
        Assert.Equal(2L, result.GetProperty("changedReportCount").GetInt64());
        Assert.False(result.GetProperty("detailsTruncated").GetBoolean());
        Assert.Collection(result.GetProperty("changedReports").EnumerateArray(),
            change => AssertChange(change, 3, 400_000, NagaDpiCapture.MouseSource, "10000000", false),
            change => AssertChange(change, 5, 550_000, NagaDpiCapture.FactorySource,
                "04200000000000000000000000000000", true));
        Assert.Empty(errors.ToString());

        static void AssertChange(JsonElement change, long frame, long ticks, string source, string report, bool recordedPress)
        {
            Assert.Equal(frame, change.GetProperty("frameNumber").GetInt64());
            Assert.Equal(ticks, change.GetProperty("ticks").GetInt64());
            Assert.Equal(source, change.GetProperty("source").GetString());
            Assert.Equal(report, change.GetProperty("report").GetString());
            Assert.Equal("dpi-up", change.GetProperty("button").GetString());
            Assert.Equal(10, change.GetProperty("windowMs").GetInt32());
            Assert.Equal(recordedPress, change.GetProperty("recordedPress").GetBoolean());
            Assert.Equal(!recordedPress, change.GetProperty("candidatePress").GetBoolean());
        }
    }

    [Fact]
    public void Json_reports_the_full_change_count_when_details_are_truncated()
    {
        using var capture = new StringReader(CombinedCapture(repetitions: 1001));
        using var output = new StringWriter();
        using var errors = new StringWriter();

        Assert.Equal(0, NagaDpiWindowComparison.Compare(capture, [0], output, errors, json: true));

        using var document = JsonDocument.Parse(output.ToString());
        var result = document.RootElement;
        AssertCounts(result.GetProperty("recorded"), 30, 1001, 0);
        AssertCounts(Assert.Single(result.GetProperty("windows").EnumerateArray()), 0, 2002, 0);
        Assert.Equal(1001L, result.GetProperty("changedReportCount").GetInt64());
        Assert.True(result.GetProperty("detailsTruncated").GetBoolean());
        var details = result.GetProperty("changedReports");
        Assert.Equal(1000, details.GetArrayLength());
        Assert.Equal(3L, details[0].GetProperty("frameNumber").GetInt64());
        Assert.Equal(3999L, details[999].GetProperty("frameNumber").GetInt64());
        Assert.Empty(errors.ToString());
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void Recounts_legacy_overlap_only_captures_without_using_their_samples(bool json)
    {
        using var capture = new StringReader("""
            {"version":3,"device":"1532:0053","initial":{"ticks":0,"dpi":null},"source":"both","reportLength":4}
            {"ticks":100000,"report":"04210000000000000000000000000000","before":{"ticks":0,"dpi":null},"after":[{"ticks":110000,"dpi":null}],"source":"factory-report-04"}
            {"ticks":500000,"report":"04000000000000000000000000000000","before":{"ticks":0,"dpi":null},"after":[],"source":"factory-report-04"}
            {"ticks":700000,"report":"08000000","before":{"ticks":0,"dpi":null},"after":[{"ticks":710000,"dpi":null}],"source":"mouse-back-forward"}
            {"ticks":900000,"report":"00000000","before":{"ticks":0,"dpi":null},"after":[],"source":"mouse-back-forward"}
            """);
        using var output = new StringWriter();
        using var errors = new StringWriter();

        Assert.Equal(0, NagaDpiWindowComparison.Compare(capture, [30], output, errors, json: json));
        if (json)
        {
            using var document = JsonDocument.Parse(output.ToString());
            var result = document.RootElement;
            AssertCounts(result.GetProperty("recorded"), null, 0, 2);
            AssertCounts(Assert.Single(result.GetProperty("windows").EnumerateArray()), 30, 0, 1);
            var change = Assert.Single(result.GetProperty("changedReports").EnumerateArray());
            Assert.Equal("dpi-down", change.GetProperty("button").GetString());
        }
        else
        {
            Assert.Contains("Recorded (overlap only): DPI Up=0 DPI Down=2", output.ToString());
            Assert.Contains("30 ms: DPI Up=0 DPI Down=1", output.ToString());
        }
        Assert.Empty(errors.ToString());
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void Rejects_single_interface_captures(bool mouse)
    {
        using var writer = new StringWriter();
        NagaDpiCapture.WriteHeader(writer, new(0, null), mouseReportLength: mouse ? 4 : null);
        using var capture = new StringReader(writer.ToString());
        using var output = new StringWriter();
        using var errors = new StringWriter();

        Assert.Equal(1, NagaDpiWindowComparison.Compare(capture, [30], output, errors));
        Assert.Empty(output.ToString());
        Assert.Contains("combined", errors.ToString(), StringComparison.OrdinalIgnoreCase);
    }

    [Theory]
    [InlineData(false, false)]
    [InlineData(true, false)]
    [InlineData(false, true)]
    public void A_malformed_record_prevents_partial_results(bool details, bool json)
    {
        using var capture = new StringReader(CombinedCapture() + "{\n");
        using var output = new StringWriter();
        using var errors = new StringWriter();

        Assert.Equal(1, NagaDpiWindowComparison.Compare(capture, [0, 30], output, errors, details: details, json: json));
        Assert.Empty(output.ToString());
        Assert.Contains("line 6", errors.ToString());
    }

    static string EqualCountsCapture()
    {
        using var writer = new StringWriter();
        var initial = new NagaDpiCapture.Sample(0, new(800, 900));
        NagaDpiCapture.WriteHeader(writer, initial, mouseReportLength: 4, both: true, mirrorWindowMs: 30);
        NagaDpiCapture.WriteFrame(writer, new(100_000, "04200000000000000000000000000000", initial,
            [new(110_000, new(900, 900))], NagaDpiCapture.FactorySource));
        NagaDpiCapture.WriteFrame(writer, new(200_000, "04000000000000000000000000000000", initial,
            [], NagaDpiCapture.FactorySource));
        NagaDpiCapture.WriteFrame(writer, new(400_000, "10000000", initial, [], NagaDpiCapture.MouseSource));
        NagaDpiCapture.WriteFrame(writer, new(500_000, "00000000", initial, [], NagaDpiCapture.MouseSource));
        NagaDpiCapture.WriteFrame(writer, new(550_000, "04200000000000000000000000000000", initial,
            [new(560_000, new(900, 900))], NagaDpiCapture.FactorySource));
        NagaDpiCapture.WriteFrame(writer, new(600_000, "04000000000000000000000000000000", initial,
            [], NagaDpiCapture.FactorySource));
        return writer.ToString();
    }

    static void AssertCounts(JsonElement counts, int? windowMs, long dpiUp, long dpiDown)
    {
        var window = counts.GetProperty("windowMs");
        if (windowMs is int expected)
            Assert.Equal(expected, window.GetInt32());
        else
            Assert.Equal(JsonValueKind.Null, window.ValueKind);
        Assert.Equal(dpiUp, counts.GetProperty("dpiUp").GetInt64());
        Assert.Equal(dpiDown, counts.GetProperty("dpiDown").GetInt64());
    }

    static string CombinedCapture(int repetitions = 1)
    {
        using var writer = new StringWriter();
        var initial = new NagaDpiCapture.Sample(0, new(800, 900));
        NagaDpiCapture.WriteHeader(writer, initial, mouseReportLength: 4, both: true, mirrorWindowMs: 30);
        for (int i = 0; i < repetitions; i++)
        {
            long offset = 1_000_000L * i;
            NagaDpiCapture.WriteFrame(writer, new(offset + 100_000, "04200000000000000000000000000000", initial,
                [new(offset + 110_000, new(900, 900))], NagaDpiCapture.FactorySource));
            NagaDpiCapture.WriteFrame(writer, new(offset + 500_000, "04000000000000000000000000000000", initial,
                [], NagaDpiCapture.FactorySource));
            NagaDpiCapture.WriteFrame(writer, new(offset + 700_000, "10000000", initial, [], NagaDpiCapture.MouseSource));
            NagaDpiCapture.WriteFrame(writer, new(offset + 900_000, "00000000", initial, [], NagaDpiCapture.MouseSource));
        }
        return writer.ToString();
    }
}
