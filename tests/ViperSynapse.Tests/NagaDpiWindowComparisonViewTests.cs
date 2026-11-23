using System.Text;
using Avalonia.Controls;
using Avalonia.Input.Platform;
using Avalonia.Interactivity;
using Avalonia.Threading;
using ViperSynapse.App.ViewModels;
using ViperSynapse.App.Views;
using ViperSynapse.Cli;
using ViperSynapse.Core.Protocol;
using ViperSynapse.Hardware;

namespace ViperSynapse.Tests;

public sealed class NagaDpiWindowComparisonViewTests
{
    [Fact]
    public Task The_viewer_offers_to_open_a_combined_capture() => HeadlessApp.Session.Dispatch(() =>
    {
        var window = new NagaDpiWindowComparisonView();
        window.Show();
        try
        {
            var open = Assert.IsType<Button>(window.FindControl<Button>("OpenCaptureButton"));
            Assert.True(open.IsEffectivelyVisible);
            var compare = Assert.IsType<Button>(window.FindControl<Button>("CompareCaptureButton"));
            Assert.False(compare.IsEffectivelyVisible);
        }
        finally
        {
            window.Close();
        }
    }, CancellationToken.None);

    [Fact]
    public Task Direct_capture_compares_selected_windows_and_preserves_results_on_bad_input() =>
        HeadlessApp.Session.Dispatch<bool>(async () =>
    {
        var model = new NagaDpiWindowComparisonViewModel();
        var window = new NagaDpiWindowComparisonView { DataContext = model };
        var directory = Directory.CreateTempSubdirectory("viper-naga-open-");
        window.Show();
        try
        {
            const string sampleName = "naga-chroma-combined.jsonl";
            string path = Path.Combine(directory.FullName, sampleName);
            using (var sample = typeof(NagaDpiWindowComparisonViewTests).Assembly.GetManifestResourceStream(sampleName)!)
            {
                await using var file = File.Create(path);
                await sample.CopyToAsync(file);
            }

            model.SelectCapture(path);
            Dispatcher.UIThread.RunJobs();
            var windows = Assert.IsType<TextBox>(window.FindControl<TextBox>("CandidateWindowsBox"));
            var compare = Assert.IsType<Button>(window.FindControl<Button>("CompareCaptureButton"));
            var counts = Assert.IsType<ListBox>(window.FindControl<ListBox>("CountsList"));
            var changes = Assert.IsType<ListBox>(window.FindControl<ListBox>("ChangesList"));
            Assert.True(compare.IsEffectivelyVisible);
            Assert.True(compare.IsEnabled);

            async Task Compare(string values)
            {
                windows.Text = values;
                Dispatcher.UIThread.RunJobs();
                Assert.NotNull(compare.Command);
                compare.Command.Execute(null);
                await model.CompareCaptureCommand.ExecutionTask!;
                Dispatcher.UIThread.RunJobs();
            }

            await Compare("60, 10 30, 10");
            Assert.Empty(model.Error);
            Assert.Equal(sampleName, model.FileName);
            Assert.Equal(new[] { ("Recorded (30 ms)", 1L), ("60 ms", 1L), ("10 ms", 2L), ("30 ms", 1L) },
                counts.Items.Cast<NagaDpiWindowCountRow>().Select(row => (row.Window, row.DpiUp)));
            var changed = Assert.IsType<NagaDpiWindowChangeRow>(Assert.Single(changes.Items));
            Assert.Equal((3L, "10000000", "No press", "Press"), (changed.FrameNumber, changed.Report, changed.Recorded, changed.Candidate));
            Assert.Contains("3,40.000 ms,Mouse Back / Forward,10000000,DPI Up,10 ms,No press,Press", model.CreateCsv());

            await Compare("1001");
            Assert.Contains("whole numbers", model.Error);
            Assert.Same(changed, Assert.Single(changes.Items));
            await Compare("20");
            Assert.Empty(model.Error);
            Assert.Empty(changes.Items);
            Assert.Equal("20 ms", counts.Items.Cast<NagaDpiWindowCountRow>().Last().Window);

            string malformedPath = Path.Combine(directory.FullName, "malformed.jsonl");
            await File.WriteAllTextAsync(malformedPath, await File.ReadAllTextAsync(path) + "{invalid}\n");
            model.SelectCapture(malformedPath);
            await Compare("10");
            Assert.Contains("line 6", model.Error);
            Assert.Equal(sampleName, model.FileName);
            Assert.Equal("20 ms", counts.Items.Cast<NagaDpiWindowCountRow>().Last().Window);
            Assert.Empty(changes.Items);
        }
        finally
        {
            window.Close();
            directory.Delete(recursive: true);
        }
        return true;
    }, CancellationToken.None);

