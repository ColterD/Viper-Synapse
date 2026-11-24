using System.Net.Sockets;
using System.Runtime.InteropServices;
using System.Text;
using Tmds.DBus.Protocol;

namespace ViperSynapse.Service;

/// <summary>The program in front: its executable's name (from the process) and its window class.</summary>
public readonly record struct FocusedProgram(string? Executable, string? WindowClass)
{
    public static FocusedProgram FromPid(uint pid, string? windowClass)
    {
        string? exe = null;
        if (pid > 0)
        {
            try { exe = Path.GetFileName(File.ResolveLinkTarget($"/proc/{pid}/exe", returnFinalTarget: true)?.FullName); }
            catch (Exception e) when (e is IOException or UnauthorizedAccessException) { }
            if (exe == null)
            {
                try { exe = File.ReadAllText($"/proc/{pid}/comm").Trim(); }
                catch (Exception e) when (e is IOException or UnauthorizedAccessException) { }
            }
        }
        return new FocusedProgram(exe, windowClass);
    }

    /// <summary>True when a linked entry names this program: its executable or its window class, ignoring case
    /// and a trailing ".exe" (games under Proton show their Windows executable).</summary>
    public bool Matches(string linked)
    {
        static string Norm(string s) => s.Trim().EndsWith(".exe", StringComparison.OrdinalIgnoreCase) ? s.Trim()[..^4] : s.Trim();
        var want = Norm(Path.GetFileName(linked));
        return want.Length > 0
            && (Executable != null && string.Equals(Norm(Executable), want, StringComparison.OrdinalIgnoreCase)
                || WindowClass != null && string.Equals(Norm(WindowClass), want, StringComparison.OrdinalIgnoreCase));
    }
}

/// <summary>
/// Tells the service which program is in front, for profiles linked to programs. Each desktop family needs its
/// own way: KWin runs a small script that calls the service on every activation (Plasma on Wayland and X11),
/// Hyprland announces focus on its event socket, other X11 desktops publish _NET_ACTIVE_WINDOW. GNOME on Wayland
/// offers no way without a shell extension; there linked profiles stay manual.
/// </summary>
public sealed class FocusWatcher : IDisposable
{
    readonly Action<FocusedProgram> _focused;
    readonly CancellationTokenSource _stop = new();
    sealed record KWinSession(DBusConnection Connection, DirectoryInfo? TemporaryDirectory);
    KWinSession? _kwin;

    public string Method { get; private set; } = "none";

    public FocusWatcher(Action<FocusedProgram> focused) => _focused = focused;

    /// <summary>Picks the way for this session and starts it.</summary>
    public async Task StartAsync(string? sessionBus)
    {
        var desktop = Environment.GetEnvironmentVariable("XDG_CURRENT_DESKTOP") ?? "";
        if (desktop.Contains("KDE", StringComparison.OrdinalIgnoreCase) && sessionBus != null && await StartKWinAsync(sessionBus).ConfigureAwait(false))
            Method = "kwin";
        else if (ResolveHyprlandSocketPath(
            Environment.GetEnvironmentVariable("HYPRLAND_INSTANCE_SIGNATURE"),
            Environment.GetEnvironmentVariable("XDG_RUNTIME_DIR"),
            Environment.GetEnvironmentVariable("UID")) is { } socketPath)
        {
            Method = "hyprland";
            _ = Task.Run(() => WatchHyprland(socketPath));
        }
        else if (!string.IsNullOrEmpty(Environment.GetEnvironmentVariable("DISPLAY")) && string.IsNullOrEmpty(Environment.GetEnvironmentVariable("WAYLAND_DISPLAY")))
        {
            Method = "x11";
            new Thread(WatchX11) { IsBackground = true, Name = "focus x11" }.Start();
        }
    }

    // ---- KWin -------------------------------------------------------------------------------------------

    const string ScriptName = "viper-synapse-focus";
    const string FallbackScriptFile = "focus.js";

    /// <summary>KWin's script calls the service's ActiveWindow method with the window's class and process id.</summary>
    static string KWinScript() => """
        function report(w) {
            callDBus("org.vipersynapse.Daemon", "/org/vipersynapse/Daemon", "org.vipersynapse.Daemon1", "ActiveWindow",
                     w ? String(w.resourceClass) : "", w ? w.pid : 0);
        }
        workspace.windowActivated.connect(report);
        report(workspace.activeWindow);
        """;

