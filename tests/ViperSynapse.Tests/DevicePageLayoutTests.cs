using Avalonia;
using Avalonia.Automation;
using Avalonia.Controls;
using Avalonia.Headless;
using Avalonia.Input;
using Avalonia.Media;
using Avalonia.Threading;
using Avalonia.VisualTree;
using ViperSynapse.App.Controls;
using ViperSynapse.App.Services;
using ViperSynapse.App.ViewModels;
using ViperSynapse.App.Views;
using ViperSynapse.Core.Bindings;
using ViperSynapse.Core.Devices;

namespace ViperSynapse.Tests;

public sealed class DevicePageLayoutTests
{
    [Fact]
    public Task Device_and_macro_editors_reflow_at_the_minimum_width() =>
        HeadlessApp.Session.Dispatch(() =>
        {
            var main = new MainViewModel(new DaemonClient(), new AppPreferences(), false);
            var info = new DeviceInfo("02eb", "02eb", "Razer Tartarus V2 Pro", "keypad", "usb-emulated",
                true, true, "FIXTURE", "v1.00", "/dev/emulated", [], false, null);
            var device = new DeviceViewModel(info, main) { ShowKeyList = true };
            var view = new DeviceView { DataContext = device };
            var macro = new MacrosView { DataContext = main.Macros };
            var host = new Border { Width = 1080, Child = view };
            var window = new Window { Width = 1500, Height = 680, Content = host };
            window.Show();
            try
            {
                window.UpdateLayout();
                var pages = Assert.IsType<ListBox>(view.FindControl<ListBox>("DevicePages"));
                var profile = Assert.IsType<StackPanel>(view.FindControl<StackPanel>("ProfileControls"));
                var keyList = Assert.IsType<Border>(view.FindControl<Border>("CustomizeKeyList"));
                var workArea = Assert.IsType<Panel>(view.FindControl<Panel>("CustomizeWorkArea"));
                var drawing = Assert.IsType<Panel>(view.FindControl<Panel>("CustomizeDrawing"));
                var stick = Assert.IsType<Border>(view.FindControl<Border>("CustomizeThumbstick"));
                Assert.True(pages.TranslatePoint(default, view)!.Value.Y < profile.Bounds.Top);
                Assert.True(keyList.Bounds.Bottom <= workArea.Bounds.Top);
                Assert.True(drawing.Bounds.Bottom <= stick.Bounds.Top);

                host.Width = 1440;
                window.UpdateLayout();
                Assert.True(keyList.Bounds.Right <= workArea.Bounds.Left);
                Assert.True(drawing.Bounds.Right <= stick.Bounds.Left);

                host.Child = macro;
                host.Width = 1080;
                window.UpdateLayout();
                var add = Assert.IsType<Border>(macro.FindControl<Border>("MacroAdd"));
                var actions = Assert.IsType<Border>(macro.FindControl<Border>("MacroActions"));
                Assert.True(add.Bounds.Bottom <= actions.Bounds.Top);
                Assert.True(actions.Bounds.Height > 0);

                host.Width = 1440;
                window.UpdateLayout();
                Assert.True(add.Bounds.Right <= actions.Bounds.Left);
                Assert.True(actions.Bounds.Width > 470);
            }
            finally
            {
                window.Close();
                device.Detach();
            }
        }, CancellationToken.None);

    [Fact]
    public Task Individual_key_lighting_can_be_painted_by_keyboard() =>
        HeadlessApp.Session.Dispatch(() =>
        {
            var main = new MainViewModel(new DaemonClient(), new AppPreferences(), false);
            var info = new DeviceInfo("02eb", "02eb", "Razer Tartarus V2 Pro", "keypad", "usb-emulated",
                true, true, "FIXTURE", "v1.00", "/dev/emulated", [], false, null);
            var device = new DeviceViewModel(info, main);
            device.Lighting.Load(null);
            device.Lighting.EffectTab = LightingViewModel.EffectTabs[1];
            var view = new DeviceLightingView { DataContext = device };
            var window = new Window { Width = 1080, Height = 680, Content = view };
            window.Show();
            try
            {
                window.UpdateLayout();
                var list = Assert.IsType<ItemsControl>(view.FindControl<ItemsControl>("PaintKeyList"));
                var button = list.GetVisualDescendants().OfType<Button>().First();
                var key = Assert.IsType<DeviceKey>(button.DataContext);
                Assert.Equal($"Paint {key.Label}", AutomationProperties.GetName(button));
                var colour = Color.FromRgb(0x26, 0x6F, 0xAE);
                device.Lighting.PaintColor = colour;
                Assert.True(button.Focus(NavigationMethod.Tab));
                window.KeyPressQwerty(PhysicalKey.Space, RawInputModifiers.None);
                window.KeyReleaseQwerty(PhysicalKey.Space, RawInputModifiers.None);
                Dispatcher.UIThread.RunJobs();
                Assert.Equal(colour, device.Lighting.KeyColors![key.Number]);
                Assert.Same(button, window.FocusManager!.GetFocusedElement());
            }
            finally
            {
                window.Close();
                device.Detach();
            }
        }, CancellationToken.None);

