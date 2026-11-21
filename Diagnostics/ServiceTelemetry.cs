using System.Diagnostics;

namespace WinSystemHelper;

internal sealed class ServiceTelemetry
{
    private readonly object _telemetrySync = new();

    private readonly DateTimeOffset _startedAt = DateTimeOffset.Now;

    private readonly Stopwatch _uptime = Stopwatch.StartNew();

    private DateTimeOffset? _lastPollingSuccessAt;

    private DateTimeOffset? _lastPollingFailureAt;

    private DateTimeOffset? _lastWakeAlertAt;

    private DateTimeOffset? _lastStartupAlertAt;

    private DateTimeOffset? _lastSmartAlertAt;

    private string? _lastError;

    private int _telegramPollingFailureStreak;

    public TelemetrySnapshot GetTelemetrySnapshot()
    {
        lock (_telemetrySync)
        {
            return new TelemetrySnapshot(
                _lastPollingSuccessAt,
                _lastPollingFailureAt,
                _lastWakeAlertAt,
                _lastStartupAlertAt,
                _lastSmartAlertAt,
                _lastError,
                _telegramPollingFailureStreak);
        }
    }

    public void RecordPollingSuccess()
    {
        lock (_telemetrySync)
        {
            _lastPollingSuccessAt = DateTimeOffset.Now;
            _telegramPollingFailureStreak = 0;
        }
    }

    public void RecordPollingFailure(Exception exception)
    {
        lock (_telemetrySync)
        {
            _lastPollingFailureAt = DateTimeOffset.Now;
            _telegramPollingFailureStreak++;
            _lastError = $"Telegram polling: {exception.GetType().Name}: {exception.Message}";
        }
    }

    public void RecordError(string error)
    {
        lock (_telemetrySync)
        {
            _lastError = error;
        }
    }

    public DateTimeOffset StartedAt => _startedAt;

    public TimeSpan Uptime => _uptime.Elapsed;

    public int PollingFailureStreak => _telegramPollingFailureStreak;

    public void RecordWakeAlert() => RecordAlert(ref _lastWakeAlertAt);

    public void RecordStartupAlert() => RecordAlert(ref _lastStartupAlertAt);

    public void RecordSmartAlert() => RecordAlert(ref _lastSmartAlertAt);

    private void RecordAlert(ref DateTimeOffset? timestamp)
    {
        lock (_telemetrySync)
        {
            timestamp = DateTimeOffset.Now;
        }
    }
}
