using System.Buffers.Binary;
using System.Runtime.InteropServices;
using ViperSynapse.Hardware.Linux;

namespace ViperSynapse.Hardware;

internal interface IHidrawButtonReader : IDisposable
{
    void Start();
}

internal sealed class HidrawButtonReader : IHidrawButtonReader
{
    const int DescriptorMaxLength = 4096;
    internal delegate void ReportHandler(ReadOnlySpan<byte> report);

    readonly int _fd;
    readonly string _path;
    readonly Thread _thread;
    readonly ReportHandler _onReport;
    readonly Action<Exception> _onLost;
    bool _started;
    int _disposed;

    HidrawButtonReader(int fd, string path, ReportHandler onReport, Action<Exception> onLost)
    {
        _fd = fd;
        _path = path;
        _onReport = onReport;
        _onLost = onLost;
        _thread = new Thread(Listen) { IsBackground = true, Name = "HID input " + path };
    }

    internal static IHidrawButtonReader Open(string path, ReportHandler onReport, Action<Exception> onLost)
    {
        int fd = Libc.Open(path, Libc.O_RDONLY | Libc.O_CLOEXEC | Libc.O_NONBLOCK);
        if (fd < 0)
            throw new IOException($"{path}: {Libc.ErrorText(Marshal.GetLastPInvokeError())}");
        try
        {
            return new HidrawButtonReader(fd, path, onReport, onLost);
        }
        catch
        {
            Libc.Close(fd);
            throw;
        }
    }

    public void Start()
    {
        lock (_thread)
        {
            ObjectDisposedException.ThrowIf(_disposed != 0, this);
            _thread.Start();
            _started = true;
        }
    }

    internal static bool TryReadGenericDescriptor(HidrawNode node, string sysRoot, out ReadOnlySpan<byte> descriptor)
    {
        descriptor = default;
        var device = Path.Combine(sysRoot, Path.GetFileName(node.DevicePath), "device");
        try
        {
            // A device-specific driver may already translate these reports to evdev events.
            if (new DirectoryInfo(Path.Combine(device, "driver")).ResolveLinkTarget(false)?.Name != "hid-generic")
                return false;
            using var stream = File.OpenRead(Path.Combine(device, "report_descriptor"));
            var buffer = new byte[DescriptorMaxLength + 1];
            int length = stream.ReadAtLeast(buffer, buffer.Length, false);
            if (length > DescriptorMaxLength)
                return false;
            descriptor = buffer.AsSpan(0, length);
            return true;
        }
        catch (IOException) { return false; }
        catch (UnauthorizedAccessException) { return false; }
    }

    internal static bool TryGetInputReportBits(ReadOnlySpan<byte> descriptor, byte reportId, int maxBits, out int bits) =>
        TryGetInputReportBits(descriptor, reportId, maxBits, requireMouseButtons: false, out bits);

    internal static bool TryGetMouseButtonReportBits(ReadOnlySpan<byte> descriptor, int maxBits, out int bits) =>
        TryGetInputReportBits(descriptor, 0, maxBits, requireMouseButtons: true, out bits);

