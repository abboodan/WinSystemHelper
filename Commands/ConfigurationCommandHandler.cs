using System.Security;
using System.Text.Json;
using Telegram.Bot;
using Telegram.Bot.Exceptions;
using static WinSystemHelper.CommandUtilities;

namespace WinSystemHelper;

internal sealed class ConfigurationCommandHandler(
    ILogger<Worker> logger,
    CommandReply reply,
    ITelegramBotClient botClient,
    RuntimeConfiguration runtime,
    DevelopmentMode developmentMode) : ICommandHandler
{
    public IReadOnlyList<string> Commands { get; } = ["/config"];

    public Task HandleAsync(CommandRequest request, CancellationToken cancellationToken)
    {
        return HandleConfigCommandAsync(request.Text, request.ChatId, cancellationToken);
    }

    private async Task HandleConfigCommandAsync(
        string commandText,
        long chatId,
        CancellationToken cancellationToken)
    {
        string[] parts = commandText.Split(' ', StringSplitOptions.RemoveEmptyEntries);
        if (parts.Length == 1)
        {
            await reply.SendTelegramMessageOnceAsync(BuildSafeConfigurationReport(), cancellationToken);
            return;
        }

        string section = parts[1].ToLowerInvariant();
        switch (section)
        {
            case "admins":
                await reply.SendTelegramMessageOnceAsync(BuildAdminsReport(), cancellationToken);
                break;
            case "admin":
                await HandleConfigAdminCommandAsync(parts, chatId, cancellationToken);
                break;
            case "alerts":
                await HandleConfigAlertsCommandAsync(parts, cancellationToken);
                break;
            case "export":
            case "raw":
                await reply.SendTelegramMessageOnceAsync(BuildSafeConfigurationJson(), cancellationToken);
                break;
            case "set":
                await HandleConfigSetCommandAsync(parts, cancellationToken);
                break;
            default:
                await reply.SendTelegramMessageOnceAsync(
                    "⚠️ Usage: /config | /config export | /config alerts on|off | /config admins | /config admin add|remove [ChatId] | /config set [Key] [Value]",
                    cancellationToken);
                break;
        }
    }

    private async Task HandleConfigAlertsCommandAsync(
        string[] parts,
        CancellationToken cancellationToken)
    {
        if (parts.Length < 3 || !RuntimeConfiguration.TryParseBoolean(parts[2], out bool enabled))
        {
            await reply.SendTelegramMessageOnceAsync("⚠️ Usage: /config alerts on|off", cancellationToken);
            return;
        }

        if (!await SaveConfigurationOrReplyAsync(() => runtime.SetAlertsEnabled(enabled), cancellationToken))
        {
            return;
        }

        await reply.SendTelegramMessageOnceAsync(
            enabled ? "✅ Smart alerts enabled." : "✅ Smart alerts disabled.",
            cancellationToken);
    }

    private async Task HandleConfigAdminCommandAsync(
        string[] parts,
        long requesterChatId,
        CancellationToken cancellationToken)
    {
        if (parts.Length < 4 ||
            !long.TryParse(parts[3], out long targetChatId) ||
            targetChatId == 0)
        {
            await reply.SendTelegramMessageOnceAsync(
                "⚠️ Usage: /config admin add|remove [ChatId]",
                cancellationToken);
            return;
        }

        string action = parts[2].ToLowerInvariant();
        switch (action)
        {
            case "add":
                try
                {
                    using CancellationTokenSource timeout =
                        CreateTimeoutTokenSource(cancellationToken, CommandReply.SendTimeout);
                    await botClient.GetChat(targetChatId, timeout.Token);
                }
                catch (Exception ex) when (ex is ApiRequestException or HttpRequestException or TaskCanceledException)
                {
                    await reply.SendTelegramMessageOnceAsync(
                        $"⚠️ Admin chat {targetChatId} could not be validated: {ex.Message}",
                        cancellationToken);
                    return;
                }

                if (!await SaveConfigurationOrReplyAsync(
                    () => runtime.UpdateAdminChatIds(current =>
                        current.Contains(targetChatId) ? current : [.. current, targetChatId]),
                    cancellationToken))
                {
                    return;
                }

                await reply.SendTelegramMessageOnceAsync($"✅ Admin chat {targetChatId} added.", cancellationToken);
                break;
            case "remove":
                if (targetChatId == requesterChatId)
                {
                    if (runtime.GetAdminChatIdsSnapshot().Length <= 1)
                    {
                        await reply.SendTelegramMessageOnceAsync(
                            "⚠️ This is the only configured admin. Add another admin first, then remove this one.",
                            cancellationToken);
                        return;
                    }
                }

                long[] currentAdmins = runtime.GetAdminChatIdsSnapshot();
                if (!currentAdmins.Contains(targetChatId))
                {
                    await reply.SendTelegramMessageOnceAsync($"⚠️ Admin chat {targetChatId} is not configured.", cancellationToken);
                    return;
                }

                if (currentAdmins.Length <= 1)
                {
                    await reply.SendTelegramMessageOnceAsync("⚠️ Refusing to remove the last configured admin.", cancellationToken);
                    return;
                }

                if (!await SaveConfigurationOrReplyAsync(
                    () => runtime.UpdateAdminChatIds(current => current.Where(id => id != targetChatId).ToArray()),
                    cancellationToken))
                {
                    return;
                }

                await reply.SendTelegramMessageOnceAsync($"✅ Admin chat {targetChatId} removed.", cancellationToken);
                break;
            default:
                await reply.SendTelegramMessageOnceAsync(
                    "⚠️ Usage: /config admin add|remove [ChatId]",
                    cancellationToken);
                break;
        }
    }

    private async Task HandleConfigSetCommandAsync(
        string[] parts,
        CancellationToken cancellationToken)
    {
        if (parts.Length < 4)
        {
            await reply.SendTelegramMessageOnceAsync("⚠️ Usage: /config set [Key] [Value]", cancellationToken);
            return;
        }

        string key = parts[2];
        string value = string.Join(' ', parts.Skip(3));

        bool updated = false;
        string? result = null;
        if (!await SaveConfigurationOrReplyAsync(
            () => updated = runtime.TrySetConfigurationValue(key, value, out result),
            cancellationToken))
        {
            return;
        }

        if (!updated)
        {
            await reply.SendTelegramMessageOnceAsync($"⚠️ {result}", cancellationToken);
            return;
        }

        await reply.SendTelegramMessageOnceAsync($"✅ {result}", cancellationToken);
    }

    private string BuildSafeConfigurationReport()
    {
        AppConfiguration configuration = runtime.GetConfigurationSnapshot();
        return string.Join(
            Environment.NewLine,
            "⚙️ WinSystemHelper configuration:",
            $"Admins: {runtime.GetAdminChatIdsSnapshot().Length}",
            $"Mode: {developmentMode.Name}",
            $"AlertsEnabled: {configuration.AlertsEnabled}",
            $"AllowDevelopmentMode: {configuration.AllowDevelopmentMode}",
            $"BatteryLowPercent: {configuration.BatteryLowPercent}",
            $"DiskLowPercent: {configuration.DiskLowPercent}",
            $"HealthCheckIntervalMinutes: {configuration.HealthCheckIntervalMinutes}",
            $"PublicIpCacheMinutes: {configuration.PublicIpCacheMinutes}",
            $"PublicIpFailureBackoffMinutes: {configuration.PublicIpFailureBackoffMinutes}",
            $"DangerousCommandConfirmationSeconds: {configuration.DangerousCommandConfirmationSeconds}",
            $"DangerousCommandCooldownSeconds: {configuration.DangerousCommandCooldownSeconds}",
            $"MicCooldownSeconds: {configuration.MicCooldownSeconds}",
            $"PersistentMicLoopEnabled: {configuration.PersistentMicLoopEnabled}",
            $"OutboxMaxSizeMB: {configuration.OutboxMaxSizeMB} (1 MB = 1048576 bytes)",
            $"AllowCrossAdminConfirmations: {configuration.AllowCrossAdminConfirmations}",
            $"PowerEventAlertsEnabled: {configuration.PowerEventAlertsEnabled}",
            $"PowerEventOutboxEnabled: {configuration.PowerEventOutboxEnabled}",
            $"PowerCommandPreDelaySeconds: {configuration.PowerCommandPreDelaySeconds}",
            $"GitHubOwner: {configuration.GitHubOwner}",
            $"GitHubRepo: {configuration.GitHubRepo}",
            $"GitHubReleaseAssetName: {configuration.GitHubReleaseAssetName}",
            "BotToken: configured (hidden)");
    }

    private string BuildSafeConfigurationJson()
    {
        AppConfiguration configuration = runtime.GetConfigurationSnapshot();
        var safeConfiguration = new
        {
            botToken = "configured (hidden)",
            adminChatIds = runtime.GetAdminChatIdsSnapshot(),
            configuration.AlertsEnabled,
            configuration.AllowDevelopmentMode,
            configuration.BatteryLowPercent,
            configuration.DiskLowPercent,
            configuration.HealthCheckIntervalMinutes,
            configuration.PublicIpCacheMinutes,
            configuration.PublicIpFailureBackoffMinutes,
            configuration.DangerousCommandConfirmationSeconds,
            configuration.DangerousCommandCooldownSeconds,
            configuration.MicCooldownSeconds,
            configuration.PersistentMicLoopEnabled,
            configuration.OutboxMaxSizeMB,
            configuration.AllowCrossAdminConfirmations,
            configuration.PowerEventAlertsEnabled,
            configuration.PowerEventOutboxEnabled,
            configuration.PowerCommandPreDelaySeconds,
            configuration.GitHubOwner,
            configuration.GitHubRepo,
            configuration.GitHubReleaseAssetName
        };

        return string.Join(
            Environment.NewLine,
            "⚙️ Safe config.json preview:",
            "```json",
            JsonSerializer.Serialize(safeConfiguration, ConfigurationJsonOptions),
            "```");
    }

    private string BuildAdminsReport()
    {
        string[] admins = runtime.GetAdminChatIdsSnapshot()
            .Select((admin, index) => $"{index + 1}. {admin}")
            .ToArray();

        return string.Join(
            Environment.NewLine,
            ["👥 Configured admins:", .. admins]);
    }

    private async Task<bool> SaveConfigurationOrReplyAsync(Action save, CancellationToken cancellationToken)
    {
        try
        {
            save();
            return true;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or SecurityException)
        {
            logger.LogWarning(ex, "Failed to save runtime configuration.");
            await reply.SendTelegramMessageOnceAsync(
                $"⚠️ Failed to save config.json: {ex.Message}",
                cancellationToken);
            return false;
        }
    }
}
