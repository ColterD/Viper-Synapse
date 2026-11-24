using ViperSynapse.App.Services;
using ViperSynapse.App.ViewModels;
using ViperSynapse.Core.Bindings;
using ViperSynapse.Core.Lighting;
using ViperSynapse.Core.Protocol;

namespace ViperSynapse.Tests;

public sealed class EditingRecoveryTests
{
    static MainViewModel Workspace() => new(new DaemonClient(), new AppPreferences(Notifications: false), false);

    static DeviceInfo Info(ushort[]? keys = null, string model = "ffff", bool known = false) => new(model, model, "Emulated", known ? "mouse" : "unknown", "usb-test",
        known, false, null, null, null, [], false, null, keys ?? [30, 48], "available");

    static DeviceViewModel Device(MainViewModel main, ushort[]? keys = null, string model = "ffff", bool known = false) => new(Info(keys, model, known), main);

    [Fact]
    public Task A_macro_assignment_requires_a_new_target_after_its_device_disconnects() => HeadlessApp.Session.Dispatch<bool>(async () =>
    {
        var main = new MainViewModel(new DaemonClient(), new AppPreferences(Notifications: false), false)
        {
            ReadDeviceSettingsAsync = _ => Task.FromResult(DeviceSettings.Create()),
            ReadLiveDeviceAsync = _ => Task.FromResult<DeviceState?>(null),
        };
        var originalInfo = Info();
        var otherInfo = Info(model: "fffe");
        await main.RefreshDevicesAsync(() => Task.FromResult<IReadOnlyList<DeviceInfo>>([originalInfo, otherInfo]), reloadExisting: true);
        var original = main.Devices.Single(device => device.Key == originalInfo.Key);
        var other = main.Devices.Single(device => device.Key == otherInfo.Key);
        var macros = main.Macros;
        macros.SelectedPage = "KEY BINDS";
        macros.StartAssigningCommand.Execute(null);
        var macro = Macro.Create("Test macro", []);
        macros.AssignMacro = macro;
        macros.AssignPlayback = BindingEditorViewModel.Playbacks[1];
        Assert.Same(original, macros.AssignDevice);
        Assert.True(macros.AssignCommand.CanExecute(null));

        main.Show(main.Settings);
        await main.RefreshDevicesAsync(() => Task.FromResult<IReadOnlyList<DeviceInfo>>([otherInfo]));
        Assert.Same(other, Assert.Single(main.Devices));
        Assert.True(macros.Assigning);
        Assert.Null(macros.AssignDevice);
        Assert.Null(macros.AssignKey);
        Assert.Same(macro, macros.AssignMacro);
        Assert.Equal(BindingEditorViewModel.Playbacks[1], macros.AssignPlayback);
        Assert.False(macros.AssignCommand.CanExecute(null));
        Assert.Same(other, Assert.Single(macros.Devices));
        return true;
    }, CancellationToken.None);

    [Theory]
    [InlineData("same")]
    [InlineData("profile")]
    [InlineData("keymap")]
    [InlineData("layer")]
    [InlineData("source")]
    public Task A_new_binding_save_only_retires_failures_for_the_same_target(string change) => HeadlessApp.Session.Dispatch<bool>(async () =>
    {
        bool reject = true;
        var main = new MainViewModel(new DaemonClient(), new AppPreferences(Notifications: false), false)
        {
            WriteDeviceSettingsAsync = (_, _) => reject ? Task.FromException(new IOException("storage unavailable")) : Task.CompletedTask,
        };
        var device = Device(main);
        main.Tabs.Add(device);
        var first = Profile.Create("First") with { Keymaps = [Keymap.Empty("Primary"), Keymap.Empty("Secondary")] };
        var other = Profile.Create("Other");
        await device.ReloadSettingsAsync(_ => Task.FromResult(new DeviceSettings([first, other], first.Id, 0)));
        var rows = device.Rows.Where(row => row.Bindable).ToArray();

        async Task BindAsync(KeyRowViewModel row, ushort key)
        {
            device.SelectedKey = row;
            var editor = Assert.IsType<BindingEditorViewModel>(device.Editor);
            editor.SelectedFunction = BindingEditorViewModel.Functions.Single(entry => entry.Kind == FunctionKind.Keyboard);
            editor.CapturedKey = key;
            editor.SaveCommand.Execute(null);
            await main.DeviceSaveState(device.Key).Completion;
        }

        await BindAsync(rows[0], 46);
        long failedRevision = Assert.IsType<DeviceSaveFeedback>(main.GetBindingFailure(device.Key)).Revision;
        device.SelectedKey = null;
        reject = false;
        if (change == "profile")
            device.SelectedProfile = device.Profiles.Single(choice => choice.Id == other.Id);
        if (change == "keymap")
            device.SelectedKeymap = 1;
        if (change == "layer")
            device.Hypershift = true;
        await BindAsync(rows[change == "source" ? 1 : 0], 47);

        Assert.Null(device.Editor);
        if (change == "same")
            Assert.Null(main.GetBindingFailure(device.Key));
        else
            Assert.Equal(failedRevision, main.GetBindingFailure(device.Key)?.Revision);
        return true;
    }, CancellationToken.None);

