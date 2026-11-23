using System.Diagnostics;
using System.Runtime.CompilerServices;
using System.Text.Json;
using System.Text.Json.Serialization;
using Tmds.DBus.Protocol;
using ViperSynapse.Core.Bindings;
using ViperSynapse.Core.Protocol;

namespace ViperSynapse.App.Services;

/// <summary>A connected device as the service describes it.</summary>
/// <summary>The device's own sensitivity, polling rate and battery.</summary>
public sealed record DeviceState(IReadOnlyList<ViperSynapse.Core.Lighting.DpiStage>? Stages, int ActiveStage, int PollingRate, int? BatteryPercent, bool? Charging, int LiftOffLevel = -1,
    int SleepSeconds = 0, int LowBatteryPercent = 0, bool? ScrollAcceleration = null, bool? SmartReel = null,
    byte? OnboardProfile = null, ViperSynapse.Core.Lighting.ScrollWheelMode? WheelMode = null);

public sealed record DeviceInfo(
    string Key, string ProductId, string Name, string Type, string Port, bool Known, bool Verified,
    string? Serial, string? Firmware, string? ControlNode, IReadOnlyList<string> InputNodes, bool Remapping, string? Problem,
    IReadOnlyList<ushort>? InputControls = null, string? InputControlsStatus = null, string? Connection = null,
    byte? OnboardProfile = null, bool NativeMacroKeys = false,
    string? Transport = null, bool? ControlReady = null, string? ControlIssue = null, string? InputIssue = null,
    bool? VirtualOutputReady = null);

[JsonSourceGenerationOptions(PropertyNamingPolicy = JsonKnownNamingPolicy.CamelCase)]
[JsonSerializable(typeof(List<DeviceInfo>))]
[JsonSerializable(typeof(DeviceState))]
internal sealed partial class ClientJson : JsonSerializerContext;

internal sealed class SettingsConflictException() : InvalidOperationException(
    "Settings changed in the service while you were editing. Reload and review the changes before saving again.");

/// <summary>
/// The window's line to viper-synapsed on the session bus. Every call is asynchronous and completes on a
/// pool thread; callers marshal results to the UI thread themselves. When the service is not running, the
/// client starts the copy installed next to the window (a development build, or a session without the
/// systemd unit) and waits for it to take its name.
/// </summary>
public sealed class DaemonClient : IAsyncDisposable
{
    const string BusName = "org.vipersynapse.Daemon", ObjectPath = "/org/vipersynapse/Daemon", Interface = "org.vipersynapse.Daemon1";

    DBusConnection? _connection;
    readonly ConditionalWeakTable<object, RevisionTag> _revisions = new();

    sealed class RevisionTag
    {
        public string? Value;
    }

    public event Action? DevicesChanged;
    public event Action<string>? SettingsChanged;

    /// <summary>A watched thumbstick moved: device, x, y (-1 to 1, y negative upwards). Raised on a pool thread.</summary>
    public event Action<string, double, double>? StickMoved;

    public async Task ConnectAsync(CancellationToken cancel = default)
    {
        _connection?.Dispose();
        _connection = null;
        var connection = new DBusConnection(DBusAddress.Session ?? throw new InvalidOperationException("No session bus."));
        try
        {
            await connection.ConnectAsync().ConfigureAwait(false);
            _connection = connection;
            await connection.AddMatchAsync(
                new MatchRule { Type = MessageType.Signal, Interface = Interface, Path = ObjectPath },
                static (Message m, object? _) =>
                {
                    var r = m.GetBodyReader();
                    return m.SignatureAsString switch
                    {
                        "s" => (m.MemberAsString ?? "", r.ReadString(), 0.0, 0.0),
                        "sdd" => (m.MemberAsString ?? "", r.ReadString(), r.ReadDouble(), r.ReadDouble()),
                        _ => (m.MemberAsString ?? "", (string?)null, 0.0, 0.0),
                    };
                },
                (Notification<(string Member, string? Arg, double X, double Y)> n) =>
                {
                    if (!n.HasValue) return;
                    var signal = n.Value;
                    if (signal.Member == "DevicesChanged") DevicesChanged?.Invoke();
                    else if (signal.Member == "SettingsChanged" && signal.Arg != null) SettingsChanged?.Invoke(signal.Arg);
                    else if (signal.Member == "StickMoved" && signal.Arg != null) StickMoved?.Invoke(signal.Arg, signal.X, signal.Y);
                },
                emitOnCapturedContext: false, ObserverFlags.None, null).ConfigureAwait(false);
            await EnsureDaemonAsync(cancel).ConfigureAwait(false);
        }
        catch
        {
            _connection = null;
            connection.Dispose();
            throw;
        }
    }

