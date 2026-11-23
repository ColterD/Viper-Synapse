using System.Collections.ObjectModel;
using Avalonia.Threading;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using ViperSynapse.App.Services;
using ViperSynapse.Core.Bindings;
using ViperSynapse.Core.Protocol;

namespace ViperSynapse.App.ViewModels;

/// <summary>The tab bar icon for each destination.</summary>
public enum TabIcon
{
    Devices,
    Keypad,
    Mouse,
    Keyboard,
    Device,
    LinkedGames,
    Macro,
    Settings,
}

/// <summary>One tab of the top bar: SYNAPSE, a device, LINKED GAMES, MACRO or SETTINGS.</summary>
public abstract partial class TabViewModel : ObservableObject
{
    public abstract string Header { get; }

    /// <summary>The page title centred in the bar under the tabs.</summary>
    public virtual string Title => Header;

    public abstract TabIcon Icon { get; }
}

/// <summary>
/// The window: its top-bar tabs, back/forward through them, and the connection to the service. Results of service
/// calls arrive on pool threads and are applied on the UI thread; nothing here waits on the service from the UI
/// thread.
/// </summary>
public sealed partial class MainViewModel : ObservableObject
{
    readonly DaemonClient _client;
    readonly SemaphoreSlim _discoveryGate = new(1, 1);
    bool _serviceReady;
    bool _backgroundScanRunning, _backgroundScanPending;
    readonly Dictionary<string, (long Revision, Task Completion)> _deviceSaves = [];
    readonly List<TabViewModel> _back = [], _forward = [];
    bool _navigating;
    long _requestedDeviceSnapshot;

    public ObservableCollection<TabViewModel> Tabs { get; } = [];
    public IReadOnlyList<TabViewModel> PrimaryTabs { get; }

    public TabViewModel? SelectedPrimaryTab
    {
        get => SelectedTab is DeviceViewModel ? null : SelectedTab;
        set { if (value != null) SelectedTab = value; }
    }

    public DeviceViewModel? SelectedDeviceTab
    {
        get => SelectedTab as DeviceViewModel;
        set { if (value != null) SelectedTab = value; }
    }

    [ObservableProperty]
    public partial TabViewModel? SelectedTab { get; set; }

    [ObservableProperty]
    public partial string? Problem { get; set; }

    public HomeViewModel Home { get; }
    public LinkedGamesViewModel LinkedGames { get; }
    public MacrosViewModel Macros { get; }
    public SettingsViewModel Settings { get; }

    public string Title => SelectedTab?.Title ?? "";
    public bool CanGoBack => _back.Count > 0;
    public bool CanGoForward => _forward.Count > 0;

    public MainViewModel(DaemonClient client, AppPreferences preferences, bool startAtLogin, string built = "")
    {
        _client = client;
        ConnectServiceAsync = () => client.ConnectAsync();
        ReadDeviceListAsync = client.GetDevicesAsync;
        ReadDeviceSettingsAsync = client.GetSettingsAsync;
        ReadLiveDeviceAsync = client.GetUnitStateAsync;
        Settings = new SettingsViewModel(preferences, startAtLogin, built);
        Home = new HomeViewModel(this);
        LinkedGames = new LinkedGamesViewModel(this);
        Macros = new MacrosViewModel(client, this);
        PrimaryTabs = [Home, LinkedGames, Macros, Settings];
        Tabs.Add(Home);
        Tabs.Add(LinkedGames);
        Tabs.Add(Macros);
        Tabs.Add(Settings);
        SelectedTab = Home;
        _client.DevicesChanged += () => Dispatcher.UIThread.Post(() => _ = RefreshAfterDeviceChangeAsync());
        _client.StickMoved += (key, x, y) => Dispatcher.UIThread.Post(() =>
        {
            foreach (var device in Devices.Where(d => d.Key == key))
                device.StickAt(x, y);
        });
        _client.SettingsChanged += key => Dispatcher.UIThread.Post(() =>
        {
            foreach (var device in Devices.Where(d => d.Key == key))
                _ = device.ReloadSettingsAsync(ReadDeviceSettingsAsync);
        });
    }

    public DaemonClient Client => _client;

    internal Func<string, DeviceSettings, Task>? WriteDeviceSettingsAsync { get; init; }

    internal (long Revision, Task Completion) DeviceSaveState(string key) =>
        _deviceSaves.GetValueOrDefault(key, (0, Task.CompletedTask));

    internal Task FinishDeviceEditsAsync(TimeSpan timeout)
    {
        foreach (var device in Devices)
            device.FlushPendingEdits();
        return Task.WhenAll(_deviceSaves.Values.Select(save => save.Completion)).WaitAsync(timeout);
    }

