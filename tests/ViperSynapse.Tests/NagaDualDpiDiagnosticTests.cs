using System.Text.Json;
using Microsoft.Extensions.Time.Testing;
using ViperSynapse.Cli;
using ViperSynapse.Core.Devices;
using ViperSynapse.Core.Protocol;
using ViperSynapse.Hardware;
using ViperSynapse.Hardware.Linux;

namespace ViperSynapse.Tests;

public sealed class NagaDualDpiDiagnosticTests
{
    const string MousePath = "/dev/hidraw0";
    const string FactoryPath = "/dev/hidraw1";
    const string MouseSource = "mouse-back-forward";
    const string FactorySource = "factory-report-04";
    static readonly HidrawNode Mouse = new(MousePath, 0x1532, LegacyRemapControls.NagaChroma, 0, "Naga Chroma", "usb-1/input0");
    static readonly HidrawNode Factory = new(FactoryPath, 0x1532, LegacyRemapControls.NagaChroma, 1, "Naga Chroma", "usb-1/input1");

    [Theory]
    [InlineData(true, 0x20, 0x10, "DPI Up")]
    [InlineData(false, 0x21, 0x08, "DPI Down")]
    public void Overlapping_reports_count_one_press_in_live_capture_replay_and_comparison(
        bool factoryFirst, byte usage, byte mouseBit, string button)
    {
        using var cancellation = CancellationTokenSource.CreateLinkedTokenSource(TestContext.Current.CancellationToken);
        cancellation.CancelAfter(TimeSpan.FromSeconds(3));
        using var output = new CapturedOutput(line =>
        {
            if (line.Contains($"{button} release report=", StringComparison.Ordinal))
                cancellation.Cancel();
        });
        using var errors = new StringWriter();
        using var capture = new StringWriter();
        var firmware = new Firmware();
        var factoryReport = FactoryReport(usage);
        var factoryRelease = FactoryReport(0);
        byte[] mouseReport = [(byte)(mouseBit | 0x61), 1, 2, 3];
        byte[] mouseRelease = [0x61, 4, 5, 6];
        var readers = new Readers((factory, mouse) =>
        {
            if (factoryFirst)
            {
                factory.Report(factoryReport);
                mouse.Report(mouseReport);
                factory.Report(factoryRelease);
                mouse.Report(mouseRelease);
            }
            else
            {
                mouse.Report(mouseReport);
                factory.Report(factoryReport);
                mouse.Report(mouseRelease);
                factory.Report(factoryRelease);
            }
        });

        Assert.Equal(0, Run(readers, firmware, output, errors, cancellation.Token, () => capture));
        Assert.Equal(2, firmware.Reads);
        Assert.True(readers.Factory.Disposed);
        Assert.True(readers.Mouse.Disposed);
        Assert.True(firmware.Disposed);

        string exported = capture.ToString();
        var lines = exported.Split('\n', StringSplitOptions.RemoveEmptyEntries);
        Assert.Equal(5, lines.Length);
        using var header = JsonDocument.Parse(lines[0]);
        Assert.Equal(5, header.RootElement.GetProperty("version").GetInt32());
        Assert.Equal("both", header.RootElement.GetProperty("source").GetString());
        Assert.Equal(4, header.RootElement.GetProperty("reportLength").GetInt32());
        Assert.Equal(30, header.RootElement.GetProperty("mirrorWindowMs").GetInt32());
        string firstSource = factoryFirst ? FactorySource : MouseSource;
        string secondSource = factoryFirst ? MouseSource : FactorySource;
        byte[] firstPress = factoryFirst ? factoryReport : mouseReport;
        byte[] secondPress = factoryFirst ? mouseReport : factoryReport;
        byte[] firstRelease = factoryFirst ? factoryRelease : mouseRelease;
        byte[] secondRelease = factoryFirst ? mouseRelease : factoryRelease;
        AssertFrame(lines[1], firstSource, firstPress, samples: 1);
        AssertFrame(lines[2], secondSource, secondPress, samples: 0);
        AssertFrame(lines[3], firstSource, firstRelease, samples: 0);
        AssertFrame(lines[4], secondSource, secondRelease, samples: 0);

        using var replayInput = new StringReader(exported);
        using var replayOutput = new StringWriter();
        Assert.Equal(0, NagaDpiCapture.Replay(replayInput, replayOutput, errors));
        var replayed = ReportLines(replayOutput.ToString());
        Assert.Equal(ReportLines(output.ToString()), replayed);
        Assert.Collection(replayed,
            line => Assert.Contains($"{button} press report={Convert.ToHexString(firstPress)}", line),
            line => Assert.Contains("edges=none", line),
            line => Assert.Contains("edges=none", line),
            line => Assert.Contains($"{button} release report={Convert.ToHexString(secondRelease)}", line));
        Assert.Contains("before=800x900@", replayed[0]);
        Assert.Contains("after=1600x1700@", replayed[0]);

        using var factoryCompareInput = new StringReader(exported);
        using var otherCompareInput = new StringReader(exported);
        using var comparison = new StringWriter();
        Assert.Equal(0, NagaDpiComparison.Compare(factoryCompareInput, otherCompareInput, comparison, errors));
        Assert.Contains($"{button} #1", comparison.ToString());
        Assert.DoesNotContain($"{button} #2", comparison.ToString());
        Assert.Contains("press=same release=same", comparison.ToString());
        Assert.Empty(errors.ToString());
    }

