using System.Collections.ObjectModel;
using Avalonia.Threading;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using ViperSynapse.App.Services;
using ViperSynapse.Core.Bindings;
using ViperSynapse.Core.Protocol;

namespace ViperSynapse.App.ViewModels;

internal sealed record BindingSaveTarget(Guid ProfileId, int KeymapIndex, string KeymapName, bool Hypershift, ushort Source);

internal sealed record DeviceSaveFeedback(long Revision, bool Pending, DeviceSettings? Draft = null,
    DeviceSettings? Baseline = null, Exception? Error = null,
    string? DeviceName = null, Func<DeviceSettings, bool, DeviceSettings>? ApplyBinding = null, string? ReloadProblem = null,
    DeviceSettings? Attempted = null, BindingSaveTarget? BindingTarget = null);

internal sealed record DeviceApplyFeedback(IReadOnlyList<UnitApplyOutcome>? Units, bool Pending = false);

public sealed record DeviceSaveNotice(string Key, string Message, DeviceViewModel? Device)
{
    public bool CanOpen => Device != null;
}

public sealed partial class MainViewModel
{
    readonly Dictionary<string, DeviceSaveFeedback> _deviceSaveFeedback = [];
    readonly Dictionary<string, DeviceApplyFeedback> _deviceApplyFeedback = [];
    readonly Dictionary<string, DeviceSaveFeedback> _ordinarySaveFailures = [];
    readonly Dictionary<(string Key, long Revision), DeviceSaveFeedback> _bindingSaveFailures = [];
    readonly Dictionary<string, (long Revision, DeviceSettings Settings)> _committedDeviceSettings = [];
    readonly ObservableCollection<DeviceSaveNotice> _deviceSaveNotices = [];

    public ReadOnlyObservableCollection<DeviceSaveNotice> DeviceSaveNotices => _saveNotices ??= new(_deviceSaveNotices);
    ReadOnlyObservableCollection<DeviceSaveNotice>? _saveNotices;

    internal DeviceSaveFeedback? GetSaveFeedback(string key)
    {
        var latest = _deviceSaveFeedback.GetValueOrDefault(key);
        var ordinary = _ordinarySaveFailures.GetValueOrDefault(key);
        var binding = GetBindingFailure(key);
        if (latest is { Pending: true })
            return latest;
        if (ordinary is { Error: SettingsConflictException })
            return ordinary;
        if (binding is { Error: SettingsConflictException })
            return binding;
        return ordinary ?? binding;
    }

    internal DeviceSaveFeedback? GetBindingFailure(string key) => _bindingSaveFailures
        .Where(entry => entry.Key.Key == key).Select(entry => entry.Value)
        .OrderByDescending(failure => failure.Error is SettingsConflictException)
        .ThenByDescending(failure => failure.Revision).FirstOrDefault();

    internal bool HasSettingsConflict(string key) => _ordinarySaveFailures.GetValueOrDefault(key)?.Error is SettingsConflictException ||
        _bindingSaveFailures.Any(entry => entry.Key.Key == key && entry.Value.Error is SettingsConflictException);

    internal bool HasUnsavedProfileSettings(string key) => _ordinarySaveFailures.ContainsKey(key) || HasSettingsConflict(key);

    internal DeviceApplyFeedback? GetApplyFeedback(string key) => _deviceApplyFeedback.GetValueOrDefault(key);

    void SetSaveFeedback(string key, DeviceSaveFeedback feedback)
    {
        _deviceSaveFeedback[key] = feedback;
        NotifyDeviceSaveFeedback(key);
    }

    void NotifyDeviceSaveFeedback(string key)
    {
        foreach (var device in Devices.Where(device => device.Key == key))
            device.NotifySaveFeedback();
        RefreshDeviceSaveNotice(key);
    }

    void RefreshDeviceSaveNotices()
    {
        foreach (var key in _deviceSaveFeedback.Keys.Concat(_ordinarySaveFailures.Keys)
            .Concat(_bindingSaveFailures.Keys.Select(item => item.Key)).Distinct())
            RefreshDeviceSaveNotice(key);
    }

