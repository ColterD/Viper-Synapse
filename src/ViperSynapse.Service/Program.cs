using System.Runtime.InteropServices;
using ViperSynapse.Hardware;
using ViperSynapse.Hardware.Linux;

namespace ViperSynapse.Service;

static class Program
{
    internal static FileSystemWatcher WatchInputNodes(string path, FileSystemEventHandler changed, string filter = "*")
    {
        var watcher = new FileSystemWatcher(path)
        {
            IncludeSubdirectories = false,
            Filter = filter,
            NotifyFilter = NotifyFilters.FileName | NotifyFilters.Attributes | NotifyFilters.Security,
        };
        watcher.Created += changed;
        watcher.Deleted += changed;
        watcher.Changed += changed;
        watcher.EnableRaisingEvents = true;
        return watcher;
    }

    static async Task<int> Main(string[] args)
    {
        string root = args.Length >= 2 && args[0] == "--config" ? args[1] : SettingsStore.DefaultRoot();
        using var stop = new CancellationTokenSource();
        using var sigterm = PosixSignalRegistration.Create(PosixSignal.SIGTERM, c => { c.Cancel = true; stop.Cancel(); });
        using var sigint = PosixSignalRegistration.Create(PosixSignal.SIGINT, c => { c.Cancel = true; stop.Cancel(); });

        using var devices = new DeviceManager(new SettingsStore(root), () => UinputDevice.Create("Viper Synapse virtual input"));
        using var bus = new DaemonBus(devices, () =>
        {
            // Grabbed devices are heard through the virtual device, which carries what they send.
            var held = devices.Devices.Where(d => d.Remapping).SelectMany(d => d.InputNodes);
            return MacroRecorder.KeyboardsAndMice(held);
        }, stop.Cancel);
        try
        {
            await bus.ConnectAsync().ConfigureAwait(false);
        }
        catch (Exception e) when (e is InvalidOperationException or Tmds.DBus.Protocol.DBusConnectionException)
        {
            Console.Error.WriteLine(e.Message);
            return 1;
        }

        devices.Refresh();

        using var idle = new IdleWatcher();
        idle.Changed += devices.Idle;
        await idle.StartAsync(Tmds.DBus.Protocol.DBusAddress.Session).ConfigureAwait(false);
        using var focus = new FocusWatcher(devices.Focused);
        bus.ActiveWindow += focus.ReportFromKWin;
        await focus.StartAsync(Tmds.DBus.Protocol.DBusAddress.Session).ConfigureAwait(false);
        Console.WriteLine($"Linked programs: following the focused window through {focus.Method}.");
        foreach (var d in devices.Devices)
            Console.WriteLine($"{d.Name} ({d.ProductId}) {d.Firmware} {(d.Remapping ? "remapping" : "idle")} {d.Problem}");

        // Plugging and unplugging shows up as nodes coming and going in /dev/input.
        var pending = 0;
        var retryControl = 0;
        var stopping = stop.Token;
        void Changed(bool permissionChanged)
        {
            if (permissionChanged)
                Interlocked.Exchange(ref retryControl, 1);
            if (Interlocked.Exchange(ref pending, 1) == 0)
                _ = RefreshAfterUdevAsync();
        }
        async Task RefreshAfterUdevAsync()
        {
            // udev needs a moment to set the permissions on a new node.
            try
            {
                await Task.Delay(500, stopping).ConfigureAwait(false);
                Volatile.Write(ref pending, 0);
                devices.Refresh(forceControlRetry: Interlocked.Exchange(ref retryControl, 0) != 0);
            }
            catch (OperationCanceledException) when (stopping.IsCancellationRequested) { }
            catch (Exception e) when (e is IOException or UnauthorizedAccessException)
            {
                Console.Error.WriteLine($"Could not refresh devices after a udev change: {e.Message}");
            }
        }
        using var watcher = Directory.Exists("/dev/input") ? WatchInputNodes("/dev/input", (_, _) => Changed(false)) : null;
        using var hidrawWatcher = WatchInputNodes("/dev", (_, e) => Changed(e.ChangeType == WatcherChangeTypes.Changed), "hidraw*");

        // A sleeping mouse can return while the receiver's Linux nodes remain present.
        using var receiverPoll = new PeriodicTimer(TimeSpan.FromSeconds(5));
        try
        {
            while (await receiverPoll.WaitForNextTickAsync(stop.Token).ConfigureAwait(false))
                devices.RefreshReceivers();
        }
        catch (OperationCanceledException) { }
        return 0;
    }
}
