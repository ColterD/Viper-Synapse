namespace ViperSynapse.Service;

/// <summary>Owns one analog reader and its presentation lease. A session can use the same lease without a reader.</summary>
internal sealed class StickWatch : IDisposable
{
    readonly Lock _gate = new();
    Timer? _timer;
    IDisposable? _reader;
    long _generation, _until, _lastSent;
    bool _opening, _retiring, _disposed;

    public long? Renew(int milliseconds, bool needReader)
    {
        lock (_gate)
        {
            if (_disposed)
                return null;
            _until = Environment.TickCount64 + milliseconds;
            if (needReader || _reader != null)
                (_timer ??= new Timer(_ => Expire())).Change(milliseconds, Timeout.Infinite);
            if (!needReader || _reader != null || _opening || _retiring)
                return null;
            _opening = true;
            return ++_generation;
        }
    }

    public void Complete(long generation, IDisposable? reader)
    {
        lock (_gate)
        {
            _opening = false;
            if (_generation == generation && !_disposed && Environment.TickCount64 < _until)
            {
                _reader = reader;
                return;
            }
            _retiring = reader != null;
        }
        FinishRetirement(reader);
    }

    public bool ShouldReport(long? readerGeneration)
    {
        lock (_gate)
        {
            long now = Environment.TickCount64;
            if (_disposed || now >= _until || now - _lastSent < 33 ||
                (readerGeneration is long generation && (_generation != generation || _reader == null)))
                return false;
            _lastSent = now;
            return true;
        }
    }

    void Expire()
    {
        IDisposable? reader;
        lock (_gate)
        {
            long remaining = _until - Environment.TickCount64;
            if (remaining > 0)
            {
                _timer?.Change((int)remaining, Timeout.Infinite);
                return;
            }
            reader = TakeReader();
        }
        FinishRetirement(reader);
    }

    public void RetireReader()
    {
        IDisposable? reader;
        lock (_gate)
            reader = TakeReader();
        FinishRetirement(reader);
    }

    IDisposable? TakeReader()
    {
        ++_generation;
        var reader = _reader;
        _reader = null;
        if (reader != null)
            _retiring = true;
        return reader;
    }

    void FinishRetirement(IDisposable? reader)
    {
        if (reader == null)
            return;
        try { reader.Dispose(); }
        finally { lock (_gate) _retiring = false; }
    }

    public void Dispose()
    {
        IDisposable? reader;
        lock (_gate)
        {
            if (_disposed)
                return;
            _disposed = true;
            _timer?.Dispose();
            _timer = null;
            reader = TakeReader();
        }
        FinishRetirement(reader);
    }
}
