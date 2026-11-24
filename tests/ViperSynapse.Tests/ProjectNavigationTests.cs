using System.Reflection;
using Avalonia.Controls;
using Avalonia.Input.Platform;
using Avalonia.Interactivity;
using Avalonia.Threading;
using Avalonia.VisualTree;
using ViperSynapse.App.Services;
using ViperSynapse.App.ViewModels;
using ViperSynapse.App.Views;

namespace ViperSynapse.Tests;

public sealed class ProjectNavigationTests
{
    [Fact]
    public Task General_settings_reflow_without_resetting_the_accent_picker() =>
        HeadlessApp.Session.Dispatch(() =>
        {
            var settings = new MainViewModel(new DaemonClient(), new AppPreferences(), false).Settings;
            settings.SelectedPage = "GENERAL";
            var view = new SettingsView { DataContext = settings };
            var host = new Border { Width = 1440, Child = view };
            var window = new Window { Width = 1500, Height = 760, Content = host };
            window.Show();
            try
            {
                window.UpdateLayout();
                var left = view.FindControl<StackPanel>("GeneralPreferences")!;
                var right = view.FindControl<StackPanel>("GeneralProject")!;
                var picker = view.FindControl<ComboBox>("AccentChoiceBox")!;
                var accent = picker.SelectedItem;
                Assert.NotNull(accent);
                Assert.True(picker.Focus());
                Assert.Equal(left.Bounds.Y, right.Bounds.Y);
                Assert.True(left.Bounds.X < right.Bounds.X);

                host.Width = 1080;
                window.UpdateLayout();
                Assert.Equal(left.Bounds.X, right.Bounds.X);
                Assert.True(left.Bounds.Bottom <= right.Bounds.Y);
                Assert.Same(accent, picker.SelectedItem);
                Assert.True(picker.IsFocused);

                host.Width = 1440;
                window.UpdateLayout();
                Assert.Equal(left.Bounds.Y, right.Bounds.Y);
                Assert.Same(accent, picker.SelectedItem);
            }
            finally
            {
                window.Close();
            }
        }, CancellationToken.None);

    [Fact]
    public Task Settings_explains_the_build_and_links_to_the_fork_release() =>
        HeadlessApp.Session.Dispatch(() =>
        {
            var settings = new MainViewModel(new DaemonClient(), new AppPreferences(), false).Settings;
            settings.SelectedPage = "GENERAL";
            var view = new SettingsView { DataContext = settings };
            var window = new Window { Content = view };
            window.Show();
            try
            {
                Dispatcher.UIThread.RunJobs();
                Assert.Equal(AppBranding.Name, view.FindControl<TextBlock>("AboutAppName")!.Text);
                var version = typeof(SettingsViewModel).Assembly.GetCustomAttribute<AssemblyInformationalVersionAttribute>()!
                    .InformationalVersion.Split('+', 2)[0];
                Assert.Equal($"Running build {version}", view.FindControl<TextBlock>("RunningBuild")!.Text);
                var notes = Assert.IsType<HyperlinkButton>(view.FindControl<HyperlinkButton>("BuildReleaseNotes"));
                Assert.Equal($"v{version}", notes.Content);
                Assert.Equal($"/ColterD/Viper-Synapse/releases/tag/v{version}", notes.NavigateUri!.AbsolutePath);
                var latest = Assert.IsType<HyperlinkButton>(view.FindControl<HyperlinkButton>("LatestStableRelease"));
                Assert.Equal("/ColterD/Viper-Synapse/releases/latest", latest.NavigateUri!.AbsolutePath);
                var repository = Assert.IsType<HyperlinkButton>(view.FindControl<HyperlinkButton>("ForkRepository"));
                Assert.Equal("/ColterD/Viper-Synapse", repository.NavigateUri!.AbsolutePath);
            }
            finally
            {
                window.Close();
            }
        }, CancellationToken.None);

    [Fact]
    public Task Help_opens_a_menu_with_the_fork_documentation_link() =>
        HeadlessApp.Session.Dispatch(async () =>
        {
            var main = new MainViewModel(new DaemonClient(), new AppPreferences(), false);
            var window = new MainWindow { DataContext = main };
            window.Show();
            try
            {
                Assert.Equal("DEVICES", main.Home.Header);
                Assert.Equal(AppBranding.Name, window.Title);
                Assert.Equal(TabIcon.Devices, main.Home.Icon);
                var icon = window.GetVisualDescendants().OfType<PathIcon>().FirstOrDefault(path =>
                    ReferenceEquals(path.DataContext, main.Home));
                Assert.NotNull(icon?.Data);
                var help = Assert.IsType<Button>(window.FindControl<Button>("HelpButton"));
                var menu = Assert.IsType<Flyout>(help.Flyout);
                menu.ShowAt(help);
                Dispatcher.UIThread.RunJobs();
                var content = Assert.IsType<StackPanel>(menu.Content);
                var docs = Assert.Single(content.Children.OfType<HyperlinkButton>(), link => Equals(link.Content, "Documentation library"));
                Assert.Equal($"https://github.com/ColterD/Viper-Synapse/blob/v{AppBranding.Version}/docs/README.md", docs.NavigateUri!.AbsoluteUri);
                var copy = Assert.Single(content.Children.OfType<Button>(), button => Equals(button.Content, "Copy documentation link"));
                copy.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
                Assert.Equal(docs.NavigateUri.AbsoluteUri, await window.Clipboard!.TryGetTextAsync());
                menu.Hide();
            }
            finally
            {
                window.Close();
            }
            return true;
        }, CancellationToken.None);
}