    async Task EnsureDaemonAsync(CancellationToken cancel)
    {
        Process? child = null;
        try
        {
            await EnsureMatchingBuildAsync(Service.DaemonBus.BuildId,
                () => ServiceHasOwnerAsync().WaitAsync(TimeSpan.FromSeconds(2), cancel), () => RunningBuildAsync(cancel),
                () => QuitServiceAsync().WaitAsync(TimeSpan.FromSeconds(2), cancel), () => child = StartService(),
                () => child?.HasExited == true ? child.ExitCode : null,
                TimeSpan.FromSeconds(5), TimeSpan.FromSeconds(10), cancel).ConfigureAwait(false);
        }
        finally
        {
            child?.Dispose();
        }
    }

    internal static async Task EnsureMatchingBuildAsync(string expected, Func<Task<bool>> hasOwner,
        Func<Task<string?>> readBuild, Func<Task> quit, Action start, Func<int?> exitCode,
        TimeSpan stopTimeout, TimeSpan startTimeout, CancellationToken cancel = default)
    {
        if (await hasOwner().ConfigureAwait(false))
        {
            if (await readBuild().ConfigureAwait(false) == expected)
                return;
            await quit().ConfigureAwait(false);
            var stopping = Stopwatch.StartNew();
            while (await hasOwner().ConfigureAwait(false))
            {
                if (stopping.Elapsed >= stopTimeout)
                    throw new TimeoutException("The previous Viper service is still running. Quit it and retry the scan.");
                await Task.Delay(100, cancel).ConfigureAwait(false);
            }
        }
        cancel.ThrowIfCancellationRequested();
        start();
        var starting = Stopwatch.StartNew();
        do
        {
            if (await readBuild().ConfigureAwait(false) == expected)
                return;
            if (exitCode() is int exit)
                throw new InvalidOperationException($"The matching {AppBranding.Name} service exited during startup (exit code {exit}).");
            if (starting.Elapsed >= startTimeout)
                break;
            await Task.Delay(100, cancel).ConfigureAwait(false);
        } while (starting.Elapsed < startTimeout);
        throw new TimeoutException($"The matching {AppBranding.Name} service did not become ready. Check the installed app and service builds.");
    }

    async Task<string?> RunningBuildAsync(CancellationToken cancel)
    {
        try
        {
            return await CallStringAsync("BuildId").WaitAsync(TimeSpan.FromSeconds(2), cancel).ConfigureAwait(false);
        }
        catch (DBusErrorReplyException)
        {
            return null;
        }
    }

    Task<bool> ServiceHasOwnerAsync()
    {
        var connection = Connection;
        using var writer = connection.GetMessageWriter();
        writer.WriteMethodCallHeader(destination: "org.freedesktop.DBus", path: "/org/freedesktop/DBus",
            @interface: "org.freedesktop.DBus", member: "NameHasOwner", signature: "s");
        writer.WriteString(BusName);
        return connection.CallMethodAsync(writer.CreateMessage(), static (Message m, object? _) => m.GetBodyReader().ReadBool(), null);
    }

