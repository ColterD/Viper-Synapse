using System.Text.Json;
using Avalonia.Controls;
using Avalonia.Controls.Primitives;
using Avalonia.VisualTree;
using ViperSynapse.App.Services;
using ViperSynapse.App.ViewModels;
using ViperSynapse.App.Views;
using ViperSynapse.Core.Engine;
using ViperSynapse.Core.Lighting;
using ViperSynapse.Core.Protocol;
using ViperSynapse.Hardware;
using ViperSynapse.Service;
using DeviceInfo = ViperSynapse.App.Services.DeviceInfo;

namespace ViperSynapse.Tests;

public class OnboardPageTests
{
    [Theory]
    [InlineData(false, false, OnboardRestoreStatus.Restored)]
    [InlineData(true, false, OnboardRestoreStatus.RolledBack)]
    [InlineData(true, true, OnboardRestoreStatus.RecoveryRequired)]
    public Task The_page_saves_a_backup_and_requires_confirmation_for_restore(
        bool failRestore, bool failRollback, OnboardRestoreStatus expectedStatus) =>
        HeadlessApp.Session.Dispatch<bool>(async () =>
        {
            var directory = Directory.CreateTempSubdirectory("viper-gui-backup-");
            try
            {
                var firmware = new BasiliskBackupTests.BackupFirmware
                {
                    FailArchiveCommand = failRestore ? (byte)0x16 : null,
                    FailRollback = failRollback,
                    EffectResetsBrightness = true
                };
                firmware.Buttons[(0x39, 0)] = BasiliskOnboard.Keyboard(0x68, 1);
                firmware.Effects[0x04] = [0x01, 0, 0, 1, 1, 40, 100];
                firmware.Brightness[0x0A] = 93;
                firmware.Effects[0x0A] = [0x04, 1, 0x28, 0];
                using var manager = new DeviceManager(new SettingsStore(directory.FullName), new Sink());
                manager.Refresh([new("/dev/emulated", 0x1532, 0x00E3, 3, "Basilisk", "usb-gui/input3")], [],
                    _ => true, _ => new RazerTransport(firmware) { AnswerDelay = TimeSpan.Zero });
                IReadOnlyList<DeviceInfo> Devices() =>
                    JsonSerializer.Deserialize(manager.DevicesJson(), ClientJson.Default.ListDeviceInfo)!;

                string? file = null;
                OnboardRestoreResult? result = null;
                var onboard = new OnboardEditorViewModel(() => Task.FromResult(Devices()),
                    request => Task.FromResult(manager.ManageOnboard(request)),
                    request => Task.FromResult(manager.BackupOnboard(OnboardBackupCodec.ReadRequest(OnboardBackupCodec.WriteRequest(request)))),
                    request =>
                    {
                        var reply = manager.RestoreOnboard(OnboardBackupCodec.ReadRequest(OnboardBackupCodec.WriteRequest(request)));
                        var verified = OnboardBackupCodec.ReadResult(OnboardBackupCodec.WriteResult(reply));
                        result = verified;
                        return Task.FromResult(verified);
                    },
                    new OnboardBackupFiles(
                        archive =>
                        {
                            file = OnboardBackupCodec.WriteBackup(archive);
                            return Task.FromResult(true);
                        },
                        () => Task.FromResult(file == null ? null : OnboardBackupCodec.ReadBackup(file))));
                var main = new MainViewModel(new DaemonClient(), new AppPreferences(), false);
                var device = new DeviceViewModel(Assert.Single(Devices()), main, onboard);
                var view = new DeviceView { DataContext = device };
                var window = new Window { Width = 1350, Height = 900, Content = view };
                window.Show();
                try
                {
                    device.SelectedPage = DeviceViewModel.OnboardPage;
                    await Until(() => onboard.Slots.Count == 2 && !onboard.IsBusy);
                    var panel = Assert.Single(view.GetVisualDescendants().OfType<OnboardEditorView>());
                    T Find<T>(string name) where T : Control => Assert.IsType<T>(panel.FindControl<T>(name));

                    Click(Find<Button>("OnboardBackupButton"));
                    await Until(() => file != null && !onboard.IsBusy);
                    Assert.Empty(firmware.Writes);
                    Assert.Equal(new DpiStage(800, 801), Assert.Single(OnboardBackupCodec.ReadBackup(file!).Stages));

                    firmware.Dpi = new DpiStage(1600, 1601);
                    firmware.Buttons[(0x39, 0)] = BasiliskOnboard.Disabled();
                    firmware.Brightness[0x04] = 113;
                    firmware.Effects[0x04] = [0x03, 0, 0, 0];
                    firmware.Brightness[0x0A] = 156;
                    firmware.Effects[0x0A] = [0, 0, 0, 0];
                    firmware.SmartReel = false;
                    firmware.Acceleration = true;
                    Click(Find<Button>("OnboardOpenBackupButton"));
                    await Until(() => !onboard.IsBusy && Find<Button>("OnboardConfirmRestoreButton").IsEffectivelyVisible);
                    var preview = Find<TextBlock>("OnboardRestorePreviewText").Text!;
                    Assert.Contains("logo brightness", preview);
                    Assert.Contains("scroll-wheel brightness", preview);
                    Assert.Contains("logo lighting effect", preview);
                    Assert.Contains("underglow brightness", preview);
                    Assert.Contains("underglow lighting effect", preview);
                    Assert.DoesNotContain("scroll-wheel lighting effect", preview);
                    Assert.Contains("Smart-Reel", preview);
                    Assert.Contains("scroll acceleration", preview);
                    Assert.Empty(firmware.Writes);
                    Click(Find<Button>("OnboardCancelRestoreButton"));
                    Assert.False(Find<Button>("OnboardConfirmRestoreButton").IsEffectivelyVisible);
                    Assert.Empty(firmware.Writes);

                    Click(Find<Button>("OnboardOpenBackupButton"));
                    await Until(() => !onboard.IsBusy && Find<Button>("OnboardConfirmRestoreButton").IsEffectivelyVisible);
                    Click(Find<Button>("OnboardConfirmRestoreButton"));
                    await Until(() => result != null && !onboard.IsBusy);
                    Assert.Equal(expectedStatus, result!.Status);
                    Assert.Contains(result.Message, Find<TextBlock>("OnboardRestoreStatusText").Text!);
                    Assert.Equal(failRestore ? new DpiStage(1600, 1601) : new DpiStage(800, 801), firmware.Dpi);
                    Assert.Equal(failRestore ? BasiliskOnboard.Disabled() : BasiliskOnboard.Keyboard(0x68, 1),
                        firmware.Buttons[(0x39, 0)]);
                    Assert.Equal(failRestore ? 113 : 101, firmware.Brightness[0x04]);
                    Assert.Equal(failRestore ? 156 : 93, firmware.Brightness[0x0A]);
                    Assert.Equal(failRestore ? new byte[] { 0, 0, 0, 0 } : new byte[] { 0x04, 1, 0x28, 0 },
                        firmware.Effects[0x0A]);
                    Assert.Equal(failRestore ? new byte[] { 0x03, 0, 0, 0 } : new byte[] { 0x01, 0, 0, 1, 1, 40, 100 },
                        firmware.Effects[0x04]);
                    Assert.Equal(!failRestore, firmware.SmartReel);
                    Assert.Equal(failRestore && !failRollback, firmware.Acceleration);
                }
                finally
                {
                    window.Close();
                }
            }
            finally
            {
                directory.Delete(recursive: true);
            }
            return true;
        }, CancellationToken.None);