    [Theory]
    [InlineData(true, 1, 5, 30, 1)]
    [InlineData(false, 1, 5, 30, 1)]
    [InlineData(true, 2, 5, 30, 2)]
    [InlineData(true, 1, 31, 30, 2)]
    [InlineData(true, 1, 5, 0, 2)]
    [InlineData(true, 1, 20, 19, 2)]
    [InlineData(true, 1, 20, 20, 1)]
    [InlineData(true, 1, 20, 60, 1)]
    public void Late_mirrors_preserve_the_count_of_quick_presses(
        bool factoryFirst, int cycles, int mirrorDelayMs, int mirrorWindowMs, int logicalPresses)
    {
        var time = new FakeTimeProvider();
        using var cancellation = CancellationTokenSource.CreateLinkedTokenSource(TestContext.Current.CancellationToken);
        cancellation.CancelAfter(TimeSpan.FromSeconds(3));
        int emittedReports = 0;
        using var output = new CapturedOutput(line =>
        {
            if (line.Contains(" report=", StringComparison.Ordinal) && ++emittedReports == 4 * cycles)
                cancellation.Cancel();
        });
        using var errors = new StringWriter();
        using var capture = new StringWriter();
        var firmware = new Firmware();
        var factoryPress = FactoryReport(0x20);
        var factoryRelease = FactoryReport(0);
        byte[] mousePress = [0x10, 0, 0, 0];
        byte[] mouseRelease = [0, 0, 0, 0];
        var primaryPress = factoryFirst ? factoryPress : mousePress;
        var primaryRelease = factoryFirst ? factoryRelease : mouseRelease;
        var mirrorPress = factoryFirst ? mousePress : factoryPress;
        var mirrorRelease = factoryFirst ? mouseRelease : factoryRelease;
        string primarySource = factoryFirst ? FactorySource : MouseSource;
        string mirrorSource = factoryFirst ? MouseSource : FactorySource;

        var readers = new Readers((factory, mouse) =>
        {
            var primary = factoryFirst ? factory : mouse;
            var mirror = factoryFirst ? mouse : factory;
            for (int i = 0; i < cycles; i++)
            {
                time.Advance(TimeSpan.FromMilliseconds(5));
                primary.Report(primaryPress);
                time.Advance(TimeSpan.FromMilliseconds(5));
                primary.Report(primaryRelease);
            }

            for (int i = 0; i < cycles; i++)
            {
                time.Advance(TimeSpan.FromMilliseconds(i == 0 ? mirrorDelayMs : 5));
                mirror.Report(mirrorPress);
                time.Advance(TimeSpan.FromMilliseconds(5));
                mirror.Report(mirrorRelease);
            }
        });

        Assert.Equal(0, Run(readers, firmware, output, errors, cancellation.Token, () => capture, time, mirrorWindowMs));
        Assert.Equal(1 + logicalPresses, firmware.Reads);

        string exported = capture.ToString();
        var lines = exported.Split('\n', StringSplitOptions.RemoveEmptyEntries);
        Assert.Equal(1 + 4 * cycles, lines.Length);
        using var header = JsonDocument.Parse(lines[0]);
        Assert.Equal(5, header.RootElement.GetProperty("version").GetInt32());
        Assert.Equal(mirrorWindowMs, header.RootElement.GetProperty("mirrorWindowMs").GetInt32());
        for (int i = 0; i < cycles; i++)
        {
            AssertFrame(lines[1 + 2 * i], primarySource, primaryPress, samples: 1);
            AssertFrame(lines[2 + 2 * i], primarySource, primaryRelease, samples: 0);
            AssertFrame(lines[1 + 2 * cycles + 2 * i], mirrorSource, mirrorPress, samples: i < logicalPresses - cycles ? 1 : 0);
            AssertFrame(lines[2 + 2 * cycles + 2 * i], mirrorSource, mirrorRelease, samples: 0);
        }

        using var replayInput = new StringReader(exported);
        using var replayOutput = new StringWriter();
        Assert.Equal(0, NagaDpiCapture.Replay(replayInput, replayOutput, errors));
        var replayed = ReportLines(replayOutput.ToString());
        Assert.Equal(ReportLines(output.ToString()), replayed);
        Assert.Equal(logicalPresses, replayed.Count(line => line.Contains("DPI Up press report=", StringComparison.Ordinal)));
        Assert.Equal(logicalPresses, replayed.Count(line => line.Contains("DPI Up release report=", StringComparison.Ordinal)));
        Assert.Empty(errors.ToString());
    }

