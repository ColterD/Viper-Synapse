using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Threading;
using Avalonia.VisualTree;
using ViperSynapse.App.Services;
using ViperSynapse.App.ViewModels;
using ViperSynapse.App.Views;
using ViperSynapse.Core.Bindings;
using ViperSynapse.Core.Protocol;

namespace ViperSynapse.Tests;

public sealed class BackgroundRefreshTests
{
    static DeviceInfo Mouse(string port = "usb-emulated") => new("00e3", "00e2", "Basilisk", "mouse", port,
        true, true, port, "v1.00", "/dev/emulated", [], false, null, Connection: "wired");

    [Fact]
    public Task An_existing_card_keeps_keyboard_focus_while_scanning_and_after_failure() =>
        HeadlessApp.Session.Dispatch<bool>(async () =>
        {
            var main = new MainViewModel(new DaemonClient(), new AppPreferences(), false);
            var home = new HomeView { DataContext = main.Home };
            var window = new Window { Width = 1080, Height = 680, Content = home };
            await main.RefreshDevicesAsync(() => Task.FromResult<IReadOnlyList<DeviceInfo>>([Mouse()]));
            window.Show();
            try
            {
                var card = home.GetVisualDescendants().OfType<Button>().Single(button => button.Classes.Contains("deviceCard"));
                Assert.True(card.Focus(NavigationMethod.Tab));
                Assert.Same(card, window.FocusManager!.GetFocusedElement());

                var response = new TaskCompletionSource<IReadOnlyList<DeviceInfo>>(TaskCreationOptions.RunContinuationsAsynchronously);
                var scan = main.RefreshDevicesAsync(() => response.Task);
                Dispatcher.UIThread.RunJobs();
                Assert.True(card.IsEffectivelyVisible);
                Assert.Same(card, window.FocusManager.GetFocusedElement());

                response.SetException(new IOException("emulated scan failure"));
                await scan;
                Dispatcher.UIThread.RunJobs();
                Assert.True(main.Home.HasServiceError);
                Assert.Same(card, window.FocusManager.GetFocusedElement());

                await main.RefreshDevicesAsync(() => Task.FromResult<IReadOnlyList<DeviceInfo>>([Mouse()]));
                Dispatcher.UIThread.RunJobs();
                Assert.False(main.Home.HasServiceError);
                Assert.Same(card, window.FocusManager.GetFocusedElement());
                Assert.Same(main.Home, main.SelectedTab);
                return true;
            }
            finally
            {
                window.Close();
                await main.RefreshDevicesAsync(() => Task.FromResult<IReadOnlyList<DeviceInfo>>([]));
            }
        }, CancellationToken.None);

    [Fact]
    public Task Overlapping_refreshes_share_a_live_read_and_keep_the_profile_control_focused() =>
        HeadlessApp.Session.Dispatch<bool>(async () =>
        {
            var first = Profile.Create("First");
            var selected = Profile.Create("Selected");
            var settings = new DeviceSettings([first, selected], selected.Id, 0);
            var stateStarted = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            var stateReply = new TaskCompletionSource<DeviceState?>(TaskCreationOptions.RunContinuationsAsynchronously);
            var profileReads = 0;
            var liveReads = 0;
            var main = new MainViewModel(new DaemonClient(), new AppPreferences(), false)
            {
                ReadDeviceSettingsAsync = _ =>
                {
                    profileReads++;
                    return Task.FromResult(settings with { Profiles = settings.Profiles.Select(profile => profile with { }).ToArray() });
                },
                ReadLiveDeviceAsync = _ =>
                {
                    Interlocked.Increment(ref liveReads);
                    stateStarted.TrySetResult();
                    return stateReply.Task;
                },
            };

            var firstScan = main.RefreshDevicesAsync(() => Task.FromResult<IReadOnlyList<DeviceInfo>>([Mouse()]), reloadExisting: true);
            await stateStarted.Task.WaitAsync(TimeSpan.FromSeconds(3));
            var device = Assert.Single(main.Devices);
            main.Show(device);
            var view = new DeviceView { DataContext = device };
            var window = new Window { Width = 1440, Height = 900, Content = view };
            window.Show();
            try
            {
                var profileBox = Assert.IsType<ComboBox>(view.FindControl<ComboBox>("DeviceProfileBox"));
                Assert.True(profileBox.Focus(NavigationMethod.Tab));
                var choice = Assert.IsType<ProfileChoice>(profileBox.SelectedItem);
                Assert.Equal(selected.Id, choice.Id);
                var profiles = device.Profiles;

                var overlapping = main.RefreshDevicesAsync(() => Task.FromResult<IReadOnlyList<DeviceInfo>>([Mouse()]), reloadExisting: true);
                await device.ReloadSettingsAsync(main.ReadDeviceSettingsAsync);
                Assert.Equal(2, profileReads);
                Assert.Equal(1, liveReads);
                Assert.Same(choice, profileBox.SelectedItem);

                stateReply.SetResult(new(null, 0, 1000, 83, false));
                await Task.WhenAll(firstScan, overlapping).WaitAsync(TimeSpan.FromSeconds(3));
                Dispatcher.UIThread.RunJobs();
                Assert.Equal(1, liveReads);
                Assert.Equal(2, profileReads);
                Assert.Equal(83, device.BatteryPercent);
                Assert.Same(profiles, device.Profiles);
                Assert.Same(choice, profileBox.SelectedItem);
                Assert.Same(profileBox, window.FocusManager!.GetFocusedElement());
                Assert.Same(device, main.SelectedTab);
                return true;
            }
            finally
            {
                stateReply.TrySetResult(null);
                window.Close();
                await main.RefreshDevicesAsync(() => Task.FromResult<IReadOnlyList<DeviceInfo>>([]));
            }
        }, CancellationToken.None);