    [Theory]
    [InlineData(0x00E2)]
    [InlineData(0x00E3)]
    public Task The_device_page_manages_slots_and_assigns_a_verified_firmware_button(ushort productId) =>
        HeadlessApp.Session.Dispatch<bool>(async () =>
        {
            var directory = Directory.CreateTempSubdirectory("viper-onboard-gui-");
            try
            {
                var firmware = new BasiliskOnboardTests.Firmware();
                using var manager = new DeviceManager(new SettingsStore(directory.FullName), new Sink());
                manager.Refresh([new("/dev/emulated", 0x1532, productId, 3, "Basilisk", "usb-gui/input3")], [],
                    _ => true, _ => new RazerTransport(firmware) { AnswerDelay = TimeSpan.Zero });
                IReadOnlyList<DeviceInfo> Devices() =>
                    JsonSerializer.Deserialize(manager.DevicesJson(), ClientJson.Default.ListDeviceInfo)!;

                var requests = new List<OnboardRequest>();
                var onboard = new OnboardEditorViewModel(() => Task.FromResult(Devices()), request =>
                {
                    var decoded = BasiliskOnboard.ReadRequest(BasiliskOnboard.WriteRequest(request));
                    requests.Add(decoded);
                    return Task.FromResult(BasiliskOnboard.ReadResult(BasiliskOnboard.WriteResult(manager.ManageOnboard(decoded))));
                });
                var main = new MainViewModel(new DaemonClient(), new AppPreferences(), false);
                var device = new DeviceViewModel(Assert.Single(Devices()), main, onboard);
                var view = new DeviceView { DataContext = device };
                var window = new Window { Width = 1350, Height = 850, Content = view };
                window.Show();
                try
                {
                    Assert.Contains(DeviceViewModel.OnboardPage, device.Pages);
                    device.SelectedPage = DeviceViewModel.OnboardPage;
                    await Until(() => onboard.Slots.Count == 2 && !onboard.IsBusy);
                    var panel = Assert.Single(view.GetVisualDescendants().OfType<OnboardEditorView>());
                    Assert.True(panel.IsVisible);
                    T Find<T>(string name) where T : Control => Assert.IsType<T>(panel.FindControl<T>(name));

                    Find<ComboBox>("OnboardNewSlotBox").SelectedItem = (byte)4;
                    Click(Find<Button>("OnboardCreateButton"));
                    await Until(() => onboard.Slots.Any(slot => slot.Id == 4) && !onboard.IsBusy);
                    Find<ComboBox>("OnboardSlotBox").SelectedItem = onboard.Slots.Single(slot => slot.Id == 4);
                    Find<TextBox>("OnboardNameBox").Text = "Work";
                    Click(Find<Button>("OnboardRenameButton"));
                    await Until(() => onboard.Slots.Any(slot => slot.Label.Contains("Work", StringComparison.Ordinal)) && !onboard.IsBusy);
                    Click(Find<Button>("OnboardActivateButton"));
                    await Until(() => onboard.ActiveSlot == 4 && !onboard.IsBusy);
                    Assert.False(Find<Button>("OnboardBeginDeleteButton").IsEnabled);

                    Find<ComboBox>("FirmwareButtonBox").SelectedItem = onboard.Buttons.Single(button => button.Id == 0x60);
                    var layer = Assert.IsAssignableFrom<ToggleButton>(panel.FindControl<Control>("FirmwareHypershiftToggle"));
                    layer.IsChecked = true;
                    Find<ComboBox>("FirmwareKeyBox").SelectedItem = onboard.Keys.Single(key => key.Usage == 0x68);
                    Find<CheckBox>("FirmwareControlCheckBox").IsChecked = true;
                    Click(Find<Button>("FirmwareAssignButton"));
                    await Until(() => requests.Any(request => request.Operation == OnboardOperation.SetKeyboard) && !onboard.IsBusy);
                    var assignment = Assert.Single(requests, request => request.Operation == OnboardOperation.SetKeyboard);
                    Assert.Equal(new byte[] { 4, 0x60, 1, 0x68, 1 }, new[] { assignment.Slot, assignment.Button, assignment.Layer, assignment.Usage, assignment.Modifiers });
                    Assert.Contains("F13", Find<TextBlock>("FirmwareBindingText").Text!);
                    Click(Find<Button>("FirmwareReadButton"));
                    await Until(() => requests.Any(request => request.Operation == OnboardOperation.ReadBinding) && !onboard.IsBusy);
                    Assert.Contains("Ctrl + F13", Find<TextBlock>("FirmwareBindingText").Text!);
                    Assert.False(Find<Button>("FirmwareRestoreButton").IsEnabled);
                    Click(Find<Button>("FirmwareDisableButton"));
                    await Until(() => onboard.BindingText == "Disabled" && !onboard.IsBusy);
                    layer.IsChecked = false;
                    Click(Find<Button>("FirmwareRestoreButton"));
                    await Until(() => onboard.BindingText.Contains("Firmware default", StringComparison.Ordinal) && !onboard.IsBusy);

                    Find<ComboBox>("OnboardSlotBox").SelectedItem = onboard.Slots.Single(slot => slot.Id == 2);
                    Click(Find<Button>("OnboardBeginDeleteButton"));
                    Assert.DoesNotContain(requests, request => request.Operation == OnboardOperation.Delete);
                    Click(Find<Button>("OnboardCancelDeleteButton"));
                    Assert.False(onboard.DeletePending);
                    Click(Find<Button>("OnboardBeginDeleteButton"));
                    Click(Find<Button>("OnboardDeleteButton"));
                    await Until(() => onboard.Slots.All(slot => slot.Id != 2) && !onboard.IsBusy);
                    Assert.True(Assert.Single(requests, request => request.Operation == OnboardOperation.Delete).ConfirmDelete);
                    Assert.All(requests, request => Assert.Equal("TEST01", request.Serial));
                }
                finally
                {
                    window.Close();
                }
            }
            finally
            {
                directory.Delete(recursive: true);
            }
            return true;
        }, CancellationToken.None);

