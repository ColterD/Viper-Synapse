using ViperSynapse.App.Services;
using ViperSynapse.App.ViewModels;
using ViperSynapse.Core.Bindings;

namespace ViperSynapse.Tests;

public sealed class EditingRecoveryTests
{
    static MainViewModel Workspace() => new(new DaemonClient(), new AppPreferences(Notifications: false), false);

    static DeviceViewModel Device(MainViewModel main) => new(new DeviceInfo("ffff", "ffff", "Emulated", "unknown", "usb-test",
        false, false, null, null, null, [], false, null, [30, 48], "available"), main);

    [Fact]
    public Task A_binding_draft_saves_to_its_original_profile_keymap_and_layer() => HeadlessApp.Session.Dispatch<bool>(async () =>
    {
        var writes = new List<DeviceSettings>();
        var main = new MainViewModel(new DaemonClient(), new AppPreferences(Notifications: false), false)
        {
            WriteDeviceSettingsAsync = (_, settings) =>
            {
                writes.Add(settings);
                return Task.CompletedTask;
            },
        };
        var device = Device(main);
        var original = Profile.Create("Work") with { Keymaps = [Keymap.Empty("First"), Keymap.Empty("Second")] };
        var next = Profile.Create("Game");
        var starting = new DeviceSettings([original, next], original.Id, 1);
        await device.ReloadSettingsAsync(_ => Task.FromResult(starting));
        device.Hypershift = true;
        var row = device.Rows.First(item => item.Bindable);
        device.SelectedKey = row;
        ushort source = row.Key.Source;
        var editor = Assert.IsType<BindingEditorViewModel>(device.Editor);
        editor.SelectedFunction = BindingEditorViewModel.Functions.Single(function => function.Kind == FunctionKind.Keyboard);
        editor.CapturedKey = 46;

        ushort other = device.Rows.First(item => item.Bindable && item.Key.Source != source).Key.Source;
        var revised = original with { Keymaps = [original.Keymaps[0], original.Keymaps[1].With(other, new KeyboardBinding(47), true)] };
        await device.ReloadSettingsAsync(_ => Task.FromResult(starting with { Profiles = [revised, next], ActiveProfile = next.Id, ActiveKeymap = 0 }));
        device.Hypershift = false;
        Assert.Same(editor, device.Editor);
        Assert.Equal("Work · Second · Hypershift", editor.Target);
        editor.SaveCommand.Execute(null);
        await main.FinishDeviceEditsAsync(TimeSpan.FromSeconds(3));

        var saved = Assert.Single(writes);
        Assert.Equal(next.Id, saved.ActiveProfile);
        Assert.Equal(0, saved.ActiveKeymap);
        Assert.Equal(new KeyboardBinding(46), saved.Profiles[0].Keymaps[1].Hypershift[source]);
        Assert.Equal(new KeyboardBinding(47), saved.Profiles[0].Keymaps[1].Hypershift[other]);
        Assert.Empty(saved.Profiles[0].Keymaps[1].Standard);
        Assert.Empty(saved.Active.Keymaps[0].Hypershift);
        return true;
    }, CancellationToken.None);

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public Task Replacing_a_draft_target_preserves_the_edit_and_refuses_to_save(bool remove) => HeadlessApp.Session.Dispatch<bool>(async () =>
    {
        var main = new MainViewModel(new DaemonClient(), new AppPreferences(Notifications: false), false)
        {
            WriteDeviceSettingsAsync = (_, _) => throw new Xunit.Sdk.XunitException("A conflicted draft must not write settings."),
        };
        var device = Device(main);
        var old = Profile.Create("Work");
        var next = Profile.Create("Game");
        await device.ReloadSettingsAsync(_ => Task.FromResult(new DeviceSettings([old, next], old.Id, 0)));
        var row = device.Rows.First(row => row.Bindable);
        device.SelectedKey = row;
        var editor = Assert.IsType<BindingEditorViewModel>(device.Editor);
        editor.SelectedFunction = BindingEditorViewModel.Functions.Single(function => function.Kind == FunctionKind.Keyboard);
        editor.CapturedKey = 46;

        var changed = old with { Keymaps = [old.Keymaps[0].With(row.Key.Source, new KeyboardBinding(21))] };
        await device.ReloadSettingsAsync(_ => Task.FromResult(new DeviceSettings(remove ? [next] : [changed, next], next.Id, 0)));
        Assert.Same(editor, device.Editor);
        Assert.False(editor.CanSave);
        Assert.Equal(46, editor.CapturedKey);
        Assert.NotNull(editor.Conflict);
        editor.SaveCommand.Execute(null);
        Assert.Equal(0, main.DeviceSaveState(device.Key).Revision);
        return true;
    }, CancellationToken.None);

