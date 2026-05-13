namespace WinSystemHelper;

internal sealed class PersistentMicLoopState
{
    public bool Active { get; set; }

    public int DurationSeconds { get; set; }

    public DateTimeOffset StartedAt { get; set; }

    public long RequestedByAdminChatId { get; set; }
}
