using System.Text;
using Avalonia.Automation;
using Avalonia.Controls;
using Avalonia.Input.Platform;
using Avalonia.Interactivity;
using Avalonia.Platform.Storage;
using ViperSynapse.App.ViewModels;

namespace ViperSynapse.App.Views;

public sealed partial class NagaDpiWindowComparisonView : Window
{
    static readonly FilePickerFileType JsonFileType = new("Comparison JSON")
    {
        Patterns = ["*.json"],
        MimeTypes = ["application/json"]
    };
    static readonly FilePickerFileType CsvFileType = new("Changed reports CSV")
    {
        Patterns = ["*.csv"],
        MimeTypes = ["text/csv"]
    };
    static readonly FilePickerFileType CaptureFileType = new("Combined capture JSONL")
    {
        Patterns = ["*.jsonl"],
        MimeTypes = ["application/jsonl", "application/x-ndjson"],
    };

    bool _choosing;
    (Button Button, string Label)? _activeSortHeader;

    public NagaDpiWindowComparisonView()
    {
        InitializeComponent();
        DataContext = new NagaDpiWindowComparisonViewModel();
        Closed += (_, _) => (DataContext as NagaDpiWindowComparisonViewModel)?.CompareCaptureCommand.Cancel();
    }

    void SortReports(object? sender, RoutedEventArgs e)
    {
        if (sender is not Button { CommandParameter: NagaDpiSortColumn column, Content: string label } button ||
            DataContext is not NagaDpiWindowComparisonViewModel { IsLoading: false } model)
            return;

        if (_activeSortHeader is { } active)
        {
            active.Button.Content = active.Label;
            AutomationProperties.SetName(active.Button, active.Label);
            if (active.Button == button)
                label = active.Label;
        }

        model.SortBy(column);
        _activeSortHeader = (button, label);
        button.Content = label + (model.SortDescending ? " ↓" : " ↑");
        AutomationProperties.SetName(button, label + (model.SortDescending ? ", sorted descending" : ", sorted ascending"));
    }

    async void OpenFile(object? sender, RoutedEventArgs e) => await PickFileAsync(isCapture: false);

    async void OpenCapture(object? sender, RoutedEventArgs e) => await PickFileAsync(isCapture: true);

    async Task PickFileAsync(bool isCapture)
    {
        if (_choosing || DataContext is not NagaDpiWindowComparisonViewModel { IsLoading: false } model)
            return;

        _choosing = true;
        string name = isCapture ? "the capture" : "the comparison";
        try
        {
            var files = await StorageProvider.OpenFilePickerAsync(new FilePickerOpenOptions
            {
                Title = isCapture ? "Open Naga Chroma combined capture" : "Open Naga Chroma DPI comparison",
                AllowMultiple = false,
                FileTypeFilter = [isCapture ? CaptureFileType : JsonFileType],
            });
            using var file = files.FirstOrDefault();
            if (file == null)
                return;

            name = file.Name;
            if (isCapture)
            {
                model.SelectCapture(file.TryGetLocalPath() ?? throw new IOException("Choose a local JSONL capture."));
                return;
            }
            await using var input = await file.OpenReadAsync();
            await model.LoadAsync(input, name);
        }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException or InvalidOperationException)
        {
            model.ReportOpenError(name, error);
        }
        finally
        {
            _choosing = false;
        }
    }

    async void CopyHex(object? sender, RoutedEventArgs e)
    {
        if (DataContext is not NagaDpiWindowComparisonViewModel { IsLoading: false, SelectedReport: { } report } model)
            return;

        if (Clipboard is not { } clipboard)
        {
            model.Error = "The clipboard is unavailable.";
            return;
        }

        try
        {
            await clipboard.SetTextAsync(report.Report);
        }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException or InvalidOperationException or NotSupportedException)
        {
            model.Error = $"Could not copy report hex. {error.Message}";
        }
    }

    async void ExportCsv(object? sender, RoutedEventArgs e)
    {
        if (_choosing || DataContext is not NagaDpiWindowComparisonViewModel { CanExportCsv: true } model)
            return;

        _choosing = true;
        string name = "the changed reports";
        try
        {
            string csv = model.CreateCsv();
            using var file = await StorageProvider.SaveFilePickerAsync(new FilePickerSaveOptions
            {
                Title = "Export changed reports",
                SuggestedFileName = "naga-dpi-changed-reports.csv",
                DefaultExtension = "csv",
                FileTypeChoices = [CsvFileType],
                ShowOverwritePrompt = true,
            });
            if (file == null)
                return;

            name = file.Name;
            string path = file.TryGetLocalPath() ?? throw new IOException("Choose a local file for the CSV export.");
            await SaveCsvAsync(path, csv);
            model.Error = "";
        }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException or InvalidOperationException)
        {
            model.ReportExportError(name, error);
        }
        finally
        {
            _choosing = false;
        }
    }

    internal static async Task SaveCsvAsync(string path, string csv)
    {
        path = Path.GetFullPath(path);
        string temporary = Path.Combine(Path.GetDirectoryName(path)!, $".viper-csv-{Guid.NewGuid():N}.tmp");
        bool created = false;
        try
        {
            await using (var output = new FileStream(temporary, FileMode.CreateNew, FileAccess.Write, FileShare.None, useAsync: true, bufferSize: 4096))
            {
                created = true;
                await output.WriteAsync(Encoding.UTF8.GetBytes(csv));
            }
            File.Move(temporary, path, overwrite: true);
            created = false;
        }
        finally
        {
            if (created)
                File.Delete(temporary);
        }
    }
}
