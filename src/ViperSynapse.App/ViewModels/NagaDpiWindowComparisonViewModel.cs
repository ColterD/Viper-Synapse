using System.Globalization;
using System.Text;
using System.Text.Json;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using ViperSynapse.Core.Protocol;
using ViperSynapse.Hardware;

namespace ViperSynapse.App.ViewModels;

internal sealed record NagaDpiWindowCountRow(string Window, long DpiUp, long DpiDown);

internal sealed record NagaDpiWindowChangeRow(long FrameNumber, string Time, string Source, string Report, string Button,
    string Window, string Recorded, string Candidate);

internal enum NagaDpiSortColumn { Frame, Time, Input, Report, Button, Window, Recorded, Candidate }

internal sealed record NagaDpiFilterOption<T>(T Value, string Label)
{
    public override string ToString() => Label;
}

internal sealed partial class NagaDpiWindowComparisonViewModel : ObservableObject
{
    const int MaxCandidateWindows = 16;
    const int MaxCaptureFrames = 100_000;
    static readonly NagaDpiFilterOption<int?> AllWindows = new(null, "All windows");
    NagaDpiWindowExport? _export;
    string? _capturePath;
    NagaDpiSortColumn? _sortColumn;
    internal bool SortDescending { get; private set; }

    public static IReadOnlyList<NagaDpiFilterOption<string?>> ButtonOptions { get; } =
        [new(null, "All buttons"), new("dpi-up", ButtonName("dpi-up")), new("dpi-down", ButtonName("dpi-down"))];

    public static IReadOnlyList<NagaDpiFilterOption<string?>> SourceOptions { get; } =
        [new(null, "All sources"), new("factory-report-04", SourceName("factory-report-04")), new("mouse-back-forward", SourceName("mouse-back-forward"))];