    [Fact]
    public Task Loads_the_cli_export_and_shows_counts_and_changed_reports() => HeadlessApp.Session.Dispatch<bool>(async () =>
    {
        var model = new NagaDpiWindowComparisonViewModel();
        var window = new NagaDpiWindowComparisonView { DataContext = model };
        window.Show();
        try
        {
            using var export = EmulatedExport([10, 30], includeDown: true);
            await model.LoadAsync(export, "emulated-comparison.json");
            Dispatcher.UIThread.RunJobs();

            Assert.True(model.HasResults);
            Assert.Equal("emulated-comparison.json", model.FileName);
            Assert.True(string.IsNullOrEmpty(model.Error));
            Assert.Equal("2 changed reports.", model.DetailStatus);
            Assert.Collection(model.Counts,
                row => Assert.Equal(("Recorded (30 ms)", 1L, 1L), (row.Window, row.DpiUp, row.DpiDown)),
                row => Assert.Equal(("10 ms", 2L, 2L), (row.Window, row.DpiUp, row.DpiDown)),
                row => Assert.Equal(("30 ms", 1L, 1L), (row.Window, row.DpiUp, row.DpiDown)));
            Assert.Collection(model.Changes,
                row =>
                {
                    Assert.Equal(3L, row.FrameNumber);
                    Assert.Equal("40.000 ms", row.Time);
                    Assert.Equal("Mouse Back / Forward", row.Source);
                    Assert.Equal("10000000", row.Report);
                    Assert.Equal("DPI Up", row.Button);
                    Assert.Equal("10 ms", row.Window);
                    Assert.Equal("No press", row.Recorded);
                    Assert.Equal("Press", row.Candidate);
                },
                row =>
                {
                    Assert.Equal(7L, row.FrameNumber);
                    Assert.Equal("140.000 ms", row.Time);
                    Assert.Equal("08000000", row.Report);
                    Assert.Equal("DPI Down", row.Button);
                });

            var counts = Assert.IsAssignableFrom<ItemsControl>(window.FindControl<ItemsControl>("CountsList"));
            var changes = Assert.IsAssignableFrom<ItemsControl>(window.FindControl<ItemsControl>("ChangesList"));
            var open = Assert.IsType<Button>(window.FindControl<Button>("OpenFileButton"));
            Assert.True(open.IsEffectivelyVisible);
            Assert.True(counts.IsEffectivelyVisible);
            Assert.True(changes.IsEffectivelyVisible);
            Assert.Equal(model.Counts.Cast<object>(), counts.Items);
            Assert.Equal(model.Changes.Cast<object>(), changes.Items);
        }
        finally
        {
            window.Close();
        }
        return true;
    }, CancellationToken.None);