    void RefreshDeviceSaveNotice(string key)
    {
        var old = _deviceSaveNotices.FirstOrDefault(notice => notice.Key == key);
        var feedback = GetSaveFeedback(key);
        var failure = feedback?.Error != null ? feedback : _ordinarySaveFailures.GetValueOrDefault(key) ?? GetBindingFailure(key);
        if (failure == null || SelectedTab is DeviceViewModel selected && selected.Key == key)
        {
            if (old != null)
                _deviceSaveNotices.Remove(old);
            return;
        }

        var device = Devices.FirstOrDefault(device => device.Key == key);
        string name = device?.ShortName ?? failure.DeviceName ?? key;
        string message = failure.Pending ? $"{name} is saving again…" : $"Couldn't save the software profile for {name}.";
        if (device == null && !failure.Pending)
            message += " Reconnect it to recover your edits.";
        var notice = new DeviceSaveNotice(key, message, device);
        if (old == null)
            _deviceSaveNotices.Add(notice);
        else if (old != notice)
            _deviceSaveNotices[_deviceSaveNotices.IndexOf(old)] = notice;
    }

    [RelayCommand]
    void OpenDeviceSave(DeviceViewModel? device)
    {
        if (device == null || !Devices.Contains(device))
            return;
        device.ShowBindingSaveCommand.Execute(null);
        Show(device);
    }

    void CompleteSave(string key, DeviceSaveFeedback request, IReadOnlyList<UnitApplyOutcome>? units, Exception? error,
        long retryBindingRevision, DeviceSettings? baseline, DeviceSettings attempted)
    {
        var failed = request with { Pending = false, Error = error, Attempted = attempted };
        if (request.ApplyBinding != null)
        {
            _bindingSaveFailures.Remove((key, retryBindingRevision));
            if (error != null)
                _bindingSaveFailures[(key, request.Revision)] = failed;
            else if (request.BindingTarget is { } target)
                foreach (var older in _bindingSaveFailures.Where(entry => entry.Key.Key == key &&
                    entry.Key.Revision < request.Revision && entry.Value.BindingTarget == target).Select(entry => entry.Key).ToArray())
                    _bindingSaveFailures.Remove(older);
        }
        else if (error != null)
        {
            _ordinarySaveFailures[key] = failed;
        }
        if (error == null)
        {
            _committedDeviceSettings[key] = (request.Revision, attempted);
            _deviceApplyFeedback[key] = new(units);
            foreach (var device in Devices.Where(device => device.Key == key))
                device.InitializeSavedSettings(attempted);
            if (_ordinarySaveFailures.TryGetValue(key, out var ordinary) &&
                (request.ApplyBinding == null || ReferenceEquals(request.Baseline, ordinary.Draft) || ReferenceEquals(baseline, ordinary.Attempted)))
                _ordinarySaveFailures.Remove(key);
        }
        if (_deviceSaveFeedback.GetValueOrDefault(key)?.Revision == request.Revision)
            _deviceSaveFeedback.Remove(key);
        NotifyDeviceSaveFeedback(key);
    }

    internal void CancelBindingFailure(string key, long revision)
    {
        if (_bindingSaveFailures.GetValueOrDefault((key, revision)) is { Error: not SettingsConflictException } &&
            _bindingSaveFailures.Remove((key, revision)))
            NotifyDeviceSaveFeedback(key);
    }

    internal void ClearReloadedFailures(string key)
    {
        foreach (var failure in _bindingSaveFailures.Keys.Where(item => item.Key == key).ToArray())
            _bindingSaveFailures.Remove(failure);
        _ordinarySaveFailures.Remove(key);
        _committedDeviceSettings.Remove(key);
        NotifyDeviceSaveFeedback(key);
    }

    internal void FailSaveReload(string key, DeviceSaveFeedback failed, string message)
    {
        var retry = failed with { ReloadProblem = message };
        if (failed.ApplyBinding != null)
            _bindingSaveFailures[(key, failed.Revision)] = retry;
        else
            _ordinarySaveFailures[key] = retry;
        NotifyDeviceSaveFeedback(key);
    }

