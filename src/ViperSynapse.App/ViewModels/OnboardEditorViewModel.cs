using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using ViperSynapse.App.Services;
using ViperSynapse.Core.Protocol;

namespace ViperSynapse.App.ViewModels;

public sealed record OnboardTarget(string Serial, string Label);
public sealed record OnboardSlotChoice(byte Id, string Label, string Name);
public sealed record OnboardKeyChoice(byte Usage, string Label);

public sealed partial class OnboardEditorViewModel(
    Func<Task<IReadOnlyList<DeviceInfo>>> getDevices,
    Func<OnboardRequest, Task<OnboardResult>> manage,
    Func<OnboardArchiveRequest, Task<OnboardBackup>>? backup = null,
    Func<OnboardArchiveRequest, Task<OnboardRestoreResult>>? restore = null,
    OnboardBackupFiles? files = null) : ObservableObject
{
    static readonly IReadOnlyList<OnboardKeyChoice> KeyboardKeys = BuildKeyChoices();
    readonly Func<OnboardArchiveRequest, Task<OnboardBackup>>? _backup = backup;
    readonly Func<OnboardArchiveRequest, Task<OnboardRestoreResult>>? _restore = restore;
    readonly bool _fixedBackupFiles = files != null;
    OnboardBackupFiles? _backupFiles = files;
    long _targetRevision, _selectionRevision;
    bool _loadTarget, _replacingTargets, _replacingSlots;
    OnboardRequest? _pendingDelete;

    [ObservableProperty] public partial IReadOnlyList<OnboardTarget> Targets { get; private set; } = [];
    [ObservableProperty] public partial OnboardTarget? SelectedTarget { get; set; }
    [ObservableProperty] public partial IReadOnlyList<OnboardSlotChoice> Slots { get; private set; } = [];
    [ObservableProperty] public partial OnboardSlotChoice? SelectedSlot { get; set; }
    [ObservableProperty] public partial IReadOnlyList<byte> AvailableSlots { get; private set; } = [];
    [ObservableProperty] public partial byte? NewSlot { get; set; }
    [ObservableProperty] public partial string Name { get; set; } = "";
    [ObservableProperty] public partial bool DeletePending { get; private set; }
    [ObservableProperty] public partial byte? ActiveSlot { get; private set; }
    [ObservableProperty] public partial OnboardButton? SelectedButton { get; set; } = BasiliskOnboard.Buttons[0];
    [ObservableProperty] public partial bool IsHypershift { get; set; }
    [ObservableProperty] public partial OnboardKeyChoice? SelectedKey { get; set; }
    [ObservableProperty] public partial bool Control { get; set; }
    [ObservableProperty] public partial bool Shift { get; set; }
    [ObservableProperty] public partial bool Alt { get; set; }
    [ObservableProperty] public partial bool Meta { get; set; }
    [ObservableProperty] public partial string BindingText { get; private set; } = "";
    [ObservableProperty] public partial string Status { get; private set; } = "";
    [ObservableProperty] public partial bool IsBusy { get; private set; }

    public IReadOnlyList<OnboardButton> Buttons => BasiliskOnboard.Buttons;
    public IReadOnlyList<OnboardKeyChoice> Keys => KeyboardKeys;

    public bool HasTarget => SelectedTarget is { } target && Targets.Any(t => t.Serial == target.Serial);
    public bool HasSlot => HasTarget && SelectedSlot is { } slot && Slots.Any(s => s.Id == slot.Id);
    public bool CanRefresh => !IsBusy;
    public bool CanActivate => !IsBusy && HasSlot && SelectedSlot!.Id != ActiveSlot;
    public bool CanDelete => CanActivate && SelectedSlot!.Id != 1;
    public bool CanConfirmDelete => CanDelete && DeletePending && _pendingDelete is { } request
        && request.Serial == SelectedTarget?.Serial && request.Slot == SelectedSlot?.Id;
    public bool CanReadBinding => !IsBusy && HasSlot && SelectedButton is { } button && Buttons.Contains(button);
    public bool CanEditBinding => CanReadBinding && SelectedSlot!.Id == ActiveSlot;
    public bool CanRestore => CanEditBinding && !IsHypershift;
    public bool CanAssign => CanEditBinding && SelectedKey is { } key && Keys.Contains(key);
    public bool CanCreate => !IsBusy && HasTarget && NewSlot is byte slot && AvailableSlots.Contains(slot);
    public bool CanRename => !IsBusy && HasSlot && Name is { } name
        && name.Trim().Length is >= 1 and <= 80 && !name.Any(char.IsControl)
        && name.Trim() != SelectedSlot!.Name;

    partial void OnSelectedTargetChanged(OnboardTarget? oldValue, OnboardTarget? newValue)
    {
        if (!_replacingTargets)
            TargetChanged(oldValue, newValue);
    }

    void TargetChanged(OnboardTarget? oldValue, OnboardTarget? newValue)
    {
        if (oldValue?.Serial == newValue?.Serial)
            return;
        _targetRevision++;
        InvalidateArchiveSelection();
        ArchiveStatus = "";
        ClearSlots();
        Status = "";
        _loadTarget = HasTarget;
        if (_loadTarget && !IsBusy)
            _ = RunAsync(LoadTargetAsync);
    }

    partial void OnSelectedSlotChanged(OnboardSlotChoice? oldValue, OnboardSlotChoice? newValue)
    {
        if (!_replacingSlots)
            SlotChanged(oldValue, newValue);
    }

    void SlotChanged(OnboardSlotChoice? oldValue, OnboardSlotChoice? newValue)
    {
        if (oldValue?.Id != newValue?.Id)
        {
            InvalidateArchiveSelection();
            ArchiveStatus = "";
            CancelDelete();
            ClearBinding();
        }
        if (oldValue?.Id != newValue?.Id || oldValue?.Name != newValue?.Name)
            Name = newValue?.Name ?? "";
        NotifyState();
    }

    partial void OnSelectedButtonChanged(OnboardButton? value) => ClearBinding();
    partial void OnIsHypershiftChanged(bool value) => ClearBinding();
    partial void OnSelectedKeyChanged(OnboardKeyChoice? value) => NotifyState();
    partial void OnNameChanged(string value) => NotifyState();
    partial void OnNewSlotChanged(byte? value) => NotifyState();
    partial void OnIsBusyChanged(bool value) => NotifyState();

    [RelayCommand(CanExecute = nameof(CanRefresh))]
    Task RefreshAsync() => RefreshTargetsAsync();

    internal Task RefreshTargetsAsync(IReadOnlyList<DeviceInfo>? snapshot = null)
    {
        return RunAsync(async () =>
        {
            CancelDelete();
            InvalidateArchiveSelection();
            var devices = snapshot ?? await getDevices();
            var targets = devices.Where(Eligible)
                .GroupBy(d => d.Serial!, StringComparer.Ordinal).Where(group => group.Count() == 1)
                .Select(group => group.Single())
                .OrderBy(d => d.Serial, StringComparer.Ordinal)
                .Select(d => new OnboardTarget(d.Serial!, $"{d.Serial} · {(d.Connection == "wired" ? "Wired USB" : "HyperSpeed")}"))
                .ToArray();
            var previous = SelectedTarget;
            _replacingTargets = true;
            try
            {
                if (!Targets.SequenceEqual(targets))
                    Targets = targets;
                SelectedTarget = Targets.FirstOrDefault(target => target.Serial == previous?.Serial)
                    ?? (Targets.Count == 1 ? Targets[0] : null);
            }
            finally { _replacingTargets = false; }
            TargetChanged(previous, SelectedTarget);
            if (HasTarget)
            {
                _loadTarget = true;
                await LoadTargetAsync();
            }
            else
            {
                Status = Targets.Count == 0 ? "No Basilisk V4 Pro is available over wired USB or HyperSpeed."
                    : "Choose a mouse to read its onboard profiles.";
                NotifyState();
            }
        });
    }

    static bool Eligible(DeviceInfo device) =>
        (device.Connection == "wired" && string.Equals(device.ProductId, "00e2", StringComparison.OrdinalIgnoreCase)
        || device.Connection == "hyperspeed" && string.Equals(device.ProductId, "00e3", StringComparison.OrdinalIgnoreCase))
        && !string.IsNullOrWhiteSpace(device.ControlNode) && device.Serial is { Length: >= 1 and <= 22 } serial
        && !string.IsNullOrWhiteSpace(serial) && !serial.Any(char.IsControl);

    async Task LoadTargetAsync()
    {
        _loadTarget = false;
        if (!HasTarget)
            return;
        var revision = _targetRevision;
        try
        {
            await SendAsync(new OnboardRequest(OnboardOperation.List, Serial: SelectedTarget!.Serial));
        }
        catch
        {
            if (revision == _targetRevision)
                ClearSlots();
            throw;
        }
    }

    [RelayCommand(CanExecute = nameof(CanCreate))]
    Task CreateAsync() => CanCreate ? RunRequestAsync(new(OnboardOperation.Create, NewSlot!.Value, Serial: SelectedTarget!.Serial)) : Task.CompletedTask;

    [RelayCommand(CanExecute = nameof(CanActivate))]
    Task ActivateAsync() => CanActivate ? RunRequestAsync(ForSlot(OnboardOperation.Select)) : Task.CompletedTask;

    [RelayCommand(CanExecute = nameof(CanRename))]
    Task RenameAsync() => CanRename ? RunRequestAsync(ForSlot(OnboardOperation.Rename) with { Name = Name.Trim() }) : Task.CompletedTask;

    [RelayCommand(CanExecute = nameof(CanDelete))]
    void BeginDelete()
    {
        if (!CanDelete)
            return;
        _pendingDelete = ForSlot(OnboardOperation.Delete) with { ConfirmDelete = true };
        DeletePending = true;
        NotifyState();
    }

    [RelayCommand]
    void CancelDelete()
    {
        _pendingDelete = null;
        DeletePending = false;
        NotifyState();
    }

    [RelayCommand(CanExecute = nameof(CanConfirmDelete))]
    Task DeleteAsync()
    {
        if (!CanConfirmDelete || _pendingDelete is not { } request)
            return Task.CompletedTask;
        CancelDelete();
        return RunRequestAsync(request);
    }

    [RelayCommand(CanExecute = nameof(CanReadBinding))]
    Task ReadBindingAsync() => CanReadBinding ? RunRequestAsync(ForButton(OnboardOperation.ReadBinding)) : Task.CompletedTask;

    [RelayCommand(CanExecute = nameof(CanAssign))]
    Task AssignAsync() => CanAssign
        ? RunRequestAsync(ForButton(OnboardOperation.SetKeyboard) with { Usage = SelectedKey!.Usage, Modifiers = Modifiers })
        : Task.CompletedTask;

    [RelayCommand(CanExecute = nameof(CanEditBinding))]
    Task DisableAsync() => CanEditBinding ? RunRequestAsync(ForButton(OnboardOperation.Disable)) : Task.CompletedTask;

    [RelayCommand(CanExecute = nameof(CanRestore))]
    Task RestoreAsync() => CanRestore ? RunRequestAsync(ForButton(OnboardOperation.Restore)) : Task.CompletedTask;

    byte Modifiers => (byte)((Control ? 1 : 0) | (Shift ? 2 : 0) | (Alt ? 4 : 0) | (Meta ? 8 : 0));

    OnboardRequest ForSlot(OnboardOperation operation) => new(operation, SelectedSlot!.Id, Serial: SelectedTarget!.Serial);
    OnboardRequest ForButton(OnboardOperation operation) =>
        ForSlot(operation) with { Button = SelectedButton!.Id, Layer = IsHypershift ? (byte)1 : (byte)0 };

    Task RunRequestAsync(OnboardRequest request) => RunAsync(() => SendAsync(request));

    async Task RunAsync(Func<Task> action)
    {
        if (IsBusy)
            return;
        IsBusy = true;
        Status = "";
        try
        {
            await action();
        }
        catch (Exception error)
        {
            if (!_loadTarget)
                Status = error.Message;
        }
        finally
        {
            IsBusy = false;
        }
        if (_loadTarget)
            await RunAsync(LoadTargetAsync);
    }

    async Task SendAsync(OnboardRequest request)
    {
        long targetRevision = _targetRevision, selectionRevision = _selectionRevision;
        if (request.Button != 0)
            BindingText = "";
        OnboardResult result;
        try { result = await manage(request); }
        catch when (targetRevision != _targetRevision)
        {
            return;
        }
        if (targetRevision != _targetRevision || request.Serial != SelectedTarget?.Serial)
            return;
        if (result.Serial != request.Serial)
            throw new InvalidDataException("The reply was for a different mouse. Refresh the onboard profiles and try again.");
        if (result.Slots is not { Count: >= 1 and <= 5 } || !result.Slots.Contains(result.ActiveSlot)
            || !result.Slots.Contains((byte)1) || result.Slots.Any(slot => !BasiliskOnboard.IsSlot(slot))
            || result.Slots.Distinct().Count() != result.Slots.Count)
            throw new InvalidDataException("The mouse returned an invalid onboard profile list.");
        if (request.Button != 0 && (result.Binding is not { Block.Length: 7 } reply
            || reply.Layer != request.Layer && !(request.Operation == OnboardOperation.ReadBinding && request.Layer == 1 && reply.Layer == 0)))
            throw new InvalidDataException("The mouse did not confirm the requested binding layer.");

        byte? preferred = selectionRevision == _selectionRevision && request.Operation is OnboardOperation.Create or OnboardOperation.Select
            ? request.Slot : SelectedSlot?.Id;
        ApplySlots(result, preferred);
        if (selectionRevision == _selectionRevision && result.Binding is { } binding
            && request.Slot == SelectedSlot?.Id && request.Button == SelectedButton?.Id)
            BindingText = DescribeBinding(binding, request.Layer, SelectedButton!);
        Status = request.Operation switch
        {
            OnboardOperation.Create => $"Created slot {request.Slot}.",
            OnboardOperation.Select => $"Slot {request.Slot} is active.",
            OnboardOperation.Rename => $"Saved the local name for slot {request.Slot}.",
            OnboardOperation.Delete => $"Deleted slot {request.Slot}.",
            OnboardOperation.SetKeyboard or OnboardOperation.Disable or OnboardOperation.Restore => "Saved the onboard binding.",
            _ => "",
        };
    }

    void ApplySlots(OnboardResult result, byte? preferred)
    {
        if (_pendingRestore != null || ActiveSlot != result.ActiveSlot)
            InvalidateArchiveSelection();
        ActiveSlot = result.ActiveSlot;
        var slots = result.Slots.Order().Select(id =>
        {
            string name = result.Names?.GetValueOrDefault(id) ?? "";
            string label = name.Length == 0 ? $"Slot {id}" : $"Slot {id} · {name}";
            return new OnboardSlotChoice(id, id == result.ActiveSlot ? $"{label} (active)" : label, name);
        }).ToArray();
        var previous = SelectedSlot;
        _replacingSlots = true;
        try
        {
            if (!Slots.SequenceEqual(slots))
                Slots = slots;
            SelectedSlot = Slots.FirstOrDefault(slot => slot.Id == preferred) ?? Slots.Single(slot => slot.Id == result.ActiveSlot);
        }
        finally { _replacingSlots = false; }
        SlotChanged(previous, SelectedSlot);
        var available = Enumerable.Range(2, 4).Select(id => (byte)id).Except(result.Slots).ToArray();
        byte? newSlot = NewSlot;
        if (!AvailableSlots.SequenceEqual(available))
            AvailableSlots = available;
        NewSlot = newSlot is byte previousChoice && AvailableSlots.Contains(previousChoice)
            ? previousChoice : AvailableSlots.Count == 0 ? null : AvailableSlots[0];
        if (DeletePending)
            CancelDelete();
        NotifyState();
    }

    void ClearSlots()
    {
        Slots = [];
        AvailableSlots = [];
        ActiveSlot = null;
        SelectedSlot = null;
        NewSlot = null;
        CancelDelete();
        ClearBinding();
    }

    void ClearBinding()
    {
        _selectionRevision++;
        BindingText = "";
        NotifyState();
    }

    void NotifyState()
    {
        foreach (string property in new[] { nameof(HasTarget), nameof(HasSlot), nameof(CanRefresh), nameof(CanActivate), nameof(CanDelete),
            nameof(CanConfirmDelete), nameof(CanReadBinding), nameof(CanEditBinding), nameof(CanRestore), nameof(CanAssign), nameof(CanCreate), nameof(CanRename) })
            OnPropertyChanged(property);

        RefreshCommand.NotifyCanExecuteChanged();
        CreateCommand.NotifyCanExecuteChanged();
        ActivateCommand.NotifyCanExecuteChanged();
        RenameCommand.NotifyCanExecuteChanged();
        BeginDeleteCommand.NotifyCanExecuteChanged();
        DeleteCommand.NotifyCanExecuteChanged();
        ReadBindingCommand.NotifyCanExecuteChanged();
        AssignCommand.NotifyCanExecuteChanged();
        DisableCommand.NotifyCanExecuteChanged();
        RestoreCommand.NotifyCanExecuteChanged();
        NotifyArchiveState();
    }

    static string DescribeBinding(OnboardBinding binding, byte requestedLayer, OnboardButton button)
    {
        var bytes = binding.Block.AsSpan();
        string description = bytes switch
        {
            [0, 1, 0, 0, 0, 0, 0] => "Disabled",
            [2, 2, var modifiers, var usage, 0, 0, 0] => DescribeKeyboard(modifiers, usage),
            _ when bytes.SequenceEqual(button.Factory.Span) => $"Firmware default · {button.Name}",
            _ => "Firmware action",
        };
        return requestedLayer == 1 && binding.Layer == 0 ? $"Inherited from Normal · {description}" : description;
    }

    static string DescribeKeyboard(byte modifiers, byte usage)
    {
        string key = KeyboardKeys.FirstOrDefault(choice => choice.Usage == usage)?.Label ?? $"HID key {usage:X2}";
        var parts = new List<string>();
        if ((modifiers & 0x11) != 0) parts.Add("Ctrl");
        if ((modifiers & 0x22) != 0) parts.Add("Shift");
        if ((modifiers & 0x44) != 0) parts.Add("Alt");
        if ((modifiers & 0x88) != 0) parts.Add("Super");
        parts.Add(key);
        return string.Join(" + ", parts);
    }

    static IReadOnlyList<OnboardKeyChoice> BuildKeyChoices()
    {
        var keys = new List<OnboardKeyChoice>();
        for (int i = 0; i < 26; i++)
            keys.Add(new((byte)(4 + i), ((char)('A' + i)).ToString()));
        for (int i = 0; i < 10; i++)
            keys.Add(new((byte)(0x1E + i), ((i + 1) % 10).ToString()));
        for (int i = 1; i <= 24; i++)
            keys.Add(new((byte)(i <= 12 ? 0x39 + i : 0x68 + i - 13), $"F{i}"));
        keys.AddRange([
            new(0x28, "Enter"), new(0x29, "Escape"), new(0x2A, "Backspace"), new(0x2B, "Tab"), new(0x2C, "Space"),
            new(0x49, "Insert"), new(0x4A, "Home"), new(0x4B, "Page Up"), new(0x4C, "Delete"),
            new(0x4D, "End"), new(0x4E, "Page Down"), new(0x4F, "Right Arrow"), new(0x50, "Left Arrow"),
            new(0x51, "Down Arrow"), new(0x52, "Up Arrow"),
        ]);
        return keys.AsReadOnly();
    }
}
