using Avalonia.Threading;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using ViperSynapse.App.Services;
using ViperSynapse.Core.Bindings;
using ViperSynapse.Core.Protocol;

namespace ViperSynapse.App.ViewModels;

internal sealed record DeviceSaveFeedback(long Revision, bool Pending, DeviceSettings? Draft = null,
    DeviceSettings? Baseline = null, IReadOnlyList<UnitApplyOutcome>? Units = null, Exception? Error = null);

public sealed partial class MainViewModel
{
    readonly Dictionary<string, DeviceSaveFeedback> _deviceSaveFeedback = [];

    internal DeviceSaveFeedback? GetSaveFeedback(string key) => _deviceSaveFeedback.GetValueOrDefault(key);

    void SetSaveFeedback(string key, DeviceSaveFeedback feedback)
    {
        _deviceSaveFeedback[key] = feedback;
        foreach (var device in Devices.Where(device => device.Key == key))
            device.NotifySaveFeedback();
    }

    void CompleteSave(string key, long revision, IReadOnlyList<UnitApplyOutcome>? units, Exception? error)
    {
        if (GetSaveFeedback(key) is not { } current || current.Revision != revision)
            return;
        SetSaveFeedback(key, error == null ? new(revision, false, Units: units) : current with { Pending = false, Error = error });
    }

    internal void ClearSaveFailure(string key)
    {
        _deviceSaveFeedback.Remove(key);
        foreach (var device in Devices.Where(device => device.Key == key))
            device.NotifySaveFeedback();
    }

    internal async Task RetrySaveAsync(string key, UnitApplyOutcome? unit)
    {
        if (GetSaveFeedback(key) is not { Pending: false } current)
            return;
        if (current.Draft is { } draft)
        {
            await SaveDeviceSettings(key, draft, current.Baseline).ConfigureAwait(false);
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
            if (feedback.Pending)
                return feedback.Draft == null ? "Retrying hardware…" : "Saving…";
            if (feedback.Error is { } error)
                return $"Could not save to profile. {error.Message}";
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

    public bool HasSaveStatus => _main.GetSaveFeedback(Key) != null;

    public bool CanRetrySave => _main.GetSaveFeedback(Key) is { Pending: false } feedback &&
        (feedback.Draft != null ? feedback.Error is not SettingsConflictException && !OwnsBindingSave(feedback.Revision)
            : SelectedApplyOutcome is { Status: UnitApplyStatus.Failed, Serial.Length: > 0 });

    public bool CanReloadSave => _main.GetSaveFeedback(Key) is { Pending: false, Error: SettingsConflictException };

    [RelayCommand]
    Task RetrySave() => CanRetrySave ? _main.RetrySaveAsync(Key, SelectedApplyOutcome) : Task.CompletedTask;

    [RelayCommand]
    void RequestReloadSave()
    {
        if (!CanReloadSave || _main.GetSaveFeedback(Key) is not { } failed)
            return;
        ConfirmationMessage = "Discard the unsaved profile edits and reload from the service?";
        _confirmProfileChange = () => _ = ReloadFailedSaveAsync(failed);
    }

    async Task ReloadFailedSaveAsync(DeviceSaveFeedback failed)
    {
        bool Current() => !_detached && ReferenceEquals(_main.GetSaveFeedback(Key), failed);
        if (ReloadingSave || !Current())
            return;
        ReloadingSave = true;
        try
        {
            var fresh = await _main.ReadDeviceSettingsAsync(Key).WaitAsync(TimeSpan.FromSeconds(5)).ConfigureAwait(false);
            await Dispatcher.UIThread.InvokeAsync(() =>
            {
                if (!Current())
                    return;
                SelectedKey = null;
                Lighting.TakePendingSettings();
                Performance.TakePendingSettings();
                Load(fresh, fromService: true);
                _main.ClearSaveFailure(Key);
            });
        }
        catch (Exception error)
        {
            _main.Fail("Could not reload the saved profile", error, Current);
        }
        finally
        {
            await Dispatcher.UIThread.InvokeAsync(() => ReloadingSave = false);
        }
    }

    internal void NotifySaveFeedback()
    {
        OnPropertyChanged(nameof(SaveStatus));
        OnPropertyChanged(nameof(HasSaveStatus));
        OnPropertyChanged(nameof(CanRetrySave));
        OnPropertyChanged(nameof(CanReloadSave));
    }
}