    [Fact]
    public void A_quick_repress_counts_even_while_the_previous_mirror_is_still_held()
    {
        var time = new FakeTimeProvider();
        var factoryPress = FactoryReport(0x20);
        var factoryRelease = FactoryReport(0);
        using var cancellation = CancellationTokenSource.CreateLinkedTokenSource(TestContext.Current.CancellationToken);
        cancellation.CancelAfter(TimeSpan.FromSeconds(3));
        using var output = new CapturedOutput(line =>
        {
            if (line.Contains($"DPI Up release report={Convert.ToHexString(factoryRelease)}", StringComparison.Ordinal))
                cancellation.Cancel();
        });
        using var errors = new StringWriter();
        using var capture = new StringWriter();
        var firmware = new Firmware();
        byte[] mousePress = [0x10, 0, 0, 0];
        byte[] mouseRelease = [0, 0, 0, 0];
        var readers = new Readers((factory, mouse) =>
        {
            Send(factory, factoryPress);
            Send(mouse, mousePress);
            Send(factory, factoryRelease);
            Send(factory, factoryPress);
            Send(mouse, mouseRelease);
            Send(factory, factoryRelease);
        });

        Assert.Equal(0, Run(readers, firmware, output, errors, cancellation.Token, () => capture, time));
        Assert.Equal(3, firmware.Reads);

        using var replayInput = new StringReader(capture.ToString());
        using var replayOutput = new StringWriter();
        Assert.Equal(0, NagaDpiCapture.Replay(replayInput, replayOutput, errors));
        var reports = ReportLines(output.ToString());
        Assert.Equal(reports, ReportLines(replayOutput.ToString()));
        Assert.Collection(reports,
            line => Assert.Contains($"DPI Up press report={Convert.ToHexString(factoryPress)}", line),
            line => Assert.Contains("edges=none", line),
            line => Assert.Contains("edges=none", line),
            line => Assert.Contains($"DPI Up release report={Convert.ToHexString(factoryPress)}", line),
            line => Assert.Contains($"DPI Up press report={Convert.ToHexString(factoryPress)}", line),
            line => Assert.Contains($"report={Convert.ToHexString(mouseRelease)} input={MouseSource} edges=none", line),
            line => Assert.Contains($"DPI Up release report={Convert.ToHexString(factoryRelease)}", line));
        Assert.Empty(errors.ToString());

        void Send(Reader reader, byte[] report)
        {
            time.Advance(TimeSpan.FromMilliseconds(5));
            reader.Report(report);
        }
    }

    [Fact]
    public void Version_three_replay_keeps_its_original_overlap_rule()
    {
        using var capture = new StringReader("""
            {"version":3,"device":"1532:0053","initial":{"ticks":0,"dpi":null},"source":"both","reportLength":4}
            {"ticks":100000,"report":"04200000000000000000000000000000","before":{"ticks":0,"dpi":null},"after":[{"ticks":110000,"dpi":null}],"source":"factory-report-04"}
            {"ticks":150000,"report":"04000000000000000000000000000000","before":{"ticks":0,"dpi":null},"after":[],"source":"factory-report-04"}
            {"ticks":200000,"report":"10000000","before":{"ticks":0,"dpi":null},"after":[{"ticks":210000,"dpi":null}],"source":"mouse-back-forward"}
            {"ticks":250000,"report":"00000000","before":{"ticks":0,"dpi":null},"after":[],"source":"mouse-back-forward"}
            """);
        using var output = new StringWriter();
        using var errors = new StringWriter();

        Assert.Equal(0, NagaDpiCapture.Replay(capture, output, errors));
        Assert.Equal(2, ReportLines(output.ToString()).Count(line => line.Contains("DPI Up press report=", StringComparison.Ordinal)));
        Assert.Empty(errors.ToString());
    }