    static Process StartService()
    {
        var next = Path.Combine(AppContext.BaseDirectory, "viper-synapsed");
        var info = File.Exists(next) ? new ProcessStartInfo(next) : new ProcessStartInfo("viper-synapsed");
        info.UseShellExecute = false;
        try
        {
            return Process.Start(info) ?? throw new InvalidOperationException("Could not start the Viper service process.");
        }
        catch (System.ComponentModel.Win32Exception e)
        {
            throw new InvalidOperationException($"Could not start the Viper service ({info.FileName}): {e.Message}", e);
        }
    }

    public async Task<IReadOnlyList<DeviceInfo>> GetDevicesAsync() =>
        JsonSerializer.Deserialize(await CallStringAsync("GetDevices").ConfigureAwait(false), ClientJson.Default.ListDeviceInfo) ?? [];

    public async Task<DeviceState?> GetDeviceStateAsync(string device) =>
        JsonSerializer.Deserialize(await CallStringAsync("GetDeviceState", device).ConfigureAwait(false), ClientJson.Default.DeviceState);

    public async Task<DeviceState?> GetUnitStateAsync(DeviceInfo device)
    {
        var connection = Connection;
        var message = Build(connection, "GetUnitState", "ssss", [device.Key, device.Port, device.ProductId, device.Serial ?? ""]);
        string json = await connection.CallMethodAsync(message, static (Message m, object? _) => m.GetBodyReader().ReadString(), null)
            .ConfigureAwait(false);
        return JsonSerializer.Deserialize(json, ClientJson.Default.DeviceState);
    }

    public async Task<OnboardResult> ManageOnboardAsync(OnboardRequest request) =>
        BasiliskOnboard.ReadResult(await CallStringAsync("Onboard", BasiliskOnboard.WriteRequest(request)).ConfigureAwait(false));

    public async Task<OnboardBackup> BackupOnboardAsync(OnboardArchiveRequest request) =>
        OnboardBackupCodec.ReadBackup(await CallStringAsync("BackupOnboard", OnboardBackupCodec.WriteRequest(request)).ConfigureAwait(false));

    public async Task<OnboardRestoreResult> RestoreOnboardAsync(OnboardArchiveRequest request) =>
        OnboardBackupCodec.ReadResult(await CallStringAsync("RestoreOnboard", OnboardBackupCodec.WriteRequest(request)).ConfigureAwait(false));

    public Task<DeviceSettings> GetSettingsAsync(string device) =>
        ReadSnapshotAsync("GetSettingsSnapshot", SettingsJson.ReadSettings, device);

    public async Task SetSettingsAsync(string device, DeviceSettings settings, DeviceSettings? baseline = null) =>
        await CommitSettingsAsync(device, settings, baseline).ConfigureAwait(false);

    internal Task<SettingsCommitResult> CommitSettingsAsync(string device, DeviceSettings settings, DeviceSettings? baseline) =>
        CommitAsync("CommitSettings", "GetSettingsSnapshot", settings, baseline, SettingsJson.Write(settings), device);

    public Task<MacroLibrary> GetMacrosAsync() => ReadSnapshotAsync("GetMacrosSnapshot", SettingsJson.ReadMacros);

    public async Task SetMacrosAsync(MacroLibrary macros, MacroLibrary? baseline = null) =>
        await CommitAsync("CommitMacros", "GetMacrosSnapshot", macros, baseline, SettingsJson.Write(macros)).ConfigureAwait(false);

    public Task<ShortcutList> GetShortcutsAsync() => ReadSnapshotAsync("GetShortcutsSnapshot", SettingsJson.ReadShortcuts);

    public async Task SetShortcutsAsync(ShortcutList shortcuts, ShortcutList? baseline = null) =>
        await CommitAsync("CommitShortcuts", "GetShortcutsSnapshot", shortcuts, baseline, SettingsJson.Write(shortcuts)).ConfigureAwait(false);

    public Task RetryDiscoveryAsync() => CallAsync("RetryDiscovery", null);

    internal bool IsConnected => _connection != null;