    [Fact]
    public Task An_edited_managed_dpi_row_requires_matching_confirmed_coordinates() => HeadlessApp.Session.Dispatch<bool>(() =>
    {
        var performance = Device(Workspace(), model: "0053", known: true).Performance;
        Assert.True(performance.SupportsManagedDpiSteps);
        performance.Load(new PerformanceSettings([new(600, 650), new(600, 650)], 0, 500) { ManagedDpiSteps = true });
        var row = performance.Stages[0];
        var displayed = new List<bool>();
        row.PropertyChanged += (_, change) =>
        {
            if (change.PropertyName == nameof(StageRow.DisplayActive))
                displayed.Add(row.DisplayActive);
        };

        performance.ConfirmedDpiStep = 0;
        Assert.False(row.DisplayActive);
        performance.LastConfirmedManagedDpi = new(600, 650);
        Assert.True(row.DisplayActive);
        Assert.False(performance.Stages[1].DisplayActive);
        row.X = 700;
        Assert.False(row.DisplayActive);
        performance.LastConfirmedManagedDpi = new(700, 650);
        Assert.True(row.DisplayActive);
        row.Y = 750;
        Assert.False(row.DisplayActive);
        Assert.Equal(new[] { false, true, false, true, false }, displayed);
        performance.TakePendingSettings();
        return true;
    }, CancellationToken.None);

    [Theory]
    [InlineData(false, false)]
    [InlineData(false, true)]
    [InlineData(true, false)]
    [InlineData(true, true)]
    public Task A_profile_refresh_only_waits_for_failures_affecting_the_saved_settings(bool binding, bool conflict) =>
        HeadlessApp.Session.Dispatch<bool>(async () =>
        {
            var main = new MainViewModel(new DaemonClient(), new AppPreferences(Notifications: false), false)
            {
                WriteDeviceSettingsAsync = (_, _) => Task.FromException(conflict ? new SettingsConflictException() : new IOException("storage unavailable")),
            };
            var device = Device(main);
            var initial = DeviceSettings.Create();
            await device.ReloadSettingsAsync(_ => Task.FromResult(initial));
            if (binding)
            {
                device.SelectedKey = device.Rows.First(row => row.Bindable);
                device.Editor!.SelectedFunction = BindingEditorViewModel.Functions.Single(entry => entry.Kind == FunctionKind.Disable);
                device.Editor.SaveCommand.Execute(null);
            }
            else
                device.AddProfileCommand.Execute(null);
            await main.DeviceSaveState(device.Key).Completion;

            var newer = initial with { Profiles = [initial.Active with { Name = "New name from service" }] };
            int reads = 0;
            await device.ReloadSettingsAsync(_ => { reads++; return Task.FromResult(newer); });
            Assert.Equal(binding && !conflict ? 1 : 0, reads);
            if (binding && !conflict)
            {
                Assert.Equal("New name from service", device.CurrentSettings.Active.Name);
                Assert.NotNull(device.Editor?.SaveError);
            }
            else if (!binding)
                Assert.Equal(2, device.CurrentSettings.Profiles.Count);
            else
                Assert.NotNull(device.Editor?.Conflict);
            return true;
        }, CancellationToken.None);

    [Theory]
    [InlineData(false, false)]
    [InlineData(false, true)]
    [InlineData(true, false)]
    [InlineData(true, true)]
    public Task Managed_dpi_selection_respects_failures_affecting_the_saved_settings(bool binding, bool conflict) =>
        HeadlessApp.Session.Dispatch<bool>(async () =>
        {
            var info = Info([2, 0x119], model: "0053", known: true) with { Connection = "wired", Serial = "emulated" };
            var selections = new List<(DeviceInfo Unit, Guid Profile, int Step, DpiStage Dpi)>();
            var main = new MainViewModel(new DaemonClient(), new AppPreferences(Notifications: false), false)
            {
                WriteDeviceSettingsAsync = (_, _) => Task.FromException(conflict ? new SettingsConflictException() : new IOException("storage unavailable")),
                SelectManagedDpiStepAsync = (unit, profile, step, dpi) =>
                {
                    selections.Add((unit, profile, step, dpi));
                    return Task.FromResult<DeviceState?>(null);
                },
                ReadLiveDeviceAsync = _ => Task.FromResult<DeviceState?>(null),
            };
            var device = new DeviceViewModel(info, main);
            var dpi = new DpiStage(1200, 1250);
            var profile = Profile.Create("Mouse") with
            {
                Performance = new PerformanceSettings([new(600, 650), dpi], 0, 500) { ManagedDpiSteps = true },
            };
            await device.ReloadSettingsAsync(_ => Task.FromResult(new DeviceSettings([profile], profile.Id, 0)));
            if (binding)
            {
                device.SelectedKey = device.Rows.First(row => row.Bindable);
                device.Editor!.SelectedFunction = BindingEditorViewModel.Functions.Single(entry => entry.Kind == FunctionKind.Disable);
                device.Editor.SaveCommand.Execute(null);
            }
            else
                device.Performance.PollingRate = 1000;

            await device.ActivateDpiStageAsync(device.Performance.Stages[1]);
            if (binding && !conflict)
                Assert.Equal((info, profile.Id, 1, dpi), Assert.Single(selections));
            else
                Assert.Empty(selections);
            return true;
        }, CancellationToken.None);

