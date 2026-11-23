using System.Diagnostics;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.Media;
using Avalonia.Media.Imaging;
using Avalonia.Threading;
using Avalonia.VisualTree;
using ViperSynapse.App.Controls;
using ViperSynapse.App.Services;
using ViperSynapse.App.ViewModels;
using ViperSynapse.App.Views;
using ViperSynapse.Core.Bindings;

namespace ViperSynapse.DesktopSmoke;

static class Program
{
    [STAThread]
    public static int Main()
    {
        var root = Environment.GetEnvironmentVariable("VIPER_DESKTOP_SMOKE_ROOT");
        if (string.IsNullOrWhiteSpace(root) || string.IsNullOrEmpty(Environment.GetEnvironmentVariable("DISPLAY")) ||
            string.IsNullOrEmpty(Environment.GetEnvironmentVariable("DBUS_SESSION_BUS_ADDRESS")))
            throw new InvalidOperationException("Run this smoke test through tests/desktop-smoke/run.sh.");

        using var fixture = new EmulatedDevices(root);
        using var bus = fixture.CreateBus();
        fixture.Refresh(includeNaga: true);
        bus.ConnectAsync().GetAwaiter().GetResult();
        ViperSynapse.App.Program.BuildAvaloniaApp()
            .With(new X11PlatformOptions { RenderingMode = [X11RenderingMode.Software] })
            .SetupWithoutStarting();

        var client = new DaemonClient();
        var main = new MainViewModel(client, new AppPreferences(), false);
        var window = new MainWindow { DataContext = main, Width = 1080, Height = 680 };
        using var finished = new CancellationTokenSource(TimeSpan.FromSeconds(75));
        var exitCode = 1;
        window.Show();
        Dispatcher.UIThread.Post(async () =>
        {
            try
            {
                await VerifyAsync(fixture, window, main);
                exitCode = 0;
            }
            catch (Exception error)
            {
                Console.Error.WriteLine(error);
            }
            finally
            {
                try
                {
                    await main.RefreshDevicesAsync(() => Task.FromResult<IReadOnlyList<DeviceInfo>>([]));
                    await client.DisposeAsync();
                    window.Close();
                }
                catch (Exception error)
                {
                    Console.Error.WriteLine(error);
                    exitCode = 1;
                }
                finally
                {
                    finished.Cancel();
                }
            }
        });
        Dispatcher.UIThread.MainLoop(finished.Token);
        return exitCode;
    }