    internal async Task<UnitApplyOutcome> RetryUnitApplyAsync(string key, UnitApplyOutcome unit) =>
        JsonSerializer.Deserialize(await CallStringAsync("RetryUnitApply", key, unit.Port, unit.ProductId, unit.Serial ?? "")
            .ConfigureAwait(false), SettingsCommitJson.Default.UnitApplyOutcome) ?? throw new InvalidDataException("No device result was returned.");

    async Task<T> ReadSnapshotAsync<T>(string member, Func<string, T> parse, params string[] scope) where T : class
    {
        var snapshot = await ReadSnapshotEnvelopeAsync(member, scope).ConfigureAwait(false);
        var value = parse(snapshot.Json);
        _revisions.GetValue(value, static _ => new()).Value = snapshot.Revision;
        return value;
    }

    async Task<SettingsSnapshot> ReadSnapshotEnvelopeAsync(string member, string[] scope) =>
        JsonSerializer.Deserialize(await CallStringAsync(member, scope).ConfigureAwait(false), SettingsCommitJson.Default.SettingsSnapshot)
            ?? throw new InvalidDataException("The service returned no settings snapshot.");

    async Task<SettingsCommitResult> CommitAsync(string member, string readMember, object edited, object? baseline,
        string json, params string[] scope)
    {
        var expected = baseline == null
            ? (await ReadSnapshotEnvelopeAsync(readMember, scope).ConfigureAwait(false)).Revision
            : _revisions.TryGetValue(baseline, out var revision) ? revision.Value : null;
        if (expected == null)
            throw new InvalidOperationException("The original settings have not loaded. Reload before saving your changes.");
        var editedRevision = _revisions.GetValue(edited, static _ => new());
        editedRevision.Value = expected;
        var result = await WriteSnapshotAsync(member, scope, expected, json).ConfigureAwait(false);
        bool persistedExactSnapshot = result.Saved;
        if (!result.Saved && baseline is DeviceSettings original && edited is DeviceSettings change &&
            original.ActiveProfile == change.ActiveProfile && original.ActiveKeymap == change.ActiveKeymap)
        {
            // Automatic profile switches only change the selection, not the profiles being edited.
            // Replay the edit over that selection once, leaving every conflicting content change alone.
            var latest = await ReadSnapshotEnvelopeAsync(readMember, scope).ConfigureAwait(false);
            if (RebaseOverProfileSelection(latest.Json, original, change) is { } rebased)
                result = await WriteSnapshotAsync(member, scope, latest.Revision, SettingsJson.Write(rebased)).ConfigureAwait(false);
        }
        if (!result.Saved)
            throw new SettingsConflictException();
        // A rebased write has different active selection from the local object. Keep that object's old
        // revision so a subsequent pending edit still compares the selection explicitly.
        if (persistedExactSnapshot)
            editedRevision.Value = result.Revision;
        return result;
    }

    async Task<SettingsCommitResult> WriteSnapshotAsync(string member, string[] scope, string expected, string json) =>
        JsonSerializer.Deserialize(await CallStringAsync(member, [.. scope, expected, json]).ConfigureAwait(false),
            SettingsCommitJson.Default.SettingsCommitResult) ?? throw new InvalidDataException("The service returned no save result.");

    internal static DeviceSettings? RebaseOverProfileSelection(string latestJson, DeviceSettings original, DeviceSettings change)
    {
        var latest = SettingsJson.ReadSettings(latestJson);
        var normalized = latest with { ActiveProfile = original.ActiveProfile, ActiveKeymap = original.ActiveKeymap };
        return SettingsJson.Write(normalized) == SettingsJson.Write(original)
            ? change with { ActiveProfile = latest.ActiveProfile, ActiveKeymap = latest.ActiveKeymap }
            : null;
    }

    /// <summary>Follow the device's thumbstick for the next 15 seconds (ask again to keep following).</summary>
    public Task WatchStickAsync(string device) => CallAsync("WatchStick", "s", device);

