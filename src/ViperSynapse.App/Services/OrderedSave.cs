using Avalonia.Threading;

namespace ViperSynapse.App.Services;

/// <summary>Queues immutable UI snapshots in edit order. Only the latest result changes the visible save state.</summary>
internal sealed class OrderedSave<T>(Func<T, Task> write, Action changed)
{
    T _latest = default!;

    public long Revision { get; private set; }
    public Task Pending { get; private set; } = Task.CompletedTask;
    public bool Saving { get; private set; }
    public bool HasSaved { get; private set; }
    public Exception? Failure { get; private set; }
    public Exception? ReloadFailure { get; private set; }
    public bool DiscardRequested { get; private set; }
    public bool Reloading { get; private set; }
    public bool HasUncommittedChanges => Saving || Failure != null;

    public void Enqueue(T value)
    {
        _latest = value;
        long revision = ++Revision;
        Failure = null;
        ReloadFailure = null;
        DiscardRequested = false;
        Reloading = false;
        Saving = true;
        Pending = SaveAfterAsync(Pending, revision, value);
        changed();
    }

    public void Retry()
    {
        if (Failure != null)
            Enqueue(_latest);
    }

    public void RequestDiscard(bool requested)
    {
        if (Failure == null || Reloading)
            return;
        DiscardRequested = requested;
        changed();
    }

    public async Task DiscardAsync<TSnapshot>(Func<Task<TSnapshot>> read, Action<TSnapshot> accept, Func<bool> stillCurrent)
    {
        if (!DiscardRequested || Failure == null || Reloading)
            return;
        long revision = Revision;
        DiscardRequested = false;
        Reloading = true;
        ReloadFailure = null;
        changed();
        TSnapshot snapshot = default!;
        Exception? error = null;
        try { snapshot = await read().ConfigureAwait(false); }
        catch (Exception e) { error = e; }

        await Dispatcher.UIThread.InvokeAsync(() =>
        {
            if (Revision != revision || !Reloading)
                return;
            Reloading = false;
            ReloadFailure = error;
            if (error == null && stillCurrent())
            {
                accept(snapshot);
                ++Revision;
                _latest = default!;
                HasSaved = false;
                Failure = null;
            }
            changed();
        });
    }

    async Task SaveAfterAsync(Task previous, long revision, T value)
    {
        await previous.ConfigureAwait(false);
        Exception? error = null;
        try { await write(value).ConfigureAwait(false); }
        catch (Exception e) { error = e; }

        await Dispatcher.UIThread.InvokeAsync(() =>
        {
            if (Revision != revision)
                return;
            Failure = error;
            Saving = false;
            HasSaved = error == null;
            changed();
        });
    }
}