    async Task<bool> StartKWinAsync(string address)
    {
        DBusConnection? connection = null;
        DirectoryInfo? fallbackDirectory = null;
        string path;
        var scriptLoaded = false;
        try
        {
            if (Environment.GetEnvironmentVariable("XDG_RUNTIME_DIR") is { Length: > 0 } runtime)
            {
                path = Path.Join(runtime, $"viper-synapse-focus-{Environment.UserName}.js");
                await File.WriteAllTextAsync(path, KWinScript()).ConfigureAwait(false);
            }
            else
            {
                fallbackDirectory = Directory.CreateTempSubdirectory("viper-synapse-focus-");
                path = Path.Join(fallbackDirectory.FullName, FallbackScriptFile);
                await using var script = new FileStream(path, FileMode.CreateNew, FileAccess.Write, FileShare.Read,
                    bufferSize: 4096, useAsync: true);
                await using var writer = new StreamWriter(script);
                await writer.WriteAsync(KWinScript()).ConfigureAwait(false);
            }

            connection = new DBusConnection(address);
            await connection.ConnectAsync().ConfigureAwait(false);
            // A script left by an earlier run would report twice.
            await TryCall(connection, "unloadScript", ScriptName).ConfigureAwait(false);
            await connection.CallMethodAsync(Load(connection, path)).ConfigureAwait(false);
            scriptLoaded = true;
            await connection.CallMethodAsync(Simple(connection, "start")).ConfigureAwait(false);
            _kwin = new KWinSession(connection, fallbackDirectory);
            connection = null;
            fallbackDirectory = null;
            return true;
        }
        catch (Exception e) when (e is DBusErrorReplyException or DBusConnectionException or IOException or UnauthorizedAccessException)
        {
            Console.Error.WriteLine($"Linked programs: KWin did not take the focus script ({e.Message}).");
            return false;
        }
        finally
        {
            if (connection != null)
                await StopKWinAsync(new KWinSession(connection, fallbackDirectory), scriptLoaded).ConfigureAwait(false);
            else
                CleanupKWinFallback(fallbackDirectory);
        }
    }

    static MessageBuffer Load(DBusConnection c, string path)
    {
        using var w = c.GetMessageWriter();
        w.WriteMethodCallHeader(destination: "org.kde.KWin", path: "/Scripting", @interface: "org.kde.kwin.Scripting", member: "loadScript", signature: "ss");
        w.WriteString(path);
        w.WriteString(ScriptName);
        return w.CreateMessage();
    }

    static MessageBuffer Simple(DBusConnection c, string member, string? argument = null)
    {
        using var w = c.GetMessageWriter();
        w.WriteMethodCallHeader(destination: "org.kde.KWin", path: "/Scripting", @interface: "org.kde.kwin.Scripting", member: member, signature: argument == null ? null : "s");
        if (argument != null)
            w.WriteString(argument);
        return w.CreateMessage();
    }

    static async Task TryCall(DBusConnection c, string member, string argument)
    {
        try { await c.CallMethodAsync(Simple(c, member, argument)).ConfigureAwait(false); }
        catch (DBusErrorReplyException) { }
    }

    static async Task StopKWinAsync(KWinSession session, bool unload)
    {
        try
        {
            if (unload)
                await TryCall(session.Connection, "unloadScript", ScriptName).ConfigureAwait(false);
        }
        catch (Exception e) when (e is DBusConnectionException or IOException)
        {
            Console.Error.WriteLine($"Linked programs: could not unload KWin's focus script ({e.Message}).");
        }
        finally
        {
            try { session.Connection.Dispose(); }
            finally { CleanupKWinFallback(session.TemporaryDirectory); }
        }
    }

    static void CleanupKWinFallback(DirectoryInfo? directory)
    {
        if (directory == null)
            return;
        try
        {
            File.Delete(Path.Join(directory.FullName, FallbackScriptFile));
            directory.Delete();
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
            Console.Error.WriteLine($"Linked programs: could not remove KWin's temporary focus script ({e.Message}).");
        }
    }

    /// <summary>What KWin's script sends, through the service's bus object.</summary>
    public void ReportFromKWin(string windowClass, uint pid) => _focused(FocusedProgram.FromPid(pid, windowClass));

    // ---- Hyprland ---------------------------------------------------------------------------------------