    [Fact]
    public Task Changed_report_filters_intersect_without_filtering_the_press_counts() => HeadlessApp.Session.Dispatch<bool>(async () =>
    {
        var model = new NagaDpiWindowComparisonViewModel();
        var window = new NagaDpiWindowComparisonView { DataContext = model };
        window.Show();
        try
        {
            using var export = EmulatedExport([5, 10, 30], includeDown: true, reverseDown: true);
            await model.LoadAsync(export, "filter-comparison.json");
            Dispatcher.UIThread.RunJobs();

            var windowFilter = Assert.IsType<ComboBox>(window.FindControl<ComboBox>("WindowFilterBox"));
            var buttonFilter = Assert.IsType<ComboBox>(window.FindControl<ComboBox>("ButtonFilterBox"));
            var sourceFilter = Assert.IsType<ComboBox>(window.FindControl<ComboBox>("SourceFilterBox"));
            var counts = Assert.IsAssignableFrom<ItemsControl>(window.FindControl<ItemsControl>("CountsList"));
            var changes = Assert.IsAssignableFrom<ItemsControl>(window.FindControl<ItemsControl>("ChangesList"));
            var allCounts = model.Counts.ToArray();
            Assert.All(new[] { windowFilter, buttonFilter, sourceFilter }, box =>
            {
                Assert.True(box.IsEffectivelyVisible);
                Assert.Equal(0, box.SelectedIndex);
            });
            Assert.Equal(4, model.Changes.Count);

            Select(windowFilter, 2);
            Assert.Equal(new[] { 3L, 7L }, model.Changes.Select(row => row.FrameNumber));
            Assert.All(model.Changes, row => Assert.Equal("10 ms", row.Window));

            Select(buttonFilter, 1);
            var up = Assert.Single(model.Changes);
            Assert.Equal((3L, "DPI Up", "Mouse Back / Forward"), (up.FrameNumber, up.Button, up.Source));

            Select(sourceFilter, 1);
            Assert.Empty(model.Changes);
            Assert.Empty(changes.Items);
            Assert.Contains("No exported reports match the filters.", model.DetailStatus);

            Select(buttonFilter, 0);
            var down = Assert.Single(model.Changes);
            Assert.Equal((7L, "DPI Down", "Factory report 04"), (down.FrameNumber, down.Button, down.Source));
            Select(sourceFilter, 0);
            Assert.Equal(2, model.Changes.Count);
            Select(windowFilter, 0);
            Assert.Equal(4, model.Changes.Count);

            Select(sourceFilter, 2);
            Assert.Equal(2, model.Changes.Count);
            Assert.All(model.Changes, row => Assert.Equal("Mouse Back / Forward", row.Source));

            Select(windowFilter, 2);
            Select(buttonFilter, 1);
            Assert.Single(model.Changes);
            Assert.Equal(allCounts, model.Counts);
            Assert.Equal(allCounts.Cast<object>(), counts.Items);

            using var replacement = EmulatedExport([17, 30], includeDown: true, reverseDown: true);
            await model.LoadAsync(replacement, "replacement.json");
            Dispatcher.UIThread.RunJobs();
            Assert.All(new[] { windowFilter, buttonFilter, sourceFilter }, box => Assert.Equal(0, box.SelectedIndex));
            Assert.Equal(new[] { 3L, 7L }, model.Changes.Select(row => row.FrameNumber));
            Assert.All(model.Changes, row => Assert.Equal("17 ms", row.Window));
            Assert.Equal(model.Changes.Cast<object>(), changes.Items);
        }
        finally
        {
            window.Close();
        }
        return true;

        static void Select(ComboBox box, int index)
        {
            box.SelectedIndex = index;
            Dispatcher.UIThread.RunJobs();
        }
    }, CancellationToken.None);

    [Fact]
    public Task Csv_export_saves_only_reports_matching_all_active_filters() => HeadlessApp.Session.Dispatch<bool>(async () =>
    {
        var model = new NagaDpiWindowComparisonViewModel();
        var window = new NagaDpiWindowComparisonView { DataContext = model };
        var directory = Directory.CreateTempSubdirectory("viper-naga-csv-");
        window.Show();
        try
        {
            var exportButton = Assert.IsType<Button>(window.FindControl<Button>("ExportCsvButton"));
            Assert.False(exportButton.IsEnabled);

            using var export = EmulatedExport([5, 10, 30], includeDown: true, reverseDown: true);
            await model.LoadAsync(export, "filter-comparison.json");
            Dispatcher.UIThread.RunJobs();

            Assert.Equal(4, model.Changes.Count);
            Assert.True(exportButton.IsEnabled);

            var windowFilter = Assert.IsType<ComboBox>(window.FindControl<ComboBox>("WindowFilterBox"));
            var buttonFilter = Assert.IsType<ComboBox>(window.FindControl<ComboBox>("ButtonFilterBox"));
            var sourceFilter = Assert.IsType<ComboBox>(window.FindControl<ComboBox>("SourceFilterBox"));
            windowFilter.SelectedIndex = 2; // 10 ms
            buttonFilter.SelectedIndex = 2; // DPI Down
            sourceFilter.SelectedIndex = 1; // Factory report 04
            Dispatcher.UIThread.RunJobs();
            Assert.Single(model.Changes);
            Assert.True(exportButton.IsEnabled);

            string outputPath = Path.Combine(directory.FullName, "changed-reports.csv");
            await File.WriteAllTextAsync(outputPath, new string('x', 1000));
            await NagaDpiWindowComparisonView.SaveCsvAsync(outputPath, model.CreateCsv());

            const string expected = "Frame,Time,Input,Raw report,Button,Window,Recorded,Candidate\r\n" +
                "7,140.000 ms,Factory report 04,04210000000000000000000000000000,DPI Down,10 ms,No press,Press\r\n";
            Assert.Equal(Encoding.UTF8.GetBytes(expected), await File.ReadAllBytesAsync(outputPath));
            Assert.Equal(outputPath, Assert.Single(Directory.GetFiles(directory.FullName)));

            sourceFilter.SelectedIndex = 2; // Mouse Back / Forward
            Dispatcher.UIThread.RunJobs();
            Assert.Empty(model.Changes);
            Assert.False(exportButton.IsEnabled);
        }
        finally
        {
            window.Close();
            directory.Delete(recursive: true);
        }
        return true;
    }, CancellationToken.None);