    static bool TryGetInputReportBits(ReadOnlySpan<byte> descriptor, byte reportId, int maxBits, bool requireMouseButtons, out int bits)
    {
        const uint ButtonPage = 0x09;
        (uint Id, uint Size, uint Count, uint UsagePage) globals = default;
        var stack = new Stack<(uint Id, uint Size, uint Count, uint UsagePage)>();
        (uint? Min, uint? Max, bool Other, bool HasButtons) local = default;
        bits = 0;
        bool hasData = false;
        for (int i = 0; i < descriptor.Length;)
        {
            byte prefix = descriptor[i++];
            if (prefix == 0xFE)
            {
                if (descriptor.Length - i < 2)
                    return false;
                int length = descriptor[i] + 2;
                if (descriptor.Length - i < length)
                    return false;
                i += length;
                continue;
            }

            int size = prefix & 3;
            if (size == 3)
                size = 4;
            if (descriptor.Length - i < size)
                return false;
            var data = descriptor.Slice(i, size);
            i += size;
            uint value = size switch
            {
                1 => data[0],
                2 => BinaryPrimitives.ReadUInt16LittleEndian(data),
                4 => BinaryPrimitives.ReadUInt32LittleEndian(data),
                _ => 0,
            };
            uint FullUsage() => size == 4 ? value : globals.UsagePage <= ushort.MaxValue
                ? (globals.UsagePage << 16) | value : 0;
            // The high six bits identify the HID item type and tag.
            switch (prefix & 0xFC)
            {
                case 0x04: globals.UsagePage = value; break;
                case 0x74: globals.Size = value; break;
                case 0x84:
                    if (value is 0 or > byte.MaxValue)
                        return false;
                    globals.Id = value;
                    break;
                case 0x94: globals.Count = value; break;
                case 0xA4: stack.Push(globals); break;
                case 0xB4:
                    if (!stack.TryPop(out globals))
                        return false;
                    break;
                case 0x08 when requireMouseButtons:
                    local.Other = true;
                    local.HasButtons |= (FullUsage() >> 16) == ButtonPage;
                    break;
                case 0x18 when requireMouseButtons:
                    uint usageMin = FullUsage();
                    local.Other |= local.Min.HasValue || size == 0;
                    local.Min = usageMin;
                    local.HasButtons |= (usageMin >> 16) == ButtonPage;
                    break;
                case 0x28 when requireMouseButtons:
                    uint usageMax = FullUsage();
                    local.Other |= local.Max.HasValue || size == 0;
                    local.Max = usageMax;
                    local.HasButtons |= (usageMax >> 16) == ButtonPage;
                    break;
                case 0xA8 when requireMouseButtons: // Local delimiter can define alternative usages.
                    local.Other = true;
                    break;
                case 0x80 when globals.Id == reportId:
                    ulong inputBits = (ulong)globals.Size * globals.Count;
                    if (inputBits == 0 || inputBits > (ulong)(maxBits - bits))
                        return false;
                    if (requireMouseButtons)
                    {
                        if (size == 0)
                            return false;
                        if (bits == 0)
                        {
                            // Bits 0–4 must be the first five Button usages, as a Data/Variable/Absolute field.
                            if ((value & 7) != 2 || globals.UsagePage != ButtonPage || globals.Size != 1 ||
                                globals.Count < 5 || local.Other || local.Min != 0x00090001 ||
                                local.Max is not uint max || max >> 16 != ButtonPage || globals.Count > (max & 0xFFFF))
                                return false;
                        }
                        else if ((value & 1) == 0 && (globals.UsagePage == ButtonPage || local.HasButtons))
                        {
                            return false;
                        }
                    }
                    bits += (int)inputBits;
                    hasData |= (value & 1) == 0;
                    break;
                case 0x80 when reportId == 0:
                    // An unnumbered stream cannot safely be decoded alongside numbered input reports.
                    return false;
            }
            if ((prefix & 0x0C) == 0)
                local = default;
        }
        return stack.Count == 0 && hasData;
    }

    unsafe void Listen()
    {
        // Keep space for larger unrelated reports so a truncated read cannot look like a supported button report.
        var buffer = new byte[64];
        var poll = new Libc.PollFd { Fd = _fd, Events = Libc.POLLIN };
        try
        {
            while (Volatile.Read(ref _disposed) == 0)
            {
                poll.Revents = 0;
                int ready = Libc.Poll(&poll, 1, 100);
                if (ready < 0)
                {
                    int errno = Marshal.GetLastPInvokeError();
                    if (errno == Libc.EINTR)
                        continue;
                    throw new IOException($"{_path}: {Libc.ErrorText(errno)}");
                }
                if ((poll.Revents & (Libc.POLLERR | Libc.POLLHUP | Libc.POLLNVAL)) != 0)
                    throw new IOException($"{_path}: the input report node is unavailable.");
                if ((poll.Revents & Libc.POLLIN) == 0)
                    continue;

                nint count;
                fixed (byte* data = buffer)
                    count = Libc.Read(_fd, data, (nuint)buffer.Length);
                if (count < 0)
                {
                    int errno = Marshal.GetLastPInvokeError();
                    if (errno is Libc.EINTR or Libc.EAGAIN)
                        continue;
                    throw new IOException($"{_path}: {Libc.ErrorText(errno)}");
                }
                if (count == 0)
                    throw new IOException($"{_path}: the input report node closed.");
                if (Volatile.Read(ref _disposed) == 0)
                    _onReport(buffer.AsSpan(0, (int)count));
            }
        }
        catch (Exception error)
        {
            if (Volatile.Read(ref _disposed) == 0)
                _onLost(error);
        }
    }

    public void Dispose()
    {
        lock (_thread)
        {
            if (_disposed != 0)
                return;
            Volatile.Write(ref _disposed, 1);
        }
        if (_started)
            _thread.Join();
        Libc.Close(_fd);
    }
}
