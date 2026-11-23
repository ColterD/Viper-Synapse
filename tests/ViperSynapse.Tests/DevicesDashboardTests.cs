using Avalonia.Controls;
using Avalonia.Controls.Primitives;
using Avalonia.Interactivity;
using Avalonia.Threading;
using Avalonia.VisualTree;
using ViperSynapse.App.Services;
using ViperSynapse.App.ViewModels;
using ViperSynapse.App.Views;

namespace ViperSynapse.Tests;

public sealed class DevicesDashboardTests
{
    static DeviceInfo Mouse(string port = "usb-mouse") => new("0053", "0053", "Razer Naga Chroma", "mouse", port,
        true, false, port, "v1.00", "/dev/emulated", [], false, null, Connection: "wired");

    static DeviceInfo Keyboard => new("0221", "0221", "Razer BlackWidow Chroma V2", "keyboard", "usb-keyboard",
        true, false, "KEYBOARD", "v1.00", "/dev/emulated", [], false, null, Connection: "wired");

    [Theory]
    [InlineData(0, false, 0)]
    [InlineData(1, false, 1)]
    [InlineData(2, false, 2)]
    [InlineData(2, true, 1)]
    public Task Landing_page_shows_scanning_then_the_detected_devices(int units, bool sameModel, int cards) =>
        HeadlessApp.Session.Dispatch<bool>(async () =>
        {
            var main = new MainViewModel(new DaemonClient(), new AppPreferences(), false);
            var home = new HomeView { DataContext = main.Home };
            var window = new Window { Width = 1080, Height = 680, Content = home };
            window.Show();
            try
            {
                Assert.Same(main.Home, main.SelectedTab);
                Assert.Equal("DEVICES", main.Home.Header);
                Assert.True(IsVisible(home, "DeviceScanStatus"));
                Assert.False(IsVisible(home, "DeviceSetupHint"));
                Assert.False(IsVisible(home, "DeviceServiceError"));
                var response = new TaskCompletionSource<IReadOnlyList<DeviceInfo>>(TaskCreationOptions.RunContinuationsAsynchronously);
                var refresh = main.RefreshDevicesAsync(() => response.Task);
                Assert.True(IsVisible(home, "DeviceScanStatus"));
                response.SetResult(units switch
                {
                    0 => [],
                    1 => [Mouse()],
                    _ => [Mouse(), sameModel ? Mouse("usb-mouse-2") : Keyboard],
                });
                await refresh;
                Dispatcher.UIThread.RunJobs();
                Assert.False(IsVisible(home, "DeviceScanStatus"));
                Assert.False(IsVisible(home, "DeviceServiceError"));
                Assert.Equal(units == 0, IsVisible(home, "DeviceSetupHint"));
                var buttons = home.GetVisualDescendants().OfType<Button>().Where(button => button.Classes.Contains("deviceCard")).ToArray();
                Assert.Equal(cards, buttons.Length);
                if (sameModel)
                    Assert.Equal(2, Assert.Single(main.Devices).UnitChoices.Count);
                if (cards > 0)
                {
                    buttons[^1].RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
                    Assert.Same(main.Devices.Last(), main.SelectedTab);
                }
                return true;
            }
            finally
            {
                await main.RefreshDevicesAsync(() => Task.FromResult<IReadOnlyList<DeviceInfo>>([]));
                window.Close();
            }
        }, CancellationToken.None);

    [Fact]
    public Task Device_cards_reflow_on_resize_and_keep_focus() =>
        HeadlessApp.Session.Dispatch<bool>(async () =>
        {
            var main = new MainViewModel(new DaemonClient(), new AppPreferences(), false);
            var home = new HomeView { DataContext = main.Home };
            var host = new Border { Width = 1080, Child = home };
            var window = new Window { Width = 1500, Height = 760, Content = host };
            window.Show();
            try
            {
                await main.RefreshDevicesAsync(() => Task.FromResult<IReadOnlyList<DeviceInfo>>([Mouse(), Keyboard]));
                window.UpdateLayout();
                var grid = Assert.Single(home.GetVisualDescendants().OfType<UniformGrid>());
                Assert.Equal(2, grid.Children.Count);
                Assert.Equal(grid.Children[0].Bounds.X, grid.Children[1].Bounds.X);
                Assert.True(grid.Children[0].Bounds.Y < grid.Children[1].Bounds.Y);
                var button = grid.Children[0].GetVisualDescendants().OfType<Button>().First();
                Assert.True(button.Focus());

                host.Width = 1440;
                window.UpdateLayout();
                Assert.Equal(grid.Children[0].Bounds.Y, grid.Children[1].Bounds.Y);
                Assert.True(grid.Children[0].Bounds.X < grid.Children[1].Bounds.X);
                Assert.True(button.IsFocused);

                host.Width = 1080;
                window.UpdateLayout();
                Assert.True(grid.Children[0].Bounds.Y < grid.Children[1].Bounds.Y);
                Assert.True(button.IsFocused);
            }
            finally
            {
                await main.RefreshDevicesAsync(() => Task.FromResult<IReadOnlyList<DeviceInfo>>([]));
                window.Close();
            }
            return true;
        }, CancellationToken.None);