    [Fact]
    public Task Csv_follows_the_sorted_order_of_the_filtered_rows() => HeadlessApp.Session.Dispatch<bool>(async () =>
    {
        var model = new NagaDpiWindowComparisonViewModel();
        var window = new NagaDpiWindowComparisonView { DataContext = model };
        window.Show();
        try
        {
            using var export = EmulatedExport([5, 10, 30], includeDown: true, repetitions: 3, reverseDown: true);
            await model.LoadAsync(export, "sort-comparison.json");
            Dispatcher.UIThread.RunJobs();

            var changes = Assert.IsType<ListBox>(window.FindControl<ListBox>("ChangesList"));
            var frame = Assert.IsType<Button>(window.FindControl<Button>("SortFrame"));
            Click(frame);
            Click(frame);

            window.FindControl<ComboBox>("WindowFilterBox")!.SelectedIndex = 2;
            window.FindControl<ComboBox>("ButtonFilterBox")!.SelectedIndex = 1;
            window.FindControl<ComboBox>("SourceFilterBox")!.SelectedIndex = 2;
            Dispatcher.UIThread.RunJobs();

            Assert.Equal(new[] { 11L, 7L, 3L }, changes.Items.Cast<NagaDpiWindowChangeRow>().Select(row => row.FrameNumber));
            Assert.Equal("Frame,Time,Input,Raw report,Button,Window,Recorded,Candidate\r\n" +
                "11,240.000 ms,Mouse Back / Forward,10000000,DPI Up,10 ms,No press,Press\r\n" +
                "7,140.000 ms,Mouse Back / Forward,10000000,DPI Up,10 ms,No press,Press\r\n" +
                "3,40.000 ms,Mouse Back / Forward,10000000,DPI Up,10 ms,No press,Press\r\n", model.CreateCsv());

            using var replacement = EmulatedExport([17, 30], includeDown: true);
            await model.LoadAsync(replacement, "replacement.json");
            Dispatcher.UIThread.RunJobs();
            Assert.Equal(new[] { 7L, 3L }, changes.Items.Cast<NagaDpiWindowChangeRow>().Select(row => row.FrameNumber));
            Assert.Equal("Frame ↓", frame.Content);
        }
        finally
        {
            window.Close();
        }
        return true;
    }, CancellationToken.None);

