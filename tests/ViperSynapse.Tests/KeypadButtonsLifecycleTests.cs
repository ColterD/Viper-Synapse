using System.Runtime.InteropServices;
using System.Reflection;
using ViperSynapse.Hardware;

namespace ViperSynapse.Tests;

public class KeypadButtonsLifecycleTests
{
    [DllImport("libc", SetLastError = true)]
    static extern int mkfifo(string path, uint mode);

    [DllImport("libc", SetLastError = true)]
    static extern int open(string path, int flags);

    [DllImport("libc", SetLastError = true)]
    static extern nint write(int fd, byte[] data, nuint count);

    [DllImport("libc", SetLastError = true)]
    static extern int close(int fd);

    [Fact]
    public void Immediate_eof_notifies_the_registered_stopped_callback()
    {
        string path = Path.Join(Path.GetTempPath(), "viper-keypad-stop-" + Guid.NewGuid().ToString("N"));
        File.WriteAllBytes(path, []);
        using var stopped = new ManualResetEventSlim();
        KeypadButtons? buttons = null;
        KeypadButtons? observed = null;
        try
        {
            Action<KeypadButtons> onStopped = source => { observed = source; stopped.Set(); };
            var start = typeof(KeypadButtons).GetMethod("Start", BindingFlags.Public | BindingFlags.Static,
                [typeof(string), typeof(Action<bool>), typeof(Action<int>), typeof(Action<KeypadButtons>)]);
            Assert.NotNull(start);
            buttons = (KeypadButtons)start.Invoke(null, [path, null, null, onStopped])!;
            Assert.True(stopped.Wait(TimeSpan.FromSeconds(3), TestContext.Current.CancellationToken));
            Assert.Same(buttons, observed);
        }
        finally
        {
            buttons?.Dispose();
            File.Delete(path);
        }
    }

    [Fact]
    public void Intentional_dispose_does_not_report_an_unexpected_stop()
    {
        string directory = Path.Join(Path.GetTempPath(), "viper-keypad-stop-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(directory);
        string path = Path.Join(directory, "input");
        Assert.Equal(0, mkfifo(path, 0x180));
        int writer = open(path, 2 | 0x800);
        Assert.True(writer >= 0);
        KeypadButtons? buttons = null;
        bool reported = false;
        try
        {
            Action<KeypadButtons> onStopped = _ => reported = true;
            var start = typeof(KeypadButtons).GetMethod("Start", BindingFlags.Public | BindingFlags.Static,
                [typeof(string), typeof(Action<bool>), typeof(Action<int>), typeof(Action<KeypadButtons>)]);
            Assert.NotNull(start);
            buttons = (KeypadButtons)start.Invoke(null, [path, null, null, onStopped])!;
            buttons.Dispose();
            buttons = null;
            Assert.False(reported);
        }
        finally
        {
            buttons?.Dispose();
            close(writer);
            Directory.Delete(directory, recursive: true);
        }
    }

    [Fact]
    public void Listener_exit_notification_is_registered_before_an_immediate_eof()
    {
        string path = Path.Join(Path.GetTempPath(), "viper-keypad-eof-" + Guid.NewGuid().ToString("N"));
        File.WriteAllBytes(path, []);
        using var released = new ManualResetEventSlim();
        KeypadButtons? buttons = null;
        try
        {
            Action<bool> onHypershift = held => { if (!held) released.Set(); };
            Action<int> onKeymap = _ => { };
            var start = typeof(KeypadButtons).GetMethod("Start", BindingFlags.Public | BindingFlags.Static,
                [typeof(string), typeof(Action<bool>), typeof(Action<int>)]);
            Assert.NotNull(start);
            buttons = (KeypadButtons)start.Invoke(null, [path, onHypershift, onKeymap])!;
            Assert.True(released.Wait(TimeSpan.FromSeconds(3), TestContext.Current.CancellationToken),
                "The exit callback did not receive the immediate EOF notification.");
        }
        finally
        {
            buttons?.Dispose();
            File.Delete(path);
        }
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public void Listener_registers_callbacks_before_reading_queued_first_report(bool hypershift)
    {
        string directory = Path.Join(Path.GetTempPath(), "viper-keypad-start-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(directory);
        string path = Path.Join(directory, "input");
        Assert.Equal(0, mkfifo(path, 0x180));
        int writer = open(path, 2 | 0x800);
        Assert.True(writer >= 0);
        using var received = new ManualResetEventSlim();
        KeypadButtons? buttons = null;
        try
        {
            byte[] first = hypershift ? [0x04, 0x01] : [0x05, 0x5E, 0x03];
            Assert.Equal((nint)first.Length, write(writer, first, (nuint)first.Length));
            Action<bool> onHypershift = held => { if (held && hypershift) received.Set(); };
            Action<int> onKeymap = value => { if (value == 3 && !hypershift) received.Set(); };
            var start = typeof(KeypadButtons).GetMethod("Start", BindingFlags.Public | BindingFlags.Static,
                [typeof(string), typeof(Action<bool>), typeof(Action<int>)]);
            Assert.NotNull(start);
            buttons = (KeypadButtons)start.Invoke(null, [path, onHypershift, onKeymap])!;
            Assert.True(received.Wait(TimeSpan.FromSeconds(3), TestContext.Current.CancellationToken),
                "The callback did not receive the report already waiting when the listener started.");
        }
        finally
        {
            buttons?.Dispose();
            close(writer);
            Directory.Delete(directory, recursive: true);
        }
    }

    [Fact]
    public void Listener_clears_held_hypershift_when_the_input_node_disappears()
    {
        string directory = Path.Join(Path.GetTempPath(), "viper-keypad-exit-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(directory);
        string path = Path.Join(directory, "input");
        Assert.Equal(0, mkfifo(path, 0x180)); // 0600
        // Keep the write end open while Start opens the FIFO read end.
        int writer = open(path, 2 | 0x800); // O_RDWR | O_NONBLOCK
        Assert.True(writer >= 0);
        using var pressed = new ManualResetEventSlim();
        using var released = new ManualResetEventSlim();
        KeypadButtons? buttons = null;
        try
        {
            buttons = KeypadButtons.Start(path);
            buttons.Hypershift += held => { if (held) pressed.Set(); else released.Set(); };
            Assert.Equal(2, write(writer, [0x04, 0x01], 2));
            Assert.True(pressed.Wait(TimeSpan.FromSeconds(3), TestContext.Current.CancellationToken), "The listener did not receive the press.");
            Assert.Equal(0, close(writer));
            writer = -1;

            Assert.True(released.Wait(TimeSpan.FromSeconds(3), TestContext.Current.CancellationToken), "The listener did not clear Hypershift on hangup.");
        }
        finally
        {
            if (writer >= 0) close(writer);
            buttons?.Dispose();
            Directory.Delete(directory, recursive: true);
        }
    }
}