    [Fact]
    public void Version_four_replay_keeps_its_fixed_thirty_millisecond_window()
    {
        using var capture = new StringReader("""
            {"version":4,"device":"1532:0053","initial":{"ticks":0,"dpi":null},"source":"both","reportLength":4}
            {"ticks":100000,"report":"04200000000000000000000000000000","before":{"ticks":0,"dpi":null},"after":[{"ticks":110000,"dpi":null}],"source":"factory-report-04"}
            {"ticks":150000,"report":"04000000000000000000000000000000","before":{"ticks":0,"dpi":null},"after":[],"source":"factory-report-04"}
            {"ticks":450000,"report":"10000000","before":{"ticks":0,"dpi":null},"after":[],"source":"mouse-back-forward"}
            {"ticks":500000,"report":"00000000","before":{"ticks":0,"dpi":null},"after":[],"source":"mouse-back-forward"}
            """);
        using var output = new StringWriter();
        using var errors = new StringWriter();

        Assert.Equal(0, NagaDpiCapture.Replay(capture, output, errors));
        Assert.Single(ReportLines(output.ToString()), line => line.Contains("DPI Up press report=", StringComparison.Ordinal));
        Assert.Empty(errors.ToString());
    }

    [Theory]
    [InlineData("")]
    [InlineData("\"mirrorWindowMs\":-1,")]
    [InlineData("\"mirrorWindowMs\":1001,")]
    [InlineData("\"mirrorWindowMs\":null,")]
    [InlineData("\"mirrorWindowMs\":30.5,")]
    public void Replay_rejects_invalid_version_five_mirror_windows(string field)
    {
        using var capture = new StringReader($$"""
            {"version":5,{{field}}"device":"1532:0053","initial":{"ticks":0,"dpi":null},"source":"both","reportLength":4}
            """);
        using var output = new StringWriter();
        using var errors = new StringWriter();

        Assert.Equal(1, NagaDpiCapture.Replay(capture, output, errors));
        Assert.Empty(output.ToString());
        Assert.Contains("line 1", errors.ToString());
    }

    [Fact]
    public void Parse_accepts_the_window_before_or_after_other_combined_options()
    {
        var first = Assert.IsType<NagaDpiDiagnostic.Options>(NagaDpiDiagnostic.Parse(
            [MousePath, "--mirror-window-ms", "0", "--capture", "test.jsonl", "--both"]));
        var last = Assert.IsType<NagaDpiDiagnostic.Options>(NagaDpiDiagnostic.Parse(
            [MousePath, "--both", "--capture", "test.jsonl", "--mirror-window-ms", "1000"]));

        Assert.Equal(MousePath, first.Control);
        Assert.Equal("test.jsonl", first.Capture);
        Assert.Equal(first.Mode, last.Mode);
        Assert.Equal(0, first.MirrorWindowMs);
        Assert.Equal(1000, last.MirrorWindowMs);
    }