    [Fact]
    public Task Health_and_lighting_have_separate_views_and_share_page_styles() =>
        HeadlessApp.Session.Dispatch(() =>
        {
            var main = new MainViewModel(new DaemonClient(), new AppPreferences(), false);
            var info = new DeviceInfo("00e3", "00e3", "Razer Basilisk V4 Pro", "mouse", "usb-emulated",
                true, true, "FIXTURE", "v1.00", "/dev/emulated", ["/dev/events"], false, null,
                Connection: "hyperspeed", ControlReady: true);
            var device = new DeviceViewModel(info, main);
            var view = new DeviceView { DataContext = device };
            var window = new Window { Width = 1080, Height = 680, Content = view };
            window.Show();
            try
            {
                var lighting = Assert.IsAssignableFrom<UserControl>(view.FindControl<UserControl>("LightingPage"));
                var health = Assert.IsAssignableFrom<UserControl>(view.FindControl<UserControl>("HealthPage"));
                var pages = Assert.IsType<ListBox>(view.FindControl<ListBox>("DevicePages"));

                pages.SelectedItem = "LIGHTING";
                Dispatcher.UIThread.RunJobs();
                Assert.True(lighting.IsEffectivelyVisible);
                Assert.False(health.IsEffectivelyVisible);
                var lightingTitle = Assert.IsType<TextBlock>(lighting.FindControl<TextBlock>("DevicePageTitle"));

                pages.SelectedItem = "HEALTH";
                Dispatcher.UIThread.RunJobs();
                Assert.True(health.IsEffectivelyVisible);
                Assert.False(lighting.IsEffectivelyVisible);
                var healthTitle = Assert.IsType<TextBlock>(health.FindControl<TextBlock>("DevicePageTitle"));
                Assert.Equal("Lighting", lightingTitle.Text);
                Assert.Equal("Health and compatibility", healthTitle.Text);
                Assert.Contains("devicePageTitle", healthTitle.Classes);
                Assert.Contains("devicePageTitle", lightingTitle.Classes);
                Assert.Equal(healthTitle.FontSize, lightingTitle.FontSize);
                Assert.Equal(healthTitle.FontWeight, lightingTitle.FontWeight);
            }
            finally
            {
                device.Detach();
                window.Close();
            }
        }, CancellationToken.None);

    [Fact]
    public Task Extracted_lighting_canvas_uses_the_selected_device_and_renders_frame_changes() =>
        HeadlessApp.Session.Dispatch<bool>(async () =>
        {
            var main = new MainViewModel(new DaemonClient(), new AppPreferences(), false)
            {
                WriteDeviceSettingsAsync = (_, _) => Task.CompletedTask,
            };
            var info = new DeviceInfo("02eb", "02eb", "Razer Tartarus V2 Pro", "keypad", "usb-emulated",
                true, true, "FIXTURE", "v1.00", "/dev/emulated", [], false, null);
            var device = new DeviceViewModel(info, main) { SelectedPage = "LIGHTING" };
            await device.ReloadSettingsAsync(_ => Task.FromResult(DeviceSettings.Create()));
            device.Lighting.EffectTab = LightingViewModel.EffectTabs[1];
            Assert.True(device.Lighting.ShowKeyCanvas);
            var view = new DeviceView { DataContext = device };
            var window = new Window { Width = 1080, Height = 680, Content = view };
            window.Show();
            try
            {
                var lighting = Assert.IsType<DeviceLightingView>(view.FindControl<DeviceLightingView>("LightingPage"));
                Dispatcher.UIThread.RunJobs();
                Assert.True(lighting.IsEffectivelyEnabled);
                var canvas = Assert.IsType<DeviceDrawing>(lighting.FindControl<DeviceDrawing>("PaintCanvas"));
                Assert.True(canvas.IsEffectivelyVisible);
                Assert.Same(device, canvas.Device);

                var fill = lighting.GetVisualDescendants().OfType<Button>().Single(button => Equals(button.Content, "Fill all"));
                var paint = Color.FromRgb(0x37, 0x73, 0xC6);
                device.Lighting.PaintColor = paint;
                fill.Command!.Execute(null);
                Dispatcher.UIThread.RunJobs();
                Assert.NotEmpty(canvas.PaintColors!);
                Assert.All(canvas.PaintColors!.Values, color => Assert.Equal(paint, color));

                canvas.BringIntoView();
                Dispatcher.UIThread.RunJobs();
                // This point is inside the top row of the Tartarus drawing.
                var target = canvas.TranslatePoint(new Point(canvas.Bounds.Width * 0.52, 51), window);
                Assert.NotNull(target);
                var singleKey = Color.FromRgb(0xE8, 0x9A, 0x29);
                device.Lighting.PaintColor = singleKey;
                window.MouseDown(target.Value, MouseButton.Left);
                window.MouseUp(target.Value, MouseButton.Left);
                Dispatcher.UIThread.RunJobs();
                Assert.Contains(singleKey, canvas.PaintColors!.Values);
                Assert.Contains(paint, canvas.PaintColors!.Values);
            }
            finally
            {
                device.Detach();
                window.Close();
            }
            return true;
        }, CancellationToken.None);

}
