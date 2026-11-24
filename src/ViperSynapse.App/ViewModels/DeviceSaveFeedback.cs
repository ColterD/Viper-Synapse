using System.Collections.ObjectModel;
using Avalonia.Threading;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using ViperSynapse.App.Services;
using ViperSynapse.Core.Bindings;
using ViperSynapse.Core.Protocol;

namespace ViperSynapse.App.ViewModels;

internal sealed record DeviceSaveFeedback(long Revision, bool Pending, DeviceSettings? Draft = null,
    DeviceSettings? Baseline = null, IReadOnlyList<UnitApplyOutcome>? Units = null, Exception? Error = null,
    string? DeviceName = null, Func<DeviceSettings, bool, DeviceSettings>? ApplyBinding = null, string? ReloadProblem = null,
    DeviceSettings? Attempted = null);

public sealed record DeviceSaveNotice(string Key, string Message, DeviceViewModel? Device)
{
    public bool CanOpen => Device != null;
}

public sealed partial class MainViewModel
{
    readonly Dictionary<string, DeviceSaveFeedback> _deviceSaveFeedback = [];
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
        return ordinary ?? binding ?? latest;
    }

    internal DeviceSaveFeedback? GetBindingFailure(string key) => _bindingSaveFailures
        .Where(entry => entry.Key.Key == key).Select(entry => entry.Value)
        .OrderByDescending(failure => failure.Error is SettingsConflictException)
        .ThenByDescending(failure => failure.Revision).FirstOrDefault();

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
        }
        else if (error != null)
        {
            _ordinarySaveFailures[key] = failed;
        }
        if (error == null)
        {
            _committedDeviceSettings[key] = (request.Revision, attempted);
            foreach (var device in Devices.Where(device => device.Key == key))
                device.InitializeSavedSettings(attempted);
            if (_ordinarySaveFailures.TryGetValue(key, out var ordinary) &&
                (request.ApplyBinding == null || ReferenceEquals(request.Baseline, ordinary.Draft) || ReferenceEquals(baseline, ordinary.Attempted)))
                _ordinarySaveFailures.Remove(key);
        }
        if (_deviceSaveFeedback.GetValueOrDefault(key)?.Revision == request.Revision)
        {
            if (error == null)
                _deviceSaveFeedback[key] = new(request.Revision, false, Units: units, DeviceName: request.DeviceName);
            else
                _deviceSaveFeedback.Remove(key);
        }
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

    internal async Task RetrySaveAsync(string key, UnitApplyOutcome? unit)
    {
        if (GetSaveFeedback(key) is not { Pending: false } current)
            return;
        if (current.Draft is { } draft)
        {
            if (current.ApplyBinding is not { } applyBinding)
                await SaveDeviceSettings(key, draft, current.Baseline).ConfigureAwait(false);
            else
            {
                var baseline = _committedDeviceSettings.TryGetValue(key, out var committed) && committed.Revision > current.Revision
                    ? committed.Settings : current.Baseline;
                await SaveDeviceSettings(key, draft, baseline, applyBinding,
                    error =>
                    {
                        if (error == null)
                            foreach (var device in Devices.Where(device => device.Key == key))
                                device.ApplySavedBinding(settings => applyBinding(settings, false));
                    }, current.Revision).ConfigureAwait(false);
            }
            return;
        }
        if (unit == null || current.Units == null)
            return;
        SetSaveFeedback(key, current with { Pending = true });
        UnitApplyOutcome result;
        try
        {
            result = await Client.RetryUnitApplyAsync(key, unit).ConfigureAwait(false);
        }
        catch (Exception e)
        {
            result = unit with { Status = UnitApplyStatus.Failed, Detail = e.Message };
        }
        await Dispatcher.UIThread.InvokeAsync(() =>
        {
            if (GetSaveFeedback(key)?.Revision == current.Revision)
                SetSaveFeedback(key, current with { Units = current.Units.Select(item => item == unit ? result : item).ToArray() });
        });
    }
}

public sealed partial class DeviceViewModel
{
    [ObservableProperty]
    public partial bool ReloadingSave { get; private set; }

    UnitApplyOutcome? SelectedApplyOutcome => _main.GetSaveFeedback(Key)?.Units?.FirstOrDefault(unit =>
        unit.Port == Info.Port && unit.ProductId == Info.ProductId && unit.Serial == Info.Serial);

    public string SaveStatus
    {
        get
        {
            if (_main.GetSaveFeedback(Key) is not { } feedback)
                return "";
            if (IsCustomize && OwnsBindingSave(feedback.Revision) && (feedback.Pending || feedback.Error != null))
                return "";
            if (feedback.Pending)
                return feedback.Draft == null ? "Retrying hardware…" : "Saving…";
            if (feedback.ReloadProblem is { } problem)
                return problem;
            if (feedback.Error is SettingsConflictException)
                return "The saved profile changed. Discard your unsaved profile edits and reload to continue.";
            if (CanShowBindingSave)
                return "Couldn't save this binding. Return to the binding editor to retry.";
            if (feedback.Error != null)
                return "Couldn't save to the software profile.";
            string result = SelectedApplyOutcome switch
            {
                { Status: UnitApplyStatus.Failed or UnitApplyStatus.Unavailable or UnitApplyStatus.Disconnected, Detail: { } detail } => detail,
                { ReadbackConfirmations: > 0 } unit => $"Device reported {unit.ReadbackConfirmations} readback confirmations",
                { Status: UnitApplyStatus.Acknowledged } => "Device acknowledged the commands",
                { Status: UnitApplyStatus.NoFirmwareChanges } => "No firmware changes were sent",
                _ => "",
            };
            int other = feedback.Units?.Count(unit => unit.Port != Info.Port && unit.Status == UnitApplyStatus.Failed) ?? 0;
            return "Saved to profile" + (result.Length > 0 ? $" · {result}" : "") +
                (other > 0 ? $" · {other} other unit(s) need attention" : "");
        }
    }

    public bool HasSaveStatus => SaveStatus.Length > 0;

    public bool CanRetrySave => _main.GetSaveFeedback(Key) is { Pending: false } feedback &&
        (feedback.Draft != null ? feedback.Error is not SettingsConflictException && !OwnsBindingSave(feedback.Revision)
            : SelectedApplyOutcome is { Status: UnitApplyStatus.Failed, Serial.Length: > 0 });

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
    Task RetrySave() => CanRetrySave ? _main.RetrySaveAsync(Key, SelectedApplyOutcome) : Task.CompletedTask;

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
        OnPropertyChanged(nameof(CanReloadSave));
        OnPropertyChanged(nameof(CanShowBindingSave));
    }
}