    static async Task VerifyAsync(EmulatedDevices fixture, MainWindow window, MainViewModel main)
    {
        await main.StartAsync().WaitAsync(TimeSpan.FromSeconds(10));
        window.UpdateLayout();
        Require(window.TryGetPlatformHandle() is { } handle && handle.Handle != IntPtr.Zero, "The desktop window has no native handle.");
        Require(Math.Abs(window.RenderScaling - 1.5) < 0.02 && window.ClientSize.Width >= 1079 && window.ClientSize.Width <= 1081,
            $"Expected the 1080 DIP minimum at 150% scale, got {window.ClientSize.Width} DIP and {window.RenderScaling:P0}.");
        Require(main.Home.HasScanResult && main.Home.Devices.Count >= 8, "The emulated devices were not discovered.");

        var primaryTabs = Find<ListBox>(window, "PrimaryTabsList");
        var settings = primaryTabs.ContainerFromIndex(3);
        Require(settings is { IsEffectivelyVisible: true } && settings.Bounds.Right <= primaryTabs.Bounds.Width,
            "Settings is clipped by device tabs.");
        primaryTabs.SelectedItem = main.Settings;
        Require(ReferenceEquals(main.SelectedTab, main.Settings), "Settings could not be selected.");
        primaryTabs.SelectedItem = main.Home;

        var home = Descendant<HomeView>(window);
        var affected = home.GetVisualDescendants().OfType<Button>()
            .Single(button => button.DataContext is DashboardUnitAttention entry && entry.Unit.Serial == "HYP02");
        affected.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
        Require(main.SelectedTab is DeviceViewModel { SelectedPage: "HEALTH", InspectedUnit.Serial: "HYP02" },
            "The mixed-unit Health link did not select the affected receiver.");
        Console.WriteLine("Native desktop: minimum width, 150% scaling, overflow navigation and targeted Health passed.");

        var naga = main.Devices.Single(device => device.Key == "0053");
        main.Show(naga);
        await naga.ReloadSettingsAsync(main.ReadDeviceSettingsAsync);
        var settingsPath = Path.Combine(fixture.Profiles, "0053.json");
        var original = File.ReadAllBytes(settingsPath);
        // Substituting a directory makes the daemon's atomic file replacement fail even as root.
        File.Delete(settingsPath);
        Directory.CreateDirectory(settingsPath);
        try
        {
            naga.AddProfileCommand.Execute(null);
            await UntilAsync(() => naga.CanRetrySave, "A rejected save did not expose Retry save.");
        }
        finally
        {
            Directory.Delete(settingsPath);
            File.WriteAllBytes(settingsPath, original);
        }
        var deviceView = Descendant<DeviceView>(window);
        var retry = Find<Control>(deviceView, "DeviceSaveStatus").GetVisualDescendants().OfType<Button>()
            .Single(button => Equals(button.Content, "Retry save"));
        Require(retry.IsEffectivelyVisible && retry.IsEnabled, "Retry save is unavailable after rejection.");
        var retryCommand = retry.Command ?? throw new InvalidOperationException("Retry save has no command.");
        Require(retryCommand.CanExecute(retry.CommandParameter), "The retry command is disabled after rejection.");
        retryCommand.Execute(retry.CommandParameter);
        await UntilAsync(() => naga.SaveStatus.StartsWith("Saved to profile", StringComparison.Ordinal),
            "Saved profile feedback was missing after retry.");
        Console.WriteLine("Native desktop: failed persistence and visible retry passed.");

        await VerifyLinkedGameDuringRemapSaveAsync(fixture, naga.Info);

        var profilesBeforeUnplug = File.ReadAllText(settingsPath);
        var keyButton = deviceView.GetVisualDescendants().OfType<Button>().First(button =>
            button.Classes.Contains("keyCell") && button.IsEnabled && button.IsEffectivelyVisible);
        ActivateWithKeyboard(keyButton);
        await UntilAsync(() => naga.Editor != null, "Keyboard activation did not open the remap editor.");
        naga.Editor!.SelectedFunction = BindingEditorViewModel.Functions.Single(entry => entry.Kind == FunctionKind.Disable);
        await Task.Run(() => fixture.Refresh(includeNaga: false));
        await UntilAsync(() => !main.Home.Devices.Contains(naga), "The unplugged device still has a tab.");
        Require(ReferenceEquals(main.SelectedTab, main.Home) && File.ReadAllText(settingsPath) == profilesBeforeUnplug,
            "Unplugging during an uncommitted remap changed the saved profile or left a stale tab.");

        var keypad = main.Devices.Single(device => device.Key == "02eb");
        keypad.SelectedPage = "LIGHTING";
        main.Show(keypad);
        keypad.Lighting.EffectTab = LightingViewModel.EffectTabs[1];
        window.UpdateLayout();
        var lightingView = Descendant<DeviceLightingView>(window);
        var paintList = Find<ItemsControl>(lightingView, "PaintKeyList");
        var paintButton = paintList.GetVisualDescendants().OfType<Button>().First();
        var colour = Color.FromRgb(0x24, 0x68, 0xC6);
        keypad.Lighting.PaintColor = colour;
        ActivateWithKeyboard(paintButton);
        Require(keypad.Lighting.KeyColors?.Values.Contains(colour) == true,
            "Keyboard activation did not update the painting.");
        Console.WriteLine("Native desktop: keyboard remapping and lighting, and unplug during editing passed.");

        MeasurePageSwitching(window, keypad);
    }

    static async Task VerifyLinkedGameDuringRemapSaveAsync(EmulatedDevices fixture, DeviceInfo info)
    {
        const string executable = "viper-smoke-game.exe";
        var work = Profile.Create("Work") with { Keymaps = [Keymap.Empty("First"), Keymap.Empty("Second")] };
        var game = Profile.Create("Game") with { LinkedPrograms = [executable] };
        await Task.Run(() => fixture.SetProfiles(info.Key, new DeviceSettings([work, game], work.Id, 1)));
        await using var client = new DaemonClient();
        await client.ConnectAsync().WaitAsync(TimeSpan.FromSeconds(10));
        var original = await client.GetSettingsAsync(info.Key).WaitAsync(TimeSpan.FromSeconds(8));
        var saveStarted = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var allowCommit = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var main = new MainViewModel(client, new AppPreferences(Notifications: false), false)
        {
            WriteDeviceSettingsAsync = async (key, edit) =>
            {
                saveStarted.TrySetResult();
                await allowCommit.Task.WaitAsync(TimeSpan.FromSeconds(8));
                await client.SetSettingsAsync(key, edit, original);
            },
        };
        var device = new DeviceViewModel(info, main);
        await device.ReloadSettingsAsync(_ => Task.FromResult(original));
        try
        {
            device.Hypershift = true;
            var key = device.Rows.First(row => row.Bindable);
            device.SelectedKey = key;
            var editor = device.Editor ?? throw new InvalidOperationException("The remap editor did not open.");
            Require(editor.Target == "Work · Second · Hypershift", "The editor did not pin the original keymap and layer.");
            editor.SelectedFunction = BindingEditorViewModel.Functions.Single(entry => entry.Kind == FunctionKind.Disable);
            editor.SaveCommand.Execute(null);
            await saveStarted.Task.WaitAsync(TimeSpan.FromSeconds(8));

            try
            {
                Require(editor.IsSaving, "The linked-game switch did not overlap a pending remap save.");
                await Task.Run(() => fixture.FocusGame(executable));
                var switched = await client.GetSettingsAsync(info.Key).WaitAsync(TimeSpan.FromSeconds(8));
                Require(switched.ActiveProfile == game.Id && switched.ActiveKeymap == 0,
                    "The linked game did not switch profiles before the remap reached the daemon.");
                Require(switched.Profiles.Single(p => p.Id == work.Id).Keymaps[1].Resolve(key.Key.Source, true) is DefaultBinding,
                    "The binding was already persisted before the linked-game switch.");
            }
            finally { allowCommit.TrySetResult(); }

            await main.FinishDeviceEditsAsync(TimeSpan.FromSeconds(8));
            await UntilAsync(() => device.Editor == null, "The rebased remap did not complete successfully.");
            var saved = await client.GetSettingsAsync(info.Key).WaitAsync(TimeSpan.FromSeconds(8));
            var savedWork = saved.Profiles.Single(p => p.Id == work.Id);
            Require(saved.ActiveProfile == game.Id && saved.ActiveKeymap == 0,
                "The remap save reverted the game's active selection.");
            Require(savedWork.Keymaps[1].Hypershift.GetValueOrDefault(key.Key.Source) is DisableBinding &&
                savedWork.Keymaps[0].Resolve(key.Key.Source, true) is DefaultBinding &&
                savedWork.Keymaps[1].Resolve(key.Key.Source, false) is DefaultBinding &&
                saved.Active.Keymaps[0].Resolve(key.Key.Source, true) is DefaultBinding,
                "The remap was written outside the original profile, keymap or layer.");
            Console.WriteLine("Native desktop: a linked game switched profiles during a pending remap save without changing its target.");
        }
        finally { device.Detach(); }
    }

