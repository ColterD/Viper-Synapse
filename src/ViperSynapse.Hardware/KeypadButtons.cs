using System.Runtime.InteropServices;
using ViperSynapse.Hardware.Linux;

namespace ViperSynapse.Hardware;

/// <summary>
/// The Tartarus V2 Pro's own buttons, which its firmware reports on interface 1 instead of sending keys (read from
/// the device, 2026-10-22): report 04 carries the Hypershift button (04 01 held, 04 00 let go), report 05 5E the
/// keymap the keymap button switched to (1-8), report 05 01 the on-board profile the profile button switched to.
/// Only listening: the reports reach the system as they always do.
/// </summary>
public sealed class KeypadButtons : IDisposable
{
    readonly int _fd;
    readonly Thread _thread;
    readonly Action<KeypadButtons>? _onStopped;
    volatile bool _stopping;

    public event Action<bool>? Hypershift;
    public event Action<int>? KeymapSelected;
    public event Action<int>? ProfileSelected;

    KeypadButtons(int fd, string path, Action<KeypadButtons>? onStopped)
    {
        _fd = fd;
        _onStopped = onStopped;
        _thread = new Thread(Listen) { IsBackground = true, Name = "keypad buttons " + path };
    }

    public static KeypadButtons Start(string hidrawPath) => Start(hidrawPath, null, null);

    public static KeypadButtons Start(string hidrawPath, Action<bool>? onHypershift, Action<int>? onKeymapSelected = null)
        => Start(hidrawPath, onHypershift, onKeymapSelected, null);

    public static KeypadButtons Start(string hidrawPath, Action<bool>? onHypershift, Action<int>? onKeymapSelected,
        Action<KeypadButtons>? onStopped)
    {
        int fd = Libc.Open(hidrawPath, Libc.O_RDONLY | Libc.O_CLOEXEC);
        if (fd < 0)
            throw new IOException($"{hidrawPath}: {Libc.ErrorText(Marshal.GetLastPInvokeError())}");
        try
        {
            var buttons = new KeypadButtons(fd, hidrawPath, onStopped);
            // Register before the reader starts so its first report or immediate exit is observable.
            buttons.Hypershift += onHypershift;
            buttons.KeymapSelected += onKeymapSelected;
            buttons._thread.Start();
            return buttons;
        }
        catch
        {
            Libc.Close(fd);
            throw;
        }
    }

    /// <summary>Reads a report and says what it means. Public for the tests.</summary>
    public void Handle(ReadOnlySpan<byte> report)
    {
        if (report.Length >= 2 && report[0] == 0x04)
            Hypershift?.Invoke(report[1] != 0);
        else if (report.Length >= 3 && report[0] == 0x05 && report[1] == 0x5E)
            KeymapSelected?.Invoke(report[2]);
        else if (report.Length >= 3 && report[0] == 0x05 && report[1] == 0x01)
            ProfileSelected?.Invoke(report[2]);
    }

    unsafe void Listen()
    {
        var buffer = new byte[64];
        var poll = new Libc.PollFd { Fd = _fd, Events = Libc.POLLIN };
        try
        {
            while (!_stopping)
            {
                poll.Revents = 0;
                int ready = Libc.Poll(&poll, 1, 100);
                if (ready <= 0)
                    continue;
                if ((poll.Revents & (Libc.POLLERR | Libc.POLLHUP | Libc.POLLNVAL)) != 0)
                    return;
                nint n;
                fixed (byte* b = buffer)
                    n = Libc.Read(_fd, b, (nuint)buffer.Length);
                if (n <= 0)
                    return;
                Handle(buffer.AsSpan(0, (int)n));
            }
        }
        finally
        {
            // A lost node cannot deliver the release report for a held firmware button.
            try { Hypershift?.Invoke(false); }
            finally
            {
                if (!_stopping)
                    _onStopped?.Invoke(this);
            }
        }
    }

    public void Dispose()
    {
        _stopping = true;
        _thread.Join();
        Libc.Close(_fd);
    }
}
