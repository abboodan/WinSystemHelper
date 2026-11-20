namespace WinSystemHelper;

public sealed class CommandExecution(int id, bool priority)
{
    public static readonly AsyncLocal<CommandExecution?> Current = new();
    private int _sequence;
    public int Id { get; } = id;
    public bool Priority { get; } = priority;
    public long? ReplyExpires { get; set; }
    public System.Collections.Concurrent.ConcurrentQueue<Task> BackgroundTasks { get; } = new();
    public string NextKey() => $"command:{Id}:reply:{Interlocked.Increment(ref _sequence)}";
}

public static class CommandPolicy
{
    public static bool IsReadOnly(string text)
    {
        string[] p = text.ToLowerInvariant().Split(' ', StringSplitOptions.RemoveEmptyEntries);
        if (p.Length == 0) return true;
        return p[0] is "/start" or "/help" or "/status" or "/healthcheck" or "/version" or "/ping" or "/ip" or "/net" or "/tasks" or "/startup" or "/services"
            || (p[0] == "/queue" && p.Length == 1)
            || (p[0] == "/config" && (p.Length == 1 || p[1] is "export" or "raw" or "admins"))
            || (p[0] == "/service" && p.Length > 1 && p[1] == "status");
    }
    public static bool IsControl(string text) => text.Trim().Equals("/mic stop", StringComparison.OrdinalIgnoreCase)
        || text.Trim().Equals("/queue", StringComparison.OrdinalIgnoreCase)
        || text.StartsWith("/cancel ", StringComparison.OrdinalIgnoreCase)
        || text.StartsWith("/config set OutboxMaxSizeMB ", StringComparison.OrdinalIgnoreCase);
    public static bool IsExpired(string text, long created, long now) => !IsReadOnly(text)
        && !text.Trim().Equals("/mic stop", StringComparison.OrdinalIgnoreCase)
        && !text.StartsWith("/cancel ", StringComparison.OrdinalIgnoreCase) && now - created > 300000;
}