    static void ActivateWithKeyboard(Button button)
    {
        Require(button.Focus(NavigationMethod.Tab), "The action is not keyboard-focusable.");
        button.RaiseEvent(new KeyEventArgs { RoutedEvent = InputElement.KeyDownEvent, Key = Key.Space, PhysicalKey = PhysicalKey.Space });
        button.RaiseEvent(new KeyEventArgs { RoutedEvent = InputElement.KeyUpEvent, Key = Key.Space, PhysicalKey = PhysicalKey.Space });
    }

    static void MeasurePageSwitching(MainWindow window, DeviceViewModel device)
    {
        const int iterations = 20;
        void SwitchPages()
        {
            device.SelectedPage = "CUSTOMIZE";
            window.UpdateLayout();
            device.SelectedPage = "LIGHTING";
            window.UpdateLayout();
        }

        SwitchPages();
        var allocatedBefore = GC.GetAllocatedBytesForCurrentThread();
        var start = Stopwatch.GetTimestamp();
        for (int i = 0; i < iterations; i++)
            SwitchPages();
        var elapsed = Stopwatch.GetElapsedTime(start);
        var allocation = GC.GetAllocatedBytesForCurrentThread() - allocatedBefore;
        Console.WriteLine($"Sample: {iterations} Customize/Lighting switch pairs: {elapsed.TotalMilliseconds:F1} ms, {allocation} managed UI-thread bytes.");

        var canvas = Find<DeviceDrawing>(Descendant<DeviceLightingView>(window), "PaintCanvas");
        Require(canvas.Bounds.Width > 0 && canvas.Bounds.Height > 0, "The drawing was not laid out.");
        using var bitmap = new RenderTargetBitmap(PixelSize.FromSize(canvas.Bounds.Size, 1), new Vector(96, 96));
        bitmap.Render(canvas);
        allocatedBefore = GC.GetAllocatedBytesForCurrentThread();
        start = Stopwatch.GetTimestamp();
        for (int i = 0; i < iterations; i++)
            bitmap.Render(canvas);
        elapsed = Stopwatch.GetElapsedTime(start);
        allocation = GC.GetAllocatedBytesForCurrentThread() - allocatedBefore;
        Console.WriteLine($"Sample: {iterations} synchronous device drawing renders: {elapsed.TotalMilliseconds:F1} ms, {allocation} managed UI-thread bytes.");
    }

    static T Find<T>(Control root, string name) where T : Control =>
        root.FindControl<T>(name) ?? throw new InvalidOperationException($"Could not find {name}.");

    static T Descendant<T>(Control root) where T : Control
    {
        root.UpdateLayout();
        return root.GetVisualDescendants().OfType<T>().First(control => control.IsEffectivelyVisible);
    }

    static void Require(bool ready, string message)
    {
        if (!ready)
            throw new InvalidOperationException(message);
    }

    static async Task UntilAsync(Func<bool> ready, string why)
    {
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(8));
        try
        {
            while (!ready())
                await Task.Delay(40, timeout.Token);
        }
        catch (OperationCanceledException) when (timeout.IsCancellationRequested)
        {
            throw new TimeoutException(why);
        }
    }
}