    internal static string? ResolveHyprlandSocketPath(string? signature, string? runtime, string? uid)
    {
        if (string.IsNullOrWhiteSpace(signature)
            || signature is "." or ".."
            || signature.IndexOfAny(['/', '\\', '\0']) >= 0)
            return null;

        if (!string.IsNullOrEmpty(runtime))
        {
            if (!Path.IsPathFullyQualified(runtime) || runtime.Contains('\0'))
                return null;
        }
        else
        {
            if (string.IsNullOrEmpty(uid) || !uid.All(char.IsAsciiDigit))
                return null;
            runtime = Path.Join("/run/user", uid);
        }

        return Path.Join(runtime, "hypr", signature, ".socket2.sock");
    }

    async Task WatchHyprland(string socketPath)
    {
        try
        {
            using var socket = new Socket(AddressFamily.Unix, SocketType.Stream, ProtocolType.Unspecified);
            await socket.ConnectAsync(new UnixDomainSocketEndPoint(socketPath), _stop.Token).ConfigureAwait(false);
            using var reader = new StreamReader(new NetworkStream(socket), Encoding.UTF8);
            while (!_stop.IsCancellationRequested && await reader.ReadLineAsync(_stop.Token).ConfigureAwait(false) is { } line)
            {
                // activewindow>>CLASS,TITLE
                if (line.StartsWith("activewindow>>", StringComparison.Ordinal))
                    _focused(new FocusedProgram(null, line[14..].Split(',', 2)[0]));
            }
        }
        catch (Exception e) when (e is SocketException or IOException or OperationCanceledException)
        {
            if (!_stop.IsCancellationRequested)
                Console.Error.WriteLine($"Linked programs: Hyprland's event socket closed ({e.Message}).");
        }
    }

    // ---- X11 --------------------------------------------------------------------------------------------

    [DllImport("libX11.so.6")] static extern nint XOpenDisplay(nint name);
    [DllImport("libX11.so.6")] static extern int XCloseDisplay(nint display);
    [DllImport("libX11.so.6")] static extern nint XDefaultRootWindow(nint display);
    [DllImport("libX11.so.6")] static extern nint XInternAtom(nint display, string name, bool onlyIfExists);
    [DllImport("libX11.so.6")] static extern int XFree(nint data);
    [DllImport("libX11.so.6")]
    static extern int XGetWindowProperty(nint display, nint window, nint property, nint offset, nint length, bool delete,
        nint type, out nint actualType, out int actualFormat, out nint items, out nint bytesAfter, out nint data);

    /// <summary>Polls twice a second; one X connection of our own, never shared with a UI toolkit.</summary>
    void WatchX11()
    {
        nint display;
        try { display = XOpenDisplay(0); }
        catch (DllNotFoundException) { Console.Error.WriteLine("Linked programs: libX11 is not installed."); return; }
        if (display == 0)
            return;
        try
        {
            var root = XDefaultRootWindow(display);
            var active = XInternAtom(display, "_NET_ACTIVE_WINDOW", false);
            var pidAtom = XInternAtom(display, "_NET_WM_PID", false);
            var classAtom = XInternAtom(display, "WM_CLASS", false);
            nint last = -1;
            while (!_stop.IsCancellationRequested)
            {
                var window = ReadLong(display, root, active);
                if (window != last)
                {
                    last = window;
                    if (window != 0)
                        _focused(FocusedProgram.FromPid((uint)ReadLong(display, window, pidAtom), ReadClass(display, window, classAtom)));
                }
                _stop.Token.WaitHandle.WaitOne(500);
            }
        }
        finally
        {
            XCloseDisplay(display);
        }
    }

    static nint ReadLong(nint display, nint window, nint atom)
    {
        if (XGetWindowProperty(display, window, atom, 0, 1, false, 0, out _, out int format, out var items, out _, out var data) != 0 || data == 0)
            return 0;
        try { return items > 0 && format == 32 ? Marshal.ReadIntPtr(data) : 0; }
        finally { XFree(data); }
    }

    /// <summary>WM_CLASS holds the instance name then the class name; the class is the second.</summary>
    static string? ReadClass(nint display, nint window, nint atom)
    {
        if (XGetWindowProperty(display, window, atom, 0, 256, false, 0, out _, out _, out var items, out _, out var data) != 0 || data == 0)
            return null;
        try
        {
            var parts = Marshal.PtrToStringAnsi(data, (int)items)?.Split('\0', StringSplitOptions.RemoveEmptyEntries) ?? [];
            return parts.Length > 1 ? parts[1] : parts.FirstOrDefault();
        }
        finally
        {
            XFree(data);
        }
    }

    public void Dispose()
    {
        _stop.Cancel();
        var kwin = Interlocked.Exchange(ref _kwin, null);
        if (kwin != null)
            _ = StopKWinAsync(kwin, unload: true);
    }
}
