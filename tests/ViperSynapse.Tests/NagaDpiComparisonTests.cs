using ViperSynapse.Cli;
using ViperSynapse.Core.Lighting;
using ViperSynapse.Hardware;

namespace ViperSynapse.Tests;

public sealed class NagaDpiComparisonTests
{
    [Fact]
    public void Compares_raw_reports_and_independent_X_and_Y_changes()
    {
        string factoryPress = Report(0x20);
        string factoryRelease = Report();
        string remappedPress = Report(0x22, 0x20);
        string remappedRelease = Report(0x22);
        string factory = Capture(
            Press(10, factoryPress, new(800, 1200), new(1200, 1000)),
            Release(20, factoryRelease, new(1200, 1000)));
        string remapped = Capture(
            Press(10, remappedPress, new(800, 1200), new(800, 1200)),
            Release(20, remappedRelease, new(800, 1200)));

        var result = Compare(factory, remapped);

        Assert.Equal(0, result.Code);
        Assert.Empty(result.Errors);
        var comparison = Group(result.Output, "DPI Up #1");
        Assert.Collection(comparison,
            line =>
            {
                Assert.StartsWith("  Factory: ", line);
                Assert.Contains(factoryPress, line);
                Assert.Contains(factoryRelease, line);
                Assert.Contains("before=800x1200", line);
                Assert.Contains("after=1200x1000", line);
                Assert.Contains("DPI=changed (X +400, Y -200)", line);
            },
            line =>
            {
                Assert.StartsWith("  Remapped: ", line);
                Assert.Contains(remappedPress, line);
                Assert.Contains(remappedRelease, line);
                Assert.Contains("before=800x1200", line);
                Assert.Contains("after=800x1200", line);
                Assert.Contains("DPI=unchanged (X 0, Y 0)", line);
            },
            line => Assert.Equal("  Reports: press=different release=different", line));
    }

    [Fact]
    public void Pairs_each_button_in_its_own_order_and_distinguishes_missing_data()
    {
        string up = Report(0x20);
        string down = Report(0x21);
        string release = Report();
        string factory = Capture(
            Press(10, up, new(800, 900), new(800, 1000)),
            // This report releases Up and presses Down.
            Press(30, down, new(800, 1000), new(800, 900)),
            Release(40, release, new(800, 900)),
            Press(50, up, new(800, 900), null));
        string remapped = Capture(
            Press(10, down, new(800, 900), null),
            Release(20, release, null),
            Press(30, up, new(800, 900), new(800, 900)));

        var result = Compare(factory, remapped);

        Assert.Equal(0, result.Code);
        Assert.Empty(result.Errors);
        var firstUp = Group(result.Output, "DPI Up #1");
        Assert.Contains($"release={down}", firstUp[0]);
        Assert.Contains("DPI=changed (X 0, Y +100)", firstUp[0]);
        Assert.Contains("DPI=unchanged", firstUp[1]);
        Assert.Equal("  Reports: press=same release=unavailable", firstUp[2]);
        var secondUp = Group(result.Output, "DPI Up #2");
        Assert.Contains("DPI=unknown", secondUp[0]);
        Assert.Equal("  Remapped: no recorded press", secondUp[1]);
        Assert.Equal("  Reports: press=unavailable release=unavailable", secondUp[2]);
        var firstDown = Group(result.Output, "DPI Down #1");
        Assert.Contains("DPI=changed (X 0, Y -100)", firstDown[0]);
        Assert.Contains("DPI=unknown", firstDown[1]);
        Assert.Equal("  Reports: press=same release=same", firstDown[2]);
    }

    [Theory]
    [InlineData(true, "Factory")]
    [InlineData(false, "Remapped")]
    public void Invalid_input_identifies_the_capture_and_suppresses_partial_output(bool factoryInvalid, string label)
    {
        string valid = Capture(Press(10, Report(0x20), new(800, 900), new(800, 1100)));
        string invalid = valid + "{\n";

        var result = Compare(factoryInvalid ? invalid : valid, factoryInvalid ? valid : invalid);

        Assert.Equal(1, result.Code);
        Assert.Empty(result.Output);
        Assert.Contains($"{label} capture line 3", result.Errors);
    }

    [Fact]
    public void Simultaneous_presses_use_their_own_post_press_samples()
    {
        var frame = new NagaDpiCapture.Frame(10, Report(0x21, 0x20), new(9, new(800, 900)),
            [new(11, new(900, 900)), new(12, new(800, 1100))]);
        string capture = Capture(frame);

        var result = Compare(capture, capture);

        Assert.Equal(0, result.Code);
        Assert.Empty(result.Errors);
        Assert.Contains("DPI=changed (X +100, Y 0)", Group(result.Output, "DPI Up #1")[0]);
        Assert.Contains("DPI=changed (X 0, Y +200)", Group(result.Output, "DPI Down #1")[0]);
    }

    static string[] Group(string output, string title)
    {
        var lines = output.Split('\n').Select(line => line.TrimEnd('\r')).ToArray();
        int heading = Array.IndexOf(lines, title);
        Assert.True(heading >= 0, $"Expected group '{title}' in output:\n{output}");
        return lines.Skip(heading + 1).TakeWhile(line => line.StartsWith("  ", StringComparison.Ordinal)).ToArray();
    }

    static (int Code, string Output, string Errors) Compare(string factory, string remapped)
    {
        using var factoryInput = new StringReader(factory);
        using var remappedInput = new StringReader(remapped);
        using var output = new StringWriter();
        using var errors = new StringWriter();
        int code = NagaDpiComparison.Compare(factoryInput, remappedInput, output, errors);
        return (code, output.ToString(), errors.ToString());
    }

    static string Capture(params NagaDpiCapture.Frame[] frames)
    {
        using var writer = new StringWriter();
        NagaDpiCapture.WriteHeader(writer, new(0, new DpiStage(800, 900)));
        foreach (var frame in frames)
            NagaDpiCapture.WriteFrame(writer, frame);
        return writer.ToString();
    }

    static NagaDpiCapture.Frame Press(long ticks, string report, DpiStage? before, DpiStage? after) =>
        new(ticks, report, new(ticks - 1, before), [new(ticks + 1, after)]);

    static NagaDpiCapture.Frame Release(long ticks, string report, DpiStage? dpi) =>
        new(ticks, report, new(ticks - 1, dpi), []);

    static string Report(params byte[] usages)
    {
        var report = new byte[16];
        report[0] = 0x04;
        usages.CopyTo(report, 1);
        return Convert.ToHexString(report);
    }
}