    internal async Task<bool> FinishWorkspaceEditsAsync(TimeSpan timeout)
    {
        await Task.WhenAll(FinishDeviceEditsAsync(timeout), Macros.PendingSave, Home.Shortcuts.PendingSave,
            Settings.PendingSave).WaitAsync(timeout);
        return !Macros.HasFailedSave && !Home.Shortcuts.HasFailedSave && !Settings.HasFailedSave &&
            !_deviceSaveFeedback.Values.Any(feedback => feedback.Error != null);
    }

    // The service stores profiles by model, even if a particular unit is unplugged or a tab is recreated.
    internal Task<Exception?> SaveDeviceSettings(string key, DeviceSettings settings, DeviceSettings? baseline = null)
    {
        var previous = DeviceSaveState(key);
        long revision = previous.Revision + 1;
        SetSaveFeedback(key, new(revision, true, settings, baseline));
        var pending = SaveAfterAsync(previous.Completion, key, revision, settings, baseline);
        _deviceSaves[key] = (revision, pending);
        return pending;
    }

    async Task<Exception?> SaveAfterAsync(Task previous, string key, long revision, DeviceSettings settings, DeviceSettings? baseline)
    {
        await previous.ConfigureAwait(false);
        try
        {
            IReadOnlyList<UnitApplyOutcome>? units = null;
            if (WriteDeviceSettingsAsync is { } write)
                await write(key, settings).ConfigureAwait(false);
            else
                units = (await Client.CommitSettingsAsync(key, settings, baseline).ConfigureAwait(false)).Units;
            await Dispatcher.UIThread.InvokeAsync(() => CompleteSave(key, revision, units, null));
            return null;
        }
        catch (Exception e)
        {
            await Dispatcher.UIThread.InvokeAsync(() => CompleteSave(key, revision, null, e));
            Fail("Could not save the device settings", e, () => GetSaveFeedback(key)?.Revision == revision);
            return e;
        }
    }

    public MacroLibrary MacroLibrary => Macros.Library;

    public IEnumerable<DeviceViewModel> Devices => Tabs.OfType<DeviceViewModel>();

    partial void OnSelectedTabChanged(TabViewModel? oldValue, TabViewModel? newValue)
    {
        if (!_navigating && oldValue != null && newValue != null && oldValue != newValue)
        {
            _back.Add(oldValue);
            _forward.Clear();
        }
        (newValue as DeviceViewModel)?.Shown();
        if (newValue == LinkedGames)
            LinkedGames.Refresh();
        if (newValue == Macros)
            Macros.KeymapsChanged();
        OnPropertyChanged(nameof(Title));
        OnPropertyChanged(nameof(SelectedPrimaryTab));
        OnPropertyChanged(nameof(SelectedDeviceTab));
        OnPropertyChanged(nameof(CanGoBack));
        OnPropertyChanged(nameof(CanGoForward));
    }

    [RelayCommand]
    void GoBack() => Step(_back, _forward);

    [RelayCommand]
    void GoForward() => Step(_forward, _back);

    void Step(List<TabViewModel> from, List<TabViewModel> to)
    {
        while (from.Count > 0)
        {
            var tab = from[^1];
            from.RemoveAt(from.Count - 1);
            if (!Tabs.Contains(tab))
                continue;   // a device that has been unplugged since
            if (SelectedTab != null)
                to.Add(SelectedTab);
            _navigating = true;
            SelectedTab = tab;
            _navigating = false;
            return;
        }
    }

    /// <summary>Reads devices, settings and macros from the service again.</summary>
    [RelayCommand]
    Task Refresh()
    {
        Problem = null;
        return RefreshWorkspaceAsync(reloadExisting: true);
    }

    public Task StartAsync() => RefreshWorkspaceAsync(reloadExisting: false);

    async Task RefreshWorkspaceAsync(bool reloadExisting)
    {
        if (!await RefreshDevicesAsync(() => ReadConnectedDevicesAsync(reloadExisting), reloadExisting: reloadExisting))
            return;
        try
        {
            await Macros.LoadAsync();
        }
        catch (Exception e)
        {
            Fail("Could not load macros", e);
        }
    }

    internal Func<Task> ConnectServiceAsync { get; init; }
    internal Func<Task<IReadOnlyList<DeviceInfo>>> ReadDeviceListAsync { get; init; }
    internal Func<string, Task<DeviceSettings>> ReadDeviceSettingsAsync { get; init; }
    internal Func<DeviceInfo, Task<DeviceState?>> ReadLiveDeviceAsync { get; init; }

