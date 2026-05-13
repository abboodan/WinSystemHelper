namespace WinSystemHelper;

internal readonly record struct TelemetrySnapshot(
    DateTimeOffset? LastPollingSuccessAt,
    DateTimeOffset? LastPollingFailureAt,
    DateTimeOffset? LastWakeAlertAt,
    DateTimeOffset? LastStartupAlertAt,
    DateTimeOffset? LastSmartAlertAt,
    string? LastError,
    int PollingFailureStreak);
