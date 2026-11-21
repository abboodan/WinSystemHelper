using System.Security;
using System.Text;
using System.Text.Json;
using static WinSystemHelper.CommandUtilities;
using static WinSystemHelper.WindowsProcess;

namespace WinSystemHelper;

internal sealed class PowerEventService(
    ILogger<Worker> logger,
    RuntimeConfiguration runtime,
    ServiceFiles files,
    QueueStore queueStore)
{
    private readonly object _powerEventSync = new();

    private PowerEventKind _lastBotPowerCommandKind = PowerEventKind.Unknown;

    private DateTimeOffset _lastBotPowerCommandAt = DateTimeOffset.MinValue;

    public async Task HandleDetectedPowerEventAsync(
        PowerEventReport report,
        CancellationToken cancellationToken)
    {
        string? outboxPath = await SavePowerEventOutboxAsync(report, delivered: false, cancellationToken);

        if (!GetPowerEventAlertsEnabled())
        {
            return;
        }

        try
        {
            if (await QueuePowerReportAsync(report, cancellationToken))
            {
                await MarkPowerEventDeliveredAsync(outboxPath, report, cancellationToken);
            }
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception ex)
        {
            logger.LogWarning(ex, "Power event alert could not be delivered before the power transition.");
        }
    }

    public async Task SendPendingPowerEventReportsAsync(CancellationToken cancellationToken)
    {
        if (!GetPowerEventAlertsEnabled() || !GetPowerEventOutboxEnabled())
        {
            return;
        }

        string directory = files.GetSharedTempDirectory("PowerEvents");
        if (!Directory.Exists(directory))
        {
            return;
        }

        foreach (string file in Directory.EnumerateFiles(directory, "*.json").OrderBy(File.GetCreationTimeUtc))
        {
            cancellationToken.ThrowIfCancellationRequested();

            try
            {
                string json = await File.ReadAllTextAsync(file, Encoding.UTF8, cancellationToken);
                PowerEventReport? report = JsonSerializer.Deserialize<PowerEventReport>(json, ConfigurationJsonOptions);
                if (report is null)
                {
                    files.DeleteTempFile(file);
                    continue;
                }

                if (report.Delivered)
                {
                    files.DeleteTempFile(file);
                    continue;
                }

                await QueuePowerReportAsync(report, cancellationToken);
                files.DeleteTempFile(file);
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
                throw;
            }
            catch (Exception ex)
            {
                logger.LogWarning(ex, "Failed to process pending power event report {PowerEventFile}.", file);
            }
        }

    }

    public static PowerEventReport CreatePowerEventReport(
        PowerEventKind kind,
        PowerEventSource source,
        string message)
    {
        DateTimeOffset detectedAt = DateTimeOffset.Now;

        return new PowerEventReport
        {
            Id = Guid.NewGuid().ToString("N"),
            Kind = kind,
            Source = source,
            DetectedAt = detectedAt,
            Message = AddPowerEventTimestamp(message, detectedAt),
            Delivered = false
        };
    }

    public static string AddPowerEventTimestamp(string message, DateTimeOffset timestamp)
    {
        return string.Join(
            Environment.NewLine,
            message,
            $"🕒 Timestamp: {FormatTelegramTimestamp(timestamp)}");
    }

    private static string EnsurePowerEventMessageHasTimestamp(string message, DateTimeOffset timestamp)
    {
        return message.Contains("Timestamp:", StringComparison.OrdinalIgnoreCase)
            ? message
            : AddPowerEventTimestamp(message, timestamp);
    }

    private static string FormatTelegramTimestamp(DateTimeOffset timestamp)
    {
        return timestamp.ToString("yyyy-MM-dd HH:mm:ss zzz");
    }

    private async Task<string?> SavePowerEventOutboxAsync(
        PowerEventReport report,
        bool delivered,
        CancellationToken cancellationToken)
    {
        if (!GetPowerEventOutboxEnabled())
        {
            return null;
        }

        try
        {
            string directory = files.GetSharedTempDirectory("PowerEvents");
            Directory.CreateDirectory(directory);

            report.Delivered = delivered;
            string filePath = Path.Combine(
                directory,
                $"WinSystemHelper-power-{report.DetectedAt:yyyyMMddHHmmssfff}-{report.Id}.json");

            string json = JsonSerializer.Serialize(report, ConfigurationJsonOptions);
            await File.WriteAllTextAsync(filePath, json, new UTF8Encoding(encoderShouldEmitUTF8Identifier: false), cancellationToken);
            return filePath;
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or SecurityException)
        {
            logger.LogWarning(ex, "Failed to write power event outbox item.");
            return null;
        }
    }

    private async Task MarkPowerEventDeliveredAsync(
        string? outboxPath,
        PowerEventReport report,
        CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(outboxPath) || !File.Exists(outboxPath))
        {
            return;
        }

        try
        {
            report.Delivered = true;
            string json = JsonSerializer.Serialize(report, ConfigurationJsonOptions);
            await File.WriteAllTextAsync(outboxPath, json, new UTF8Encoding(encoderShouldEmitUTF8Identifier: false), cancellationToken);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or SecurityException)
        {
            logger.LogWarning(ex, "Failed to mark power event outbox item as delivered.");
        }
    }

    private void MarkRecentBotPowerCommand(PowerEventKind kind)
    {
        lock (_powerEventSync)
        {
            _lastBotPowerCommandKind = kind;
            _lastBotPowerCommandAt = DateTimeOffset.Now;
        }
    }

    public bool ShouldSuppressDuplicateBotPowerEvent(PowerEventKind kind)
    {
        if (kind == PowerEventKind.Unknown)
        {
            return false;
        }

        lock (_powerEventSync)
        {
            return _lastBotPowerCommandKind == kind &&
                DateTimeOffset.Now - _lastBotPowerCommandAt <= TimeSpan.FromSeconds(30);
        }
    }

    public async Task ExecutePowerCommandAsync(
        PowerEventKind kind,
        string message,
        string fileName,
        string arguments,
        CancellationToken cancellationToken)
    {
        PowerEventReport report = CreatePowerEventReport(kind, PowerEventSource.BotCommand, message);
        string? outboxPath = await SavePowerEventOutboxAsync(report, delivered: false, cancellationToken);

        MarkRecentBotPowerCommand(kind);

        if (GetPowerEventAlertsEnabled())
        {
            try
            {
                if (await QueuePowerReportAsync(report, cancellationToken))
                {
                    await MarkPowerEventDeliveredAsync(outboxPath, report, cancellationToken);
                }
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
                throw;
            }
            catch (Exception ex)
            {
                logger.LogWarning(ex, "Power command alert failed before executing {PowerEventKind}.", kind);
            }
        }

        TimeSpan preDelay = GetPowerCommandPreDelay();
        if (preDelay > TimeSpan.Zero)
        {
            await Task.Delay(preDelay, cancellationToken);
        }

        StartDetachedProcess(fileName, arguments);
    }

    private bool GetPowerEventAlertsEnabled()
    {
        return runtime.Current.PowerEventAlertsEnabled;
    }

    private bool GetPowerEventOutboxEnabled()
    {
        return runtime.Current.PowerEventOutboxEnabled;
    }

    private TimeSpan GetPowerCommandPreDelay()
    {
        return TimeSpan.FromSeconds(Math.Clamp(runtime.Current.PowerCommandPreDelaySeconds, 0, 30));
    }

    private async Task<bool> QueuePowerReportAsync(PowerEventReport report, CancellationToken token)
    {
        token.ThrowIfCancellationRequested();
        foreach (long chat in runtime.GetAdminChatIdsSnapshot())
            await queueStore.EnqueueTextAsync(chat, report.Message, dedupe: $"power:{report.Id}:{chat}");
        return true;
    }
}