    [Fact]
    public Task A_failed_settings_read_skips_live_io_and_does_not_block_retry() =>
        HeadlessApp.Session.Dispatch<bool>(async () =>
        {
            int profileReads = 0, liveReads = 0;
            var main = new MainViewModel(new DaemonClient(), new AppPreferences(), false)
            {
                ReadDeviceSettingsAsync = _ => ++profileReads == 1
                    ? Task.FromException<DeviceSettings>(new IOException("emulated service failure"))
                    : Task.FromResult(DeviceSettings.Create()),
                ReadLiveDeviceAsync = _ =>
                {
                    liveReads++;
                    return Task.FromResult<DeviceState?>(new(null, 0, 1000, 65, false));
                },
            };
            await main.RefreshDevicesAsync(() => Task.FromResult<IReadOnlyList<DeviceInfo>>([Mouse()]), reloadExisting: true);
            Dispatcher.UIThread.RunJobs();
            var device = Assert.Single(main.Devices);
            Assert.Contains("emulated service failure", main.Problem);
            Assert.Equal(0, liveReads);
            await device.ReloadAsync();
            Assert.Equal(1, liveReads);
            Assert.Equal(65, device.BatteryPercent);
            Assert.Same(device, Assert.Single(main.Home.Devices));
            await main.RefreshDevicesAsync(() => Task.FromResult<IReadOnlyList<DeviceInfo>>([]));
            return true;
        }, CancellationToken.None);

    [Fact]
    public Task Refreshing_the_onboard_page_reuses_the_discovery_snapshot() =>
        HeadlessApp.Session.Dispatch<bool>(async () =>
        {
            var mouse = Mouse();
            var main = new MainViewModel(new DaemonClient(), new AppPreferences(), false)
            {
                ReadDeviceSettingsAsync = _ => Task.FromResult(DeviceSettings.Create()),
                ReadLiveDeviceAsync = _ => Task.FromResult<DeviceState?>(null),
            };
            int separateLists = 0, slotReads = 0;
            var slotsRefreshed = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            var onboard = new OnboardEditorViewModel(
                () => { separateLists++; return Task.FromResult<IReadOnlyList<DeviceInfo>>([mouse]); },
                request =>
                {
                    if (Interlocked.Increment(ref slotReads) == 2)
                        slotsRefreshed.TrySetResult();
                    return Task.FromResult(new OnboardResult(request.Serial!, 1, [1]));
                });
            var device = new DeviceViewModel(mouse, main, onboard);
            main.Tabs.Insert(1, device);
            device.SelectedPage = DeviceViewModel.OnboardPage;
            Assert.Equal(1, separateLists);
            Assert.Equal(1, slotReads);

            await main.RefreshDevicesAsync(() => Task.FromResult<IReadOnlyList<DeviceInfo>>([mouse]));
            Assert.Equal(1, slotReads); // An unchanged hotplug scan does not reload the slots.
            await main.RefreshDevicesAsync(() => Task.FromResult<IReadOnlyList<DeviceInfo>>([mouse]), reloadExisting: true);
            await slotsRefreshed.Task.WaitAsync(TimeSpan.FromSeconds(3));
            Assert.Equal(1, separateLists);
            Assert.Equal(2, slotReads);
            await main.RefreshDevicesAsync(() => Task.FromResult<IReadOnlyList<DeviceInfo>>([]));
            return true;
        }, CancellationToken.None);

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public Task Switching_profiles_saves_pending_edits_to_the_original_profile(bool fromService) =>
        HeadlessApp.Session.Dispatch<bool>(async () =>
        {
            var original = Profile.Create("Original");
            var next = Profile.Create("Next");
            var settings = new DeviceSettings([original, next], original.Id, 0);
            var writes = new List<DeviceSettings>();
            var main = new MainViewModel(new DaemonClient(), new AppPreferences(Notifications: false), false)
            {
                WriteDeviceSettingsAsync = (_, updated) =>
                {
                    writes.Add(updated);
                    return Task.CompletedTask;
                },
            };
            var device = new DeviceViewModel(Mouse(), main);
            try
            {
                await device.ReloadSettingsAsync(_ => Task.FromResult(settings));
                device.Lighting.LightingOn = false;
                device.Performance.PollingRate = 500;

                if (fromService)
                    await device.ReloadSettingsAsync(_ => Task.FromResult(settings with { ActiveProfile = next.Id }));
                else
                    device.SelectedProfile = device.Profiles.Single(profile => profile.Id == next.Id);

                var saved = Assert.Single(writes);
                Assert.Equal(next.Id, saved.ActiveProfile);
                Assert.Equal(next.Id, device.SelectedProfile?.Id);
                var updated = saved.Profiles.Single(profile => profile.Id == original.Id);
                Assert.True(updated.Lighting?.Off);
                Assert.Equal(500, updated.Performance?.PollingRate);
                Assert.Null(saved.Active.Lighting);
                Assert.Null(saved.Active.Performance);
                return true;
            }
            finally
            {
                device.Detach();
            }
        }, CancellationToken.None);

