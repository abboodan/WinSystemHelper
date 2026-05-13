namespace WinSystemHelper;

internal readonly record struct PublicIpSnapshot(
    string? PublicIp,
    DateTimeOffset? FetchedAt,
    DateTimeOffset? NextAttemptAt,
    int FailureCount);