    [Fact]
    public Task A_transient_binding_save_failure_retries_the_visible_edit() => HeadlessApp.Session.Dispatch<bool>(async () =>
    {
        var write = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var writes = new List<DeviceSettings>();
        var main = new MainViewModel(new DaemonClient(), new AppPreferences(Notifications: false), false)
        {
            WriteDeviceSettingsAsync = (_, settings) =>
            {
                writes.Add(settings);
                return writes.Count == 1 ? write.Task : Task.CompletedTask;
            },
        };
        var device = Device(main);
        await device.ReloadSettingsAsync(_ => Task.FromResult(DeviceSettings.Create()));
        var row = device.Rows.First(item => item.Bindable);
        device.SelectedKey = row;
        var editor = Assert.IsType<BindingEditorViewModel>(device.Editor);
        editor.SelectedFunction = BindingEditorViewModel.Functions.Single(function => function.Kind == FunctionKind.Keyboard);
        editor.CapturedKey = 46;
        var rejected = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        editor.PropertyChanged += (_, change) =>
        {
            if (change.PropertyName == nameof(BindingEditorViewModel.SaveError))
                rejected.TrySetResult();
        };
        editor.SaveCommand.Execute(null);
        Assert.True(editor.IsSaving);
        Assert.Same(editor, device.Editor);
        write.SetException(new IOException("disk unavailable"));
        await rejected.Task.WaitAsync(TimeSpan.FromSeconds(3));

        Assert.Same(editor, device.Editor);
        Assert.False(editor.IsSaving);
        Assert.Equal(46, editor.CapturedKey);
        Assert.Contains("disk unavailable", editor.SaveError!);
        Assert.False(device.CanRetrySave);

        editor.CapturedKey = 47;
        editor.SaveCommand.Execute(null);
        await main.FinishDeviceEditsAsync(TimeSpan.FromSeconds(3));
        Assert.Equal(2, writes.Count);
        Assert.Equal(new KeyboardBinding(47), writes[1].Active.Keymaps[0].Standard[row.Key.Source]);
        return true;
    }, CancellationToken.None);

    [Fact]
    public Task A_binding_save_completion_is_independent_of_a_newer_queued_device_edit() => HeadlessApp.Session.Dispatch<bool>(async () =>
    {
        var first = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var next = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        int writes = 0;
        var main = new MainViewModel(new DaemonClient(), new AppPreferences(Notifications: false), false)
        {
            WriteDeviceSettingsAsync = (_, _) => ++writes == 1 ? first.Task : next.Task,
        };
        var device = Device(main);
        await device.ReloadSettingsAsync(_ => Task.FromResult(DeviceSettings.Create()));
        device.SelectedKey = device.Rows.First(row => row.Bindable);
        var editor = Assert.IsType<BindingEditorViewModel>(device.Editor);
        editor.SelectedFunction = BindingEditorViewModel.Functions.Single(function => function.Kind == FunctionKind.Keyboard);
        editor.CapturedKey = 46;
        var closed = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        device.PropertyChanged += (_, change) =>
        {
            if (change.PropertyName == nameof(DeviceViewModel.Editor) && device.Editor == null)
                closed.TrySetResult();
        };

        editor.SaveCommand.Execute(null);
        device.AddProfileCommand.Execute(null);
        first.SetResult();
        try
        {
            await closed.Task.WaitAsync(TimeSpan.FromSeconds(3));
            Assert.Null(device.Editor);
            Assert.False(main.DeviceSaveState(device.Key).Completion.IsCompleted);
        }
        finally { next.TrySetResult(); }
        await main.FinishDeviceEditsAsync(TimeSpan.FromSeconds(3));
        return true;
    }, CancellationToken.None);

