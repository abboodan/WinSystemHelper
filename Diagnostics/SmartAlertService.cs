namespace WinSystemHelper;

internal sealed class SmartAlertService(
    ILogger<Worker> logger,
    CommandReply reply,
    RuntimeConfiguration runtime,
    TelegramConnection connection,
    ServiceTelemetry telemetry,
    PublicIpCache publicIp,
    MicrophoneCommandHandler microphone)
{
    private readonly object _alertStateSync = new();

    private string? _lastPublicIpAlerted;

    private bool _batteryLowAlertActive;

    private bool _diskLowAlertActive;

    private bool _repeatedTelegramFailureAlertActive;

    public async Task SendStartupAlertAsync(CancellationToken cancellationToken)
    {
        logger.LogInformation("Startup detected. Saving notification to the durable queue.");

        string machineName = Environment.MachineName;
        string userName = Environment.UserName;
        string timestamp = DateTimeOffset.Now.ToString("yyyy-MM-dd HH:mm:ss zzz");

        await connection.SendTelegramBroadcastWithRetryAsync(
            $"🚀 Startup/Boot Alert: {machineName} booted up at {timestamp}. User: {userName}.",
            cancellationToken);

        telemetry.RecordStartupAlert();

        logger.LogInformation("Startup alert queued.");
    }

    public async Task RunSmartAlertLoopAsync(CancellationToken cancellationToken)
    {
        while (!cancellationToken.IsCancellationRequested)
        {
            try
            {
                await RunSmartAlertsOnceAsync(cancellationToken);

                telemetry.RecordSmartAlert();
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
                break;
            }
            catch (Exception ex)
            {
                telemetry.RecordError($"Smart alerts: {ex.GetType().Name}: {ex.Message}");
                logger.LogWarning(ex, "Smart alert loop iteration failed.");
            }

            try
            {
                await Task.Delay(GetSmartAlertInterval(), cancellationToken);
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
                break;
            }
        }
    }

    private async Task RunSmartAlertsOnceAsync(CancellationToken cancellationToken)
    {
        if (!runtime.Current.AlertsEnabled)
        {
            return;
        }

        await CheckBatteryAlertAsync(cancellationToken);
        await CheckDiskAlertAsync(cancellationToken);
        await CheckPublicIpChangeAlertAsync(cancellationToken);
        await microphone.CheckMicLoopAlertAsync(cancellationToken);
        await CheckRepeatedTelegramFailureAlertAsync(cancellationToken);
    }

    private async Task CheckBatteryAlertAsync(CancellationToken cancellationToken)
    {
        if (!BatteryStatus.TryGetBatteryMetric(out int percent, out string status))
        {
            return;
        }

        int threshold = Math.Clamp(runtime.Current.BatteryLowPercent, 1, 100);
        bool low = percent <= threshold && status.Equals("Discharging", StringComparison.OrdinalIgnoreCase);
        bool shouldSend;

        lock (_alertStateSync)
        {
            shouldSend = low && !_batteryLowAlertActive;
            if (low)
            {
                _batteryLowAlertActive = true;
            }
            else if (percent >= Math.Min(100, threshold + 5) || !status.Equals("Discharging", StringComparison.OrdinalIgnoreCase))
            {
                _batteryLowAlertActive = false;
            }
        }

        if (shouldSend)
        {
            await reply.SendTelegramBroadcastOnceAsync(
                $"🔋 Battery low: {percent}% | Status: {status}",
                cancellationToken);
        }
    }

    private async Task CheckDiskAlertAsync(CancellationToken cancellationToken)
    {
        int threshold = Math.Clamp(runtime.Current.DiskLowPercent, 1, 100);
        DriveInfo? lowestDrive = null;
        double lowestFreePercent = 100;

        foreach (DriveInfo drive in DriveInfo.GetDrives())
        {
            try
            {
                if (!drive.IsReady || drive.DriveType != DriveType.Fixed || drive.TotalSize <= 0)
                {
                    continue;
                }

                double freePercent = drive.AvailableFreeSpace * 100d / drive.TotalSize;
                if (freePercent < lowestFreePercent)
                {
                    lowestFreePercent = freePercent;
                    lowestDrive = drive;
                }
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                logger.LogDebug(ex, "Failed to inspect disk {DriveName}.", drive.Name);
            }
        }

        if (lowestDrive is null)
        {
            return;
        }

        bool low = lowestFreePercent <= threshold;
        bool shouldSend;

        lock (_alertStateSync)
        {
            shouldSend = low && !_diskLowAlertActive;
            if (low)
            {
                _diskLowAlertActive = true;
            }
            else if (lowestFreePercent >= Math.Min(100, threshold + 5))
            {
                _diskLowAlertActive = false;
            }
        }

        if (shouldSend)
        {
            await reply.SendTelegramBroadcastOnceAsync(
                $"💽 Disk space low: {lowestDrive.Name} has {lowestFreePercent:N1}% free.",
                cancellationToken);
        }
    }

    private async Task CheckPublicIpChangeAlertAsync(CancellationToken cancellationToken)
    {
        PublicIpLookupResult result = await publicIp.GetPublicIpAsync(forceRefresh: false, cancellationToken);
        if (!result.Success || string.IsNullOrWhiteSpace(result.PublicIp))
        {
            return;
        }

        string? previousIp;
        bool changed;

        lock (_alertStateSync)
        {
            previousIp = _lastPublicIpAlerted;
            changed = !string.IsNullOrWhiteSpace(previousIp) &&
                !previousIp.Equals(result.PublicIp, StringComparison.OrdinalIgnoreCase);
            _lastPublicIpAlerted = result.PublicIp;
        }

        if (changed)
        {
            await reply.SendTelegramBroadcastOnceAsync(
                $"🌐 Public IP changed: {previousIp} -> {result.PublicIp}",
                cancellationToken);
        }
    }

    private async Task CheckRepeatedTelegramFailureAlertAsync(CancellationToken cancellationToken)
    {
        TelemetrySnapshot snapshot = telemetry.GetTelemetrySnapshot();
        if (snapshot.PollingFailureStreak < 5)
        {
            return;
        }

        bool shouldSend;
        lock (_alertStateSync)
        {
            shouldSend = !_repeatedTelegramFailureAlertActive;
            _repeatedTelegramFailureAlertActive = true;
        }

        if (shouldSend)
        {
            await reply.SendTelegramBroadcastOnceAsync(
                $"⚠️ Telegram polling has failed {snapshot.PollingFailureStreak} time(s). Last error: {snapshot.LastError}",
                cancellationToken);
        }
    }

    private TimeSpan GetSmartAlertInterval()
    {
        return TimeSpan.FromMinutes(Math.Clamp(runtime.Current.HealthCheckIntervalMinutes, 1, 1440));
    }

    public void ResetPollingFailureAlert()
    {
        lock (_alertStateSync)
        {
            _repeatedTelegramFailureAlertActive = false;
        }
    }
}
