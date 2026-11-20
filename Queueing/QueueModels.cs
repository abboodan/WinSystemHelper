namespace WinSystemHelper;

public sealed record OutboxItem(string Id, long ChatId, string Kind, string Text,
    string? Path, long Bytes, int Duration, string? Markup, string State, int Attempts,
    long Created, long Due, long? Expires, string? Error);

public sealed record StoredCommand(int UpdateId, long ChatId, string Payload, bool ReadOnly,
    bool Priority, long Created);

public sealed record CaptureReservation(string Id, string Path, long Limit);

public sealed record QueueSnapshot(long Bytes, long Limit, int Pending, int Failed,
    int Commands, bool Paused, IReadOnlyList<OutboxItem> Problems);

public sealed class QueueCapacityException() : IOException("Queue capacity reached; existing items are preserved.");

// Signals are coalesced and never allocate a waiter per queued file.
public sealed class QueueSignal : IDisposable
{
    private readonly SemaphoreSlim _signal = new(0, 1);
    public void Pulse()
    {
        try { _signal.Release(); }
        catch (SemaphoreFullException) { }
        catch (ObjectDisposedException) { }
    }
    public Task<bool> WaitAsync(TimeSpan timeout, CancellationToken token) => _signal.WaitAsync(timeout, token);
    public void Dispose() => _signal.Dispose();
}