    [Fact]
    public Task Deleting_a_profile_requires_confirmation_of_the_named_target() => HeadlessApp.Session.Dispatch<bool>(async () =>
    {
        var writes = new List<DeviceSettings>();
        var main = new MainViewModel(new DaemonClient(), new AppPreferences(Notifications: false), false)
        {
            WriteDeviceSettingsAsync = (_, settings) =>
            {
                writes.Add(settings);
                return Task.CompletedTask;
            },
        };
        var device = Device(main);
        var original = Profile.Create("Work");
        var next = Profile.Create("Game");
        var initial = new DeviceSettings([original, next], original.Id, 0);
        await device.ReloadSettingsAsync(_ => Task.FromResult(initial));

        device.DeleteProfileCommand.Execute(null);
        Assert.Contains("Work", device.ConfirmationMessage!);
        Assert.Empty(writes);
        await device.ReloadSettingsAsync(_ => Task.FromResult(initial with { ActiveProfile = next.Id }));
        device.ConfirmProfileChangeCommand.Execute(null);
        await main.FinishDeviceEditsAsync(TimeSpan.FromSeconds(3));

        var saved = Assert.Single(writes);
        Assert.Equal(next.Id, Assert.Single(saved.Profiles).Id);
        Assert.Equal(next.Id, saved.ActiveProfile);
        Assert.Null(device.ConfirmationMessage);
        return true;
    }, CancellationToken.None);

    [Fact]
    public Task Macros_ignore_an_old_library_reply_and_keep_newer_edits_after_a_failed_save() => HeadlessApp.Session.Dispatch<bool>(async () =>
    {
        var main = Workspace();
        var readStarted = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var oldLibrary = new TaskCompletionSource<MacroLibrary>(TaskCreationOptions.RunContinuationsAsynchronously);
        var firstWrite = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var writes = new List<MacroLibrary>();
        var macros = new MacrosViewModel(main.Client, main)
        {
            ReadLibraryAsync = () => { readStarted.TrySetResult(); return oldLibrary.Task; },
            WriteLibraryAsync = library =>
            {
                writes.Add(library);
                return writes.Count == 1 ? firstWrite.Task : Task.CompletedTask;
            },
        };
        var refresh = macros.LoadAsync();
        await readStarted.Task.WaitAsync(TimeSpan.FromSeconds(3));
        macros.NewMacroCommand.Execute(null);
        macros.NewMacroCommand.Execute(null);
        Assert.Single(writes);

        oldLibrary.SetResult(MacroLibrary.Empty);
        await refresh;
        Assert.Equal(2, macros.Library.Macros.Count);
        firstWrite.SetException(new IOException("first write failed"));
        await macros.PendingSave.WaitAsync(TimeSpan.FromSeconds(3));
        Assert.Equal(new[] { 1, 2 }, writes.Select(library => library.Macros.Count));
        Assert.False(macros.HasFailedSave);
        return true;
    }, CancellationToken.None);

    [Fact]
    public Task Discarding_a_macro_conflict_requires_confirmation_and_a_successful_reload() => HeadlessApp.Session.Dispatch<bool>(async () =>
    {
        var main = Workspace();
        var saved = new MacroLibrary([Macro.Create("Saved elsewhere", [])]);
        int reads = 0;
        var macros = new MacrosViewModel(main.Client, main)
        {
            ReadLibraryAsync = () => ++reads == 1 ? Task.FromException<MacroLibrary>(new IOException("offline")) : Task.FromResult(saved),
            WriteLibraryAsync = _ => Task.FromException(new SettingsConflictException()),
        };
        macros.NewMacroCommand.Execute(null);
        await macros.PendingSave;
        var draft = Assert.Single(macros.Library.Macros);
        Assert.True(macros.CanDiscardFailedSave);
        Assert.False(macros.RetrySaveCommand.CanExecute(null));

        macros.RequestDiscardSaveCommand.Execute(null);
        Assert.True(macros.IsDiscardConfirmationVisible);
        Assert.Equal(0, reads);
        macros.CancelDiscardSaveCommand.Execute(null);
        Assert.Same(draft, Assert.Single(macros.Library.Macros));

        macros.RequestDiscardSaveCommand.Execute(null);
        await macros.ConfirmDiscardSaveCommand.ExecuteAsync(null);
        Assert.True(macros.HasFailedSave);
        Assert.Same(draft, Assert.Single(macros.Library.Macros));
        Assert.Contains("offline", macros.SaveStatus!);

        macros.RequestDiscardSaveCommand.Execute(null);
        await macros.ConfirmDiscardSaveCommand.ExecuteAsync(null);
        Assert.False(macros.HasFailedSave);
        Assert.Same(saved, macros.Library);
        return true;
    }, CancellationToken.None);

