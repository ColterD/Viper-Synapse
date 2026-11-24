using System.Diagnostics;
using System.Globalization;
using Avalonia;
using Avalonia.Automation;
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
using ViperSynapse.Core.Input;

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
        Console.WriteLine("Native desktop: initializing Avalonia.");
        ViperSynapse.App.Program.BuildAvaloniaApp()
            .With(new X11PlatformOptions
            {
                RenderingMode = [X11RenderingMode.Software],
                // The private Xvfb framebuffer and synchronous drawing samples share the UI thread.
                ShouldRenderOnUIThread = true,
                EnableSessionManagement = false,
            })
            .SetupWithoutStarting();

        var client = new DaemonClient();
        var main = new MainViewModel(client, new AppPreferences(), false);
        var window = new MainWindow { DataContext = main, Width = 1080, Height = 680 };
        using var finished = new CancellationTokenSource(TimeSpan.FromSeconds(75));
        var exitCode = 1;
        Console.WriteLine("Native desktop: showing the window.");
        window.Show();
        Dispatcher.UIThread.Post(async () =>
        {
            try
            {
                await VerifyAsync(fixture, window, main, client);
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
                    await main.FinishDeviceEditsAsync(TimeSpan.FromSeconds(5));
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

    static async Task VerifyAsync(EmulatedDevices fixture, MainWindow window, MainViewModel main, DaemonClient client)
    {
        Console.WriteLine("Native desktop: discovering emulated devices.");
        await main.StartAsync().WaitAsync(TimeSpan.FromSeconds(10));
        window.UpdateLayout();
        var handle = window.TryGetPlatformHandle() ?? throw new InvalidOperationException("The desktop window has no native handle.");
        var windowId = handle.Handle.ToInt64().ToString(CultureInfo.InvariantCulture);
        await NativeInputAsync("windowmove", "--sync", windowId, "72", "45");
        await NativeInputAsync("windowfocus", "--sync", windowId);
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
        await CaptureAsync("dashboard-compact");
        window.Width = 1440;
        window.Height = 900;
        await UntilAsync(() => window.ClientSize.Width >= 1438, "Could not resize to the default-width dashboard.");
        await CaptureAsync("dashboard-wide");
        window.Width = 1080;
        window.Height = 680;
        await UntilAsync(() => window.ClientSize.Width <= 1081, "Could not return to the minimum-width dashboard.");
        main.Home.ShowShortcutsCommand.Execute(null);
        var addShortcut = Find<Button>(home, "AddShortcutButton");
        await TabToAsync(window, addShortcut);
        await KeyAsync("space");
        await UntilAsync(() => main.Home.Shortcuts.Editing, "Keyboard activation did not open the shortcut editor.");
        var shortcutEditor = Descendant<BindingFlyout>(home);
        await TabToAsync(window, Find<Button>(shortcutEditor, "CancelBinding"));
        await KeyAsync("space");
        await UntilAsync(() => !main.Home.Shortcuts.Editing && addShortcut.IsFocused,
            "Cancelling a shortcut did not restore focus to Add a shortcut.");
        home = await VerifyShortcutFocusAsync(window, home, main, client);
        main.Home.ShowDevicesCommand.Execute(null);
        Button? affected = null;
        await UntilAsync(() => (affected = home.GetVisualDescendants().OfType<Button>().FirstOrDefault(button =>
            button.IsEffectivelyVisible && button.DataContext is DashboardUnitAttention entry && entry.Unit.Serial == "HYP02")) != null,
            "The dashboard did not show the affected receiver after leaving Global Shortcuts.");
        affected!.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
        Require(main.SelectedTab is DeviceViewModel { SelectedPage: "HEALTH", InspectedUnit.Serial: "HYP02" },
            "The mixed-unit Health link did not select the affected receiver.");
        var basilisk = (DeviceViewModel)main.SelectedTab!;
        var readySnapshot = (await client.GetDevicesAsync()).Select(unit => unit.Serial == "HYP02"
            ? unit with { InputNodes = ["emulated-input"], InputIssue = null, ControlReady = true, ControlIssue = null, Problem = null } : unit).ToArray();
        var warningSnapshot = readySnapshot.Select(unit => unit.Serial == "HYP02"
            ? unit with { ControlReady = false, ControlNode = null } : unit).ToArray();
        await main.RefreshDevicesAsync(() => Task.FromResult<IReadOnlyList<DeviceInfo>>(warningSnapshot));
        Require(basilisk.HasOnboardPage && basilisk.HasDeviceProblem, "The receiver fixture needs Onboard and a warning.");
        var basiliskView = Descendant<DeviceView>(window);
        foreach (int width in new[] { 1080, 1440 })
        {
            window.Width = width;
            await UntilAsync(() => Math.Abs(window.ClientSize.Width - width) < 2, "Could not resize the Onboard page.");
            basilisk.SelectedPage = "ONBOARD";
            var health = basiliskView.GetVisualDescendants().OfType<Button>().Single(button => Equals(button.Content, "Health"));
            Require(health.IsEffectivelyVisible, "Onboard hid the receiver's hardware warning and Health action.");
            await TabToAsync(window, health);
            await CaptureAsync($"onboard-warning-{width}");
            await KeyAsync("space");
            await UntilAsync(() => basilisk.SelectedPage == "HEALTH" && basilisk.InspectedUnit?.Serial == "HYP02",
                "The Onboard Health action did not keep the affected receiver selected.");
        }
        basilisk.SelectedPage = "ONBOARD";
        await main.RefreshDevicesAsync(() => Task.FromResult<IReadOnlyList<DeviceInfo>>(readySnapshot));
        Require(!basilisk.HasDeviceProblem && !basiliskView.GetVisualDescendants().OfType<Button>()
            .Any(button => Equals(button.Content, "Health") && button.IsEffectivelyVisible), "The recovered receiver retained its old warning.");
        window.Width = 1080;
        await UntilAsync(() => window.ClientSize.Width <= 1081, "Could not return to the minimum-width device page.");
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
            main.Show(main.Settings);
            await UntilAsync(() => naga.CanRetrySave, "A rejected save did not expose Retry save.");
            var openDevice = window.GetVisualDescendants().OfType<Button>().Single(button => Equals(button.Content, "Open device"));
            Require(openDevice.IsEffectivelyVisible, "A save failure disappeared after navigating away.");
            await TabToAsync(window, openDevice);
            await CaptureAsync("save-failure-after-navigation");
            await KeyAsync("space");
            await UntilAsync(() => ReferenceEquals(main.SelectedTab, naga), "The save failure did not route back to its device.");
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
        await TabToAsync(window, retry);
        await KeyAsync("space");
        await UntilAsync(() => naga.SaveStatus.StartsWith("Saved to profile", StringComparison.Ordinal),
            "Saved profile feedback was missing after retry.");
        Console.WriteLine("Native desktop: failed persistence and visible retry passed.");

        await VerifyNativeRemapAsync(window, deviceView, naga, client, settingsPath);
        await VerifyLinkedGameDuringRemapSaveAsync(fixture, naga.Info);
        await VerifyManagedDpiAsync(fixture, window, deviceView, main, naga, client);

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
        await UntilAsync(() => window.FocusManager?.GetFocusedElement() is TextBlock { Name: "DevicesHeading" },
            "Unplugging during an edit did not return focus to Devices.");

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

    static async Task VerifyManagedDpiAsync(EmulatedDevices fixture, MainWindow window, DeviceView device, MainViewModel main,
        DeviceViewModel naga, DaemonClient client)
    {
        naga.SelectedPage = "PERFORMANCE";
        var toggle = Find<ToggleSwitch>(device, "ManagedDpiToggle");
        await TabToAsync(window, toggle);
        await KeyAsync("space");
        await UntilAsync(() => naga.Performance.ManagedDpiSteps, "The managed DPI switch did not turn on.");
        var add = Find<Button>(device, "AddManagedDpiStage");
        await TabToAsync(window, add);
        await KeyAsync("space");
        await UntilAsync(() => naga.Performance.Stages.Count == 2, "Adding a managed DPI step did not create the second row.");
        naga.Performance.Stages[0].X = 700;
        naga.Performance.Stages[1].X = 1300;
        await main.FinishDeviceEditsAsync(TimeSpan.FromSeconds(5));
        Require((await client.GetSettingsAsync(naga.Key)).Active.Performance is { ManagedDpiSteps: true, Stages.Count: 2 },
            "The managed DPI steps were not saved to the emulated profile.");
        var status = Find<TextBlock>(device, "ManagedDpiStatus");
        await UntilAsync(() => status.Text == "Mouse confirmed step 1.", "The selected DPI step was not confirmed.");
        fixture.PressDpi(up: true);
        await UntilAsync(() => status.Text == "Mouse confirmed step 2.", "DPI Up did not display the second step.");
        Require((await client.GetUnitStateAsync(naga.Info))?.Stages?[0].X == 1300, "DPI Up did not apply 1300 DPI.");
        var entryStep = device.GetVisualDescendants().OfType<Button>().Single(button =>
            button.Classes.Contains("stage") && button.DataContext is StageRow { Number: 1 });
        await UntilAsync(() => !entryStep.Classes.Contains("active"),
            "The startup step still appeared active after the hardware button press.");
        await TabToAsync(window, entryStep);
        await KeyAsync("space");
        await UntilAsync(() => status.Text == "Mouse confirmed step 1." && entryStep.Classes.Contains("active"),
            "Selecting the startup step did not set and highlight it again.");
        fixture.PressDpi(up: true);
        await UntilAsync(() => status.Text == "Mouse confirmed step 2.", "DPI Up could not apply after manual selection.");
        fixture.RejectDpiWrites(true);
        fixture.PressDpi(up: false);
        await UntilAsync(() => status.Text?.Contains("Could not confirm", StringComparison.Ordinal) == true,
            "A failed DPI write did not appear in Performance.");
        fixture.RejectDpiWrites(false);
        fixture.PressDpi(up: false);
        await UntilAsync(() => status.Text == "Mouse confirmed step 1.", "DPI Down did not recover and display the first step.");
        Require((await client.GetUnitStateAsync(naga.Info))?.Stages?[0].X == 700, "DPI Down did not apply 700 DPI.");
        await CaptureAsync("naga-managed-dpi");
        naga.SelectedPage = "CUSTOMIZE";
        fixture.SimulateFirmwareDpi(2100, 2200);
        naga.SelectedPage = "PERFORMANCE";
        await UntilAsync(() => status.Text?.Contains("2100 × 2200", StringComparison.Ordinal) == true,
            "Returning to Performance did not report the changed firmware DPI.");
        fixture.SimulateFirmwareDpi(2300, 2400);
        await main.RefreshCommand.ExecuteAsync(null);
        await UntilAsync(() => status.Text?.Contains("2300 × 2400", StringComparison.Ordinal) == true,
            "Refreshing did not report the new firmware DPI.");
        await CaptureAsync("naga-managed-dpi-drift");
        naga.SelectedPage = "CUSTOMIZE";
        Console.WriteLine("Native desktop: managed Naga DPI steps, failure recovery and firmware readback passed.");
    }

    static async Task VerifyNativeRemapAsync(MainWindow window, DeviceView device, DeviceViewModel model,
        DaemonClient client, string settingsPath)
    {
        var row = device.GetVisualDescendants().OfType<Button>().First(button =>
            button.Classes.Contains("keyCell") && button.IsEnabled && button.IsEffectivelyVisible);
        var source = ((KeyRowViewModel)row.DataContext!).Key.Source;
        await TabToAsync(window, row);
        await KeyAsync("space");
        await UntilAsync(() => model.Editor != null, "Keyboard activation did not open the remap editor.");
        var editor = Descendant<BindingFlyout>(device);
        var assignment = Find<ComboBox>(editor, "AssignmentType");
        await UntilAsync(() => assignment.IsFocused, "Opening the editor did not focus the assignment type.");
        var draft = model.Editor ?? throw new InvalidOperationException("The editor did not open.");
        draft.SelectedFunction = BindingEditorViewModel.Functions.Single(entry => entry.Kind == FunctionKind.Keyboard);
        var capture = Find<Button>(editor, "KeyCapture");
        var choose = Find<Button>(editor, "ChooseKey");
        var save = Find<Button>(editor, "SaveBinding");
        var cancel = Find<Button>(editor, "CancelBinding");
        await TabToAsync(window, capture);
        await ArmCaptureAsync();
        await KeyAsync("Tab");
        await UntilAsync(() => choose.IsFocused && draft.CapturedKey == 0 && Equals(capture.Content, "Capture key"),
            "Tab did not leave capture without assigning a key.");
        await KeyAsync("shift+Tab");
        await UntilAsync(() => capture.IsFocused, "Shift+Tab did not return to Capture key.");
        await ArmCaptureAsync();
        await KeyAsync("Escape");
        await UntilAsync(() => capture.IsFocused && draft.CapturedKey == 0 && Equals(capture.Content, "Capture key"),
            "Escape did not cancel capture.");
        await KeyAsync("Tab");
        await UntilAsync(() => choose.IsFocused, "Tab did not focus Choose key.");
        await KeyAsync("space");
        await UntilAsync(() => choose.Flyout is { IsOpen: true }, "Choose key did not open its menu.");
        await KeyAsync("Home", "Return");
        await UntilAsync(() => KeyCodes.Name(draft.CapturedKey) == "KEY_TAB" && choose.Flyout is { IsOpen: false },
            "Choose key did not assign Tab and close its menu.");
        await TabToAsync(window, choose);
        await KeyAsync("space");
        await UntilAsync(() => choose.Flyout is { IsOpen: true }, "Choose key did not reopen its menu.");
        await KeyAsync("End", "Return");
        await UntilAsync(() => KeyCodes.Name(draft.CapturedKey) == "KEY_ESC" && choose.Flyout is { IsOpen: false },
            "Choose key did not assign Escape and close its menu.");
        await TabToAsync(window, capture);
        await ArmCaptureAsync();
        KeyEventArgs? input = null;
        void ObserveInput(object? sender, KeyEventArgs e) => input = e;
        window.AddHandler(InputElement.KeyDownEvent, ObserveInput, RoutingStrategies.Tunnel, handledEventsToo: true);
        try
        {
            // Send the private evdev keymap's F8 keycode. Older xdotool versions can add Alt when resolving the F8 keysym.
            await NativeInputAsync("key", "74");
            await UntilAsync(() => KeyCodes.Name(draft.CapturedKey) == "KEY_F8", "Capture did not receive F8.");
        }
        catch (TimeoutException)
        {
            Console.Error.WriteLine($"Capture input: key={input?.Key}, physical={input?.PhysicalKey}, source={input?.Source?.GetType().Name}; " +
                $"focus={window.FocusManager?.GetFocusedElement()}, label={capture.Content}, selected={KeyCodes.Name(draft.CapturedKey)}.");
            throw;
        }
        finally
        {
            window.RemoveHandler(InputElement.KeyDownEvent, ObserveInput);
        }
        await UntilAsync(() => Equals(capture.Content, "Capture key"), "Capture kept listening after receiving F8.");
        await TabToAsync(window, save);
        await CaptureAsync("remap-save-focused");
        var position = save.TranslatePoint(new Point(0, save.Bounds.Height), window);
        Require(position is { } footer && footer.Y <= window.ClientSize.Height && cancel.IsEffectivelyVisible,
            "The editor footer was cut off at the minimum window size.");

        var original = File.ReadAllBytes(settingsPath);
        File.Delete(settingsPath);
        Directory.CreateDirectory(settingsPath);
        try
        {
            await KeyAsync("space");
            await UntilAsync(() => draft.SaveError != null && save.IsFocused, "The failed binding did not focus Retry save.");
            Require(Equals(save.Content, "Retry save") && KeyCodes.Name(draft.CapturedKey) == "KEY_F8" &&
                row.DataContext is KeyRowViewModel { Changed: false }, "The failed binding appeared saved or lost its draft.");
            await CaptureAsync("remap-retry-focused");
        }
        finally
        {
            Directory.Delete(settingsPath);
            File.WriteAllBytes(settingsPath, original);
        }

        await KeyAsync("space");
        await UntilAsync(() => model.Editor == null && row.IsFocused && model.SaveStatus.StartsWith("Saved to profile", StringComparison.Ordinal),
            "Successful retry did not restore focus to the edited row.");
        var saved = (await client.GetSettingsAsync(model.Key)).ActiveKeymapOrFirst.Resolve(source, false);
        Require(saved is KeyboardBinding first && KeyCodes.Name(first.Key) == "KEY_F8", "Retry did not persist F8.");
        Require(window.DataContext is MainViewModel { Problem: null }, "A stale save banner remained after successful retry.");
        await CaptureAsync("remap-recovered");

        await KeyAsync("space");
        await UntilAsync(() => model.Editor != null, "Keyboard activation did not reopen the remap editor.");
        var reopened = Descendant<BindingFlyout>(device);
        model.Editor!.SelectedFunction = BindingEditorViewModel.Functions[0];
        await TabToAsync(window, Find<Button>(reopened, "CancelBinding"));
        await KeyAsync("space");
        await UntilAsync(() => model.Editor == null && row.IsFocused, "Cancelling did not restore focus to the edited row.");
        Require(saved == (await client.GetSettingsAsync(model.Key)).ActiveKeymapOrFirst.Resolve(source, false),
            "Cancelling changed the saved binding.");

        await KeyAsync("space");
        await UntilAsync(() => model.Editor != null, "Keyboard activation did not reopen the editor for the default binding.");
        reopened = Descendant<BindingFlyout>(device);
        model.Editor!.SelectedFunction = BindingEditorViewModel.Functions[0];
        await TabToAsync(window, Find<Button>(reopened, "SaveBinding"));
        await KeyAsync("space");
        await UntilAsync(() => model.Editor == null && row.IsFocused, "Saving did not restore focus to the edited row.");
        Require((await client.GetSettingsAsync(model.Key)).ActiveKeymapOrFirst.Resolve(source, false) is DefaultBinding,
            "A normal keyboard save did not restore the default binding.");
        Console.WriteLine("Native desktop: Tab navigation, visible focus, remap retry and focus return passed.");

        async Task ArmCaptureAsync()
        {
            // Send Space by its private evdev keycode so arming cannot also restore a modifier.
            await NativeInputAsync("key", "65");
            await UntilAsync(() => capture.IsFocused && Equals(capture.Content, "Listening…"),
                "Capture did not enter listening mode with keyboard focus.");
        }
    }

    static async Task<HomeView> VerifyShortcutFocusAsync(Window window, HomeView home, MainViewModel main, DaemonClient client)
    {
        var shortcuts = main.Home.Shortcuts;
        var keypad = main.Devices.Single(device => device.Key == "02eb");
        var keys = keypad.Rows.Where(row => row.Bindable).Take(2).Select(row => row.Key.Source).ToArray();
        var existing = GlobalShortcut.Create(keys, new KeyboardBinding(46));
        await client.SetShortcutsAsync(new ShortcutList([existing]), await client.GetShortcutsAsync());
        await shortcuts.LoadAsync();
        Button EditButton() => home.GetVisualDescendants().OfType<Button>().Single(button =>
            button.DataContext is ShortcutRow row && row.Shortcut.Id == existing.Id &&
            AutomationProperties.GetName(button) == "Edit shortcut");

        var original = EditButton();
        await TabToAsync(window, original);
        await KeyAsync("space");
        await UntilAsync(() => shortcuts.Editing, "Keyboard activation did not start shortcut editing.");
        main.Show(main.Settings);
        main.Show(main.Home);
        home = Descendant<HomeView>(window);
        var editor = Descendant<BindingFlyout>(home);
        shortcuts.Editor!.CapturedKey = 47;
        await TabToAsync(window, Find<Button>(editor, "SaveBinding"));
        await KeyAsync("space");
        await UntilAsync(() => !shortcuts.Editing && !ReferenceEquals(original, EditButton()) && EditButton().IsFocused,
            "Saving a shortcut did not return focus to its rebuilt edit button.");
        Require(EditButton().Classes.Contains(":focus-visible"), "The rebuilt shortcut edit button has no visible focus.");
        await UntilAsync(() => shortcuts.SaveStatus == "Global shortcuts saved.", "The edited shortcut was not saved.");
        Require((await client.GetShortcutsAsync()).Shortcuts.Single().Binding == new KeyboardBinding(47),
            "The edited shortcut did not preserve its new action.");
        await KeyAsync("space");
        await UntilAsync(() => shortcuts.Editing, "Keyboard activation did not reopen the shortcut editor.");
        await TabToAsync(window, Find<Button>(Descendant<BindingFlyout>(home), "CancelBinding"));
        await KeyAsync("space");
        await UntilAsync(() => !shortcuts.Editing && EditButton().IsFocused, "Cancelling did not focus the edited shortcut.");

        await KeyAsync("space");
        await UntilAsync(() => shortcuts.Editing, "The shortcut editor did not reopen before removal.");
        shortcuts.DeleteCommand.Execute(shortcuts.Rows.Single());
        await UntilAsync(() => !shortcuts.Editing && shortcuts.Rows.Count == 0 && Find<Button>(home, "AddShortcutButton").IsFocused,
            "Removing the edited shortcut did not focus Add a shortcut.");
        Console.WriteLine("Native desktop: rebuilt shortcut focus, cancel and removal fallback passed.");
        return home;
    }

    static async Task TabToAsync(Window window, Control control)
    {
        await UntilAsync(() => control.IsEffectivelyVisible && control.IsEffectivelyEnabled,
            $"The Tab target {control.Name ?? control.GetType().Name} did not become available.");
        if (control.IsFocused && !control.Classes.Contains(":focus-visible"))
            await AdvanceTabAsync();
        for (var i = 0; i < 90 && !control.IsFocused; ++i)
            await AdvanceTabAsync();
        await UntilAsync(() => control.IsFocused && control.Classes.Contains(":focus-visible"),
            $"{control.Name ?? control.GetType().Name} was not visibly reachable using Tab; focus is {window.FocusManager?.GetFocusedElement()}.");

        async Task AdvanceTabAsync()
        {
            var previous = window.FocusManager?.GetFocusedElement();
            await KeyAsync("Tab");
            await UntilAsync(() => window.FocusManager?.GetFocusedElement() is { } focused && !ReferenceEquals(previous, focused),
                $"Tab did not move focus from {previous}.");
        }
    }

    static Task KeyAsync(params string[] keys) => NativeInputAsync(["key", "--clearmodifiers", .. keys]);

    static async Task CaptureAsync(string state)
    {
        if (Environment.GetEnvironmentVariable("VIPER_DESKTOP_SMOKE_ARTIFACTS") is not { Length: > 0 } directory)
            return;
        Directory.CreateDirectory(directory);
        await Task.Delay(200);
        await RunToolAsync("import", "-window", "root", Path.Combine(directory, state + ".png"));
    }

    static Task NativeInputAsync(params string[] arguments) => RunToolAsync("xdotool", arguments);

    static async Task RunToolAsync(string executable, params string[] arguments)
    {
        var start = new ProcessStartInfo(executable) { UseShellExecute = false };
        foreach (var argument in arguments)
            start.ArgumentList.Add(argument);
        using var process = Process.Start(start) ?? throw new InvalidOperationException($"Could not start {executable}.");
        await process.WaitForExitAsync().WaitAsync(TimeSpan.FromSeconds(5));
        Require(process.ExitCode == 0, $"{executable} exited with status {process.ExitCode}.");
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
