using Avalonia.Threading;
using ViperSynapse.App.Services;
using ViperSynapse.App.ViewModels;
using ViperSynapse.Core.Bindings;

namespace ViperSynapse.Tests;

public sealed class DeviceTabLifecycleTests
{
    static DeviceInfo Mouse(string port = "usb-emulated") => new("00e3", "00e2", "Basilisk", "mouse", port,
        true, true, "FIXTURE", "v1.00", "/dev/emulated", [], false, null, Connection: "wired");

    [Fact]
    public Task Unplug_ignores_an_in_flight_battery_reply_and_blocks_further_reads() =>
        HeadlessApp.Session.Dispatch<bool>(async () =>
        {
            var main = new MainViewModel(new DaemonClient(), new AppPreferences(), false);
            var device = new DeviceViewModel(Mouse(), main);
            main.Tabs.Insert(1, device);
            Assert.True(device.IsPolling);
            var reply = new TaskCompletionSource<DeviceState?>(TaskCreationOptions.RunContinuationsAsynchronously);
            var readStarted = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            var oldRead = device.RefreshBatteryAsync(readUsb: _ =>
            {
                readStarted.SetResult();
                return reply.Task;
            });
            await readStarted.Task.WaitAsync(TimeSpan.FromSeconds(3));
            await main.RefreshDevicesAsync(() => Task.FromResult<IReadOnlyList<DeviceInfo>>([]));
            Assert.Empty(main.Devices);
            Assert.False(device.IsPolling);

            reply.SetResult(new(null, 0, 0, 87, false));
            await oldRead;
            Assert.Null(device.BatteryPercent);
            await device.RefreshBatteryAsync(readUsb: _ => throw new Xunit.Sdk.XunitException("Removed tab started a battery read."));
            return true;
        }, CancellationToken.None);

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public Task Removed_tabs_do_not_report_late_read_failures(bool settingsRead) =>
        HeadlessApp.Session.Dispatch<bool>(async () =>
        {
            var main = new MainViewModel(new DaemonClient(), new AppPreferences(), false);
            var device = new DeviceViewModel(Mouse(), main);
            main.Tabs.Insert(1, device);
            var started = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            var failed = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);

            async Task<T> Read<T>()
            {
                started.SetResult();
                await failed.Task;
                throw new IOException("emulated endpoint disappeared");
            }

            var read = settingsRead ? device.ReloadSettingsAsync(_ => Read<DeviceSettings>()) :
                device.ReadDeviceAsync(_ => Read<DeviceState?>());
            await started.Task.WaitAsync(TimeSpan.FromSeconds(3));
            await main.RefreshDevicesAsync(() => Task.FromResult<IReadOnlyList<DeviceInfo>>([]));
            failed.SetResult();
            await read;
            Dispatcher.UIThread.RunJobs();
            Assert.Null(main.Problem);
            return true;
        }, CancellationToken.None);

    [Fact]
    public Task Unplug_stops_thumbstick_polling_and_replug_starts_a_new_tab() =>
        HeadlessApp.Session.Dispatch<bool>(async () =>
        {
            var main = new MainViewModel(new DaemonClient(), new AppPreferences(), false);
            DeviceInfo keypad = new("02eb", "02eb", "Tartarus", "keypad", "usb-keypad",
                true, true, "FIXTURE", "v1.00", "/dev/emulated", [], false, null, Connection: "wired");
            await main.RefreshDevicesAsync(() => Task.FromResult<IReadOnlyList<DeviceInfo>>([keypad]));
            var first = Assert.Single(main.Devices);
            Assert.True(first.HasThumbstick);
            Assert.True(first.IsPolling);
            await main.RefreshDevicesAsync(() => Task.FromResult<IReadOnlyList<DeviceInfo>>([]));
            Assert.False(first.IsPolling);
            await main.RefreshDevicesAsync(() => Task.FromResult<IReadOnlyList<DeviceInfo>>([keypad]));
            var newTab = Assert.Single(main.Devices);
            Assert.NotSame(first, newTab);
            Assert.True(newTab.IsPolling);
            Assert.False(first.IsPolling);
            await main.RefreshDevicesAsync(() => Task.FromResult<IReadOnlyList<DeviceInfo>>([]));
            return true;
        }, CancellationToken.None);

    [Fact]
    public Task A_reload_started_before_an_edit_cannot_replace_the_optimistic_profile() =>
        HeadlessApp.Session.Dispatch<bool>(async () =>
        {
            var main = new MainViewModel(new DaemonClient(), new AppPreferences(), false)
            {
                WriteDeviceSettingsAsync = (_, _) => Task.CompletedTask,
            };
            var device = new DeviceViewModel(Mouse(), main);
            var stale = new TaskCompletionSource<DeviceSettings>(TaskCreationOptions.RunContinuationsAsynchronously);
            var readStarted = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            try
            {
                var reload = device.ReloadSettingsAsync(_ =>
                {
                    readStarted.SetResult();
                    return stale.Task;
                });
                await readStarted.Task.WaitAsync(TimeSpan.FromSeconds(3));
                device.AddProfileCommand.Execute(null);
                stale.SetResult(DeviceSettings.Create());
                await reload;
                Assert.Equal("Profile 2", device.SelectedProfile?.Name);
                Assert.Equal(2, device.Profiles.Count);
                return true;
            }
            finally
            {
                device.Detach();
            }
        }, CancellationToken.None);

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public Task Pending_lighting_and_performance_edits_are_saved_together_on_exit_or_unplug(bool unplug) =>
        HeadlessApp.Session.Dispatch<bool>(async () =>
        {
            var writes = new List<DeviceSettings>();
            var main = new MainViewModel(new DaemonClient(), new AppPreferences(), false)
            {
                WriteDeviceSettingsAsync = (_, settings) =>
                {
                    writes.Add(settings);
                    return Task.CompletedTask;
                },
            };
            var device = new DeviceViewModel(Mouse(), main);
            main.Tabs.Insert(1, device);
            device.AddProfileCommand.Execute(null);
            writes.Clear();
            device.Lighting.LightingOn = false;
            device.Performance.PollingRate = 500;
            Assert.Empty(writes);

            if (unplug)
                await main.RefreshDevicesAsync(() => Task.FromResult<IReadOnlyList<DeviceInfo>>([]));
            await main.FinishDeviceEditsAsync(TimeSpan.FromSeconds(3));

            var saved = Assert.Single(writes);
            Assert.True(saved.Active.Lighting?.Off);
            Assert.Equal(500, saved.Active.Performance?.PollingRate);
            if (!unplug)
                device.Detach();
            return true;
        }, CancellationToken.None);

    [Fact]
    public Task Shutdown_drain_is_bounded_when_a_write_stalls() =>
        HeadlessApp.Session.Dispatch<bool>(async () =>
        {
            var stalledWrite = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            var main = new MainViewModel(new DaemonClient(), new AppPreferences(), false)
            {
                WriteDeviceSettingsAsync = (_, _) => stalledWrite.Task,
            };

            _ = main.SaveDeviceSettings(Mouse().Key, DeviceSettings.Create());
            try
            {
                await Assert.ThrowsAsync<TimeoutException>(() => main.FinishDeviceEditsAsync(TimeSpan.Zero));
            }
            finally
            {
                stalledWrite.TrySetResult();
                await main.FinishDeviceEditsAsync(TimeSpan.FromSeconds(3));
            }
            return true;
        }, CancellationToken.None);

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public Task Quick_gui_edits_wait_for_the_previous_write_even_after_failure(bool failFirst) =>
        HeadlessApp.Session.Dispatch<bool>(async () =>
        {
            var firstStarted = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            var firstReply = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            var second = new TaskCompletionSource<DeviceSettings>(TaskCreationOptions.RunContinuationsAsynchronously);
            var calls = 0;
            var main = new MainViewModel(new DaemonClient(), new AppPreferences(), false)
            {
                WriteDeviceSettingsAsync = async (_, settings) =>
                {
                    if (Interlocked.Increment(ref calls) == 1)
                    {
                        firstStarted.SetResult();
                        await firstReply.Task;
                        if (failFirst)
                            throw new IOException("emulated save failure");
                    }
                    else
                        second.SetResult(settings);
                },
            };
            var device = new DeviceViewModel(Mouse(), main);
            main.Tabs.Insert(1, device);
            try
            {
                device.AddProfileCommand.Execute(null);
                await firstStarted.Task.WaitAsync(TimeSpan.FromSeconds(3));
                device.AddProfileCommand.Execute(null);
                Assert.NotSame(second.Task, await Task.WhenAny(second.Task, Task.Delay(100)));
            }
            finally
            {
                firstReply.TrySetResult();
                device.Detach();
            }
            var saved = await second.Task.WaitAsync(TimeSpan.FromSeconds(3));
            Assert.Equal(3, saved.Profiles.Count);
            Assert.Equal("Profile 3", saved.Active.Name);
            return true;
        }, CancellationToken.None);
}