    [Fact]
    public Task Shortcuts_retry_the_current_library_after_a_failed_save() => HeadlessApp.Session.Dispatch<bool>(async () =>
    {
        var writes = new List<ShortcutList>();
        var first = GlobalShortcut.Create([30, 48], new KeyboardBinding(46));
        var second = GlobalShortcut.Create([30, 46], new KeyboardBinding(48));
        var original = new ShortcutList([first, second]);
        var shortcuts = new ShortcutsViewModel(Workspace())
        {
            ReadShortcutsAsync = () => Task.FromResult(original),
            WriteShortcutsAsync = list =>
            {
                writes.Add(list);
                return writes.Count == 1 ? Task.FromException(new IOException("disk unavailable")) : Task.CompletedTask;
            },
        };
        await shortcuts.LoadAsync();
        shortcuts.DeleteCommand.Execute(shortcuts.Rows[0]);
        await shortcuts.PendingSave;
        Assert.True(shortcuts.HasFailedSave);
        await shortcuts.LoadAsync();
        Assert.Equal(second.Id, Assert.Single(shortcuts.Rows).Shortcut.Id);

        shortcuts.RetrySaveCommand.Execute(null);
        await shortcuts.PendingSave;
        Assert.False(shortcuts.HasFailedSave);
        Assert.Equal(2, writes.Count);
        Assert.Same(writes[0], writes[1]);
        Assert.Equal(second, Assert.Single(writes[1].Shortcuts));
        return true;
    }, CancellationToken.None);

    [Fact]
    public Task A_shortcut_conflict_can_be_discarded_to_reload_the_saved_list() => HeadlessApp.Session.Dispatch<bool>(async () =>
    {
        var first = GlobalShortcut.Create([30, 48], new KeyboardBinding(46));
        var second = GlobalShortcut.Create([30, 46], new KeyboardBinding(48));
        var original = new ShortcutList([first, second]);
        var newer = new ShortcutList([first]);
        int reads = 0;
        var shortcuts = new ShortcutsViewModel(Workspace())
        {
            ReadShortcutsAsync = () => Task.FromResult(++reads == 1 ? original : newer),
            WriteShortcutsAsync = _ => Task.FromException(new SettingsConflictException()),
        };
        await shortcuts.LoadAsync();
        shortcuts.DeleteCommand.Execute(shortcuts.Rows[0]);
        await shortcuts.PendingSave;
        Assert.Equal(second.Id, Assert.Single(shortcuts.Rows).Shortcut.Id);

        shortcuts.RequestDiscardSaveCommand.Execute(null);
        Assert.Equal(1, reads);
        await shortcuts.ConfirmDiscardSaveCommand.ExecuteAsync(null);

        Assert.False(shortcuts.HasFailedSave);
        Assert.Equal(first.Id, Assert.Single(shortcuts.Rows).Shortcut.Id);
        return true;
    }, CancellationToken.None);

    [Fact]
    public Task Sign_in_updates_finish_in_order_and_a_failure_allows_retry() => HeadlessApp.Session.Dispatch<bool>(async () =>
    {
        var writes = new List<(bool Enabled, bool Minimized)>();
        var firstWrite = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var model = new SettingsViewModel(new AppPreferences(), false)
        {
            WriteAutostartAsync = (enabled, minimized) =>
            {
                writes.Add((enabled, minimized));
                return writes.Count switch
                {
                    1 => firstWrite.Task,
                    2 => Task.FromException(new IOException("cannot change start at sign-in")),
                    _ => Task.CompletedTask,
                };
            },
        };
        model.StartAtLogin = true;
        model.StartAtLogin = false;
        Assert.Equal((true, true), Assert.Single(writes));
        firstWrite.SetResult();
        await model.PendingSave.WaitAsync(TimeSpan.FromSeconds(3));
        Assert.True(model.HasFailedSave);

        model.RetrySaveCommand.Execute(null);
        await model.PendingSave.WaitAsync(TimeSpan.FromSeconds(3));
        Assert.False(model.HasFailedSave);
        Assert.Equal(new[] { (true, true), (false, true), (false, true) }, writes);
        return true;
    }, CancellationToken.None);
}