    async Task<IReadOnlyList<DeviceInfo>> ReadConnectedDevicesAsync(bool retryHardware = false)
    {
        await _discoveryGate.WaitAsync().ConfigureAwait(false);
        try
        {
            if (!_serviceReady)
                await ConnectServiceAsync().ConfigureAwait(false);
            if (retryHardware && Client.IsConnected)
                await Client.RetryDiscoveryAsync().ConfigureAwait(false);
            var devices = await ReadDeviceListAsync().ConfigureAwait(false);
            _serviceReady = true;
            return devices;
        }
        catch
        {
            _serviceReady = false;
            throw;
        }
        finally
        {
            _discoveryGate.Release();
        }
    }

    internal async Task RefreshAfterDeviceChangeAsync()
    {
        if (_backgroundScanRunning)
        {
            _backgroundScanPending = true;
            return;
        }

        _backgroundScanRunning = true;
        try
        {
            do
            {
                _backgroundScanPending = false;
                await RefreshDevicesAsync(() => ReadConnectedDevicesAsync(), showScanning: false);
            }
            while (_backgroundScanPending);
        }
        catch (Exception e)
        {
            Fail("Could not update connected devices", e);
        }
        finally
        {
            _backgroundScanRunning = false;
        }
    }

    internal async Task<bool> RefreshDevicesAsync(Func<Task<IReadOnlyList<DeviceInfo>>> readDevices,
        bool showScanning = true, bool reloadExisting = false)
    {
        long request = Interlocked.Increment(ref _requestedDeviceSnapshot);
        bool IsCurrent() => request == Volatile.Read(ref _requestedDeviceSnapshot);

        if (showScanning)
            await Dispatcher.UIThread.InvokeAsync(() =>
            {
                if (IsCurrent())
                    Home.SetDiscoveryState(DeviceDiscoveryState.Scanning);
            });

        IReadOnlyList<DeviceInfo> devices;
        try
        {
            devices = await readDevices().ConfigureAwait(false);
        }
        catch (Exception e)
        {
            await Dispatcher.UIThread.InvokeAsync(() =>
            {
                if (IsCurrent())
                    Home.SetDiscoveryState(DeviceDiscoveryState.ServiceError, e.Message);
            });
            return false;
        }

        var reload = await Dispatcher.UIThread.InvokeAsync<DeviceViewModel[]>(() =>
        {
            if (!IsCurrent())
                return []; // The service responded, but a newer scan owns the dashboard.

            var current = Devices.ToList();
            // One tab per device model; a second identical device shares the tab and the settings.
            foreach (var group in devices.GroupBy(d => d.Key))
            {
                var units = group.ToArray();
                var tab = current.FirstOrDefault(t => t.Key == group.Key);
                if (tab != null)
                {
                    var before = tab.UnitChoices;
                    bool endpointChanged = tab.SetAttachedUnits(units);
                    if (reloadExisting || endpointChanged || !ReferenceEquals(before, tab.UnitChoices))
                        tab.RefreshOnboard(devices);
                    if (endpointChanged && !reloadExisting)
                        _ = tab.ReloadAsync();
                    current.Remove(tab);
                    continue;
                }
                tab = new DeviceViewModel(units[0], this);
                tab.SetAttachedUnits(units);
                // Devices sit between SYNAPSE and LINKED GAMES, as in Synapse.
                Tabs.Insert(Tabs.IndexOf(LinkedGames), tab);
                if (!reloadExisting)
                    _ = tab.ReloadAsync();
            }
            foreach (var gone in current)
            {
                if (SelectedTab == gone)
                    SelectedTab = Home;
                gone.Detach();
                Tabs.Remove(gone);
                _back.RemoveAll(tab => tab == gone);
                _forward.RemoveAll(tab => tab == gone);
            }
            if (current.Count > 0)
            {
                OnPropertyChanged(nameof(CanGoBack));
                OnPropertyChanged(nameof(CanGoForward));
            }
            bool deviceListChanged = current.Count > 0;
            foreach (var gone in current)
                Home.Devices.Remove(gone);
            foreach (var tab in Devices)
            {
                if (Home.Devices.Contains(tab))
                    continue;
                Home.Devices.Add(tab);
                deviceListChanged = true;
            }
            Home.SetDiscoveryState(DeviceDiscoveryState.Ready);
            if (deviceListChanged)
                Home.Shortcuts.DevicesUpdated();
            OpenRequested();
            return reloadExisting ? Devices.ToArray() : [];
        });
        foreach (var device in reload)
            await device.ReloadAsync().ConfigureAwait(false);
        return true;
    }

