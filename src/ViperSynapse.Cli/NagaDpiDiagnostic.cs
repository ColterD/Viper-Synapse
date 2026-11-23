using System.Collections.Concurrent;
using System.Globalization;
using ViperSynapse.Core.Devices;
using ViperSynapse.Core.Lighting;
using ViperSynapse.Hardware;
using ViperSynapse.Hardware.Linux;
using static ViperSynapse.Hardware.NagaDpiCapture;

namespace ViperSynapse.Cli;

internal static class NagaDpiDiagnostic
{
    const int SampleIntervalMs = 250;

    sealed record PendingFrame(long Ticks, string Report, Sample Before, Edge[] Edges, bool Mouse);
    internal sealed record Options(string Control, string? Capture, string? Mode, int MirrorWindowMs);

    internal static int RunCommand(string[] args)
    {
        if (Parse(args) is { } options)
            return Run(options);
        Console.Error.WriteLine("Usage: viper-cli naga-dpi-watch CONTROL [--back-forward | --both [--mirror-window-ms N]] [--capture FILE]");
        Console.Error.WriteLine($"The mirror window accepts 0–{NagaDpiReportDecoder.MaxMirrorWindowMs} ms and defaults to {NagaDpiReportDecoder.DefaultMirrorWindowMs} ms.");
        return 64;
    }

    internal static Options? Parse(string[] args)
    {
        if (args.Length == 0 || string.IsNullOrWhiteSpace(args[0]) || args[0].StartsWith("--", StringComparison.Ordinal))
            return null;

        string? capture = null, mode = null;
        int? window = null;
        for (int i = 1; i < args.Length; i++)
        {
            switch (args[i])
            {
                case "--back-forward" or "--both" when mode == null:
                    mode = args[i];
                    break;
                case "--capture" when capture == null && i + 1 < args.Length &&
                    !string.IsNullOrWhiteSpace(args[i + 1]) && !args[i + 1].StartsWith("--", StringComparison.Ordinal):
                    capture = args[++i];
                    break;
                case "--mirror-window-ms" when window == null && i + 1 < args.Length &&
                    int.TryParse(args[i + 1], NumberStyles.None, CultureInfo.InvariantCulture, out int value) &&
                    NagaDpiReportDecoder.ValidMirrorWindow(value):
                    window = value;
                    i++;
                    break;
                default:
                    return null;
            }
        }

        return window.HasValue && mode != "--both" ? null
            : new Options(args[0], capture, mode, window ?? NagaDpiReportDecoder.DefaultMirrorWindowMs);
    }

    static int Run(Options options)
    {
        bool backForward = options.Mode == "--back-forward", both = options.Mode == "--both";
        string? capturePath = options.Capture;
        using var cancellation = new CancellationTokenSource();
        ConsoleCancelEventHandler cancel = (_, args) =>
        {
            args.Cancel = true;
            cancellation.Cancel();
        };
        Console.CancelKeyPress += cancel;
        try
        {
            return Run(options.Control, HidrawNode.Enumerate(), node => RazerProbe.HasControlReport(node),
                node => node.Interface == 0 ? NagaChromaButtons.FindMouseBackForwardNode(node) : NagaChromaButtons.FindReportNode(node),
                path => new RazerTransport(HidrawDevice.Open(path)),
                HidrawButtonReader.Open, Console.Out, Console.Error, cancellation.Token,
                capturePath == null ? null : () => new StreamWriter(new FileStream(capturePath, FileMode.CreateNew)) { AutoFlush = true },
                backForward, both, mirrorWindowMs: options.MirrorWindowMs);
        }
        finally
        {
            Console.CancelKeyPress -= cancel;
        }
    }

