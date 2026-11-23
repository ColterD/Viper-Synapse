using System.Text;
using Avalonia.Controls;
using Avalonia.Platform.Storage;
using ViperSynapse.App.ViewModels;
using ViperSynapse.Core.Protocol;

namespace ViperSynapse.App.Views;

public sealed partial class OnboardEditorView : UserControl
{
    static readonly FilePickerFileType BackupFileType = new("Onboard backup")
    {
        Patterns = ["*.json"],
        MimeTypes = ["application/json"]
    };

    public OnboardEditorView()
    {
        InitializeComponent();
        AttachedToVisualTree += (_, _) => UseFilePicker();
        DataContextChanged += (_, _) => UseFilePicker();
    }

    void UseFilePicker()
    {
        if (DataContext is OnboardEditorViewModel model)
            model.UseBackupFiles(new OnboardBackupFiles(SaveAsync, OpenAsync));
    }

    IStorageProvider Storage => TopLevel.GetTopLevel(this)?.StorageProvider
        ?? throw new InvalidOperationException("The file picker is unavailable.");

    async Task<bool> SaveAsync(OnboardBackup backup)
    {
        string json = OnboardBackupCodec.WriteBackup(backup);
        using var file = await Storage.SaveFilePickerAsync(new FilePickerSaveOptions
        {
            Title = "Save onboard backup",
            SuggestedFileName = $"viper-onboard-slot-{backup.SourceSlot}.json",
            DefaultExtension = "json",
            FileTypeChoices = [BackupFileType],
            ShowOverwritePrompt = true
        });
        if (file == null)
            return false;

        string path = file.TryGetLocalPath() ?? throw new IOException("Choose a local file for the onboard backup.");
        string temporary = Path.Combine(Path.GetDirectoryName(path)!, $".viper-backup-{Guid.NewGuid():N}.tmp");
        try
        {
            await using (var output = new FileStream(temporary, FileMode.CreateNew, FileAccess.Write, FileShare.None))
                await output.WriteAsync(Encoding.UTF8.GetBytes(json));
            File.Move(temporary, path, overwrite: true);
        }
        finally
        {
            File.Delete(temporary);
        }
        return true;
    }

    async Task<OnboardBackup?> OpenAsync()
    {
        var files = await Storage.OpenFilePickerAsync(new FilePickerOpenOptions
        {
            Title = "Open onboard backup",
            AllowMultiple = false,
            FileTypeFilter = [BackupFileType]
        });
        using var file = files.FirstOrDefault();
        if (file == null)
            return null;

        await using var input = await file.OpenReadAsync();
        byte[] bytes = new byte[OnboardBackupCodec.MaxJsonLength + 1];
        int length = await input.ReadAtLeastAsync(bytes, bytes.Length, false);
        if (length > OnboardBackupCodec.MaxJsonLength)
            throw new InvalidDataException("The onboard backup exceeds the maximum size.");
        string json = new UTF8Encoding(false, true).GetString(bytes, 0, length).TrimStart('\uFEFF');
        return OnboardBackupCodec.ReadBackup(json);
    }
}