    [Fact]
    public Task A_later_hardware_apply_failure_can_be_retried_alongside_a_retained_binding() => HeadlessApp.Session.Dispatch<bool>(async () =>
    {
        var unit = Info() with { Serial = "emulated" };
        var failed = new UnitApplyOutcome(unit.Port, unit.ProductId, unit.Serial, UnitApplyStatus.Failed, "DPI report failed");
        var retried = new List<(string Key, UnitApplyOutcome Outcome)>();
        int commits = 0;
        var main = new MainViewModel(new DaemonClient(), new AppPreferences(Notifications: false), false)
        {
            CommitDeviceSettingsAsync = (_, _, _) => ++commits == 1
                ? Task.FromException<SettingsCommitResult>(new IOException("storage unavailable"))
                : Task.FromResult(new SettingsCommitResult("saved", true, [failed])),
            RetryUnitApplyAsync = (key, outcome) =>
            {
                retried.Add((key, outcome));
                return Task.FromResult(outcome with { Status = UnitApplyStatus.Acknowledged, Detail = null });
            },
        };
        var device = new DeviceViewModel(unit, main);
        main.Tabs.Add(device);
        await device.ReloadSettingsAsync(_ => Task.FromResult(DeviceSettings.Create()));
        device.SelectedKey = device.Rows.First(row => row.Bindable);
        var editor = Assert.IsType<BindingEditorViewModel>(device.Editor);
        editor.SelectedFunction = BindingEditorViewModel.Functions.Single(entry => entry.Kind == FunctionKind.Disable);
        editor.SaveCommand.Execute(null);
        await main.DeviceSaveState(device.Key).Completion;
        device.AddProfileCommand.Execute(null);
        await main.DeviceSaveState(device.Key).Completion;

        Assert.Contains("DPI report failed", device.SaveStatus);
        Assert.NotNull(editor.SaveError);
        var failure = Assert.IsType<DeviceSaveFeedback>(main.GetBindingFailure(device.Key));
        device.SelectedKey = null;
        Assert.True(device.CanRetrySave);
        Assert.True(device.CanRetryHardwareApply);
        await device.RetryHardwareApplyCommand.ExecuteAsync(null);
        Assert.Equal((device.Key, failed), Assert.Single(retried));
        Assert.Equal(2, commits);
        Assert.Same(failure, main.GetBindingFailure(device.Key));
        Assert.True(device.CanRetrySave);
        Assert.False(device.CanRetryHardwareApply);
        Assert.DoesNotContain("DPI report failed", device.SaveStatus);
        return true;
    }, CancellationToken.None);

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
        Assert.False(row.Changed);
        write.SetException(new IOException("disk unavailable"));
        await rejected.Task.WaitAsync(TimeSpan.FromSeconds(3));

        Assert.Same(editor, device.Editor);
        Assert.False(editor.IsSaving);
        Assert.Equal(46, editor.CapturedKey);
        Assert.Equal("Couldn't save this binding. Your draft is still open.", editor.SaveError);
        Assert.False(row.Changed);
        Assert.Null(main.Problem);
        Assert.False(device.CanRetrySave);