    internal static int Run(string controlPath, IReadOnlyList<HidrawNode> nodes,
        Func<HidrawNode, bool> hasControlReport, Func<HidrawNode, NagaReportNode?> findReportNode,
        Func<string, RazerTransport> openControl,
        Func<string, HidrawButtonReader.ReportHandler, Action<Exception>, IHidrawButtonReader> openInput,
        TextWriter output, TextWriter errors, CancellationToken cancellationToken, Func<TextWriter>? openCapture = null,
        bool backForward = false, bool both = false, TimeProvider? timeProvider = null,
        int mirrorWindowMs = NagaDpiReportDecoder.DefaultMirrorWindowMs)
    {
        if (cancellationToken.IsCancellationRequested)
            return 0;

        var naga = nodes.Where(node => node is { Bus: HidrawNode.UsbBus, VendorId: 0x1532,
            ProductId: LegacyRemapControls.NagaChroma } && node.PortPath.Length != 0).ToArray();
        var control = naga.FirstOrDefault(node => node.DevicePath == controlPath && node.Interface == 0 && hasControlReport(node));
        if (control == null)
        {
            errors.WriteLine("Select the Naga Chroma's USB control node from viper-cli list (1532:0053, interface 0).");
            return 1;
        }

        NagaReportNode? mouseInput = null;
        if (backForward || both)
        {
            if (findReportNode(control) is not { Kind: NagaReportKind.MouseBackForward,
                ReportLength: >= 1 and <= NagaChromaButtons.MaxMouseReportLength } mouse || mouse.Path != control.DevicePath)
            {
                errors.WriteLine("The Naga Chroma mouse interface must expose an unnumbered Mouse Button 4/5 report under hid-generic.");
                return 1;
            }
            mouseInput = mouse;
        }
        NagaReportNode? factoryInput = null;
        if (!backForward || both)
        {
            var inputs = naga.Where(node => node.PortPath == control.PortPath && node.Interface == 1)
                .Select(findReportNode).OfType<NagaReportNode>().Where(node => node.Kind == NagaReportKind.Dpi).ToArray();
            if (inputs.Length != 1)
            {
                errors.WriteLine("Expected one native Naga Chroma report 04 input node on the same USB port under hid-generic.");
                return 1;
            }
            factoryInput = inputs[0];
        }

        using var transport = openControl(control.DevicePath);
        using var frames = new BlockingCollection<PendingFrame>(boundedCapacity: 256);
        var clock = timeProvider ?? TimeProvider.System;
        long start = clock.GetTimestamp();
        var (latest, startupError) = ReadDpi(transport, clock, start);
        bool ControlLost(HidrawDeviceDisconnectedException? error)
        {
            if (error == null)
                return false;
            errors.WriteLine($"Naga Chroma control node {control.DevicePath} disconnected. {error.Message}");
            return true;
        }
        if (ControlLost(startupError))
            return 1;
        var inputGate = new Lock();
        (string Path, Exception Error)? inputError = null;
        void InputLost(string path, Exception error)
        {
            inputError ??= (path, error);
            frames.CompleteAdding();
        }

        // The raw reader only decodes and queues edges. Feature replies cannot delay its input polling.
        var decoder = new NagaDpiReportDecoder(factoryInput != null, mouseInput?.ReportLength, both ? mirrorWindowMs : null);
        IHidrawButtonReader? OpenReader(NagaReportNode? input, bool mouse)
        {
            if (input == null)
                return null;
            return openInput(input.Path, report =>
            {
                lock (inputGate)
                {
                    if (frames.IsAddingCompleted)
                        return;
                    var before = Volatile.Read(ref latest);
                    long ticks = clock.GetElapsedTime(start).Ticks;
                    if (decoder.Read(mouse, report, ticks) && !frames.TryAdd(new(ticks, Convert.ToHexString(report), before, decoder.Edges.ToArray(), mouse)))
                        InputLost(input.Path, new IOException("DPI button reports arrived faster than the diagnostic could sample them."));
                }
            }, error =>
            {
                lock (inputGate)
                    InputLost(input.Path, error);
            });
        }

        using var factoryReader = OpenReader(factoryInput, mouse: false);
        using var mouseReader = OpenReader(mouseInput, mouse: true);

        using var capture = openCapture?.Invoke();
        if (capture != null)
            WriteHeader(capture, latest, mouseInput?.ReportLength, both, mirrorWindowMs);
        string inputPaths = both ? $"{factoryInput?.Path},{mouseInput?.Path}" : (factoryInput ?? mouseInput)?.Path ?? "";
        output.WriteLine($"Naga Chroma control={control.DevicePath} input={inputPaths} initial={Format(latest)}." +
            (mouseInput != null ? " Back=Down, Forward=Up." : "") +
            (both ? FormattableString.Invariant($" Mirror window={mirrorWindowMs}ms.") : "") + " Press Ctrl+C to stop.");
        factoryReader?.Start();
        mouseReader?.Start();
        try
        {
            while (!frames.IsCompleted)
            {
                if (!frames.TryTake(out var pending, SampleIntervalMs, cancellationToken))
                {
                    if (!frames.IsAddingCompleted)
                    {
                        var (sample, error) = ReadDpi(transport, clock, start);
                        if (ControlLost(error))
                            return 1;
                        Volatile.Write(ref latest, sample);
                    }
                    continue;
                }

                var after = new List<Sample>(2);
                HidrawDeviceDisconnectedException? disconnected = null;
                foreach (var edge in pending.Edges)
                {
                    if (!edge.Pressed)
                        continue;
                    if (disconnected == null)
                    {
                        var (sample, error) = ReadDpi(transport, clock, start);
                        Volatile.Write(ref latest, sample);
                        disconnected = error;
                    }
                    after.Add(latest);
                }
                string? source = both ? pending.Mouse ? MouseSource : FactorySource : null;
                var frame = new Frame(pending.Ticks, pending.Report, pending.Before, after.ToArray(), source);
                if (capture != null)
                    WriteFrame(capture, frame);
                PrintFrame(output, frame, pending.Edges);
                if (ControlLost(disconnected))
                    return 1;
            }
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            return 0;
        }

        if (inputError is not { } failed)
            return 0;
        errors.WriteLine($"{failed.Path}: {failed.Error.Message}");
        return 1;
    }

    static (Sample Sample, HidrawDeviceDisconnectedException? Disconnected) ReadDpi(RazerTransport transport, TimeProvider clock, long start)
    {
        DpiStage? dpi = null;
        try
        {
            var request = NagaChromaSensitivity.ReadRequest();
            var reply = transport.Send(request);
            if (RazerProbe.Accepts(request, reply, strict: false))
                dpi = NagaChromaSensitivity.Decode(reply);
        }
        catch (HidrawDeviceDisconnectedException error) { return (new(clock.GetElapsedTime(start).Ticks, null), error); }
        catch (IOException) { }
        return (new(clock.GetElapsedTime(start).Ticks, dpi), null);
    }
}