    [Fact]
    public Task Tab_icons_use_visible_vector_shapes() => HeadlessApp.Session.Dispatch<bool>(async () =>
    {
        var main = new MainViewModel(new DaemonClient(), new AppPreferences(), false);
        await main.RefreshDevicesAsync(() => Task.FromResult<IReadOnlyList<DeviceInfo>>([Mouse()]));
        var window = new MainWindow { DataContext = main, Width = 1080, Height = 680 };
        window.Show();
        try
        {
            var tabBars = window.GetVisualDescendants().OfType<ListBox>().Where(list => list.Classes.Contains("topTabs"));
            var icons = tabBars.SelectMany(bar => bar.GetVisualDescendants()).OfType<PathIcon>().Where(icon => icon.IsEffectivelyVisible).ToArray();
            Assert.Equal(5, icons.Length);
            Assert.All(icons, icon =>
            {
                Assert.NotNull(icon.Data);
                Assert.True(icon.Bounds.Width >= 24);
                Assert.True(icon.Bounds.Height >= 24);
            });
            return true;
        }
        finally
        {
            window.Close();
            await main.RefreshDevicesAsync(() => Task.FromResult<IReadOnlyList<DeviceInfo>>([]));
        }
    }, CancellationToken.None);

    [Fact]
    public Task Application_tabs_stay_reachable_when_device_tabs_overflow() => HeadlessApp.Session.Dispatch<bool>(async () =>
    {
        var main = new MainViewModel(new DaemonClient(), new AppPreferences(), false);
        DeviceInfo[] units = Enumerable.Range(0, 12).Select(i => Mouse($"usb-{i}") with
        {
            Key = $"test-{i}",
            Name = $"Emulated device with a longer name {i}",
        }).ToArray();
        await main.RefreshDevicesAsync(() => Task.FromResult<IReadOnlyList<DeviceInfo>>(units));
        var window = new MainWindow { DataContext = main, Width = 1080, Height = 680 };
        window.Show();
        try
        {
            window.UpdateLayout();
            var primary = Assert.IsType<ListBox>(window.FindControl<ListBox>("PrimaryTabsList"));
            var devices = Assert.IsType<ListBox>(window.FindControl<ListBox>("DeviceTabsList"));
            Assert.Equal(4, primary.ItemCount);
            Assert.Equal(units.Length, devices.ItemCount);
            var settings = Assert.IsType<ListBoxItem>(primary.ContainerFromIndex(3));
            Assert.True(settings.Bounds.Right <= primary.Bounds.Width);

            var last = main.Devices.Last();
            devices.SelectedItem = last;
            Assert.Same(last, main.SelectedTab);
            primary.SelectedItem = main.Settings;
            Assert.Same(main.Settings, main.SelectedTab);
            Assert.Null(devices.SelectedItem);
            return true;
        }
        finally
        {
            window.Close();
            await main.RefreshDevicesAsync(() => Task.FromResult<IReadOnlyList<DeviceInfo>>([]));
        }
    }, CancellationToken.None);
}
