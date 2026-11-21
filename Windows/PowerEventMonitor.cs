using System.Diagnostics.Eventing.Reader;
using System.Security;
using static WinSystemHelper.CommandUtilities;

namespace WinSystemHelper;

internal sealed class PowerEventMonitor(
    ILogger<Worker> logger,
    CommandBackgroundWork backgroundWork,
    TelegramConnection connection,
    ServiceTelemetry telemetry,
    PowerEventService events) : IDisposable
{
    private const string WakeEventLogName = "System";

    private const string WakeEventLogQuery =
        "*[System[Provider[@Name='Microsoft-Windows-Power-Troubleshooter'] and EventID=1]]";

    private const string PowerEventLogName = "System";

    private const string PowerEventLogQuery =
        "*[System[(Provider[@Name='User32'] and EventID=1074) or (Provider[@Name='Microsoft-Windows-Kernel-Power'] and EventID=42)]]";

    private readonly SemaphoreSlim _resumeNotificationLock = new(1, 1);

    private readonly object _wakeEventWatcherSync = new();

    private readonly object _powerEventWatcherSync = new();

    private EventLogWatcher? _wakeEventWatcher;

    private EventLogWatcher? _powerEventWatcher;

    private long _lastWakeEventRecordId;

    private long _lastPowerEventRecordId;

    public void RegisterWakeEventWatcher()
    {
        lock (_wakeEventWatcherSync)
        {
            if (_wakeEventWatcher is not null)
            {
                return;
            }

            try
            {
                EventLogQuery query = new(WakeEventLogName, PathType.LogName, WakeEventLogQuery);
                EventLogWatcher watcher = new(query);
                watcher.EventRecordWritten += OnWakeEventRecordWritten;
                watcher.Enabled = true;

                _wakeEventWatcher = watcher;
                logger.LogInformation(
                    "Registered wake detector for {ProviderName} Event ID 1.",
                    "Microsoft-Windows-Power-Troubleshooter");
            }
            catch (Exception ex) when (ex is EventLogException or UnauthorizedAccessException)
            {
                logger.LogError(
                    ex,
                    "Failed to register wake detector against the Windows System event log.");
            }
            catch (Exception ex)
            {
                logger.LogError(
                    ex,
                    "Unexpected failure while registering the Windows System event log wake detector.");
            }
        }
    }

    public void UnregisterWakeEventWatcher()
    {
        EventLogWatcher? watcher;

        lock (_wakeEventWatcherSync)
        {
            watcher = _wakeEventWatcher;
            _wakeEventWatcher = null;
        }

        if (watcher is null)
        {
            return;
        }

        try
        {
            watcher.Enabled = false;
            watcher.EventRecordWritten -= OnWakeEventRecordWritten;
            watcher.Dispose();
        }
        catch (ObjectDisposedException)
        {
        }
        catch (Exception ex)
        {
            logger.LogWarning(ex, "Failed to unregister wake event log watcher cleanly.");
        }
    }

    public void RegisterPowerEventWatcher()
    {
        lock (_powerEventWatcherSync)
        {
            if (_powerEventWatcher is not null)
            {
                return;
            }

            try
            {
                EventLogQuery query = new(PowerEventLogName, PathType.LogName, PowerEventLogQuery);
                EventLogWatcher watcher = new(query);
                watcher.EventRecordWritten += OnPowerEventRecordWritten;
                watcher.Enabled = true;

                _powerEventWatcher = watcher;
                logger.LogInformation("Registered power event detector for User32 1074 and Kernel-Power 42.");
            }
            catch (Exception ex) when (ex is EventLogException or UnauthorizedAccessException)
            {
                logger.LogError(ex, "Failed to register power event detector against the Windows System event log.");
            }
            catch (Exception ex)
            {
                logger.LogError(ex, "Unexpected failure while registering the Windows System event log power detector.");
            }
        }
    }

    public void UnregisterPowerEventWatcher()
    {
        EventLogWatcher? watcher;

        lock (_powerEventWatcherSync)
        {
            watcher = _powerEventWatcher;
            _powerEventWatcher = null;
        }

        if (watcher is null)
        {
            return;
        }

        try
        {
            watcher.Enabled = false;
            watcher.EventRecordWritten -= OnPowerEventRecordWritten;
            watcher.Dispose();
        }
        catch (ObjectDisposedException)
        {
        }
        catch (Exception ex)
        {
            logger.LogWarning(ex, "Failed to unregister power event log watcher cleanly.");
        }
    }

    private void OnWakeEventRecordWritten(object? sender, EventRecordWrittenEventArgs e)
    {
        if (e.EventException is not null)
        {
            logger.LogWarning(e.EventException, "Wake event log watcher reported an error.");
            return;
        }

        EventRecord? record = e.EventRecord;
        if (record is null)
        {
            return;
        }

        try
        {
            long? recordId = record.RecordId;
            if (recordId.HasValue &&
                Interlocked.Exchange(ref _lastWakeEventRecordId, recordId.Value) == recordId.Value)
            {
                return;
            }

            logger.LogInformation(
                "Power-Troubleshooter resume event detected. EventRecordId: {EventRecordId}.",
                recordId);

            backgroundWork.QueueBackgroundWork(SendWakeAlertAsync, "Wake alert failed.");
        }
        catch (Exception ex)
        {
            logger.LogWarning(ex, "Failed to process Power-Troubleshooter resume event.");
        }
        finally
        {
            record.Dispose();
        }
    }

    private void OnPowerEventRecordWritten(object? sender, EventRecordWrittenEventArgs e)
    {
        if (e.EventException is not null)
        {
            logger.LogWarning(e.EventException, "Power event log watcher reported an error.");
            return;
        }

        EventRecord? record = e.EventRecord;
        if (record is null)
        {
            return;
        }

        try
        {
            long? recordId = record.RecordId;
            if (recordId.HasValue &&
                Interlocked.Exchange(ref _lastPowerEventRecordId, recordId.Value) == recordId.Value)
            {
                return;
            }

            PowerEventReport report = BuildPowerEventReportFromEventLog(record);
            if (events.ShouldSuppressDuplicateBotPowerEvent(report.Kind))
            {
                logger.LogDebug(
                    "Suppressed duplicate Windows power event {PowerEventKind} after bot command.",
                    report.Kind);
                return;
            }

            logger.LogInformation(
                "Windows power event detected. Kind: {PowerEventKind}. EventRecordId: {EventRecordId}.",
                report.Kind,
                recordId);

            backgroundWork.QueueBackgroundWork(
                token => events.HandleDetectedPowerEventAsync(report, token),
                "Power event alert failed.");
        }
        catch (Exception ex)
        {
            logger.LogWarning(ex, "Failed to process Windows power event.");
        }
        finally
        {
            record.Dispose();
        }
    }

    private async Task SendWakeAlertAsync(CancellationToken cancellationToken)
    {
        if (!await _resumeNotificationLock.WaitAsync(0, cancellationToken))
        {
            return;
        }

        try
        {
            logger.LogInformation("Resume detected. Queuing notification.");

            string machineName = Environment.MachineName;
            string userName = Environment.UserName;
            string timestamp = DateTimeOffset.Now.ToString("yyyy-MM-dd HH:mm:ss zzz");

            await connection.SendTelegramBroadcastWithRetryAsync(
                $"🔔 Administrative alert: {machineName} resumed from sleep at {timestamp}. User: {userName}.",
                cancellationToken);

            telemetry.RecordWakeAlert();

            logger.LogInformation("Wake alert queued.");
        }
        finally
        {
            _resumeNotificationLock.Release();
        }
    }

    private static PowerEventReport BuildPowerEventReportFromEventLog(EventRecord record)
    {
        string providerName = record.ProviderName ?? "Unknown";
        int eventId = record.Id;
        string description = SafeFormatEventDescription(record);
        PowerEventKind kind = DeterminePowerEventKind(providerName, eventId, description);
        DateTimeOffset detectedAt = DateTimeOffset.Now;
        string message = kind switch
        {
            PowerEventKind.Shutdown => "🛑 Windows shutdown detected from the local system.",
            PowerEventKind.Restart => "🔄 Windows restart detected from the local system.",
            PowerEventKind.Sleep => "🌙 Windows sleep detected from the local system.",
            _ => "⚠️ Unknown Windows power event detected from the local system."
        };

        if (!string.IsNullOrWhiteSpace(description))
        {
            message = $"{message} Event: {TruncateForTelegram(NormalizeWhitespace(description), 500)}";
        }

        return new PowerEventReport
        {
            Id = Guid.NewGuid().ToString("N"),
            Kind = kind,
            Source = PowerEventSource.WindowsEventLog,
            DetectedAt = detectedAt,
            Message = PowerEventService.AddPowerEventTimestamp(message, detectedAt),
            Delivered = false
        };
    }

    private static string SafeFormatEventDescription(EventRecord record)
    {
        try
        {
            return record.FormatDescription() ?? string.Empty;
        }
        catch (Exception ex) when (ex is EventLogException or InvalidOperationException)
        {
            return string.Empty;
        }
    }

    private static PowerEventKind DeterminePowerEventKind(
        string providerName,
        int eventId,
        string description)
    {
        if (providerName.Equals("Microsoft-Windows-Kernel-Power", StringComparison.OrdinalIgnoreCase) &&
            eventId == 42)
        {
            return PowerEventKind.Sleep;
        }

        if (providerName.Equals("User32", StringComparison.OrdinalIgnoreCase) && eventId == 1074)
        {
            if (description.Contains("restart", StringComparison.OrdinalIgnoreCase))
            {
                return PowerEventKind.Restart;
            }

            if (description.Contains("shutdown", StringComparison.OrdinalIgnoreCase) ||
                description.Contains("shut down", StringComparison.OrdinalIgnoreCase) ||
                description.Contains("power off", StringComparison.OrdinalIgnoreCase))
            {
                return PowerEventKind.Shutdown;
            }
        }

        return PowerEventKind.Unknown;
    }

    public string GetWakeWatcherStatus()
    {
        lock (_wakeEventWatcherSync)
        {
            return _wakeEventWatcher is null ? "Unavailable" : "Registered";
        }
    }

    public void Dispose()
    {
        _resumeNotificationLock.Dispose();
    }
}