    [ObservableProperty] public partial IReadOnlyList<NagaDpiFilterOption<int?>> WindowOptions { get; set; } = [AllWindows];
    [ObservableProperty] public partial NagaDpiFilterOption<int?>? WindowFilter { get; set; } = AllWindows;
    [ObservableProperty] public partial NagaDpiFilterOption<string?>? ButtonFilter { get; set; } = ButtonOptions[0];
    [ObservableProperty] public partial NagaDpiFilterOption<string?>? SourceFilter { get; set; } = SourceOptions[0];
    [ObservableProperty] public partial string? SearchQuery { get; set; } = "";

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasResults))]
    public partial IReadOnlyList<NagaDpiWindowCountRow> Counts { get; set; } = [];

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasChanges))]
    [NotifyPropertyChangedFor(nameof(CanExportCsv))]
    public partial IReadOnlyList<NagaDpiWindowChangeRow> Changes { get; set; } = [];

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasSelectedReport))]
    [NotifyPropertyChangedFor(nameof(CanCopyHex))]
    public partial NagaDpiWindowChangeRow? SelectedReport { get; set; }

    [ObservableProperty] public partial string FileName { get; set; } = "";
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasCapture))]
    [NotifyCanExecuteChangedFor(nameof(CompareCaptureCommand))]
    public partial string CaptureName { get; set; } = "";
    [ObservableProperty] public partial string? CandidateWindowsText { get; set; } = "0, 15, 30, 60";
    [ObservableProperty] public partial string Error { get; set; } = "";
    [ObservableProperty] public partial string DetailStatus { get; set; } = "";
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(CanExportCsv))]
    [NotifyPropertyChangedFor(nameof(CanCopyHex))]
    [NotifyCanExecuteChangedFor(nameof(CompareCaptureCommand))]
    public partial bool IsLoading { get; set; }

    public bool HasResults => Counts.Count != 0;
    public bool HasChanges => Changes.Count != 0;
    public bool CanExportCsv => HasChanges && !IsLoading;
    public bool HasSelectedReport => SelectedReport != null;
    public bool CanCopyHex => HasSelectedReport && !IsLoading;
    public bool HasCapture => CaptureName.Length > 0;
    bool CanCompareCapture => HasCapture && !IsLoading;

    partial void OnWindowFilterChanged(NagaDpiFilterOption<int?>? value) => FilterChanged();
    partial void OnButtonFilterChanged(NagaDpiFilterOption<string?>? value) => FilterChanged();
    partial void OnSourceFilterChanged(NagaDpiFilterOption<string?>? value) => FilterChanged();
    partial void OnSearchQueryChanged(string? value) => FilterChanged();

    internal async Task LoadAsync(Stream input, string name)
    {
        if (IsLoading)
            return;
        IsLoading = true;
        Error = "";
        try
        {
            var export = await NagaDpiWindowExportCodec.ReadAsync(input);
            ShowExport(export, name);
            _capturePath = null;
            CaptureName = "";
        }
        catch (Exception error) when (error is IOException or InvalidDataException or JsonException or UnauthorizedAccessException)
        {
            ReportOpenError(name, error);
        }
        finally
        {
            IsLoading = false;
        }
    }

    internal void SelectCapture(string path)
    {
        if (IsLoading)
            return;
        _capturePath = path;
        CaptureName = Path.GetFileName(path);
        Error = "";
    }

    [RelayCommand(CanExecute = nameof(CanCompareCapture))]
    async Task CompareCaptureAsync(CancellationToken cancellationToken)
    {
        if (IsLoading || _capturePath is not { } path)
            return;

        Error = "";
        var windows = new List<int>();
        foreach (var value in (CandidateWindowsText ?? "").Split([',', ' ', '\t'], StringSplitOptions.RemoveEmptyEntries))
        {
            if (!int.TryParse(value, NumberStyles.None, CultureInfo.InvariantCulture, out int window) ||
                !NagaDpiReportDecoder.ValidMirrorWindow(window))
            {
                Error = $"Enter whole numbers from 0 to {NagaDpiWindowExportCodec.MaxWindowMs} ms, separated by commas or spaces.";
                return;
            }
            if (!windows.Contains(window))
                windows.Add(window);
        }
        if (windows.Count is 0 or > MaxCandidateWindows)
        {
            Error = $"Choose between 1 and {MaxCandidateWindows} distinct candidate windows.";
            return;
        }

        string name = CaptureName;
        IsLoading = true;
        try
        {
            var export = await Task.Run(() =>
            {
                using var capture = File.OpenText(path);
                using var errors = new StringWriter(CultureInfo.InvariantCulture);
                return NagaDpiWindowAnalysis.Analyze(capture, windows, errors, maxFrames: MaxCaptureFrames, cancellationToken: cancellationToken)
                    ?? throw new InvalidDataException(errors.ToString().TrimEnd());
            }, cancellationToken);
            if (!cancellationToken.IsCancellationRequested)
                ShowExport(export, name);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested) { }
        catch (Exception error) when (error is IOException or InvalidDataException or UnauthorizedAccessException)
        {
            ReportOpenError(name, error);
        }
        finally
        {
            IsLoading = false;
        }
    }

    void ShowExport(NagaDpiWindowExport export, string name)
    {
        var recorded = export.Recorded;
        Counts = [
            new($"Recorded ({FormatWindow(recorded.WindowMs)})", recorded.DpiUp, recorded.DpiDown),
            .. export.Windows.Select(window => new NagaDpiWindowCountRow(FormatWindow(window.WindowMs), window.DpiUp, window.DpiDown)),
        ];
        _export = export;
        SelectedReport = null;
        WindowFilter = null;
        WindowOptions = [AllWindows, .. export.Windows.Select(window => new NagaDpiFilterOption<int?>(window.WindowMs, FormatWindow(window.WindowMs)))];
        WindowFilter = AllWindows;
        ButtonFilter = ButtonOptions[0];
        SourceFilter = SourceOptions[0];
        SearchQuery = "";
        ApplyFilters();
        FileName = name;
    }

    internal void ReportOpenError(string name, Exception error) => Error = $"Could not open {name}. {error.Message}";

    internal void ReportExportError(string name, Exception error) => Error = $"Could not save {name}. {error.Message}";

    internal void SortBy(NagaDpiSortColumn column)
    {
        SortDescending = _sortColumn == column && !SortDescending;
        _sortColumn = column;
        ApplyFilters();
    }

    internal string CreateCsv()
    {
        var csv = new StringBuilder("Frame,Time,Input,Raw report,Button,Window,Recorded,Candidate\r\n");
        foreach (var row in Changes)
        {
            string[] fields = [row.FrameNumber.ToString(CultureInfo.InvariantCulture), row.Time, row.Source, row.Report,
                row.Button, row.Window, row.Recorded, row.Candidate];
            csv.AppendJoin(',', fields.Select(CsvField)).Append("\r\n");
        }
        return csv.ToString();
    }

    static string CsvField(string field) => field.AsSpan().IndexOfAny(",\"\r\n") < 0
        ? field : '"' + field.Replace("\"", "\"\"", StringComparison.Ordinal) + '"';

    void FilterChanged()
    {
        if (!IsLoading)
            ApplyFilters();
    }

    void ApplyFilters()
    {
        if (_export is not { } export)
            return;

        int? window = WindowFilter?.Value;
        string? button = ButtonFilter?.Value;
        string? source = SourceFilter?.Value;
        string query = SearchQuery?.Trim() ?? "";
        bool searching = query.Length != 0;
        string hex = query.StartsWith("0x", StringComparison.OrdinalIgnoreCase) ? query[2..] : query;
        long? frame = long.TryParse(query, NumberStyles.None, CultureInfo.InvariantCulture, out long number) ? number : null;
        var matching = export.ChangedReports.Where(report =>
            (window == null || report.WindowMs == window) &&
            (button == null || report.Button == button) &&
            (source == null || report.Source == source) &&
            (!searching || report.FrameNumber == frame ||
                (hex.Length != 0 && report.Report.Contains(hex, StringComparison.OrdinalIgnoreCase))));
        var selected = SelectedReport;
        SelectedReport = null;
        Changes = SortReports(matching).Select(FormatReport).ToArray();
        if (selected != null)
            SelectedReport = Changes.FirstOrDefault(row => row == selected);

        bool filtered = window != null || button != null || source != null;
        DetailStatus = export.ChangedReportCount switch
        {
            0 => "No changed reports.",
            _ when filtered || searching => FilterStatus(export, searching),
            _ when export.DetailsTruncated =>
                FormattableString.Invariant($"Showing {Changes.Count} of {export.ChangedReportCount} changed reports."),
            1 => "1 changed report.",
            _ => FormattableString.Invariant($"{export.ChangedReportCount} changed reports."),
        };
    }

    IEnumerable<NagaDpiChangedReport> SortReports(IEnumerable<NagaDpiChangedReport> reports)
    {
        return _sortColumn switch
        {
            NagaDpiSortColumn.Frame => Order(report => report.FrameNumber),
            NagaDpiSortColumn.Time => Order(report => report.Ticks),
            NagaDpiSortColumn.Input => Order(report => SourceName(report.Source), StringComparer.Ordinal),
            NagaDpiSortColumn.Report => Order(report => report.Report, StringComparer.OrdinalIgnoreCase),
            NagaDpiSortColumn.Button => Order(report => ButtonName(report.Button), StringComparer.Ordinal),
            NagaDpiSortColumn.Window => Order(report => report.WindowMs),
            NagaDpiSortColumn.Recorded => Order(report => report.RecordedPress),
            NagaDpiSortColumn.Candidate => Order(report => report.CandidatePress),
            _ => reports,
        };

        IEnumerable<NagaDpiChangedReport> Order<T>(Func<NagaDpiChangedReport, T> key, IComparer<T>? comparer = null) =>
            SortDescending ? reports.OrderByDescending(key, comparer) : reports.OrderBy(key, comparer);
    }

    string FilterStatus(NagaDpiWindowExport export, bool searching)
    {
        int available = export.ChangedReports.Length;
        string noMatches = searching ? "No exported reports match." : "No exported reports match the filters.";
        string summary = Changes.Count == 0 ? noMatches
            : FormattableString.Invariant($"Showing {Changes.Count} of {available} exported changed reports.");
        return export.DetailsTruncated
            ? summary + FormattableString.Invariant($" The export contains {available} of {export.ChangedReportCount} changed reports.")
            : summary;
    }

    static NagaDpiWindowChangeRow FormatReport(NagaDpiChangedReport report) => new(
        report.FrameNumber,
        FormattableString.Invariant($"{TimeSpan.FromTicks(report.Ticks).TotalMilliseconds:F3} ms"),
        SourceName(report.Source), report.Report, ButtonName(report.Button), FormatWindow(report.WindowMs),
        Classification(report.RecordedPress), Classification(report.CandidatePress));

    static string SourceName(string source) => source == "mouse-back-forward" ? "Mouse Back / Forward" : "Factory report 04";
    static string ButtonName(string button) => button == "dpi-up" ? "DPI Up" : "DPI Down";
    static string FormatWindow(int? ms) => ms is int window ? FormattableString.Invariant($"{window} ms") : "overlap only";

    static string Classification(bool press) => press ? "Press" : "No press";
}