    /// <summary>For screenshots and checks: VIPER_SYNAPSE_OPEN=02eb opens a device tab, 02eb/LIGHTING a page of it,
    /// 02eb:5 also opens key 5's flyout; macros, settings, games, home and shortcuts (GLOBAL SHORTCUTS; shortcuts:add opens the editor) open those tabs. macros/KEY BINDS, macros:delay
    /// and macros:record open a MACRO page, an ADD item or the RECORD options. Applied once, when the
    /// devices first arrive.</summary>
    void OpenRequested()
    {
        if (_opened || Environment.GetEnvironmentVariable("VIPER_SYNAPSE_OPEN") is not { Length: > 0 } open)
            return;
        _opened = true;
        TabViewModel? fixedTab = open switch
        {
            "macros" => Macros,
            "settings" => Settings,
            "games" => LinkedGames,
            "home" => Home,
            "shortcuts" or "shortcuts:add" => Home,
            _ => null,
        };
        if (fixedTab != null)
        {
            SelectedTab = fixedTab;
            if (open.StartsWith("shortcuts", StringComparison.Ordinal))
                Home.ShowShortcutsCommand.Execute(null);
            if (open == "shortcuts:add")
                Dispatcher.UIThread.Post(() => Home.Shortcuts.AddCommand.Execute(null), DispatcherPriority.Background);
            return;
        }
        if (open.StartsWith("macros", StringComparison.Ordinal))
        {
            // macros/KEY BINDS opens a page; macros:delay opens an ADD item's settings, macros:record the RECORD options.
            SelectedTab = Macros;
            var (page, extra) = open.Split(':') is [var p, var x] ? (p, x) : (open, "");
            if (page.Split('/') is [_, var name] && MacrosViewModel.Pages.Contains(name))
                Macros.SelectedPage = name;
            if (extra == "record")
                Dispatcher.UIThread.Post(Macros.ShowRecordOptions, DispatcherPriority.Background);
            else if (extra.Length > 0)
                Macros.Adding = extra;
            return;
        }
        var parts = open.Split(':');
        var target = parts[0].Split('/');
        if (Devices.FirstOrDefault(d => d.Key == target[0]) is not { } device)
            return;
        SelectedTab = device;
        if (target.Length > 1 && device.Pages.Contains(target[1]))
            device.SelectedPage = target[1];
        if (parts.Length > 1 && int.TryParse(parts[1], System.Globalization.CultureInfo.InvariantCulture, out var number))
            Dispatcher.UIThread.Post(() => device.SelectedKey = device.Rows.FirstOrDefault(r => r.Key.Number == number), DispatcherPriority.Background);
    }

    bool _opened;

    public void Show(TabViewModel tab) => SelectedTab = tab;

    /// <summary>Reports a failed service call where the user can see it.</summary>
    public void Fail(string what, Exception e) => Fail(what, e, null);

    internal void Fail(string what, Exception e, Func<bool>? isCurrent) => Dispatcher.UIThread.Post(() =>
    {
        if (isCurrent is null || isCurrent())
            Problem = $"{what}: {e.Message}";
    });
}

public enum DeviceDiscoveryState
{
    Scanning,
    Ready,
    ServiceError,
}

/// <summary>The Devices landing page and global shortcuts.</summary>
public sealed partial class HomeViewModel(MainViewModel main) : TabViewModel
{
    public override string Header => "DEVICES";
    public override string Title => AppBranding.Name.ToUpperInvariant();
    public override TabIcon Icon => TabIcon.Devices;

    public ObservableCollection<DeviceViewModel> Devices { get; } = [];
    public ShortcutsViewModel Shortcuts { get; } = new(main);

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(IsScanning), nameof(HasScanResult), nameof(HasServiceError))]
    public partial DeviceDiscoveryState DiscoveryState { get; private set; }

    [ObservableProperty] public partial string? ServiceError { get; private set; }

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(ShowDevicesPage))]
    public partial bool ShowShortcutsPage { get; private set; }

    public bool IsScanning => DiscoveryState == DeviceDiscoveryState.Scanning;
    public bool HasScanResult => DiscoveryState == DeviceDiscoveryState.Ready;
    public bool HasServiceError => DiscoveryState == DeviceDiscoveryState.ServiceError;
    public bool ShowDevicesPage => !ShowShortcutsPage;

    public IAsyncRelayCommand RefreshCommand => main.RefreshCommand;

    [RelayCommand]
    void ShowShortcuts()
    {
        ShowShortcutsPage = true;
        _ = Shortcuts.LoadAsync();
    }

    [RelayCommand]
    void ShowDevices() => ShowShortcutsPage = false;

    internal void SetDiscoveryState(DeviceDiscoveryState state, string? error = null)
    {
        ServiceError = error;
        DiscoveryState = state;
    }

    public void Open(DeviceViewModel device) => main.Show(device);

    public void OpenHealth(DeviceViewModel device, DeviceUnitChoice unit)
    {
        if (!Devices.Contains(device) || !device.UnitChoices.Contains(unit))
            return;
        device.InspectedUnit = unit;
        device.SelectedPage = DeviceViewModel.HealthPage;
        main.Show(device);
    }
}