    [Fact]
    public Task Bluetooth_does_not_expose_the_firmware_page() => HeadlessApp.Session.Dispatch(() =>
        {
            var info = new DeviceInfo("00e3", "00e4", "Basilisk", "mouse", "bluetooth:123456123456",
                true, false, null, null, null, [], false, null, Connection: "bluetooth");
            var main = new MainViewModel(new DaemonClient(), new AppPreferences(), false);
            var device = new DeviceViewModel(info, main);
            Assert.DoesNotContain(DeviceViewModel.OnboardPage, device.Pages);
        }, CancellationToken.None);

    [Fact]
    public Task Refreshing_or_changing_mice_cancels_a_delete_confirmation() => HeadlessApp.Session.Dispatch<bool>(async () =>
        {
            DeviceInfo Target(string serial) => new("00e3", "00e3", "Basilisk", "mouse", serial, true, true, serial,
                "1.0", "/dev/emulated", [], false, null, Connection: "hyperspeed");
            var writes = new List<OnboardRequest>();
            var onboard = new OnboardEditorViewModel(() => Task.FromResult<IReadOnlyList<DeviceInfo>>([Target("ONE"), Target("TWO")]), request =>
            {
                if (request.Operation != OnboardOperation.List)
                    writes.Add(request);
                return Task.FromResult(new OnboardResult(request.Serial!, 1, [1, 2]));
            });
            await onboard.RefreshCommand.ExecuteAsync(null);
            Assert.Null(onboard.SelectedTarget);
            onboard.SelectedTarget = onboard.Targets.Single(target => target.Serial == "ONE");
            await Until(() => onboard.Slots.Count == 2 && !onboard.IsBusy);
            onboard.SelectedSlot = onboard.Slots.Single(slot => slot.Id == 2);
            onboard.BeginDeleteCommand.Execute(null);
            Assert.True(onboard.DeletePending);
            await onboard.RefreshCommand.ExecuteAsync(null);
            Assert.False(onboard.DeletePending);
            Assert.False(onboard.DeleteCommand.CanExecute(null));
            onboard.BeginDeleteCommand.Execute(null);
            Assert.True(onboard.DeletePending);
            onboard.SelectedTarget = onboard.Targets.Single(target => target.Serial == "TWO");
            await Until(() => !onboard.IsBusy);
            Assert.False(onboard.DeletePending);
            Assert.False(onboard.DeleteCommand.CanExecute(null));
            Assert.Empty(writes);
            return true;
        }, CancellationToken.None);