    [Fact]
    public Task Service_failure_has_a_retry_and_is_distinct_from_an_empty_scan() =>
        HeadlessApp.Session.Dispatch<bool>(async () =>
        {
            var connections = 0;
            var scans = 0;
            var retryReply = new TaskCompletionSource<IReadOnlyList<DeviceInfo>>(TaskCreationOptions.RunContinuationsAsynchronously);
            var main = new MainViewModel(new DaemonClient(), new AppPreferences(), false)
            {
                ConnectServiceAsync = () => ++connections == 1
                    ? Task.FromException(new IOException("emulated service unavailable"))
                    : Task.CompletedTask,
                ReadDeviceListAsync = () =>
                {
                    scans++;
                    return retryReply.Task;
                },
            };
            var home = new HomeView { DataContext = main.Home };
            var window = new Window { Content = home };
            window.Show();
            try
            {
                await main.StartAsync();
                Dispatcher.UIThread.RunJobs();
                Assert.True(IsVisible(home, "DeviceServiceError"));
                Assert.False(IsVisible(home, "DeviceSetupHint"));
                Assert.False(IsVisible(home, "DeviceScanStatus"));
                Assert.Equal("emulated service unavailable", main.Home.ServiceError);
                var retry = Assert.IsType<Button>(home.FindControl<Button>("RetryDeviceScanButton"));
                Assert.True(retry.IsEffectivelyVisible);
                Assert.Same(main.RefreshCommand, retry.Command);
                retry.Command!.Execute(null);
                Dispatcher.UIThread.RunJobs();
                Assert.True(IsVisible(home, "DeviceScanStatus"));
                Assert.False(IsVisible(home, "DeviceServiceError"));
                retryReply.SetResult([]);
                await main.RefreshCommand.ExecutionTask!.WaitAsync(TimeSpan.FromSeconds(3));
                Dispatcher.UIThread.RunJobs();
                Assert.True(IsVisible(home, "DeviceSetupHint"));
                Assert.False(IsVisible(home, "DeviceServiceError"));
                Assert.Equal(2, connections);
                Assert.Equal(1, scans);
                return true;
            }
            finally
            {
                window.Close();
            }
        }, CancellationToken.None);

    [Fact]
    public Task A_late_service_failure_does_not_replace_a_successful_newer_scan() =>
        HeadlessApp.Session.Dispatch<bool>(async () =>
        {
            var main = new MainViewModel(new DaemonClient(), new AppPreferences(), false);
            var oldReply = new TaskCompletionSource<IReadOnlyList<DeviceInfo>>(TaskCreationOptions.RunContinuationsAsynchronously);
            var oldScan = main.RefreshDevicesAsync(() => oldReply.Task);
            await main.RefreshDevicesAsync(() => Task.FromResult<IReadOnlyList<DeviceInfo>>([Mouse()]));
            oldReply.SetException(new IOException("old service failure"));
            await oldScan;
            Assert.True(main.Home.HasScanResult);
            Assert.Null(main.Home.ServiceError);
            Assert.Single(main.Home.Devices);
            await main.RefreshDevicesAsync(() => Task.FromResult<IReadOnlyList<DeviceInfo>>([]));
            return true;
        }, CancellationToken.None);

    [Fact]
    public Task Device_change_bursts_coalesce_without_recreating_unchanged_cards() =>
        HeadlessApp.Session.Dispatch<bool>(async () =>
        {
            var firstReply = new TaskCompletionSource<IReadOnlyList<DeviceInfo>>(TaskCreationOptions.RunContinuationsAsynchronously);
            var scans = 0;
            var main = new MainViewModel(new DaemonClient(), new AppPreferences(), false)
            {
                ConnectServiceAsync = () => Task.CompletedTask,
                ReadDeviceListAsync = () => ++scans == 1
                    ? firstReply.Task
                    : Task.FromResult<IReadOnlyList<DeviceInfo>>([Mouse(), Keyboard]),
            };
            await main.RefreshDevicesAsync(() => Task.FromResult<IReadOnlyList<DeviceInfo>>([Mouse()]));
            var mouseTab = Assert.Single(main.Home.Devices);
            var refresh = main.RefreshAfterDeviceChangeAsync();
            for (int i = 0; i < 5; i++)
                await main.RefreshAfterDeviceChangeAsync();
            Assert.Equal(1, scans);
            Assert.True(main.Home.HasScanResult);
            firstReply.SetResult([Mouse()]);
            await refresh.WaitAsync(TimeSpan.FromSeconds(3));

            Assert.Equal(2, scans);
            Assert.Equal(2, main.Home.Devices.Count);
            Assert.Same(mouseTab, main.Home.Devices[0]);
            await main.RefreshDevicesAsync(() => Task.FromResult<IReadOnlyList<DeviceInfo>>([]));
            return true;
        }, CancellationToken.None);

    static bool IsVisible(Control view, string name) => Assert.IsAssignableFrom<Control>(view.FindControl<Control>(name)).IsEffectivelyVisible;
}
