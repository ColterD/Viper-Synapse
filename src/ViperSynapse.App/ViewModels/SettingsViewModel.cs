using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using ViperSynapse.App.Services;

namespace ViperSynapse.App.ViewModels;

/// <summary>The SETTINGS tab, as Synapse 4 lays it out: SYNAPSE (auto-launch, the tray, notifications) and GENERAL
/// (appearance, language, what's new, about). Start at sign-in is off unless the user turns it on.</summary>
public sealed partial class SettingsViewModel : TabViewModel
{
    AppPreferences _preferences;
    readonly bool _ready;
    readonly string _built;
    OrderedSave<AppPreferences>? _preferenceSaves;
    OrderedSave<(bool Enabled, bool Minimized)>? _autostartSaves;

    OrderedSave<AppPreferences> PreferenceSaves => _preferenceSaves ??= new(WritePreferences, SaveStateChanged);
    OrderedSave<(bool Enabled, bool Minimized)> AutostartSaves => _autostartSaves ??=
        new(state => WriteAutostartAsync(state.Enabled, state.Minimized), SaveStateChanged);

    /// <summary>Creates the settings tab from the preferences and autostart state read at startup.</summary>
    /// <param name="preferences">Read before the UI thread started.</param>
    /// <param name="startAtLogin">Whether the autostart entry exists, also read before the UI thread started.</param>
    /// <param name="built">When this build was made.</param>
    public SettingsViewModel(AppPreferences preferences, bool startAtLogin, string built = "")
    {
        _preferences = preferences;
        _built = built;
        StartAtLogin = startAtLogin;
        StartMinimized = preferences.StartMinimized;
        CloseToTray = preferences.CloseToTray;
        Notifications = preferences.Notifications;
        SelectedAccent = AppAppearance.Resolve(preferences.Accent);
        _ready = true;
    }

    public override string Header => "SETTINGS";
    public override TabIcon Icon => TabIcon.Settings;

    public static IReadOnlyList<string> Pages { get; } = ["SYNAPSE", "GENERAL"];
    public static IReadOnlyList<AccentOption> Accents => AppAppearance.Accents;
    public static IReadOnlyList<string> Languages { get; } = ["English"];

    [ObservableProperty] public partial string SelectedPage { get; set; } = "SYNAPSE";
    [ObservableProperty] public partial bool StartAtLogin { get; set; }
    [ObservableProperty] public partial bool StartMinimized { get; set; }
    [ObservableProperty] public partial bool CloseToTray { get; set; }
    [ObservableProperty] public partial bool Notifications { get; set; }
    [ObservableProperty] public partial string Language { get; set; } = "English";

    [ObservableProperty] public partial AccentOption? SelectedAccent { get; set; }

    public bool ShowSynapse => SelectedPage == "SYNAPSE";
    public bool ShowGeneral => SelectedPage == "GENERAL";

    public string Version => $"Running build {typeof(SettingsViewModel).Assembly.GetName().Version?.ToString(3)}" + (_built.Length > 0 ? $"  ·  built {_built}" : "");

    partial void OnSelectedPageChanged(string value)
    {
        OnPropertyChanged(nameof(ShowSynapse));
        OnPropertyChanged(nameof(ShowGeneral));
    }

    partial void OnStartAtLoginChanged(bool value) => WriteAutostart();

    partial void OnStartMinimizedChanged(bool value)
    {
        Save(_preferences with { StartMinimized = value });
        WriteAutostart();
    }

    partial void OnCloseToTrayChanged(bool value) => Save(_preferences with { CloseToTray = value });

    partial void OnNotificationsChanged(bool value) => Save(_preferences with { Notifications = value });

    partial void OnSelectedAccentChanged(AccentOption? value)
    {
        if (value is null)
            return;
        AppAppearance.Apply(value);
        Save(_preferences with { Accent = value.Id });
    }

    void WriteAutostart()
    {
        if (_ready)
            AutostartSaves.Enqueue((StartAtLogin, StartMinimized));
    }

    void Save(AppPreferences preferences)
    {
        if (!_ready)
            return;
        _preferences = preferences;
        PreferenceSaves.Enqueue(preferences);
    }

    internal Task PendingSave => Task.WhenAll(PreferenceSaves.Pending, AutostartSaves.Pending);
    public bool HasFailedSave => PreferenceSaves.Failure != null || AutostartSaves.Failure != null;
    internal Func<AppPreferences, Task> WritePreferences { get; init; } = preferences => preferences.SaveAsync();
    internal Func<bool, bool, Task> WriteAutostartAsync { get; init; } = Autostart.SetAsync;

    public string? SaveStatus => PreferenceSaves.Failure is { } preferences ? $"Could not save app preferences. {preferences.Message}" :
        AutostartSaves.Failure is { } autostart ? $"Could not change start at sign-in. {autostart.Message}" :
        PreferenceSaves.Saving || AutostartSaves.Saving ? "Saving app preferences…" :
        PreferenceSaves.Revision > 0 || AutostartSaves.Revision > 0 ? "App preferences saved." : null;

    bool CanRetrySave() => HasFailedSave;

    [RelayCommand(CanExecute = nameof(CanRetrySave))]
    void RetrySave()
    {
        if (PreferenceSaves.Failure != null)
            PreferenceSaves.Enqueue(_preferences);
        if (AutostartSaves.Failure != null)
            WriteAutostart();
    }

    void SaveStateChanged()
    {
        OnPropertyChanged(nameof(SaveStatus));
        OnPropertyChanged(nameof(HasFailedSave));
        RetrySaveCommand.NotifyCanExecuteChanged();
    }
}