        editor.CapturedKey = 47;
        editor.SaveCommand.Execute(null);
        await main.FinishDeviceEditsAsync(TimeSpan.FromSeconds(3));
        Assert.Equal(2, writes.Count);
        Assert.Equal(new KeyboardBinding(47), writes[1].Active.Keymaps[0].Standard[row.Key.Source]);
        return true;
    }, CancellationToken.None);

    [Fact]
    public Task Cancelling_a_failed_binding_discards_its_retry_without_clearing_another_problem() =>
        HeadlessApp.Session.Dispatch<bool>(async () =>
        {
            var main = new MainViewModel(new DaemonClient(), new AppPreferences(Notifications: false), false)
            {
                WriteDeviceSettingsAsync = (_, _) => Task.FromException(new IOException("write rejected")),
            };
            var device = Device(main);
            await device.ReloadSettingsAsync(_ => Task.FromResult(DeviceSettings.Create()));
            var row = device.Rows.First(item => item.Bindable);
            device.SelectedKey = row;
            var editor = Assert.IsType<BindingEditorViewModel>(device.Editor);
            editor.SelectedFunction = BindingEditorViewModel.Functions.Single(function => function.Kind == FunctionKind.Disable);
            var rejected = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            editor.PropertyChanged += (_, change) =>
            {
                if (change.PropertyName == nameof(BindingEditorViewModel.SaveError) && editor.SaveError != null)
                    rejected.TrySetResult();
            };

            main.Problem = "An unrelated warning";
            editor.SaveCommand.Execute(null);
            await rejected.Task.WaitAsync(TimeSpan.FromSeconds(3));
            editor.CancelCommand.Execute(null);

            Assert.Null(device.Editor);
            Assert.False(row.Changed);
            Assert.False(device.CanRetrySave);
            Assert.Equal("An unrelated warning", main.Problem);
            Assert.True(await main.FinishWorkspaceEditsAsync(TimeSpan.FromSeconds(3)));
            return true;
        }, CancellationToken.None);

    [Fact]
    public Task A_binding_save_completion_is_independent_of_a_newer_queued_device_edit() => HeadlessApp.Session.Dispatch<bool>(async () =>
    {
        var first = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var next = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var writes = new List<DeviceSettings>();
        var main = new MainViewModel(new DaemonClient(), new AppPreferences(Notifications: false), false)
        {
            WriteDeviceSettingsAsync = (_, settings) =>
            {
                writes.Add(settings);
                return writes.Count == 1 ? first.Task : next.Task;
            },
        };
        var device = Device(main);
        var original = DeviceSettings.Create();
        await device.ReloadSettingsAsync(_ => Task.FromResult(original));
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

        device.ResetKeymapCommand.Execute(null);
        string? prompt = device.ConfirmationMessage;
        Assert.NotNull(prompt);
        editor.SaveCommand.Execute(null);
        Assert.False(device.ResetKeymapCommand.CanExecute(null));
        Assert.False(device.ConfirmProfileChangeCommand.CanExecute(null));
        device.ConfirmProfileChangeCommand.Execute(null);
        Assert.Equal(prompt, device.ConfirmationMessage);
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
        Assert.Equal(2, writes.Count);
        Assert.NotEqual(original.ActiveProfile, writes[1].ActiveProfile);
        Assert.Equal(new KeyboardBinding(46), writes[1].Profiles.Single(p => p.Id == original.ActiveProfile)
            .Keymaps[0].Standard[editor.Row.Key.Source]);
        Assert.True(device.ResetKeymapCommand.CanExecute(null));
        Assert.True(device.ConfirmProfileChangeCommand.CanExecute(null));
        device.CancelProfileChangeCommand.Execute(null);
        return true;
    }, CancellationToken.None);

    [Fact]
    public Task A_stale_queued_binding_requires_confirmed_reload_after_closing() => HeadlessApp.Session.Dispatch<bool>(async () =>
    {
        var first = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var persisted = DeviceSettings.Create();
        var reads = 0;
        bool rejectReload = false;
        TaskCompletionSource<DeviceSettings>? delayedReload = null;
        var writes = 0;
        var main = new MainViewModel(new DaemonClient(), new AppPreferences(Notifications: false), false)
        {
            WriteDeviceSettingsAsync = async (_, snapshot) =>
            {
                ++writes;
                await first.Task;
                persisted = snapshot;
            },
            ReadDeviceSettingsAsync = _ =>
            {
                ++reads;
                return rejectReload ? Task.FromException<DeviceSettings>(new IOException("read unavailable")) :
                    delayedReload?.Task ?? Task.FromResult(persisted);
            },
        };
        var device = Device(main);
        await device.ReloadSettingsAsync(main.ReadDeviceSettingsAsync);
        var row = device.Rows.First(entry => entry.Bindable);
        device.SelectedKey = row;
        var older = device.Editor!;
        older.SelectedFunction = BindingEditorViewModel.Functions.Single(entry => entry.Kind == FunctionKind.Keyboard);
        older.CapturedKey = 46;
        older.SaveCommand.Execute(null);

        device.SelectedKey = null;
        device.SelectedKey = row;
        var newer = device.Editor!;
        var conflict = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        newer.PropertyChanged += (_, change) =>
        {
            if (change.PropertyName == nameof(BindingEditorViewModel.Conflict) && newer.Conflict != null)
                conflict.TrySetResult();
        };
        newer.SelectedFunction = BindingEditorViewModel.Functions.Single(entry => entry.Kind == FunctionKind.Keyboard);
        newer.CapturedKey = 47;
        newer.SaveCommand.Execute(null);
        first.SetResult();
        await conflict.Task.WaitAsync(TimeSpan.FromSeconds(3));

        Assert.Equal(1, writes);
        Assert.Equal(47, newer.CapturedKey);
        Assert.False(newer.CanSave);
        device.Lighting.LightingOn = !device.Lighting.LightingOn;
        device.Performance.PollingRate = 500;
        Assert.True(device.Lighting.HasPendingEdits);
        Assert.True(device.Performance.HasPendingEdits);
        newer.CancelCommand.Execute(null);
        Assert.Null(device.Editor);
        Assert.Equal(1, reads);
        Assert.True(device.Lighting.HasPendingEdits);
        Assert.True(device.Performance.HasPendingEdits);
        Assert.True(device.CanReloadSave);

        device.SelectedKey = row;
        Assert.False(device.Editor!.CanSave);
        Assert.NotNull(device.Editor.Conflict);
        device.Editor.CancelCommand.Execute(null);
        device.RequestReloadSaveCommand.Execute(null);
        device.CancelProfileChangeCommand.Execute(null);
        Assert.True(device.Lighting.HasPendingEdits);
        Assert.True(device.Performance.HasPendingEdits);
        Assert.Equal(1, reads);

        async Task ConfirmReloadAsync(Action? whileReading = null)
        {
            var reloaded = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            void ReloadChanged(object? sender, System.ComponentModel.PropertyChangedEventArgs change)
            {
                if (change.PropertyName == nameof(DeviceViewModel.ReloadingSave) && !device.ReloadingSave)
                    reloaded.TrySetResult();
            }
            device.PropertyChanged += ReloadChanged;
            try
            {
                device.RequestReloadSaveCommand.Execute(null);
                device.ConfirmProfileChangeCommand.Execute(null);
                whileReading?.Invoke();
                await reloaded.Task.WaitAsync(TimeSpan.FromSeconds(3));
            }
            finally { device.PropertyChanged -= ReloadChanged; }
        }

        rejectReload = true;
        await ConfirmReloadAsync();
        Assert.Equal(2, reads);
        Assert.True(device.CanReloadSave);
        Assert.Contains("Your edits are still here", device.SaveStatus);
        Assert.True(device.Lighting.HasPendingEdits);
        Assert.True(device.Performance.HasPendingEdits);

        rejectReload = false;
        await main.SaveDeviceSettings(device.Key, device.CurrentSettings);
        Assert.True(device.CanReloadSave);
        delayedReload = new(TaskCreationOptions.RunContinuationsAsynchronously);
        await ConfirmReloadAsync(() =>
        {
            device.Lighting.LightingOn = !device.Lighting.LightingOn;
            delayedReload.SetResult(persisted);
        });
        Assert.Equal(3, reads);
        Assert.Contains("Profile edits changed", device.SaveStatus);
        Assert.True(device.Lighting.HasPendingEdits);

        delayedReload = null;
        await ConfirmReloadAsync();
        Assert.Equal(4, reads);
        Assert.False(device.Lighting.HasPendingEdits);
        Assert.False(device.Performance.HasPendingEdits);
        Assert.False(device.CanReloadSave);
        Assert.Equal(new KeyboardBinding(46), device.CurrentSettings.ActiveKeymapOrFirst.Resolve(row.Key.Source, false));
        return true;
    }, CancellationToken.None);

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public Task An_unrelated_same_model_save_does_not_clear_a_failed_binding(bool queueBeforeFailure) => HeadlessApp.Session.Dispatch<bool>(async () =>
    {
        var first = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var ordinary = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        int attempts = 0;
        var main = new MainViewModel(new DaemonClient(), new AppPreferences(Notifications: false), false)
        {
            WriteDeviceSettingsAsync = (_, _) => ++attempts switch { 1 => first.Task, 2 => ordinary.Task, _ => Task.CompletedTask },
        };
        var device = Device(main);
        main.Tabs.Add(device);
        main.Show(device);
        await device.ReloadSettingsAsync(_ => Task.FromResult(DeviceSettings.Create()));
        device.SelectedKey = device.Rows.First(row => row.Bindable);
        var editor = device.Editor!;
        editor.SelectedFunction = BindingEditorViewModel.Functions.Single(entry => entry.Kind == FunctionKind.Disable);
        editor.SaveCommand.Execute(null);
        var bindingSave = main.DeviceSaveState(device.Key).Completion;
        if (queueBeforeFailure)
            device.AddProfileCommand.Execute(null);
        main.Show(main.Settings);
        first.SetException(new IOException("write unavailable"));
        await bindingSave;
        if (!queueBeforeFailure)
            device.AddProfileCommand.Execute(null);
        Assert.Same(device, Assert.Single(main.DeviceSaveNotices).Device);
        ordinary.SetResult();
        await main.FinishDeviceEditsAsync(TimeSpan.FromSeconds(3));
        Assert.NotNull(editor.SaveError);
        var notice = Assert.Single(main.DeviceSaveNotices);
        Assert.Same(device, notice.Device);
        main.OpenDeviceSaveCommand.Execute(notice.Device);
        editor.SaveCommand.Execute(null);
        await main.FinishDeviceEditsAsync(TimeSpan.FromSeconds(3));
        main.Show(main.Settings);
        Assert.Null(device.Editor);
        Assert.Empty(main.DeviceSaveNotices);
        return true;
    }, CancellationToken.None);

    [Fact]
    public Task Cancelling_a_failed_binding_keeps_an_earlier_failed_profile_edit() => HeadlessApp.Session.Dispatch<bool>(async () =>
    {
        bool reject = true;
        DeviceSettings? persisted = null;
        var main = new MainViewModel(new DaemonClient(), new AppPreferences(Notifications: false), false)
        {
            WriteDeviceSettingsAsync = (_, settings) =>
            {
                if (reject)
                    return Task.FromException(new IOException("storage unavailable"));
                persisted = settings;
                return Task.CompletedTask;
            },
        };
        var device = Device(main);
        main.Tabs.Add(device);
        await device.ReloadSettingsAsync(_ => Task.FromResult(DeviceSettings.Create()));
        device.AddProfileCommand.Execute(null);
        await main.DeviceSaveState(device.Key).Completion;
        device.SelectedKey = device.Rows.First(row => row.Bindable);
        var editor = device.Editor!;
        editor.SelectedFunction = BindingEditorViewModel.Functions.Single(entry => entry.Kind == FunctionKind.Disable);
        editor.SaveCommand.Execute(null);
        await main.DeviceSaveState(device.Key).Completion;
        Assert.NotNull(editor.SaveError);
        editor.CancelCommand.Execute(null);

        Assert.True(device.CanRetrySave);
        Assert.False(await main.FinishWorkspaceEditsAsync(TimeSpan.FromSeconds(3)));
        reject = false;
        await device.RetrySaveCommand.ExecuteAsync(null);
        Assert.Equal(2, persisted!.Profiles.Count);
        Assert.True(await main.FinishWorkspaceEditsAsync(TimeSpan.FromSeconds(3)));
        return true;
    }, CancellationToken.None);

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public Task Retrying_a_binding_without_its_editor_preserves_newer_profile_edits(bool failProfile) => HeadlessApp.Session.Dispatch<bool>(async () =>
    {
        var original = DeviceSettings.Create();
        var saved = original;
        var pending = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        TaskCompletionSource? recovering = null;
        int attempts = 0;
        var main = new MainViewModel(new DaemonClient(), new AppPreferences(Notifications: false), false)
        {
            CommitDeviceSettingsAsync = async (_, edited, baseline) =>
            {
                if (!ReferenceEquals(saved, baseline))
                    throw new SettingsConflictException();
                int attempt = ++attempts;
                await pending.Task;
                if (recovering != null)
                    await recovering.Task;
                if (attempt == 1 || attempt == 2 && failProfile)
                    throw new IOException("storage unavailable");
                saved = edited;
                return new SettingsCommitResult("saved", true);
            },
        };
        var device = Device(main);
        main.Tabs.Add(device);
        await device.ReloadSettingsAsync(_ => Task.FromResult(original));
        var row = device.Rows.First(item => item.Bindable);
        device.SelectedKey = row;
        device.Editor!.SelectedFunction = BindingEditorViewModel.Functions.Single(entry => entry.Kind == FunctionKind.Disable);
        device.Editor.SaveCommand.Execute(null);
        device.SelectedKey = null;
        device.AddProfileCommand.Execute(null);
        pending.SetResult();
        await main.DeviceSaveState(device.Key).Completion;

        if (failProfile)
        {
            Assert.True(device.CanRetrySave);
            await device.RetrySaveCommand.ExecuteAsync(null);
            Assert.Equal(2, saved.Profiles.Count);
            Assert.IsType<DefaultBinding>(saved.Profiles[0].Keymaps[0].Resolve(row.Key.Source, false));
        }
        Assert.True(device.CanRetrySave);
        device.ResetKeymapCommand.Execute(null);
        var prompt = device.ConfirmationMessage;
        Assert.NotNull(prompt);
        recovering = new(TaskCreationOptions.RunContinuationsAsynchronously);
        var recovery = device.RetrySaveCommand.ExecuteAsync(null);
        Assert.False(device.ResetKeymapCommand.CanExecute(null));
        Assert.False(device.ConfirmProfileChangeCommand.CanExecute(null));
        device.ConfirmProfileChangeCommand.Execute(null);
        Assert.Equal(prompt, device.ConfirmationMessage);
        recovering.SetResult();
        await recovery;
        Assert.True(device.ConfirmProfileChangeCommand.CanExecute(null));
        Assert.Equal(2, saved.Profiles.Count);
        Assert.IsType<DisableBinding>(saved.Profiles[0].Keymaps[0].Resolve(row.Key.Source, false));
        Assert.Null(main.GetSaveFeedback(device.Key)?.Error);
        return true;
    }, CancellationToken.None);

    [Fact]
    public Task A_reconnected_tab_can_load_settings_and_recover_a_failed_binding() => HeadlessApp.Session.Dispatch<bool>(async () =>
    {
        var profile = Profile.Create("Saved profile");
        var original = new DeviceSettings([profile], profile.Id, 0);
        bool reject = true;
        var main = new MainViewModel(new DaemonClient(), new AppPreferences(Notifications: false), false)
        {
            WriteDeviceSettingsAsync = (_, _) => reject ? Task.FromException(new IOException("storage unavailable")) : Task.CompletedTask,
        };
        var oldDevice = Device(main);
        main.Tabs.Add(oldDevice);
        await oldDevice.ReloadSettingsAsync(_ => Task.FromResult(original));
        var row = oldDevice.Rows.First(item => item.Bindable);
        oldDevice.SelectedKey = row;
        oldDevice.Editor!.SelectedFunction = BindingEditorViewModel.Functions.Single(entry => entry.Kind == FunctionKind.Disable);
        oldDevice.Editor.SaveCommand.Execute(null);
        await main.DeviceSaveState(oldDevice.Key).Completion;
        oldDevice.Detach();
        main.Tabs.Remove(oldDevice);

        var replacement = Device(main);
        main.Tabs.Add(replacement);
        await replacement.ReadDeviceAsync(_ => Task.FromResult<DeviceState?>(new(null, 0, 500, null, null)));
        Assert.False(replacement.HasSoftwareSettings);
        Assert.NotEqual(profile.Id, replacement.CurrentSettings.ActiveProfile);
        Assert.True(replacement.CanRetrySave);
        main.Show(main.LinkedGames);
        main.LinkedGames.AddProgram("example.exe");
        Assert.False(main.LinkedGames.RemoveGameCommand.CanExecute(null));
        Assert.DoesNotContain(replacement, main.Macros.Devices);
        var save = main.DeviceSaveState(replacement.Key);
        replacement.Bind(replacement.Rows[0].Key, new KeyboardBinding(47));
        replacement.LinkProgram("example.exe", replacement.CurrentSettings.ActiveProfile);
        Assert.Equal(save, main.DeviceSaveState(replacement.Key));
        await replacement.ReloadSettingsAsync(_ => Task.FromResult(original));
        Assert.True(replacement.HasSoftwareSettings);
        Assert.Equal(profile.Id, replacement.CurrentSettings.ActiveProfile);
        Assert.True(replacement.CanRetrySave);
        replacement.Lighting.LightingOn = false;
        replacement.Performance.PollingRate = 1000;
        reject = false;
        await replacement.RetrySaveCommand.ExecuteAsync(null);
        Assert.True(replacement.HasSoftwareSettings);
        Assert.True(main.LinkedGames.RemoveGameCommand.CanExecute(null));
        Assert.Contains(replacement, main.Macros.Devices);
        Assert.Equal(profile.Id, replacement.CurrentSettings.ActiveProfile);
        Assert.IsType<DisableBinding>(replacement.CurrentSettings.ActiveKeymapOrFirst.Resolve(row.Key.Source, false));
        Assert.False(replacement.Lighting.LightingOn);
        Assert.Equal(1000, replacement.Performance.PollingRate);
        await main.FinishDeviceEditsAsync(TimeSpan.FromSeconds(3));
        return true;
    }, CancellationToken.None);

    [Fact]
    public Task A_queued_edit_can_use_a_projected_baseline_after_the_previous_write_failed() => HeadlessApp.Session.Dispatch<bool>(async () =>
    {
        var first = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var original = DeviceSettings.Create();
        var revisions = new Dictionary<DeviceSettings, int>(ReferenceEqualityComparer.Instance) { [original] = 0 };
        var saved = original;
        int attempts = 0, revision = 0;
        var main = new MainViewModel(new DaemonClient(), new AppPreferences(Notifications: false), false)
        {
            CommitDeviceSettingsAsync = async (_, edited, baseline) =>
            {
                if (baseline == null || !revisions.TryGetValue(baseline, out int expected) || expected != revision)
                    throw new SettingsConflictException();
                revisions[edited] = expected;
                int attempt = ++attempts;
                await first.Task;
                if (attempt == 2)
                    throw new IOException("one failed save");
                saved = edited;
                revisions[edited] = ++revision;
                return new SettingsCommitResult(revision.ToString(System.Globalization.CultureInfo.InvariantCulture), true);
            },
        };
        var device = Device(main);
        await device.ReloadSettingsAsync(_ => Task.FromResult(original));
        var row = device.Rows.First(item => item.Bindable);
        device.SelectedKey = row;
        device.Editor!.SelectedFunction = BindingEditorViewModel.Functions.Single(entry => entry.Kind == FunctionKind.Disable);
        device.Editor.SaveCommand.Execute(null);
        device.AddProfileCommand.Execute(null);
        device.AddProfileCommand.Execute(null);
        first.SetResult();
        await main.FinishDeviceEditsAsync(TimeSpan.FromSeconds(3));
        Assert.Equal(3, attempts);
        Assert.Equal(3, saved.Profiles.Count);
        Assert.IsType<DisableBinding>(saved.Profiles[0].Keymaps[0].Resolve(row.Key.Source, false));
        Assert.Null(main.GetSaveFeedback(device.Key)?.Error);
        return true;
    }, CancellationToken.None);

    [Fact]
    public Task Save_notices_follow_the_affected_model_and_survive_an_unrelated_save() => HeadlessApp.Session.Dispatch<bool>(async () =>
    {
        var retry = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var successes = new HashSet<string>();
        var main = new MainViewModel(new DaemonClient(), new AppPreferences(Notifications: false), false)
        {
            WriteDeviceSettingsAsync = (key, _) => successes.Contains(key)
                ? retry.Task : Task.FromException(new IOException("write unavailable")),
        };
        var device = Device(main);
        main.Tabs.Add(device);
        main.Show(device);
        await main.SaveDeviceSettings(device.Key, DeviceSettings.Create());
        Assert.Empty(main.DeviceSaveNotices);

        main.Show(main.Settings);
        await main.SaveDeviceSettings("other-model", DeviceSettings.Create());
        var notice = Assert.Single(main.DeviceSaveNotices, item => item.CanOpen);
        Assert.Contains(device.ShortName, notice.Message);
        Assert.Equal(2, main.DeviceSaveNotices.Count);
        main.OpenDeviceSaveCommand.Execute(notice.Device);
        Assert.Same(device, main.SelectedTab);
        Assert.True(device.CanRetrySave);

        successes.Add(device.Key);
        var pending = device.RetrySaveCommand.ExecuteAsync(null);
        main.Show(main.Settings);
        Assert.Contains(main.DeviceSaveNotices, item => item.Key == device.Key);
        retry.SetResult();
        await pending;
        Assert.Equal("other-model", Assert.Single(main.DeviceSaveNotices).Key);
        return true;
    }, CancellationToken.None);

    [Fact]
    public Task An_edit_based_on_the_first_visible_binding_keeps_all_three_queued_bindings() =>
        HeadlessApp.Session.Dispatch<bool>(async () =>
        {
            var first = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            var second = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            var firstVisible = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            var writes = new List<DeviceSettings>();
            var main = new MainViewModel(new DaemonClient(), new AppPreferences(Notifications: false), false)
            {
                WriteDeviceSettingsAsync = (_, snapshot) =>
                {
                    writes.Add(snapshot);
                    return writes.Count switch { 1 => first.Task, 2 => second.Task, _ => Task.CompletedTask };
                },
            };
            var device = Device(main, [30, 48, 46]);
            var original = DeviceSettings.Create();
            await device.ReloadSettingsAsync(_ => Task.FromResult(original));
            var rows = device.Rows.Where(row => row.Bindable).ToArray();
            rows[0].PropertyChanged += (_, change) =>
            {
                if (change.PropertyName == nameof(KeyRowViewModel.Changed) && rows[0].Changed)
                    firstVisible.TrySetResult();
            };
            foreach (var row in rows)
            {
                device.SelectedKey = row;
                device.Editor!.SelectedFunction = BindingEditorViewModel.Functions.Single(entry => entry.Kind == FunctionKind.Disable);
                device.Editor.SaveCommand.Execute(null);
            }

            first.SetResult();
            try
            {
                await firstVisible.Task.WaitAsync(TimeSpan.FromSeconds(3));
                Assert.False(rows[1].Changed);
                device.AddProfileCommand.Execute(null);
            }
            finally { second.TrySetResult(); }
            await main.FinishDeviceEditsAsync(TimeSpan.FromSeconds(3));

            Assert.Equal(4, writes.Count);
            Assert.NotEqual(original.ActiveProfile, writes[3].ActiveProfile);
            var committed = writes[3].Profiles.Single(p => p.Id == original.ActiveProfile).Keymaps[0];
            Assert.All(rows, row => Assert.IsType<DisableBinding>(committed.Resolve(row.Key.Source, false)));
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