    internal async Task RetrySaveAsync(string key)
    {
        if (GetSaveFeedback(key) is not { Pending: false, Draft: { } draft, Error: not SettingsConflictException } current)
            return;
        if (current.ApplyBinding is not { } applyBinding)
        {
            await SaveDeviceSettings(key, draft, current.Baseline).ConfigureAwait(false);
            return;
        }
        var baseline = _committedDeviceSettings.TryGetValue(key, out var committed) && committed.Revision > current.Revision
            ? committed.Settings : current.Baseline;
        await SaveDeviceSettings(key, draft, baseline, applyBinding,
            error =>
            {
                if (error == null)
                    foreach (var device in Devices.Where(device => device.Key == key))
                        device.ApplySavedBinding(settings => applyBinding(settings, false));
            }, current.Revision, current.BindingTarget).ConfigureAwait(false);
    }

    internal async Task RetryHardwareApplyAsync(string key, UnitApplyOutcome unit)
    {
        if (GetSaveFeedback(key) is { Pending: true } ||
            GetApplyFeedback(key) is not { Pending: false, Units: { } units } current || !units.Contains(unit))
            return;
        var pending = current with { Pending = true };
        _deviceApplyFeedback[key] = pending;
        NotifyDeviceSaveFeedback(key);
        UnitApplyOutcome result;
        try
        {
            result = await RetryUnitApplyAsync(key, unit).ConfigureAwait(false);
        }
        catch (Exception e)
        {
            result = unit with { Status = UnitApplyStatus.Failed, Detail = e.Message };
        }
        await Dispatcher.UIThread.InvokeAsync(() =>
        {
            if (!ReferenceEquals(GetApplyFeedback(key), pending))
                return;
            _deviceApplyFeedback[key] = new(units.Select(item => item == unit ? result : item).ToArray());
            NotifyDeviceSaveFeedback(key);
        });
    }
}

public sealed partial class DeviceViewModel
{
    [ObservableProperty]
    public partial bool ReloadingSave { get; private set; }

    UnitApplyOutcome? SelectedApplyOutcome => _main.GetApplyFeedback(Key)?.Units?.FirstOrDefault(unit =>
        unit.Port == Info.Port && unit.ProductId == Info.ProductId && unit.Serial == Info.Serial);

    public string SaveStatus
    {
        get
        {
            var save = _main.GetSaveFeedback(Key);
            var apply = _main.GetApplyFeedback(Key);
            string software = save switch
            {
                null => "",
                { } feedback when IsCustomize && OwnsBindingSave(feedback.Revision) => "",
                { Pending: true } => "Saving…",
                { ReloadProblem: { } problem } => problem,
                { Error: SettingsConflictException } => "The saved profile changed. Discard your unsaved profile edits and reload to continue.",
                _ when CanShowBindingSave => "Couldn't save this binding. Return to the binding editor to retry.",
                { Error: not null } => "Couldn't save to the software profile.",
                _ => "",
            };
            if (apply == null)
                return software;
            var selected = SelectedApplyOutcome;
            int other = apply.Units?.Count(unit => unit != selected && unit.Status == UnitApplyStatus.Failed) ?? 0;
            bool needsAttention = selected?.Status is UnitApplyStatus.Failed or UnitApplyStatus.Unavailable or UnitApplyStatus.Disconnected;
            if (save != null && !apply.Pending && !needsAttention && other == 0)
                return software;
            string hardware = apply.Pending ? "Retrying hardware…" : software.Length > 0 ? "Last saved profile" : "Saved to profile";
            if (!apply.Pending)
            {
                string detail = selected switch
                {
                    { Detail.Length: > 0 } unit when needsAttention => unit.Detail,
                    { Status: UnitApplyStatus.Failed } => "Couldn't apply the saved profile to this device",
                    { Status: UnitApplyStatus.Unavailable } => "Firmware control is unavailable",
                    { Status: UnitApplyStatus.Disconnected } => "The device disconnected",
                    { ReadbackConfirmations: > 0 } unit => $"Device reported {unit.ReadbackConfirmations} readback confirmations",
                    { Status: UnitApplyStatus.Acknowledged } => "Device acknowledged the commands",
                    { Status: UnitApplyStatus.NoFirmwareChanges } => "No firmware changes were sent",
                    _ => "",
                };
                if (detail.Length > 0)
                    hardware += $" · {detail}";
                if (other > 0)
                    hardware += $" · {other} other unit(s) need attention";
            }
            return software.Length > 0 ? $"{software}\n{hardware}" : hardware;
        }
    }

