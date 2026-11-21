namespace WinSystemHelper;

public sealed class CommandExecution(int id, bool priority)
{
    public static readonly AsyncLocal<CommandExecution?> Current = new();
    private int _sequence;
    public int Id { get; } = id;
    public bool Priority { get; } = priority;
    public long? SenderId { get; set; }
    public long ReceivedAt { get; set; }
    public long? ReplyExpires { get; set; }
    public System.Collections.Concurrent.ConcurrentQueue<Task> BackgroundTasks { get; } = new();
    public string NextKey() => $"command:{Id}:reply:{Interlocked.Increment(ref _sequence)}";
}

public static class CommandPolicy
{
    /// <summary>
    /// A command is accepted only when both the chat and the person who sent it are
    /// configured administrators. Checking the chat alone would let every member of an
    /// allowlisted group (and anonymous group admins) control the machine.
    /// </summary>
    public static bool IsAuthorizedSender(long chatId, long? senderId, IReadOnlyCollection<long> adminIds) =>
        senderId is long sender && sender != 0 && adminIds.Contains(chatId) && adminIds.Contains(sender);

    public static bool IsReadOnly(string text)
    {
        string[] p = text.ToLowerInvariant().Split(' ', StringSplitOptions.RemoveEmptyEntries);
        if (p.Length == 0) return true;
        return p[0] is "/start" or "/menu" or "/help" or "/status" or "/healthcheck" or "/log" or "/version" or "/ping" or "/ip" or "/net" or "/tasks" or "/startup" or "/services"
            || (p[0] == "/queue" && p.Length == 1)
            || (p[0] == "/mode" && p.Length == 1)
            || (p[0] == "/config" && (p.Length == 1 || p[1] is "export" or "raw" or "admins"))
            || (p[0] == "/service" && p.Length > 1 && p[1] == "status");
    }
    public static bool IsControl(string text) => text.Trim().Equals("/mic stop", StringComparison.OrdinalIgnoreCase)
        || text.Trim().Equals("/mode", StringComparison.OrdinalIgnoreCase)
        || IsProductionCommand(text)
        || text.Trim().Equals("/queue", StringComparison.OrdinalIgnoreCase)
        || text.StartsWith("/cancel ", StringComparison.OrdinalIgnoreCase)
        || text.StartsWith("/config set OutboxMaxSizeMB ", StringComparison.OrdinalIgnoreCase);
    public static bool IsExpired(string text, long created, long now) => !IsReadOnly(text)
        && !text.Trim().Equals("/mic stop", StringComparison.OrdinalIgnoreCase)
        && !IsProductionCommand(text)
        && !text.StartsWith("/cancel ", StringComparison.OrdinalIgnoreCase) && now - created > 300000;

    private static bool IsProductionCommand(string text)
    {
        string[] parts = text.Split(' ', StringSplitOptions.RemoveEmptyEntries);
        return parts.Length == 2 && parts[0].Equals("/mode", StringComparison.OrdinalIgnoreCase) &&
            parts[1].Equals("production", StringComparison.OrdinalIgnoreCase);
    }
}
