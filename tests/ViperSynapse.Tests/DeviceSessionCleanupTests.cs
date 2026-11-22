using System.Reflection;
using ViperSynapse.Core.Bindings;
using ViperSynapse.Core.Engine;
using ViperSynapse.Core.Input;
using ViperSynapse.Hardware;
using ViperSynapse.Hardware.Linux;

namespace ViperSynapse.Tests;

public class DeviceSessionCleanupTests
{
    sealed class Sink : IInputSink, IEngineHost
    {
        public bool FailReleases { get; set; }
        public readonly List<(ushort Code, int Value)> Events = [];
        public void Key(ushort code, int value)
        {
            if (value == 0 && FailReleases)
                throw new IOException("simulated output failure");
            lock (Events)
                Events.Add((code, value));
        }
        public void Relative(ushort axis, int value) { }
        public void Sync() { }
        public void Launch(string command) { }
        public void Open(string target) { }
        public void SwitchProfile(CycleDirection direction, Guid? profileId) { }
        public void SwitchKeymap(CycleDirection direction, int keymap) { }
        public Macro? FindMacro(Guid id) => null;
        public void Gamepad(GamepadControl control, bool down) { }
        public ShortcutList Shortcuts => ShortcutList.Empty;
        public void Report(Exception error) { }
    }

    [Fact]
    public async Task Dispose_joins_readers_and_closes_nodes_when_output_reset_fails()
    {
        var sink = new Sink();
        var engine = new RemapEngine(sink, sink);
        var session = DeviceSession.Start([], engine);
        string path = System.IO.Path.GetTempFileName();
        using var readerStarted = new ManualResetEventSlim();
        using var allowReaderToExit = new ManualResetEventSlim();
        using var disposeStarted = new ManualResetEventSlim();
        Task<Exception?>? disposing = null;
        try
        {
            var node = EvdevDevice.Open(path);
            var nodes = (List<EvdevDevice>)typeof(DeviceSession).GetField("_nodes", BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(session)!;
            nodes.Add(node);
            var thread = new Thread(() => { readerStarted.Set(); allowReaderToExit.Wait(); });
            var threads = (List<Thread>)typeof(DeviceSession).GetField("_threads", BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(session)!;
            threads.Add(thread);
            thread.Start();
            readerStarted.Wait(TestContext.Current.CancellationToken);
            engine.Key(30, 1);
            sink.FailReleases = true;

            disposing = Task.Run(() => { disposeStarted.Set(); return Record.Exception(session.Dispose); });
            disposeStarted.Wait(TestContext.Current.CancellationToken);
            Assert.NotSame(disposing, await Task.WhenAny(disposing, Task.Delay(200, TestContext.Current.CancellationToken)));
            allowReaderToExit.Set();
            Assert.IsType<IOException>(await disposing);
            Assert.False(thread.IsAlive);
            Assert.Equal(-1, (int)typeof(EvdevDevice).GetField("_fd", BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(node)!);
        }
        finally
        {
            allowReaderToExit.Set();
            if (disposing != null)
                await disposing;
            sink.FailReleases = false;
            session.Dispose();
            File.Delete(path);
        }
    }

    [Fact]
    public void Apply_updates_keymap_when_reset_reports_output_failure()
    {
        var sink = new Sink();
        var engine = new RemapEngine(sink, sink);
        using var session = DeviceSession.Start([], engine);
        engine.Key(30, 1);
        sink.FailReleases = true;
        var next = Keymap.Empty("new");
        Assert.Throws<IOException>(() => session.Apply(next));
        Assert.Same(next, engine.Keymap);
        sink.FailReleases = false;
    }

    [Fact]
    public void Start_preserves_the_open_failure_when_cleanup_also_fails()
    {
        var sink = new Sink();
        var engine = new RemapEngine(sink, sink);
        engine.Key(30, 1);
        sink.FailReleases = true;
        string missing = System.IO.Path.Join(System.IO.Path.GetTempPath(), $"missing-viper-{Guid.NewGuid()}");

        var error = Assert.Throws<IOException>(() => DeviceSession.Start([missing], engine));
        Assert.Contains(missing, error.Message);
        Assert.IsType<IOException>(error.Data["DeviceSessionCleanupFailure"]);
    }

    [Fact]
    public void An_input_event_read_before_shutdown_is_not_dispatched_after_shutdown_begins()
    {
        var sink = new Sink();
        var engine = new RemapEngine(sink, sink);
        var session = DeviceSession.Start([], engine);
        string path = System.IO.Path.GetTempFileName();
        try
        {
            using (var writer = new BinaryWriter(File.OpenWrite(path)))
            {
                writer.Write(0L); writer.Write(0L); writer.Write(EventTypes.Key); writer.Write((ushort)30); writer.Write(1);
            }
            var node = EvdevDevice.Open(path);
            typeof(EvdevDevice).GetProperty(nameof(EvdevDevice.IsGrabbed))!.GetSetMethod(true)!.Invoke(node, [true]);
            int fd = (int)typeof(EvdevDevice).GetField("_fd", BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(node)!;
            var nodes = (List<EvdevDevice>)typeof(DeviceSession).GetField("_nodes", BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(session)!;
            var threads = (List<Thread>)typeof(DeviceSession).GetField("_threads", BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(session)!;
            var gate = (Lock)typeof(DeviceSession).GetField("_engineGate", BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(session)!;
            var pump = typeof(DeviceSession).GetMethod("Pump", BindingFlags.Instance | BindingFlags.NonPublic)!;
            nodes.Add(node);
            var reader = new Thread(() => pump.Invoke(session, [node]));
            threads.Add(reader);

            lock (gate)
            {
                reader.Start();
                Assert.True(SpinWait.SpinUntil(() => File.ReadAllText($"/proc/self/fdinfo/{fd}").Contains("pos:\t24"), TimeSpan.FromSeconds(3)));
                typeof(DeviceSession).GetField("_stopping", BindingFlags.Instance | BindingFlags.NonPublic)!.SetValue(session, true);
            }
            reader.Join();
            Assert.DoesNotContain(((ushort)30, 1), sink.Events);
        }
        finally
        {
            session.Dispose();
            File.Delete(path);
        }
    }

    [Fact]
    public void Start_closes_an_opened_node_when_capability_query_fails()
    {
        var sink = new Sink();
        var engine = new RemapEngine(sink, sink);
        string path = System.IO.Path.GetTempFileName();
        try
        {
            int OpenHandles() => Directory.EnumerateFiles("/proc/self/fd").Count(fd =>
            {
                try { return File.ResolveLinkTarget(fd, false)?.FullName == path; }
                catch (IOException) { return false; }
            });
            int before = OpenHandles();
            Assert.Throws<IOException>(() => DeviceSession.Start([path], engine));
            Assert.Equal(before, OpenHandles());
        }
        finally { File.Delete(path); }
    }

    [Theory]
    [InlineData(EventTypes.Rel)]
    [InlineData(EventTypes.Abs)]
    public void A_failed_relative_or_absolute_capability_query_reports_an_error(ushort eventType)
    {
        string path = System.IO.Path.GetTempFileName();
        try
        {
            using var node = EvdevDevice.Open(path);
            Assert.Throws<IOException>(() => node.Capabilities(eventType, true));
        }
        finally { File.Delete(path); }
    }
}