    public Task StartRecordingAsync() => CallAsync("StartRecording", null);

    /// <summary>An on-screen notification through the desktop's notification service (org.freedesktop.Notifications),
    /// which every Linux desktop provides. Failing quietly: a desktop without one simply shows nothing.</summary>
    public async Task NotifyAsync(string summary, string body)
    {
        try
        {
            await Connection.CallMethodAsync(NotifyMessage(Connection, summary, body)).ConfigureAwait(false);
        }
        catch (DBusErrorReplyException)
        {
        }
    }

    static MessageBuffer NotifyMessage(DBusConnection connection, string summary, string body)
    {
        using var writer = connection.GetMessageWriter();
        writer.WriteMethodCallHeader(destination: "org.freedesktop.Notifications", path: "/org/freedesktop/Notifications",
            @interface: "org.freedesktop.Notifications", member: "Notify", signature: "susssasa{sv}i");
        writer.WriteString(AppBranding.Name);
        writer.WriteUInt32(0);
        writer.WriteString("viper-synapse");
        writer.WriteString(summary);
        writer.WriteString(body);
        writer.WriteArray(Array.Empty<string>());
        var hints = writer.WriteDictionaryStart();
        writer.WriteDictionaryEnd(hints);
        writer.WriteInt32(3000);
        return writer.CreateMessage();
    }

    /// <summary>Stops the service: Quit from the tray ends bindings and macros with the app.</summary>
    public Task QuitServiceAsync() => CallAsync("Quit", null);

    public async Task<IReadOnlyList<MacroStep>> StopRecordingAsync(RecordDelayMode mode, int defaultDelayMs, int randomMinMs = 0, int randomMaxMs = 0)
    {
        var connection = Connection;
        string json = await connection.CallMethodAsync(StopRecordingMessage(connection, mode, defaultDelayMs, randomMinMs, randomMaxMs),
            static (Message m, object? _) => m.GetBodyReader().ReadString(), null).ConfigureAwait(false);
        return SettingsJson.ReadSteps(json);
    }

    static MessageBuffer StopRecordingMessage(DBusConnection connection, RecordDelayMode mode, int defaultDelayMs, int randomMinMs, int randomMaxMs)
    {
        using var writer = connection.GetMessageWriter();
        writer.WriteMethodCallHeader(destination: BusName, path: ObjectPath, @interface: Interface, member: "StopRecording", signature: "siii");
        writer.WriteString(mode.ToString());
        writer.WriteInt32(defaultDelayMs);
        writer.WriteInt32(randomMinMs);
        writer.WriteInt32(randomMaxMs);
        return writer.CreateMessage();
    }

    DBusConnection Connection => _connection ?? throw new InvalidOperationException("Not connected.");

    // A MessageWriter cannot live across an await, so messages are built in plain methods.
    static MessageBuffer Build(DBusConnection connection, string member, string? signature, string[] arguments)
    {
        using var writer = connection.GetMessageWriter();
        writer.WriteMethodCallHeader(destination: BusName, path: ObjectPath, @interface: Interface, member: member, signature: signature);
        foreach (var argument in arguments)
            writer.WriteString(argument);
        return writer.CreateMessage();
    }

    async Task<string> CallStringAsync(string member, params string[] arguments)
    {
        var connection = Connection;
        var message = Build(connection, member, arguments.Length == 0 ? null : new string('s', arguments.Length), arguments);
        return await connection.CallMethodAsync(message, static (Message m, object? _) => m.GetBodyReader().ReadString(), null).ConfigureAwait(false);
    }

    async Task CallAsync(string member, string? signature, params string[] arguments)
    {
        var connection = Connection;
        await connection.CallMethodAsync(Build(connection, member, signature, arguments)).ConfigureAwait(false);
    }

    public ValueTask DisposeAsync()
    {
        _connection?.Dispose();
        return ValueTask.CompletedTask;
    }
}
