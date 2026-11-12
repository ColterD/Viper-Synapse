using System.Runtime.ExceptionServices;
using ViperSynapse.Core.Input;

namespace ViperSynapse.Core.Engine;

/// <summary>Coordinates held keys made by independent input producers sharing one virtual device.</summary>
public sealed class SharedModifierSink : IInputSink
{
    readonly IInputSink _sink;
    readonly Lock _gate = new();
    readonly Dictionary<ushort, int> _holders = [];
    readonly Queue<ushort> _pendingReleases = [];
    readonly HashSet<ushort> _queuedReleases = [];
    bool _pendingSync;
    readonly Owner _direct;

    public SharedModifierSink(IInputSink sink)
    {
        _sink = sink;
        _direct = new Owner(this, true);
    }

    /// <summary>Creates an independent producer whose outstanding key holds can be released on disposal.</summary>
    public Owner CreateOwner(bool collapseRepeatedDowns = false) => new(this, collapseRepeatedDowns);

    // An explicitly shared sink may also be used by a caller that does not create an owner.
    public void Key(ushort code, int value) => _direct.Key(code, value);
    public void Relative(ushort axis, int value)
    {
        lock (_gate)
        {
            FlushPending();
            _sink.Relative(axis, value);
        }
    }

    public void Sync()
    {
        lock (_gate)
        {
            FlushPending();
            SyncCore();
        }
    }

    /// <summary>Retries queued releases and their synchronization without emitting a new input event.</summary>
    public void RetryPending()
    {
        lock (_gate)
            FlushPending();
    }

    // If a release or SYN_REPORT failed, retry it before sending a subsequent input event.
    // An actively held key must never be released by a delayed retry.
    void FlushPending()
    {
        // Finish a previously emitted frame before adding queued releases to a new one.
        if (_pendingSync)
            SyncCore();
        while (_pendingReleases.TryPeek(out ushort code))
        {
            if (_holders.ContainsKey(code))
                throw new IOException("A pending key release is still held by another input producer.");
            _sink.Key(code, 0);
            _pendingReleases.Dequeue();
            _queuedReleases.Remove(code);
            _pendingSync = true;
        }
        if (_pendingSync)
            SyncCore();
    }

    void QueueRelease(ushort code)
    {
        // No new downs can pass an unresolved release, so duplicate pending ups are redundant.
        if (_queuedReleases.Add(code))
            _pendingReleases.Enqueue(code);
    }

    void SyncCore()
    {
        try
        {
            _sink.Sync();
            _pendingSync = false;
        }
        catch
        {
            _pendingSync = true;
            throw;
        }
    }

    static bool IsModifier(ushort code) => code is KeyCodes.LeftCtrl or KeyCodes.RightCtrl
        or KeyCodes.LeftShift or KeyCodes.RightShift or KeyCodes.LeftAlt or KeyCodes.RightAlt
        or KeyCodes.LeftMeta or KeyCodes.RightMeta;

    /// <summary>A single binding or macro execution's contribution to the shared virtual keyboard.</summary>
    public sealed class Owner : IInputSink, IDisposable
    {
        readonly SharedModifierSink _shared;
        readonly bool _collapseRepeatedDowns;
        readonly Dictionary<ushort, int> _held = [];
        bool _disposed;

        internal Owner(SharedModifierSink shared, bool collapseRepeatedDowns)
        {
            _shared = shared;
            _collapseRepeatedDowns = collapseRepeatedDowns;
        }

        public void Key(ushort code, int value)
        {
            lock (_shared._gate)
            {
                if (_disposed)
                    return;
                int owned = _held.GetValueOrDefault(code);
                if (value == 0 && owned > 0)
                {
                    if (owned > 1)
                    {
                        _held[code] = owned - 1;
                        return;
                    }
                    int holders = _shared._holders[code];
                    if (holders == 1)
                    {
                        try
                        {
                            _shared.FlushPending();
                            _shared._sink.Key(code, 0);
                        }
                        catch
                        {
                            _shared.QueueRelease(code);
                            _held.Remove(code);
                            _shared._holders.Remove(code);
                            throw;
                        }
                        _shared._holders.Remove(code);
                    }
                    else
                        _shared._holders[code] = holders - 1;
                    _held.Remove(code);
                    return;
                }
                // An unmatched key-up must not release a key owned by another producer.
                if (value == 0 && (IsModifier(code) || _shared._holders.ContainsKey(code)))
                    return;
                try
                {
                    _shared.FlushPending();
                    _shared._sink.Key(code, value);
                }
                catch
                {
                    if (value == 0)
                        _shared.QueueRelease(code);
                    throw;
                }
                if (value == 1)
                {
                    if (owned == 0)
                        _shared._holders[code] = _shared._holders.GetValueOrDefault(code) + 1;
                    // A chord may request the same modifier as both modifier and primary key.
                    if (owned == 0 || (IsModifier(code) && !_collapseRepeatedDowns))
                        _held[code] = owned + 1;
                }
            }
        }

        public void Relative(ushort axis, int value) => _shared.Relative(axis, value);
        public void Sync() => _shared.Sync();

        public void Dispose()
        {
            lock (_shared._gate)
            {
                if (_disposed)
                    return;
                bool released = false;
                Exception? failure = null;
                try { _shared.FlushPending(); }
                catch (Exception error) { failure = error; }
                // Primary keys are acquired after chord modifiers and should be released first.
                foreach (ushort code in _held.Keys.Reverse().ToArray())
                {
                    int holders = _shared._holders[code];
                    if (holders == 1)
                    {
                        if (failure != null)
                            _shared.QueueRelease(code);
                        else
                        {
                            try
                            {
                                _shared._sink.Key(code, 0);
                                released = true;
                            }
                            catch (Exception error)
                            {
                                failure = error;
                                _shared.QueueRelease(code);
                            }
                        }
                        _shared._holders.Remove(code);
                    }
                    else
                        _shared._holders[code] = holders - 1;
                    _held.Remove(code);
                }
                _disposed = true;
                if (released)
                {
                    try { _shared.SyncCore(); }
                    catch (Exception error) { failure ??= error; }
                }
                if (failure != null)
                    ExceptionDispatchInfo.Capture(failure).Throw();
            }
        }
    }
}