    [Fact]
    public Task An_invalid_binding_reply_does_not_change_the_slot_list() => HeadlessApp.Session.Dispatch<bool>(async () =>
        {
            DeviceInfo device = new("00e3", "00e3", "Basilisk", "mouse", "ONE", true, true, "ONE", "1.0",
                "/dev/emulated", [], false, null, Connection: "hyperspeed");
            var onboard = new OnboardEditorViewModel(() => Task.FromResult<IReadOnlyList<DeviceInfo>>([device]), request =>
                Task.FromResult(request.Operation == OnboardOperation.List
                    ? new OnboardResult("ONE", 1, [1, 2])
                    : new OnboardResult("ONE", 3, [1, 3], new OnboardBinding(0, [0]))));

            await onboard.RefreshCommand.ExecuteAsync(null);
            await onboard.ReadBindingCommand.ExecuteAsync(null);
            Assert.Equal<byte?>(1, onboard.ActiveSlot);
            Assert.Equal(new byte[] { 1, 2 }, onboard.Slots.Select(slot => slot.Id));
            Assert.Empty(onboard.BindingText);
            Assert.Contains("did not confirm", onboard.Status);
            return true;
        }, CancellationToken.None);

    [Fact]
    public Task A_delayed_binding_reply_cannot_update_another_mouse() => HeadlessApp.Session.Dispatch<bool>(async () =>
        {
            DeviceInfo Target(string serial) => new("00e3", "00e3", "Basilisk", "mouse", serial, true, true, serial,
                "1.0", "/dev/emulated", [], false, null, Connection: "hyperspeed");
            var reply = new TaskCompletionSource<OnboardResult>(TaskCreationOptions.RunContinuationsAsynchronously);
            var onboard = new OnboardEditorViewModel(
                () => Task.FromResult<IReadOnlyList<DeviceInfo>>([Target("ONE"), Target("TWO")]),
                request => request.Operation == OnboardOperation.ReadBinding ? reply.Task :
                    Task.FromResult(new OnboardResult(request.Serial!, request.Serial == "ONE" ? (byte)1 : (byte)2, [1, 2])));
            await onboard.RefreshCommand.ExecuteAsync(null);
            onboard.SelectedTarget = onboard.Targets.Single(target => target.Serial == "ONE");
            await Until(() => onboard.ActiveSlot == 1 && !onboard.IsBusy);
            var pending = onboard.ReadBindingCommand.ExecuteAsync(null);
            onboard.SelectedTarget = onboard.Targets.Single(target => target.Serial == "TWO");
            reply.SetResult(new OnboardResult("ONE", 1, [1, 2], new OnboardBinding(0, BasiliskOnboard.Keyboard(0x68, 0))));
            await pending;
            await Until(() => onboard.ActiveSlot == 2 && !onboard.IsBusy);
            Assert.Empty(onboard.BindingText);
            return true;
        }, CancellationToken.None);

    static void Click(Button button)
    {
        Assert.True(button.IsEnabled);
        Assert.NotNull(button.Command);
        Assert.True(button.Command.CanExecute(button.CommandParameter));
        button.Command.Execute(button.CommandParameter);
    }

    static async Task Until(Func<bool> condition)
    {
        var deadline = DateTime.UtcNow.AddSeconds(4);
        while (!condition() && DateTime.UtcNow < deadline)
            await Task.Delay(10);
        Assert.True(condition(), "The onboard page did not reach the expected state.");
    }

    sealed class Sink : IInputSink
    {
        public void Key(ushort code, int value) { }
        public void Relative(ushort axis, int value) { }
        public void Sync() { }
    }
}