    [Fact]
    public Task Search_combines_with_filters_and_sorting_for_display_and_csv() => HeadlessApp.Session.Dispatch<bool>(async () =>
    {
        var model = new NagaDpiWindowComparisonViewModel();
        var window = new NagaDpiWindowComparisonView { DataContext = model };
        window.Show();
        try
        {
            using var export = EmulatedExport([5, 10, 30], includeDown: true, repetitions: 3, reverseDown: true, upMouse: "10AB0000");
            await model.LoadAsync(export, "search-comparison.json");
            Dispatcher.UIThread.RunJobs();

            var search = Assert.IsType<TextBox>(window.FindControl<TextBox>("ReportSearchBox"));
            var changes = Assert.IsType<ListBox>(window.FindControl<ListBox>("ChangesList"));
            var csvButton = Assert.IsType<Button>(window.FindControl<Button>("ExportCsvButton"));
            var counts = model.Counts.ToArray();
            long[] Frames() => changes.Items.Cast<NagaDpiWindowChangeRow>().Select(row => row.FrameNumber).ToArray();
            void Search(string query)
            {
                search.Text = query;
                Dispatcher.UIThread.RunJobs();
            }

            Search(" 11 ");
            Assert.Equal(new[] { 11L, 11L }, Frames());
            Search("10000");
            Assert.Equal(new[] { 15L, 15L }, Frames());

            var frame = window.FindControl<Button>("SortFrame")!;
            Click(frame);
            Click(frame);
            window.FindControl<ComboBox>("WindowFilterBox")!.SelectedIndex = 2;
            window.FindControl<ComboBox>("ButtonFilterBox")!.SelectedIndex = 1;
            var source = window.FindControl<ComboBox>("SourceFilterBox")!;
            source.SelectedIndex = 2;

            Search(" 0xaB0 ");
            Assert.Equal(new[] { 11L, 7L, 3L }, Frames());
            Assert.True(csvButton.IsEnabled);
            Assert.Equal("Frame,Time,Input,Raw report,Button,Window,Recorded,Candidate\r\n" +
                "11,240.000 ms,Mouse Back / Forward,10AB0000,DPI Up,10 ms,No press,Press\r\n" +
                "7,140.000 ms,Mouse Back / Forward,10AB0000,DPI Up,10 ms,No press,Press\r\n" +
                "3,40.000 ms,Mouse Back / Forward,10AB0000,DPI Up,10 ms,No press,Press\r\n", model.CreateCsv());

            source.SelectedIndex = 1;
            Dispatcher.UIThread.RunJobs();
            Assert.Empty(Frames());
            Assert.False(csvButton.IsEnabled);
            Assert.Contains("No exported reports match", model.DetailStatus);

            source.SelectedIndex = 2;
            Search("0x");
            Assert.Empty(Frames());
            Search("   ");
            Assert.Equal(new[] { 11L, 7L, 3L }, Frames());
            Assert.Equal(counts, model.Counts);

            Search("11");
            Assert.Equal(new[] { 11L }, Frames());
            Assert.Equal("Frame,Time,Input,Raw report,Button,Window,Recorded,Candidate\r\n" +
                "11,240.000 ms,Mouse Back / Forward,10AB0000,DPI Up,10 ms,No press,Press\r\n", model.CreateCsv());
            using var replacement = EmulatedExport([17, 30], includeDown: true);
            await model.LoadAsync(replacement, "replacement.json");
            Dispatcher.UIThread.RunJobs();
            Assert.Equal("", search.Text);
            Assert.Equal(new[] { 7L, 3L }, Frames());
        }
        finally
        {
            window.Close();
        }
        return true;
    }, CancellationToken.None);

    [Fact]
    public Task Selected_report_shows_and_copies_full_hex_and_both_classifications() => HeadlessApp.Session.Dispatch<bool>(async () =>
    {
        var model = new NagaDpiWindowComparisonViewModel();
        var window = new NagaDpiWindowComparisonView { DataContext = model };
        window.Show();
        try
        {
            using var export = EmulatedExport([10, 100], includeDown: true, reverseDown: true, lateDown: true);
            await model.LoadAsync(export, "details-comparison.json");
            Dispatcher.UIThread.RunJobs();

            var list = Assert.IsType<ListBox>(window.FindControl<ListBox>("ChangesList"));
            var details = Assert.IsType<Border>(window.FindControl<Border>("ReportDetails"));
            var hex = Assert.IsType<TextBlock>(window.FindControl<TextBlock>("SelectedReportHex"));
            var recorded = Assert.IsType<TextBlock>(window.FindControl<TextBlock>("SelectedRecordedPress"));
            var candidate = Assert.IsType<TextBlock>(window.FindControl<TextBlock>("SelectedCandidatePress"));
            var copy = Assert.IsType<Button>(window.FindControl<Button>("CopyHexButton"));
            var clipboard = window.Clipboard;
            Assert.NotNull(clipboard);
            const string clipboardSentinel = "Existing clipboard text";
            await clipboard.SetTextAsync(clipboardSentinel);
            Assert.False(details.IsEffectivelyVisible);
            Assert.False(copy.IsEnabled);

            list.SelectedIndex = 0;
            Dispatcher.UIThread.RunJobs();
            Assert.True(details.IsEffectivelyVisible);
            Assert.Equal(("10000000", "No press", "Press"), (hex.Text, recorded.Text, candidate.Text));

            list.SelectedIndex = 1;
            Dispatcher.UIThread.RunJobs();
            const string fullHex = "04210000000000000000000000000000";
            Assert.Equal((fullHex, "Press", "No press"), (hex.Text, recorded.Text, candidate.Text));
            Assert.True(copy.IsEnabled);
            Assert.Equal(clipboardSentinel, await clipboard.TryGetTextAsync());

            var frame = window.FindControl<Button>("SortFrame")!;
            Click(frame);
            Click(frame);
            var search = window.FindControl<TextBox>("ReportSearchBox")!;
            search.Text = "0x0421";
            Dispatcher.UIThread.RunJobs();
            Assert.Equal(7L, Assert.IsType<NagaDpiWindowChangeRow>(list.SelectedItem).FrameNumber);
            Assert.Equal(fullHex, hex.Text);
            Assert.Equal(clipboardSentinel, await clipboard.TryGetTextAsync());
            Click(copy);
            Assert.Equal(fullHex, await clipboard.TryGetTextAsync());

            search.Text = "3";
            Dispatcher.UIThread.RunJobs();
            Assert.Single(list.Items);
            Assert.Null(list.SelectedItem);
            Assert.False(details.IsEffectivelyVisible);
            Assert.False(copy.IsEnabled);
            Click(copy);
            Assert.Equal(fullHex, await clipboard.TryGetTextAsync());

            search.Text = "";
            Dispatcher.UIThread.RunJobs();
            Assert.Null(list.SelectedItem);
            list.SelectedIndex = 0;
            Dispatcher.UIThread.RunJobs();
            Assert.True(details.IsEffectivelyVisible);
            using var replacement = EmulatedExport([10, 100], includeDown: true, reverseDown: true, lateDown: true);
            await model.LoadAsync(replacement, "replacement.json");
            Dispatcher.UIThread.RunJobs();
            Assert.Null(list.SelectedItem);
            Assert.False(details.IsEffectivelyVisible);
            Assert.False(copy.IsEnabled);
            Click(copy);
            Assert.Equal(fullHex, await clipboard.TryGetTextAsync());
        }
        finally
        {
            window.Close();
        }
        return true;
    }, CancellationToken.None);

