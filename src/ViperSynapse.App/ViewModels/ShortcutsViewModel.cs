using System.Collections.ObjectModel;
using System.ComponentModel;
using Avalonia.Threading;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using ViperSynapse.App.Services;
using ViperSynapse.Core.Bindings;
using ViperSynapse.Core.Devices;

namespace ViperSynapse.App.ViewModels;

/// <summary>One global shortcut as the list shows it: the keys, and what they do.</summary>
public sealed record ShortcutRow(GlobalShortcut Shortcut, string Keys, string Category, string Function);

/// <summary>
/// GLOBAL SHORTCUTS on the SYNAPSE tab: key combinations on a Razer device that do one function in every profile.
/// The keys are picked from a device's key list (they are that device's own keys, as the service sees them before
/// any remapping); the function comes from the same editor a key binding uses.
/// </summary>
public sealed partial class ShortcutsViewModel(MainViewModel main) : ObservableObject
{
    public const int MaxKeys = 3;

    ShortcutList _list = ShortcutList.Empty;
    OrderedSave<(ShortcutList List, ShortcutList Baseline)>? _persistence;
    long _loadRequest;

    OrderedSave<(ShortcutList List, ShortcutList Baseline)> Persistence => _persistence ??= new(
        save => WriteShortcutsAsync?.Invoke(save.List) ?? main.Client.SetShortcutsAsync(save.List, save.Baseline), SaveStateChanged);
    internal Func<Task<ShortcutList>> ReadShortcutsAsync { get; init; } = main.Client.GetShortcutsAsync;
    internal Func<ShortcutList, Task>? WriteShortcutsAsync { get; init; }
    internal Task PendingSave => Persistence.Pending;
    public bool HasFailedSave => Persistence.Failure != null;

    public ObservableCollection<ShortcutRow> Rows { get; } = [];

    public bool IsEmpty => Rows.Count == 0;

    public string? SaveStatus => Persistence.Reloading ? "Reloading saved shortcuts…" :
        Persistence.ReloadFailure is { } reload ? $"Could not reload saved shortcuts. Your edits are still here. {reload.Message}" :
        Persistence.Failure is SettingsConflictException ? "Saved shortcuts changed while you were editing. Your local edits are still here." :
        Persistence.Failure is { } error ? $"Could not save global shortcuts. {error.Message}" :
        Persistence.Saving ? "Saving global shortcuts…" : Persistence.HasSaved ? "Global shortcuts saved." : null;

    public bool CanDiscardFailedSave => HasFailedSave && !Persistence.Reloading && !Persistence.DiscardRequested;
    public bool CanRetryFailedSave => CanDiscardFailedSave && Persistence.Failure is not SettingsConflictException;
    public bool IsDiscardConfirmationVisible => Persistence.DiscardRequested;

    [RelayCommand(CanExecute = nameof(CanRetryFailedSave))]
    void RetrySave() => Persistence.Retry();

    [RelayCommand]
    void RequestDiscardSave() => Persistence.RequestDiscard(true);

    [RelayCommand]
    void CancelDiscardSave() => Persistence.RequestDiscard(false);

    [RelayCommand]
    async Task ConfirmDiscardSave()
    {
        var editor = Editor;
        var keys = (Key1, Key2, Key3);
        var device = EditDevice;
        bool draftChanged = false;
        void DraftChanged(object? sender, PropertyChangedEventArgs e) => draftChanged = true;
        if (editor != null)
            editor.PropertyChanged += DraftChanged;
        try
        {
            await Persistence.DiscardAsync(ReadShortcutsAsync, list =>
            {
                Close();
                Show(list);
            }, () => !draftChanged && ReferenceEquals(Editor, editor) && keys == (Key1, Key2, Key3) && EditDevice == device);
        }
        finally
        {
            if (editor != null)
                editor.PropertyChanged -= DraftChanged;
        }
    }

    void SaveStateChanged()
    {
        OnPropertyChanged(nameof(SaveStatus));
        OnPropertyChanged(nameof(HasFailedSave));
        OnPropertyChanged(nameof(CanRetryFailedSave));
        OnPropertyChanged(nameof(CanDiscardFailedSave));
        OnPropertyChanged(nameof(IsDiscardConfirmationVisible));
        RetrySaveCommand.NotifyCanExecuteChanged();
    }

    public async Task LoadAsync()
    {
        var capture = await Dispatcher.UIThread.InvokeAsync(() => (Request: ++_loadRequest, Persistence.Revision, Persistence.Pending));
        bool Current() => capture.Request == _loadRequest && capture.Revision == Persistence.Revision &&
            !Persistence.HasUncommittedChanges && !Editing;
        await capture.Pending.ConfigureAwait(false);
        if (!await Dispatcher.UIThread.InvokeAsync(Current))
            return;
        try
        {
            var list = await ReadShortcutsAsync().ConfigureAwait(false);
            await Dispatcher.UIThread.InvokeAsync(() =>
            {
                if (Current())
                    Show(list);
            });
        }
        catch (Exception e)
        {
            main.Fail("Could not read the global shortcuts", e, Current);
        }
    }

    void Show(ShortcutList list)
    {
        _list = list;
        Rows.Clear();
        foreach (var s in list.Shortcuts)
        {
            var (category, function) = BindingText.Describe(s.Binding, 0, id => main.MacroLibrary.Find(id)?.Name);
            Rows.Add(new ShortcutRow(s, string.Join(" + ", s.Keys.Select(BindingText.KeyLabel)), category, function));
        }
        OnPropertyChanged(nameof(IsEmpty));
    }

