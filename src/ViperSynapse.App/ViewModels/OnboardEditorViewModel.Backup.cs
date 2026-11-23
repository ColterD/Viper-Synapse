using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using ViperSynapse.Core.Protocol;

namespace ViperSynapse.App.ViewModels;

public sealed record OnboardBackupFiles(Func<OnboardBackup, Task<bool>> Save, Func<Task<OnboardBackup?>> Open);

public sealed partial class OnboardEditorViewModel
{
    OnboardArchiveRequest? _pendingRestore;
    long _archiveRevision;

    [ObservableProperty] public partial string? RestorePreview { get; private set; }
    [ObservableProperty] public partial string ArchiveStatus { get; private set; } = "";

    public bool HasBackupActions => _backup != null && _restore != null;
    bool CanAccessActiveSlot => !IsBusy && HasSlot && SelectedSlot!.Id == ActiveSlot;
    public bool CanBackupSlot => CanAccessActiveSlot && _backup != null && _backupFiles != null;
    public bool CanOpenSlotBackup => CanAccessActiveSlot && _restore != null && _backupFiles != null;
    public bool CanConfirmSlotRestore => CanAccessActiveSlot && _restore != null && _pendingRestore is { } request &&
        IsCurrentArchiveTarget(request);

    internal void UseBackupFiles(OnboardBackupFiles picker)
    {
        if (_fixedBackupFiles)
            return;
        _backupFiles = picker;
        NotifyArchiveState();
    }

    [RelayCommand(CanExecute = nameof(CanBackupSlot))]
    Task BackupSlotAsync()
    {
        if (!CanBackupSlot || _backup is not { } capture || _backupFiles is not { } picker)
            return Task.CompletedTask;

        CancelSlotRestore();
        var request = ArchiveTarget();
        return RunArchiveAsync(request, async isCurrent =>
        {
            var snapshot = await capture(request);
            if (!isCurrent())
                return;
            OnboardBackupCodec.Validate(snapshot);
            if (snapshot.SourceSlot != request.Slot)
                throw new InvalidDataException("The backup reply came from a different slot.");
            if (await picker.Save(snapshot) && isCurrent())
                ArchiveStatus = $"Backup saved for slot {request.Slot}.";
        });
    }

    [RelayCommand(CanExecute = nameof(CanOpenSlotBackup))]
    Task OpenSlotBackupAsync()
    {
        if (!CanOpenSlotBackup || _backupFiles is not { } picker)
            return Task.CompletedTask;

        CancelSlotRestore();
        var request = ArchiveTarget();
        return RunArchiveAsync(request, async isCurrent =>
        {
            var snapshot = await picker.Open();
            if (snapshot == null || !isCurrent())
                return;
            OnboardBackupCodec.Validate(snapshot);
            _pendingRestore = request with { Backup = snapshot };
            var settings = new List<string>();
            if (snapshot.Brightness?.Any(zone => zone.Led == OnboardBackupBrightness.Logo) == true)
                settings.Add("logo brightness");
            if (snapshot.Brightness?.Any(zone => zone.Led == OnboardBackupBrightness.Scroll) == true)
                settings.Add("scroll-wheel brightness");
            if (snapshot.Brightness?.Any(zone => zone.Led == OnboardBackupBrightness.Underglow) == true)
                settings.Add("underglow brightness");
            if (snapshot.Effects?.Any(zone => zone.Led == OnboardBackupBrightness.Logo) == true)
                settings.Add("logo lighting effect");
            if (snapshot.Effects?.Any(zone => zone.Led == OnboardBackupBrightness.Scroll) == true)
                settings.Add("scroll-wheel lighting effect");
            if (snapshot.Effects?.Any(zone => zone.Led == OnboardBackupBrightness.Underglow) == true)
                settings.Add("underglow lighting effect");
            if (snapshot.Wheel?.SmartReel != null)
                settings.Add("Smart-Reel");
            if (snapshot.Wheel?.Acceleration != null)
                settings.Add("scroll acceleration");
            RestorePreview = $"Restore backup from slot {snapshot.SourceSlot} to active slot {request.Slot} on {request.Serial}? "
                + $"The backup contains {snapshot.Stages.Count} DPI stage(s) and {snapshot.Bindings.Count} button assignment(s)."
                + (settings.Count > 0 ? $" It also includes {string.Join(", ", settings)}." : "");
        });
    }

    [RelayCommand]
    void CancelSlotRestore()
    {
        _pendingRestore = null;
        RestorePreview = null;
        NotifyArchiveState();
    }

    [RelayCommand(CanExecute = nameof(CanConfirmSlotRestore))]
    Task ConfirmSlotRestoreAsync()
    {
        if (!CanConfirmSlotRestore || _pendingRestore is not { } request || _restore is not { } apply)
            return Task.CompletedTask;

        CancelSlotRestore();
        ClearBinding();
        return RunArchiveAsync(request, async isCurrent =>
        {
            OnboardRestoreResult result;
            try { result = await apply(request); }
            catch (Exception error)
            {
                if (isCurrent())
                    ArchiveStatus = $"Restore could not be verified. Check the slot before using it. {error.Message}";
                return;
            }
            if (!isCurrent())
                return;
            string summary = result.Status switch
            {
                OnboardRestoreStatus.Restored => "Restore verified.",
                OnboardRestoreStatus.RolledBack => "Restore failed. The previous settings were restored and verified.",
                OnboardRestoreStatus.RecoveryRequired => "Restore failed and rollback could not be verified. Check the slot before using it.",
                _ => throw new InvalidDataException("The daemon returned an unknown restore result. Check the slot before using it.")
            };
            ArchiveStatus = $"{summary} {result.Message}".TrimEnd();
        });
    }

    OnboardArchiveRequest ArchiveTarget() => new(SelectedTarget!.Serial, SelectedSlot!.Id);

    bool IsCurrentArchiveTarget(OnboardArchiveRequest request) => request.Serial == SelectedTarget?.Serial &&
        request.Slot == SelectedSlot?.Id && request.Slot == ActiveSlot;

    Task RunArchiveAsync(OnboardArchiveRequest request, Func<Func<bool>, Task> action)
    {
        long revision = _archiveRevision;
        bool IsCurrent() => revision == _archiveRevision && IsCurrentArchiveTarget(request);

        return RunAsync(async () =>
        {
            ArchiveStatus = "";
            try { await action(IsCurrent); }
            catch (Exception error)
            {
                if (IsCurrent())
                    ArchiveStatus = error.Message;
            }
        });
    }

    void InvalidateArchiveSelection()
    {
        _archiveRevision++;
        if (_pendingRestore != null)
            CancelSlotRestore();
    }

    void NotifyArchiveState()
    {
        BackupSlotCommand.NotifyCanExecuteChanged();
        OpenSlotBackupCommand.NotifyCanExecuteChanged();
        ConfirmSlotRestoreCommand.NotifyCanExecuteChanged();
    }
}