    [Fact]
    public void Parse_rejects_invalid_or_misplaced_window_options()
    {
        Assert.Null(NagaDpiDiagnostic.Parse([MousePath, "--mirror-window-ms", "30"]));
        Assert.Null(NagaDpiDiagnostic.Parse([MousePath, "--back-forward", "--mirror-window-ms", "30"]));
        Assert.Null(NagaDpiDiagnostic.Parse([MousePath, "--both", "--mirror-window-ms"]));
        Assert.Null(NagaDpiDiagnostic.Parse([MousePath, "--both", "--mirror-window-ms", "-1"]));
        Assert.Null(NagaDpiDiagnostic.Parse([MousePath, "--both", "--mirror-window-ms", "1001"]));
        Assert.Null(NagaDpiDiagnostic.Parse([MousePath, "--both", "--mirror-window-ms", "30.5"]));
        Assert.Null(NagaDpiDiagnostic.Parse([MousePath, "--both", "--mirror-window-ms", "30", "--mirror-window-ms", "60"]));
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public void Losing_either_reader_ends_the_combined_session_and_closes_both_receivers(bool factoryLost)
    {
        using var cancellation = CancellationTokenSource.CreateLinkedTokenSource(TestContext.Current.CancellationToken);
        cancellation.CancelAfter(TimeSpan.FromSeconds(3));
        using var output = new StringWriter();
        using var errors = new StringWriter();
        var firmware = new Firmware();
        var readers = new Readers((factory, mouse) =>
            (factoryLost ? factory : mouse).Lose(new IOException("emulated input disconnect")));

        Assert.Equal(1, Run(readers, firmware, output, errors, cancellation.Token));
        Assert.Contains("emulated input disconnect", errors.ToString());
        Assert.Empty(ReportLines(output.ToString()));
        Assert.Equal(1, firmware.Reads);
        Assert.True(readers.Factory.Disposed);
        Assert.True(readers.Mouse.Disposed);
        Assert.True(firmware.Disposed);
    }

    static int Run(Readers readers, Firmware firmware, TextWriter output, TextWriter errors, CancellationToken cancellation,
        Func<TextWriter>? openCapture = null, TimeProvider? timeProvider = null, int mirrorWindowMs = 30) =>
        NagaDpiDiagnostic.Run(MousePath, [Mouse, Factory], node => node == Mouse,
        node => node.Interface == 0 ? new(node.DevicePath, NagaReportKind.MouseBackForward, 4) :
            new(node.DevicePath, NagaReportKind.Dpi, 16),
        path =>
        {
            Assert.Equal(MousePath, path);
            return new RazerTransport(firmware) { AnswerDelay = TimeSpan.Zero };
        }, readers.Open, output, errors, cancellation, openCapture, both: true, timeProvider: timeProvider,
        mirrorWindowMs: mirrorWindowMs);

    static byte[] FactoryReport(byte usage)
    {
        var report = new byte[16];
        report[0] = 0x04;
        report[1] = usage;
        return report;
    }

    static void AssertFrame(string line, string source, byte[] report, int samples)
    {
        using var frame = JsonDocument.Parse(line);
        Assert.Equal(source, frame.RootElement.GetProperty("source").GetString());
        Assert.Equal(Convert.ToHexString(report), frame.RootElement.GetProperty("report").GetString());
        Assert.Equal(samples, frame.RootElement.GetProperty("after").GetArrayLength());
    }

    static string[] ReportLines(string log) =>
        log.Split('\n').Where(line => line.Contains(" report=", StringComparison.Ordinal)).ToArray();

    sealed class CapturedOutput(Action<string> onLine) : StringWriter
    {
        public override void WriteLine(string? value)
        {
            base.WriteLine(value);
            if (value != null)
                onLine(value);
        }
    }

    sealed class Readers(Action<Reader, Reader> script)
    {
        public Reader Factory { get; private set; } = null!;
        public Reader Mouse { get; private set; } = null!;

        public IHidrawButtonReader Open(string path, HidrawButtonReader.ReportHandler report, Action<Exception> lost)
        {
            var reader = new Reader(Started, report, lost);
            if (path == FactoryPath)
                Factory = reader;
            else
            {
                Assert.Equal(MousePath, path);
                Mouse = reader;
            }
            return reader;
        }

        void Started()
        {
            Assert.NotNull(Factory);
            Assert.NotNull(Mouse);
            if (Factory.Started && Mouse.Started)
                script(Factory, Mouse);
        }
    }

    sealed class Reader(Action start, HidrawButtonReader.ReportHandler report, Action<Exception> lost) : IHidrawButtonReader
    {
        public bool Started { get; private set; }
        public bool Disposed { get; private set; }

        public void Start()
        {
            Assert.False(Started);
            Started = true;
            start();
        }
        public void Report(ReadOnlySpan<byte> data) => report(data);
        public void Lose(Exception error) => lost(error);
        public void Dispose() => Disposed = true;
    }

    sealed class Firmware : IHidFeatureDevice
    {
        RazerReport? _request;
        public string Path => MousePath;
        public int Reads { get; private set; }
        public bool Disposed { get; private set; }

        public void SendFeature(ReadOnlySpan<byte> wire)
        {
            Assert.Equal(RazerReport.Size + 1, wire.Length);
            Assert.Equal(0, wire[0]);
            var request = RazerReport.Parse(wire[1..]);
            Assert.Equal(0xFF, request.TransactionId);
            Assert.Equal(0x04, request.CommandClass);
            Assert.Equal(0x85, request.CommandId);
            Assert.Equal(7, request.DataSize);
            Assert.Equal(new byte[7], request.Arguments[..7].ToArray());
            _request = request;
            Reads++;
        }

        public int ReceiveFeature(Span<byte> wire)
        {
            var request = _request!;
            var (x, y) = Reads == 1 ? (800, 900) : (1600, 1700);
            var reply = RazerReport.Create(request.TransactionId, request.CommandClass, request.CommandId,
                [0, (byte)(x >> 8), (byte)x, (byte)(y >> 8), (byte)y, 0, 0]).Bytes.ToArray();
            reply[0] = (byte)ReportStatus.Successful;
            reply.CopyTo(wire[1..]);
            return wire.Length;
        }

        public void Dispose() => Disposed = true;
    }
}
