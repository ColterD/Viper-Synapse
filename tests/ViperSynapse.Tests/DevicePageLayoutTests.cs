using Avalonia;
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

namespace ViperSynapse.Tests;

public sealed class DevicePageLayoutTests
{
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
        HeadlessApp.Session.Dispatch(() =>
        {
            var main = new MainViewModel(new DaemonClient(), new AppPreferences(), false);
            var info = new DeviceInfo("02eb", "02eb", "Razer Tartarus V2 Pro", "keypad", "usb-emulated",
                true, true, "FIXTURE", "v1.00", "/dev/emulated", [], false, null);
            var device = new DeviceViewModel(info, main) { SelectedPage = "LIGHTING" };
            device.Lighting.Load(null);
            device.Lighting.EffectTab = LightingViewModel.EffectTabs[1];
            Assert.True(device.Lighting.ShowKeyCanvas);
            var view = new DeviceView { DataContext = device };
            var window = new Window { Width = 1080, Height = 680, Content = view };
            window.Show();
            try
            {
                var lighting = Assert.IsType<DeviceLightingView>(view.FindControl<DeviceLightingView>("LightingPage"));
                Dispatcher.UIThread.RunJobs();
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
        }, CancellationToken.None);

}