    [Theory]
    [InlineData("Frame", "3:10,3:5,7:100", "7:100,3:10,3:5")]
    [InlineData("Time", "3:10,3:5,7:100", "7:100,3:10,3:5")]
    [InlineData("Input", "7:100,3:10,3:5", "3:10,3:5,7:100")]
    [InlineData("Report", "7:100,3:10,3:5", "3:10,3:5,7:100")]
    [InlineData("Button", "7:100,3:10,3:5", "3:10,3:5,7:100")]
    [InlineData("Window", "3:5,3:10,7:100", "7:100,3:10,3:5")]
    [InlineData("Recorded", "3:10,3:5,7:100", "7:100,3:10,3:5")]
    [InlineData("Candidate", "7:100,3:10,3:5", "3:10,3:5,7:100")]
    public Task Each_changed_report_header_sorts_in_both_directions(string column, string ascending, string descending) =>
        HeadlessApp.Session.Dispatch<bool>(async () =>
    {
        var model = new NagaDpiWindowComparisonViewModel();
        var window = new NagaDpiWindowComparisonView { DataContext = model };
        window.Show();
        try
        {
            using var export = EmulatedExport([100, 10, 5], includeDown: true, reverseDown: true, lateDown: true);
            await model.LoadAsync(export, "sort-directions.json");
            Dispatcher.UIThread.RunJobs();

            var header = Assert.IsType<Button>(window.FindControl<Button>("Sort" + column));
            string label = Assert.IsType<string>(header.Content);
            var changes = Assert.IsType<ListBox>(window.FindControl<ListBox>("ChangesList"));
            Click(header);
            Assert.Equal(ascending, DisplayOrder());
            Assert.Equal(label + " ↑", header.Content);

            Click(header);
            Assert.Equal(descending, DisplayOrder());
            Assert.Equal(label + " ↓", header.Content);

            var other = window.FindControl<Button>(column == "Frame" ? "SortWindow" : "SortFrame")!;
            Click(other);
            Assert.Equal(label, header.Content);
            Assert.EndsWith(" ↑", Assert.IsType<string>(other.Content));

            string DisplayOrder() => string.Join(',', changes.Items.Cast<NagaDpiWindowChangeRow>()
                .Select(row => $"{row.FrameNumber}:{row.Window[..^3]}"));
        }
        finally
        {
            window.Close();
        }
        return true;
    }, CancellationToken.None);

    static void Click(Button button)
    {
        button.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
        Dispatcher.UIThread.RunJobs();
    }

