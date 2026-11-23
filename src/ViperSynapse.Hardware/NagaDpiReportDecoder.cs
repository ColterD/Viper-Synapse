using ViperSynapse.Core.Devices;
using ViperSynapse.Core.Protocol;
using static ViperSynapse.Hardware.NagaDpiCapture;

namespace ViperSynapse.Hardware;

/// <summary>Merges each button's observations across the two input streams.</summary>
internal sealed class NagaDpiReportDecoder
{
    internal const int DefaultMirrorWindowMs = 30;
    internal const int MaxMirrorWindowMs = NagaDpiWindowExportCodec.MaxWindowMs;

    readonly NagaChromaButtons.Decoder? _factory;
    readonly NagaChromaButtons.Decoder? _mouse;
    readonly int _mouseLength;
    readonly List<Edge> _edges = new(2);
    readonly ButtonState _up, _down;
    long _ticks;
    bool _sourceChanged;

    internal NagaDpiReportDecoder(bool factory, int? mouseReportLength, int? mirrorWindowMs = null)
    {
        _up = new(mirrorWindowMs);
        _down = new(mirrorWindowMs);
        if (factory)
            _factory = new(new("", NagaReportKind.Dpi, NagaChromaButtons.VendorReportLength), (source, value) => OnKey(1, source, value));
        if (mouseReportLength is int length)
        {
            _mouseLength = length;
            _mouse = new(new("", NagaReportKind.MouseBackForward, length), (source, value) => OnKey(2, source, value));
        }
    }

    internal IReadOnlyList<Edge> Edges => _edges;

    internal static bool ValidMirrorWindow(int milliseconds) => milliseconds is >= 0 and <= MaxMirrorWindowMs;

    internal int ReportLength(bool mouse) => mouse ? _mouseLength : _factory == null ? 0 : NagaChromaButtons.VendorReportLength;

    // The source may change without a logical edge, such as a second interface reporting a held button.
    internal bool Read(bool mouse, ReadOnlySpan<byte> report, long ticks)
    {
        _ticks = ticks;
        _edges.Clear();
        _sourceChanged = false;
        (mouse ? _mouse : _factory)?.Read(report);
        return _sourceChanged;
    }

    void OnKey(int owner, ushort source, int value)
    {
        if (!LegacyRemapControls.IsNagaDpiSource(source))
            return;
        _sourceChanged = true;
        var button = source == LegacyRemapControls.NagaDpiUp ? _up : _down;
        var change = button.Update(owner, value == 1, _ticks);
        if (change is Change.Release or Change.Repress)
            _edges.Add(new(source, false));
        if (change is Change.Press or Change.Repress)
            _edges.Add(new(source, true));
    }

    enum Change { None, Press, Release, Repress }

    sealed class ButtonState(int? mirrorWindowMs)
    {
        readonly long _windowTicks = (mirrorWindowMs ?? 0) * TimeSpan.TicksPerMillisecond;
        // One pending release per millisecond accommodates the device's USB report rate.
        readonly int _pendingLimit = Math.Max(64, mirrorWindowMs ?? 0);
        readonly Queue<long> _factoryReleases = new();
        readonly Queue<long> _mouseReleases = new();
        int _owners, _seen, _mirrors;

        public Change Update(int owner, bool pressed, long ticks)
        {
            if (pressed)
            {
                // Each interface preserves its own report order. Match the oldest recent cycle first.
                if (_windowTicks > 0 && TakeRecent(owner == 1 ? _mouseReleases : _factoryReleases, ticks))
                {
                    _mirrors |= owner;
                    return Change.None;
                }
                if (_owners == 0)
                {
                    _owners = _seen = owner;
                    return Change.Press;
                }
                if (mirrorWindowMs.HasValue && (_seen & owner) != 0)
                {
                    // Another cycle on the same interface proves a new press even if its old mirror is still held.
                    _mirrors |= _owners;
                    _owners = _seen = owner;
                    return Change.Repress;
                }
                _owners |= owner;
                _seen |= owner;
                return Change.None;
            }

            if ((_mirrors & owner) != 0)
            {
                _mirrors &= ~owner;
                return Change.None;
            }
            _owners &= ~owner;
            if (_owners != 0)
                return Change.None;
            if (_windowTicks > 0 && _seen == owner)
            {
                var pending = owner == 1 ? _factoryReleases : _mouseReleases;
                Expire(pending, ticks);
                // Bound the state even for replay files with arbitrarily many reports at one timestamp.
                if (pending.Count == _pendingLimit)
                    pending.Dequeue();
                pending.Enqueue(ticks);
            }
            _seen = 0;
            return Change.Release;
        }

        bool TakeRecent(Queue<long> pending, long ticks)
        {
            Expire(pending, ticks);
            return pending.TryDequeue(out _);
        }

        void Expire(Queue<long> pending, long ticks)
        {
            while (pending.TryPeek(out long release) && ticks - release > _windowTicks)
                pending.Dequeue();
        }
    }
}
