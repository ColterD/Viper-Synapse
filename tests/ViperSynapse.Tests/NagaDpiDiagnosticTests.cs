using System.Text.Json;
using ViperSynapse.Cli;
using ViperSynapse.Core.Devices;
using ViperSynapse.Core.Protocol;
using ViperSynapse.Hardware;
using ViperSynapse.Hardware.Linux;

namespace ViperSynapse.Tests;

public sealed class NagaDpiDiagnosticTests
{
    const string ControlPath = "/dev/hidraw0";
    const string InputPath = "/dev/hidraw1";
    static readonly HidrawNode Control = new(ControlPath, 0x1532, LegacyRemapControls.NagaChroma, 0, "Naga Chroma", "usb-1/input0");
    static readonly HidrawNode Input = new(InputPath, 0x1532, LegacyRemapControls.NagaChroma, 1, "Naga Chroma", "usb-1/input1");

    [Theory]
    [InlineData(0x20, "DPI Up")]
    [InlineData(0x21, "DPI Down")]
    public void Logs_press_and_release_with_live_dpi_from_get_requests_only(byte usage, string button)
    {
        using var cancellation = CreateCancellation();
        using var output = StopAfterRelease(cancellation, button);
        using var errors = new StringWriter();
        var firmware = new Firmware();
        var press = InputReport(usage, 0x22);
        var release = InputReport(0x22);
        var reader = new Reader((report, _) =>
        {
            report(InputReport(0x22)); // Tilt only.
            report(press);
            report(InputReport(0x22, usage)); // Same held usages, even with different byte order.
            report(new byte[] { 0x04 }); // Truncated report while the button is held.
            report(release);
            report(InputReport()); // Tilt release.
        });

        int result = Run([Control, Input], firmware, reader, output, errors, cancellation.Token);

        Assert.Equal(0, result);
        var edges = output.ToString().Split('\n').Where(line => line.Contains(" report=", StringComparison.Ordinal)).ToArray();
        Assert.Collection(edges,
            line =>
            {
                Assert.Contains($"{button} press report={Convert.ToHexString(press)}", line, StringComparison.OrdinalIgnoreCase);
                Assert.Contains("before=800x900", line);
                Assert.Contains("after=1600x1700", line);
            },
            line => Assert.Contains($"{button} release report={Convert.ToHexString(release)}", line, StringComparison.OrdinalIgnoreCase));
        Assert.Equal(2, firmware.Reads);
        Assert.True(reader.Disposed);
        Assert.True(firmware.Disposed);
        Assert.Equal("", errors.ToString());
    }

