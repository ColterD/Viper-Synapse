using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.ApplicationLifetimes;
using Avalonia.Layout;
using Avalonia.Markup.Xaml;
using Avalonia.Platform;
using Avalonia.Threading;
using ViperSynapse.App.Services;
using ViperSynapse.App.ViewModels;
using ViperSynapse.App.Views;

namespace ViperSynapse.App;

public sealed class App : Application
{
    internal static StartupState? Start { get; set; }

    IClassicDesktopStyleApplicationLifetime? _desktop;
    DaemonClient? _client;
    MainViewModel? _main;
    MainWindow? _window;
    bool _quitting;
    bool _requestingQuit;

    public override void Initialize() => AvaloniaXamlLoader.Load(this);

    public override void OnFrameworkInitializationCompleted()
    {
        if (ApplicationLifetime is IClassicDesktopStyleApplicationLifetime desktop)
        {
            var start = Start ?? new StartupState(new AppPreferences(), false, false, new WindowBus());
            _desktop = desktop;
            _client = new DaemonClient();
            _main = new MainViewModel(_client, start.Preferences, start.StartAtLogin, start.Built);
            BuildTray();
            start.Bus.ShowRequested += () => Dispatcher.UIThread.Post(ShowWindow);
            start.Bus.ReplaceRequested += () => Dispatcher.UIThread.Post(Quit);
            if (!start.Background)
                ShowWindow();
            _ = _main.StartAsync();
        }
        base.OnFrameworkInitializationCompleted();
    }

    void BuildTray()
    {
        var open = new NativeMenuItem($"Open {AppBranding.Name}");
        open.Click += (_, _) => ShowWindow();
        var quit = new NativeMenuItem("Quit");
        quit.Click += (_, _) => Quit();
        var tray = new TrayIcon
        {
            Icon = new WindowIcon(AssetLoader.Open(new Uri("avares://viper-synapse/Assets/icon.png"))),
            ToolTipText = AppBranding.Name,
            Menu = [open, new NativeMenuItemSeparator(), quit],
        };
        tray.Clicked += (_, _) => ShowWindow();
        TrayIcon.SetIcons(this, [tray]);
    }

    void ShowWindow()
    {
        if (_quitting)
            return;

        if (_window == null)
        {
            _window = new MainWindow { DataContext = _main };
            _window.Closing += (_, e) =>
            {
                if (_quitting)
                    return;
                e.Cancel = true;
                if (_main!.Settings.CloseToTray)
                {
                    // Closing hides: bindings and macros keep running, the tray brings the window back.
                    _window.Hide();
                }
                else
                {
                    e.Cancel = true;
                    Quit();
                }
            };
        }
        _window.Show();
        _window.WindowState = _window.WindowState == WindowState.Minimized ? WindowState.Normal : _window.WindowState;
        _window.Activate();
    }

    /// <summary>Quits the app and its service, as Synapse's Exit does: software bindings and macros stop with it.</summary>
    void Quit() => Dispatcher.UIThread.Post(() => _ = RequestQuitAsync());

    enum QuitChoice { KeepEditing, Discard, Save }

    async Task RequestQuitAsync()
    {
        if (_requestingQuit || _quitting || _main is not { } main || _client is not { } client)
            return;
        _requestingQuit = true;
        try
        {
            if (main.Macros.Dirty)
            {
                var choice = await ConfirmQuitAsync("This macro has unsaved changes.", allowSave: true);
                if (choice == QuitChoice.KeepEditing)
                    return;
                if (choice == QuitChoice.Save)
                    main.Macros.SaveCommand.Execute(null);
            }

            if (_window != null)
                _window.IsEnabled = false;
            bool saved;
            try { saved = await main.FinishWorkspaceEditsAsync(TimeSpan.FromSeconds(5)); }
            catch (TimeoutException) { saved = false; }
            if (!saved && await ConfirmQuitAsync("Some changes could not be saved. Keep editing to retry, or quit and discard those changes.",
                    allowSave: false) == QuitChoice.KeepEditing)
                return;

            _quitting = true;
            if (_window != null)
                _window.IsEnabled = false;
            try { await client.QuitServiceAsync().WaitAsync(TimeSpan.FromSeconds(3)); }
            catch (Exception e) when (e is TimeoutException or InvalidOperationException or Tmds.DBus.Protocol.DBusErrorReplyException or Tmds.DBus.Protocol.DBusConnectionException) { }
            try { await client.DisposeAsync(); }
            finally { _desktop?.Shutdown(); }
        }
        finally
        {
            _requestingQuit = false;
            if (!_quitting && _window != null)
                _window.IsEnabled = true;
        }
    }

    Task<QuitChoice> ConfirmQuitAsync(string message, bool allowSave)
    {
        ShowWindow();
        var owner = _window!;
        owner.IsEnabled = true;
        var dialog = new Window
        {
            Title = $"Quit {AppBranding.Name}?",
            SizeToContent = SizeToContent.WidthAndHeight,
            CanResize = false,
            WindowStartupLocation = WindowStartupLocation.CenterOwner,
        };
        Button Choose(string text, QuitChoice choice)
        {
            var button = new Button { Content = text };
            button.Click += (_, _) => dialog.Close(choice);
            return button;
        }
        var cancel = Choose("Keep editing", QuitChoice.KeepEditing);
        cancel.IsCancel = true;
        var actions = new WrapPanel { HorizontalAlignment = HorizontalAlignment.Right };
        actions.Children.Add(cancel);
        actions.Children.Add(Choose("Quit without saving", QuitChoice.Discard));
        if (allowSave)
            actions.Children.Add(Choose("Save and quit", QuitChoice.Save));
        dialog.Content = new StackPanel
        {
            Margin = new Thickness(20),
            Width = 360,
            Spacing = 16,
            Children = { new TextBlock { Text = message, TextWrapping = Avalonia.Media.TextWrapping.Wrap }, actions },
        };
        dialog.Opened += (_, _) => cancel.Focus();
        return dialog.ShowDialog<QuitChoice>(owner);
    }
}