    public bool HasSaveStatus => SaveStatus.Length > 0;

    public bool CanRetrySave => _main.GetSaveFeedback(Key) is { Pending: false, Draft: not null, Error: not SettingsConflictException } feedback &&
        !OwnsBindingSave(feedback.Revision);

    public bool CanRetryHardwareApply => !_detached && _main.GetSaveFeedback(Key) is not { Pending: true } &&
        _main.GetApplyFeedback(Key) is { Pending: false } && SelectedApplyOutcome is { Status: UnitApplyStatus.Failed, Serial.Length: > 0 };

    public bool CanReloadSave => _main.GetSaveFeedback(Key) is { Pending: false, Error: SettingsConflictException };

    public bool CanShowBindingSave => !IsCustomize && _main.GetSaveFeedback(Key) is { Pending: false, Error: not null and not SettingsConflictException } feedback &&
        OwnsBindingSave(feedback.Revision);

    [RelayCommand]
    void ShowBindingSave()
    {
        if (CanShowBindingSave)
            SelectedPage = "CUSTOMIZE";
    }

    [RelayCommand]
    Task RetrySave() => CanRetrySave ? _main.RetrySaveAsync(Key) : Task.CompletedTask;

    [RelayCommand]
    Task RetryHardwareApply() => CanRetryHardwareApply && SelectedApplyOutcome is { } unit
        ? _main.RetryHardwareApplyAsync(Key, unit) : Task.CompletedTask;

    [RelayCommand]
    void RequestReloadSave()
    {
        if (!CanReloadSave || _main.GetSaveFeedback(Key) is not { } failed)
            return;
        SetProfileConfirmation("Discard all unsaved edits to this software profile, including Lighting and Performance, and reload from the service?",
            () => _ = ReloadFailedSaveAsync(failed));
    }

    async Task ReloadFailedSaveAsync(DeviceSaveFeedback failed)
    {
        long saveRevision = _main.DeviceSaveState(Key).Revision;
        bool Current() => !_detached && _main.DeviceSaveState(Key).Revision == saveRevision && ReferenceEquals(_main.GetSaveFeedback(Key), failed);
        if (ReloadingSave || !Current())
            return;
        var editRevision = (Lighting.EditRevision, Performance.EditRevision);
        ReloadingSave = true;
        Lighting.PausePendingEdits();
        Performance.PausePendingEdits();
        try
        {
            var fresh = await _main.ReadDeviceSettingsAsync(Key).WaitAsync(TimeSpan.FromSeconds(5)).ConfigureAwait(false);
            await Dispatcher.UIThread.InvokeAsync(() =>
            {
                if (!Current())
                    return;
                if (editRevision != (Lighting.EditRevision, Performance.EditRevision))
                {
                    _main.FailSaveReload(Key, failed, "Profile edits changed while reloading. Review your edits before confirming again.");
                    return;
                }
                SelectedKey = null;
                Lighting.TakePendingSettings();
                Performance.TakePendingSettings();
                Load(fresh, fromService: true);
                _main.ClearReloadedFailures(Key);
            });
        }
        catch (Exception error)
        {
            System.Diagnostics.Trace.TraceWarning("Could not reload the saved profile for {0}: {1}", Key, error);
            await Dispatcher.UIThread.InvokeAsync(() =>
            {
                if (Current())
                    _main.FailSaveReload(Key, failed, "Couldn't reload the saved profile. Your edits are still here. Try Discard and reload… again.");
            });
        }
        finally
        {
            await Dispatcher.UIThread.InvokeAsync(() =>
            {
                Lighting.ResumePendingEdits();
                Performance.ResumePendingEdits();
                ReloadingSave = false;
            });
        }
    }

    internal void NotifySaveFeedback()
    {
        ResetKeymapCommand.NotifyCanExecuteChanged();
        ConfirmProfileChangeCommand.NotifyCanExecuteChanged();
        OnPropertyChanged(nameof(SaveStatus));
        OnPropertyChanged(nameof(HasSaveStatus));
        OnPropertyChanged(nameof(CanRetrySave));
        OnPropertyChanged(nameof(CanRetryHardwareApply));
        OnPropertyChanged(nameof(CanReloadSave));
        OnPropertyChanged(nameof(CanShowBindingSave));
    }
}
