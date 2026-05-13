namespace WinSystemHelper;

internal readonly record struct PendingConfirmation(
    int Id,
    string CommandKey,
    string Description,
    long RequesterChatId,
    DateTimeOffset ExpiresAt,
    Func<CancellationToken, Task> Operation);
