using System.Text.Json;
using Avalonia.Controls;
using Avalonia.Media;
using Avalonia.Threading;
using ViperSynapse.App.Controls;
using ViperSynapse.App.Services;
using ViperSynapse.App.ViewModels;
using ViperSynapse.App.Views;

namespace ViperSynapse.Tests;

public sealed class AppearanceTests
{
    [Fact]
    public Task Changing_the_accent_updates_an_already_open_view() => HeadlessApp.Session.Dispatch<bool>(async () =>
    {
        var main = new MainViewModel(new DaemonClient(), new AppPreferences(), false);
        var writes = new List<AppPreferences>();
        var firstWrite = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var model = new SettingsViewModel(main, new AppPreferences(), false)
        {
            SelectedPage = "GENERAL",
            WritePreferences = async preferences =>
            {
                writes.Add(preferences);
                if (writes.Count == 1)
                    await firstWrite.Task;
            },
        };
        var settings = new SettingsView { DataContext = model };
        var heading = new TextBlock { Classes = { "heading" }, Text = "Preview" };
        var slider = new ValueSlider();
        var stick = new StickView();
        var drawing = new DeviceDrawing();
        var window = new Window { Content = new StackPanel { Children = { heading, slider, stick, drawing, settings } } };
        window.Show();
        try
        {
            var picker = Assert.IsType<ComboBox>(settings.FindControl<ComboBox>("AccentChoiceBox"));
            Assert.True(picker.IsEffectivelyVisible);
            Assert.Equal(Color.Parse("#44D62C"), Assert.IsAssignableFrom<ISolidColorBrush>(heading.Foreground).Color);
            picker.SelectedIndex = 1;
            Dispatcher.UIThread.RunJobs();
            var blue = Color.Parse("#70BEFF");
            Assert.Equal(blue, Assert.IsAssignableFrom<ISolidColorBrush>(heading.Foreground).Color);
            Assert.Equal(blue, Assert.IsAssignableFrom<ISolidColorBrush>(Assert.IsType<Border>(slider.Children[0]).Background).Color);
            Assert.Equal(blue, stick.AccentColor);
            Assert.Equal(blue, drawing.AccentColor);

            picker.SelectedIndex = 2;
            Dispatcher.UIThread.RunJobs();
            Assert.Equal("blue", Assert.Single(writes).Accent);
            firstWrite.SetResult();
            await model.PendingSave;
            Assert.Equal(new[] { "blue", "teal" }, writes.Select(preferences => preferences.Accent));
            var saved = JsonSerializer.Deserialize(
                JsonSerializer.Serialize(writes[^1], PreferencesJson.Default.AppPreferences), PreferencesJson.Default.AppPreferences)!;
            Assert.Equal("teal", new SettingsViewModel(main, saved, false).SelectedAccent?.Id);
            Assert.Equal(Color.Parse("#5CDBC5"), Assert.IsAssignableFrom<ISolidColorBrush>(heading.Foreground).Color);
            return true;
        }
        finally
        {
            firstWrite.TrySetResult();
            window.Close();
            AppAppearance.Apply(AppAppearance.Resolve(null));
        }
    }, CancellationToken.None);

    [Theory]
    [InlineData("{}")]
    [InlineData("{\"accent\":\"unknown\"}")]
    public void Old_or_unknown_preferences_keep_the_default_accent(string json)
    {
        var preferences = JsonSerializer.Deserialize(json, PreferencesJson.Default.AppPreferences)!;
        Assert.Equal("green", AppAppearance.Resolve(preferences.Accent).Id);
    }

    [Fact]
    public Task Dashboard_replaces_setup_guidance_when_a_device_appears() => HeadlessApp.Session.Dispatch<bool>(async () =>
    {
        var main = new MainViewModel(new DaemonClient(), new AppPreferences(), false);
        var dashboard = new HomeView { DataContext = main.Home };
        var window = new Window { Content = dashboard };
        window.Show();
        try
        {
            await main.RefreshDevicesAsync(() => Task.FromResult<IReadOnlyList<DeviceInfo>>([]));
            var hint = Assert.IsType<Border>(dashboard.FindControl<Border>("DeviceSetupHint"));
            Assert.True(hint.IsEffectivelyVisible);
            var info = new DeviceInfo("00e3", "00e4", "Basilisk", "mouse", "bluetooth:123456123456",
                true, false, null, null, null, [], false, null, Connection: "bluetooth");
            main.Home.Devices.Add(new DeviceViewModel(info, main));
            Dispatcher.UIThread.RunJobs();
            Assert.False(hint.IsEffectivelyVisible);
            main.Home.Devices.Clear();
            Dispatcher.UIThread.RunJobs();
            Assert.True(hint.IsEffectivelyVisible);
            return true;
        }
        finally
        {
            window.Close();
        }
    }, CancellationToken.None);
}
