namespace WinSystemHelper;

internal static class QueueItemKind
{
    public const string Text = "text";
    public const string Voice = "voice";
    public const string Photo = "photo";
}

internal static class OutboxState
{
    public const string Reserved = "reserved";
    public const string Ready = "ready";
    public const string Failed = "failed";
}

internal static class StoredCommandState
{
    public const string Completed = "completed";
    public const string Failed = "failed";
    public const string Expired = "expired";
    public const string Unknown = "unknown";
}