    [Fact]
    public Task A_bad_export_preserves_the_last_comparison_and_shows_an_error() => HeadlessApp.Session.Dispatch<bool>(async () =>
    {
        var model = new NagaDpiWindowComparisonViewModel();
        using var export = EmulatedExport([30]);
        await model.LoadAsync(export, "valid.json");

        Assert.True(model.HasResults);
        Assert.Equal("No changed reports.", model.DetailStatus);
        Assert.Empty(model.Changes);

        using var invalid = new MemoryStream("{\"version\":1,"u8.ToArray());
        await model.LoadAsync(invalid, "broken.json");

        Assert.True(model.HasResults);
        Assert.Equal("valid.json", model.FileName);
        Assert.Contains("broken.json", model.Error);
        Assert.Equal("No changed reports.", model.DetailStatus);
        Assert.Equal(2, model.Counts.Count);
        return true;
    }, CancellationToken.None);

    [Fact]
    public Task Shows_the_full_changed_report_count_when_the_export_is_truncated() => HeadlessApp.Session.Dispatch<bool>(async () =>
    {
        var model = new NagaDpiWindowComparisonViewModel();
        using var export = EmulatedExport([10], repetitions: 1001);
        await model.LoadAsync(export, "truncated.json");

        Assert.True(model.HasResults);
        Assert.Equal(1000, model.Changes.Count);
        Assert.Equal("Showing 1000 of 1001 changed reports.", model.DetailStatus);
        return true;
    }, CancellationToken.None);

    [Fact]
    public Task Rejects_an_oversized_export_before_displaying_any_rows() => HeadlessApp.Session.Dispatch<bool>(async () =>
    {
        var model = new NagaDpiWindowComparisonViewModel();
        using var export = new MemoryStream(new byte[NagaDpiWindowExportCodec.MaxJsonBytes + 1]);

        await model.LoadAsync(export, "oversized.json");

        Assert.False(model.HasResults);
        Assert.Contains("1 MiB", model.Error);
        return true;
    }, CancellationToken.None);

    static MemoryStream EmulatedExport(int[] windows, bool includeDown = false, int repetitions = 1, bool reverseDown = false,
        bool lateDown = false, string upMouse = "10000000")
    {
        using var capture = new StringWriter();
        var initial = new NagaDpiCapture.Sample(0, new(800, 900));
        NagaDpiCapture.WriteHeader(capture, initial, mouseReportLength: 4, both: true, mirrorWindowMs: 30);
        for (int i = 0; i < repetitions; i++)
            Press(i * 1_000_000L, "04200000000000000000000000000000", upMouse, false);
        if (includeDown)
            Press(repetitions * 1_000_000L, "04210000000000000000000000000000", "08000000", reverseDown, lateDown);

        using var input = new StringReader(capture.ToString());
        using var output = new StringWriter();
        using var errors = new StringWriter();
        Assert.True(NagaDpiWindowComparison.Compare(input, windows, output, errors, json: true) == 0, errors.ToString());
        Assert.Empty(errors.ToString());
        return new MemoryStream(Encoding.UTF8.GetBytes(output.ToString()));

        void Press(long offset, string factory, string mouse, bool mouseFirst, bool lateMirror = false)
        {
            const string factoryRelease = "04000000000000000000000000000000", mouseRelease = "00000000";
            string firstSource = mouseFirst ? NagaDpiCapture.MouseSource : NagaDpiCapture.FactorySource;
            string secondSource = mouseFirst ? NagaDpiCapture.FactorySource : NagaDpiCapture.MouseSource;
            NagaDpiCapture.WriteFrame(capture, new(offset + 100_000, mouseFirst ? mouse : factory, initial,
                [new(offset + 110_000, initial.Dpi)], firstSource));
            NagaDpiCapture.WriteFrame(capture, new(offset + 200_000, mouseFirst ? mouseRelease : factoryRelease, initial,
                [], firstSource));
            long mirror = offset + (lateMirror ? 600_000L : 400_000L);
            // A late report falls outside the recorded window and is replayed as another press.
            NagaDpiCapture.WriteFrame(capture, new(mirror, mouseFirst ? factory : mouse, initial,
                lateMirror ? [new(mirror + 10_000, initial.Dpi)] : [], secondSource));
            NagaDpiCapture.WriteFrame(capture, new(mirror + 100_000L, mouseFirst ? factoryRelease : mouseRelease, initial,
                [], secondSource));
        }
    }
}