    void Commit(ShortcutList list)
    {
        var baseline = _list;
        Show(list);
        Persistence.Enqueue((list, baseline));
    }

    // ---- Adding or changing one -------------------------------------------------------------------------

    Guid? _editing;

    [ObservableProperty] public partial bool Editing { get; set; }
    [ObservableProperty] public partial DeviceViewModel? EditDevice { get; set; }
    [ObservableProperty] public partial DeviceKey? Key1 { get; set; }
    [ObservableProperty] public partial DeviceKey? Key2 { get; set; }
    [ObservableProperty] public partial DeviceKey? Key3 { get; set; }
    [ObservableProperty] public partial BindingEditorViewModel? Editor { get; set; }
    [ObservableProperty] public partial string? Problem { get; set; }

    public IEnumerable<DeviceViewModel> Devices => main.Devices;

    /// <summary>The chosen device's keys that can be part of a shortcut.</summary>
    public IReadOnlyList<DeviceKey> KeyChoices => EditDevice?.Rows.Where(r => r.Bindable && !SourceKeys.IsWheel(r.Key.Source)).Select(r => r.Key).ToList() ?? [];

    partial void OnEditDeviceChanged(DeviceViewModel? oldValue, DeviceViewModel? newValue)
    {
        if (oldValue != null) oldValue.PropertyChanged -= DeviceChanged;
        if (newValue != null) newValue.PropertyChanged += DeviceChanged;
        RefreshKeyChoices();
    }

    void DeviceChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName == nameof(DeviceViewModel.Rows))
            RefreshKeyChoices();
    }

    void RefreshKeyChoices()
    {
        var choices = KeyChoices;
        var selected = new[] { Key1, Key2, Key3 };
        if (Editing && selected.OfType<DeviceKey>().Any(key => !choices.Any(choice => choice.Source == key.Source)))
        {
            // Clearing only the missing key could silently turn a three-key shortcut into a two-key one.
            Key1 = Key2 = Key3 = null;
            Problem = "Device controls changed. Please select the shortcut keys again before saving.";
        }
        else
        {
            Key1 = choices.FirstOrDefault(k => k.Source == Key1?.Source);
            Key2 = choices.FirstOrDefault(k => k.Source == Key2?.Source);
            Key3 = choices.FirstOrDefault(k => k.Source == Key3?.Source);
        }
        OnPropertyChanged(nameof(KeyChoices));
    }

    internal void DevicesUpdated()
    {
        if (EditDevice != null && !main.Devices.Contains(EditDevice))
            EditDevice = null;
        OnPropertyChanged(nameof(Devices));
        OnPropertyChanged(nameof(KeyChoices));
    }

    [RelayCommand]
    void Add() => Open(null, [], new KeyboardBinding(0));

    [RelayCommand]
    void Change(ShortcutRow row) => Open(row.Shortcut.Id, row.Shortcut.Keys, row.Shortcut.Binding);

    [RelayCommand]
    void Delete(ShortcutRow row)
    {
        if (_editing == row.Shortcut.Id)
            Close();
        Commit(new ShortcutList(_list.Shortcuts.Where(s => s.Id != row.Shortcut.Id).ToList()));
    }

    void Open(Guid? id, IReadOnlyList<ushort> keys, Binding binding)
    {
        _editing = id;
        OnPropertyChanged(nameof(Devices));
        // The device whose keys these are, else the first one.
        EditDevice = main.Devices.FirstOrDefault(d => keys.Count > 0 &&
            keys.All(k => !SourceKeys.IsWheel(k) && d.Rows.Any(r => r.Bindable && r.Key.Source == k)))
                     ?? main.Devices.FirstOrDefault();
        Problem = null;
        DeviceKey? Pick(int i) => i < keys.Count ? KeyChoices.FirstOrDefault(k => k.Source == keys[i]) : null;
        Key1 = Pick(0);
        Key2 = Pick(1);
        Key3 = Pick(2);
        if (keys.Any(k => !KeyChoices.Any(choice => choice.Source == k)))
        {
            Key1 = Key2 = Key3 = null;
            Problem = "Some saved keys are unavailable. Please select the shortcut keys again before saving.";
        }
        var row = new KeyRowViewModel(new DeviceKey(0, "Shortcut", 0, 0, "other", 0));
        Editor = new BindingEditorViewModel(row, binding, main.MacroLibrary, [], 8, (_, b) => Save(b), Close,
            id == null ? "NEW SHORTCUT" : "EDIT SHORTCUT");
        Editing = true;
    }

    void Save(Binding binding)
    {
        var keys = new[] { Key1, Key2, Key3 }.OfType<DeviceKey>().Select(k => k.Source).Distinct().ToList();
        if (keys.Count < 2)
        {
            Problem = "A shortcut is a combination: pick at least two keys.";
            return;
        }
        if (binding is DefaultBinding)
        {
            Problem = "Pick what the shortcut does.";
            return;
        }
        if (_list.Shortcuts.Any(s => s.Id != _editing && s.Keys.Order().SequenceEqual(keys.Order())))
        {
            Problem = "Another shortcut already uses these keys.";
            return;
        }
        var list = _editing is Guid id
            ? _list.Shortcuts.Select(s => s.Id == id ? s with { Keys = keys, Binding = binding } : s).ToList()
            : [.. _list.Shortcuts, GlobalShortcut.Create(keys, binding)];
        Close();
        Commit(new ShortcutList(list));
    }

    void Close()
    {
        Editing = false;
        Editor = null;
        EditDevice = null;
        _editing = null;
    }
}