    [Fact]
    public void Reports_a_failed_live_read_as_unavailable_and_still_logs_the_release()
    {
        using var cancellation = CreateCancellation();
        using var output = StopAfterRelease(cancellation, "DPI Up");
        using var errors = new StringWriter();
        var firmware = new Firmware { FailRead = 2 };
        var reader = new Reader((report, _) =>
        {
            report(InputReport(0x20));
            report(InputReport());
        });

        Assert.Equal(0, Run([Control, Input], firmware, reader, output, errors, cancellation.Token));

        var log = output.ToString();
        Assert.Contains("before=800x900", log);
        Assert.Contains("after=unavailable", log, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("DPI Up release", log);
        Assert.True(reader.Disposed);
        Assert.True(firmware.Disposed);
    }

    [Fact]
    public void Each_press_uses_the_most_recent_completed_sample()
    {
        using var cancellation = CreateCancellation();
        using var errors = new StringWriter();
        var firmware = new Firmware();
        var reader = new Reader((report, _) =>
        {
            report(InputReport(0x20));
            report(InputReport());
        });
        using var output = new ObservingOutput(line =>
        {
            if (line.Contains("DPI Up release report=", StringComparison.Ordinal))
            {
                reader.Report(InputReport(0x21));
                reader.Report(InputReport());
            }
            if (line.Contains("DPI Down release report=", StringComparison.Ordinal))
                cancellation.Cancel();
        });

        Assert.Equal(0, Run([Control, Input], firmware, reader, output, errors, cancellation.Token));

        var pressLines = output.ToString().Split('\n').Where(line => line.Contains(" press report=", StringComparison.Ordinal));
        Assert.Collection(pressLines,
            line =>
            {
                Assert.Contains("DPI Up press", line);
                Assert.Contains("before=800x900@", line);
                Assert.Contains("after=1600x1700@", line);
            },
            line =>
            {
                Assert.Contains("DPI Down press", line);
                Assert.Contains("before=1600x1700@", line);
                Assert.Contains("after=1000x1100@", line);
            });
        Assert.Equal(3, firmware.Reads);
    }

    [Fact]
    public void Emulated_capture_replays_raw_edges_and_live_dpi_samples_offline()
    {
        using var cancellation = CreateCancellation();
        using var output = StopAfterRelease(cancellation, "DPI Down");
        using var errors = new StringWriter();
        using var capture = new StringWriter();
        var firmware = new Firmware { FailRead = 3 };
        var up = InputReport(0x20, 0x22);
        var swap = InputReport(0x21, 0x22);
        var release = InputReport(0x22);
        var reader = new Reader((report, _) =>
        {
            report(up);
            report(InputReport(0x22, 0x20)); // No new edge, so this snapshot need not be exported.
            report(swap); // One snapshot releases DPI Up and presses DPI Down.
            report(release);
        });

        Assert.Equal(0, Run([Control, Input], firmware, reader, output, errors, cancellation.Token, () => capture));
        Assert.Equal(3, firmware.Reads);
        Assert.True(reader.Disposed);
        Assert.True(firmware.Disposed);

        string exported = capture.ToString();
        var lines = exported.Split('\n', StringSplitOptions.RemoveEmptyEntries);
        Assert.Equal(4, lines.Length);
        using var header = JsonDocument.Parse(lines[0]);
        Assert.Equal(1, header.RootElement.GetProperty("version").GetInt32());
        Assert.Equal("1532:0053", header.RootElement.GetProperty("device").GetString());
        using var swapFrame = JsonDocument.Parse(lines[2]);
        Assert.Equal(Convert.ToHexString(swap), swapFrame.RootElement.GetProperty("report").GetString());
        var failedRead = Assert.Single(swapFrame.RootElement.GetProperty("after").EnumerateArray());
        Assert.Equal(JsonValueKind.Null, failedRead.GetProperty("dpi").ValueKind);

        using var input = new StringReader(exported);
        using var replay = new StringWriter();
        Assert.Equal(0, NagaDpiCapture.Replay(input, replay, errors));

        var replayed = EdgeLines(replay.ToString());
        Assert.Equal(EdgeLines(output.ToString()), replayed);
        Assert.Collection(replayed,
            line => Assert.Contains($"DPI Up press report={Convert.ToHexString(up)}", line),
            line => Assert.Contains($"DPI Up release report={Convert.ToHexString(swap)}", line),
            line => Assert.Contains($"DPI Down press report={Convert.ToHexString(swap)}", line),
            line => Assert.Contains($"DPI Down release report={Convert.ToHexString(release)}", line));
        Assert.Contains("after=1600x1700@", replayed[0]);
        Assert.Contains("after=unavailable@", replayed[2]);
        Assert.Equal(3, firmware.Reads);
        Assert.Equal("", errors.ToString());
    }

    [Fact]
    public void Emulated_mouse_capture_replays_Back_and_Forward_and_compares_against_factory()
    {
        using var factoryCancellation = CreateCancellation();
        using var factoryOutput = StopAfterRelease(factoryCancellation, "DPI Down");
        using var factoryCapture = new StringWriter();
        using var errors = new StringWriter();
        var factoryFirmware = new Firmware();
        var factoryUp = InputReport(0x20);
        var factoryDown = InputReport(0x21);
        var factoryRelease = InputReport();
        var factoryReader = new Reader((report, _) =>
        {
            report(factoryUp);
            report(factoryDown);
            report(factoryRelease);
        });
        Assert.Equal(0, Run([Control, Input], factoryFirmware, factoryReader, factoryOutput, errors,
            factoryCancellation.Token, () => factoryCapture));
        Assert.Equal(3, factoryFirmware.Reads);

        using var mouseCancellation = CreateCancellation();
        using var mouseOutput = StopAfterRelease(mouseCancellation, "DPI Up");
        using var mouseCapture = new StringWriter();
        var mouseFirmware = new Firmware { FixedDpi = true };
        byte[] back = [0x69, 0x01, 0x02, 0x03];
        byte[] forward = [0x71, 0x04, 0x05, 0x06];
        byte[] mouseRelease = [0x61, 0x07, 0x08, 0x09];
        var mouseReader = new Reader((report, _) =>
        {
            report([0x61, 0x00, 0x00, 0x00]); // Primary button and tilt bits alone produce no DPI edge.
            report(back);
            report([0x08, 0x0A, 0x0B, 0x0C]); // Movement and other buttons may change while Back is held.
            report([0x00, 0x00, 0x00]); // Wrong length must leave the held Back state intact.
            report(forward);
            report(mouseRelease);
        });
        Assert.Equal(0, Run([Control, Input], mouseFirmware, mouseReader, mouseOutput, errors,
            mouseCancellation.Token, () => mouseCapture, backForward: true));
        Assert.Equal(3, mouseFirmware.Reads);
        Assert.True(mouseFirmware.Disposed);
        Assert.True(mouseReader.Disposed);

        var frames = mouseCapture.ToString().Split('\n', StringSplitOptions.RemoveEmptyEntries);
        Assert.Equal(4, frames.Length);
        using var header = JsonDocument.Parse(frames[0]);
        Assert.Equal(2, header.RootElement.GetProperty("version").GetInt32());
        Assert.Equal("1532:0053", header.RootElement.GetProperty("device").GetString());
        Assert.Equal("mouse-back-forward", header.RootElement.GetProperty("source").GetString());
        Assert.Equal(4, header.RootElement.GetProperty("reportLength").GetInt32());

        using var mouseReplayInput = new StringReader(mouseCapture.ToString());
        using var replay = new StringWriter();
        Assert.Equal(0, NagaDpiCapture.Replay(mouseReplayInput, replay, errors));
        var replayed = EdgeLines(replay.ToString());
        Assert.Equal(EdgeLines(mouseOutput.ToString()), replayed);
        Assert.Collection(replayed,
            line => Assert.Contains($"DPI Down press report={Convert.ToHexString(back)}", line),
            line => Assert.Contains($"DPI Down release report={Convert.ToHexString(forward)}", line),
            line => Assert.Contains($"DPI Up press report={Convert.ToHexString(forward)}", line),
            line => Assert.Contains($"DPI Up release report={Convert.ToHexString(mouseRelease)}", line));

        using var factoryCompareInput = new StringReader(factoryCapture.ToString());
        using var mouseCompareInput = new StringReader(mouseCapture.ToString());
        using var comparison = new StringWriter();
        Assert.Equal(0, NagaDpiComparison.Compare(factoryCompareInput, mouseCompareInput, comparison, errors));
        var comparisonLines = comparison.ToString().Split('\n').Select(line => line.TrimEnd('\r')).ToArray();
        AssertComparison("DPI Up #1", factoryUp, factoryDown, forward, mouseRelease, "X +800, Y +800");
        AssertComparison("DPI Down #1", factoryDown, factoryRelease, back, forward, "X +200, Y +200");
        Assert.Empty(errors.ToString());

        void AssertComparison(string heading, byte[] factoryPress, byte[] factoryReleaseReport, byte[] mousePress,
            byte[] mouseReleaseReport, string factoryDelta)
        {
            int start = Array.IndexOf(comparisonLines, heading);
            Assert.True(start >= 0, $"Missing comparison for {heading}.");
            Assert.Collection(comparisonLines.Skip(start + 1).TakeWhile(line => line.StartsWith("  ", StringComparison.Ordinal)),
                line =>
                {
                    Assert.Contains($"Factory: press={Convert.ToHexString(factoryPress)} release={Convert.ToHexString(factoryReleaseReport)}", line);
                    Assert.Contains($"DPI=changed ({factoryDelta})", line);
                },
                line =>
                {
                    Assert.Contains($"Remapped: press={Convert.ToHexString(mousePress)} release={Convert.ToHexString(mouseReleaseReport)}", line);
                    Assert.Contains("DPI=unchanged (X 0, Y 0)", line);
                },
                line => Assert.Equal("  Reports: press=different release=different", line));
        }
    }

    [Fact]
    public void Back_and_Forward_mode_requires_a_qualified_mouse_input_before_opening()
    {
        using var output = new StringWriter();
        using var errors = new StringWriter();
        var queried = new List<HidrawNode>();

        int code = NagaDpiDiagnostic.Run(ControlPath, [Control, Input], node => node == Control,
            node =>
            {
                queried.Add(node);
                return node == Input ? new NagaReportNode(node.DevicePath, NagaReportKind.Dpi, 16) : null;
            },
            _ => throw new InvalidOperationException("The control device must stay unopened."),
            (_, _, _) => throw new InvalidOperationException("The input device must stay unopened."),
            output, errors, TestContext.Current.CancellationToken, backForward: true);

        Assert.Equal(1, code);
        Assert.Equal(Control, Assert.Single(queried));
        Assert.Empty(output.ToString());
        Assert.NotEmpty(errors.ToString());
    }

    [Theory]
    [InlineData("unsupported-input", 4, "08000000", 1)]
    [InlineData("mouse-back-forward", 9, "08000000", 1)]
    [InlineData("mouse-back-forward", 4, "080000", 2)]
    [InlineData("mouse-back-forward", 4, "60000000", 2)]
    public void Replay_rejects_invalid_mouse_capture_headers_and_reports(string source, int length, string hex, int line)
    {
        using var input = new StringReader($$$"""
            {"version":2,"device":"1532:0053","source":"{{{source}}}","reportLength":{{{length}}},"initial":{"ticks":0,"dpi":null}}
            {"ticks":1,"report":"{{{hex}}}","before":{"ticks":0,"dpi":null},"after":[{"ticks":2,"dpi":null}]}
            """);
        using var output = new StringWriter();
        using var errors = new StringWriter();

        Assert.Equal(1, NagaDpiCapture.Replay(input, output, errors));
        Assert.DoesNotContain(" report=", output.ToString());
        Assert.Contains($"line {line}", errors.ToString());
    }

    [Theory]
    [InlineData("04")]
    [InlineData("04200000000000000000000000000000")]
    public void Replay_rejects_truncated_reports_and_press_frames_without_after_samples(string hex)
    {
        using var input = new StringReader($$$$"""
            {"version":1,"device":"1532:0053","initial":{"ticks":0,"dpi":{"x":800,"y":900}}}
            {"ticks":1,"report":"{{{{hex}}}}","before":{"ticks":0,"dpi":{"x":800,"y":900}},"after":[]}
            """);
        using var output = new StringWriter();
        using var errors = new StringWriter();

        Assert.Equal(1, NagaDpiCapture.Replay(input, output, errors));
        Assert.DoesNotContain(" report=", output.ToString());
        Assert.NotEmpty(errors.ToString());
    }

    [Theory]
    [InlineData("""{"ticks":10,"report":"04200000000000000000000000000000","before":{"ticks":0,"dpi":null},"after":[{"ticks":9,"dpi":null}]}""")]
    [InlineData("")]
    public void Replay_rejects_a_sample_before_its_press_and_oversized_records(string frame)
    {
        const string header = """{"version":1,"device":"1532:0053","initial":{"ticks":0,"dpi":null}}""";
        using var input = new StringReader(header + "\n" + (frame.Length == 0 ? new string(' ', 4097) : frame));
        using var output = new StringWriter();
        using var errors = new StringWriter();

        Assert.Equal(1, NagaDpiCapture.Replay(input, output, errors));
        Assert.DoesNotContain(" report=", output.ToString());
        Assert.Contains("line 2", errors.ToString());
    }

    [Fact]
    public void Replay_rejects_samples_that_run_backward_across_frames()
    {
        using var input = new StringReader("""
            {"version":1,"device":"1532:0053","initial":{"ticks":0,"dpi":null}}
            {"ticks":10,"report":"04200000000000000000000000000000","before":{"ticks":0,"dpi":null},"after":[{"ticks":100,"dpi":null}]}
            {"ticks":20,"report":"04210000000000000000000000000000","before":{"ticks":0,"dpi":null},"after":[{"ticks":30,"dpi":null}]}
            """);
        using var output = new StringWriter();
        using var errors = new StringWriter();

        Assert.Equal(1, NagaDpiCapture.Replay(input, output, errors));
        Assert.Contains("DPI Up press", output.ToString());
        Assert.DoesNotContain("DPI Down press", output.ToString());
        Assert.Contains("line 3", errors.ToString());
    }

    [Fact]
    public void A_lost_input_reader_closes_both_nodes_and_returns_an_error()
    {
        using var cancellation = CreateCancellation();
        using var output = new StringWriter();
        using var errors = new StringWriter();
        var firmware = new Firmware();
        var reader = new Reader((_, lost) => lost(new IOException("emulated unplug")));

        Assert.Equal(1, Run([Control, Input], firmware, reader, output, errors, cancellation.Token));

        Assert.Contains("emulated unplug", errors.ToString());
        Assert.True(reader.Disposed);
        Assert.True(firmware.Disposed);
    }

    [Fact]
    public void A_lost_control_node_logs_the_observed_press_and_closes_both_nodes()
    {
        using var cancellation = CreateCancellation();
        using var output = new StringWriter();
        using var errors = new StringWriter();
        var firmware = new Firmware { DisconnectRead = 2 };
        var reader = new Reader((report, _) => report(InputReport(0x20)));

        Assert.Equal(1, Run([Control, Input], firmware, reader, output, errors, cancellation.Token));

        Assert.Contains("DPI Up press", output.ToString());
        Assert.Contains("after=unavailable", output.ToString());
        Assert.Contains($"control node {ControlPath} disconnected", errors.ToString());
        Assert.True(reader.Disposed);
        Assert.True(firmware.Disposed);
    }

    [Fact]
    public void A_report_interface_on_another_usb_port_is_never_opened()
    {
        using var output = new StringWriter();
        using var errors = new StringWriter();
        var otherMouseInput = Input with { Phys = "usb-2/input1" };
        var firmware = new Firmware();
        var reader = new Reader((_, _) => { });

        Assert.Equal(1, Run([Control, otherMouseInput], firmware, reader, output, errors, TestContext.Current.CancellationToken));

        Assert.False(reader.Opened);
        Assert.Equal(0, firmware.Reads);
    }

    static int Run(IReadOnlyList<HidrawNode> nodes, Firmware firmware, Reader reader,
        TextWriter output, TextWriter errors, CancellationToken cancellation, Func<TextWriter>? openCapture = null,
        bool backForward = false) =>
        NagaDpiDiagnostic.Run(ControlPath, nodes,
            node => node.Interface == 0,
            node => backForward ? new NagaReportNode(node.DevicePath, NagaReportKind.MouseBackForward, 4) :
                node.Interface == 1 ? new NagaReportNode(node.DevicePath, NagaReportKind.Dpi, 16) : null,
            path =>
            {
                Assert.Equal(ControlPath, path);
                return new RazerTransport(firmware) { AnswerDelay = TimeSpan.Zero };
            },
            (path, report, lost) =>
            {
                Assert.Equal(backForward ? ControlPath : InputPath, path);
                return reader.Open(report, lost);
            },
            output, errors, cancellation, openCapture, backForward);

    static CancellationTokenSource CreateCancellation()
    {
        var cancellation = CancellationTokenSource.CreateLinkedTokenSource(TestContext.Current.CancellationToken);
        cancellation.CancelAfter(TimeSpan.FromSeconds(3));
        return cancellation;
    }

    static byte[] InputReport(params byte[] usages)
    {
        var report = new byte[16];
        report[0] = 0x04;
        usages.CopyTo(report, 1);
        return report;
    }

    static string[] EdgeLines(string log) =>
        log.Split('\n').Where(line => line.Contains(" report=", StringComparison.Ordinal)).ToArray();

    static ObservingOutput StopAfterRelease(CancellationTokenSource cancellation, string button) => new(line =>
    {
        if (line.Contains($"{button} release report=", StringComparison.Ordinal))
            cancellation.Cancel();
    });

    sealed class ObservingOutput(Action<string> onLine) : StringWriter
    {
        public override void WriteLine(string? value)
        {
            base.WriteLine(value);
            if (value != null)
                onLine(value);
        }
    }

    sealed class Reader(Action<HidrawButtonReader.ReportHandler, Action<Exception>> script) : IHidrawButtonReader
    {
        HidrawButtonReader.ReportHandler? _report;
        Action<Exception>? _lost;
        public bool Opened => _report is not null;
        public bool Disposed { get; private set; }

        public IHidrawButtonReader Open(HidrawButtonReader.ReportHandler report, Action<Exception> lost)
        {
            _report = report;
            _lost = lost;
            return this;
        }

        public void Start() => script(_report!, _lost!);
        public void Report(ReadOnlySpan<byte> report) => _report!(report);
        public void Dispose() => Disposed = true;
    }

    sealed class Firmware : IHidFeatureDevice
    {
        RazerReport? _request;
        public string Path => ControlPath;
        public int Reads { get; private set; }
        public int? FailRead { get; init; }
        public int? DisconnectRead { get; init; }
        public bool FixedDpi { get; init; }
        public bool Disposed { get; private set; }

        public void SendFeature(ReadOnlySpan<byte> wire)
        {
            Assert.Equal(RazerReport.Size + 1, wire.Length);
            Assert.Equal(0, wire[0]);
            _request = RazerReport.Parse(wire[1..]);
            Assert.Equal(0xFF, _request.TransactionId);
            Assert.Equal(0x04, _request.CommandClass);
            Assert.Equal(0x85, _request.CommandId);
            Assert.Equal(7, _request.DataSize);
            Assert.Equal(new byte[7], _request.Arguments[..7].ToArray());
            Reads++;
        }

        public int ReceiveFeature(Span<byte> wire)
        {
            if (Reads == DisconnectRead)
                throw new HidrawDeviceDisconnectedException("emulated control disconnect");
            if (Reads == FailRead)
                throw new IOException("emulated DPI read failure");

            var request = _request!;
            var (x, y) = Reads switch
            {
                1 => (800, 900),
                _ when FixedDpi => (800, 900),
                2 => (1600, 1700),
                _ => (1000, 1100),
            };
            var reply = RazerReport.Create(request.TransactionId, request.CommandClass, request.CommandId,
                [0, (byte)(x >> 8), (byte)x, (byte)(y >> 8), (byte)y, 0, 0]).Bytes.ToArray();
            reply[0] = (byte)ReportStatus.Successful;
            reply.CopyTo(wire[1..]);
            return wire.Length;
        }

        public void Dispose() => Disposed = true;
    }
}
