using System.Diagnostics.CodeAnalysis;
using Telegram.Bot.Types;

namespace WinSystemHelper;

internal sealed class RuntimeConfiguration(
    AppConfiguration configuration,
    ConfigurationFileService configurationFiles,
    QueueStore queueStore)
{
    private readonly object _configurationSync = new();

    private long[] _adminChatIds = NormalizeAdminChatIds(configuration.GetEffectiveAdminChatIds());

    public AppConfiguration Current => configuration;

    public void SetAlertsEnabled(bool enabled)
    {
        lock (_configurationSync)
        {
            SaveAndApply(config => config.AlertsEnabled = enabled);
        }
    }

    private static long[] NormalizeAdminChatIds(IEnumerable<long> adminChatIds)
    {
        return adminChatIds
            .Where(chatId => chatId != 0)
            .Distinct()
            .ToArray();
    }

    public long[] GetAdminChatIdsSnapshot()
    {
        return Volatile.Read(ref _adminChatIds);
    }

    public bool IsAdminChatId(long chatId)
    {
        return GetAdminChatIdsSnapshot().Contains(chatId);
    }

    public bool IsAuthorizedMessage(Message message)
    {
        return CommandPolicy.IsAuthorizedSender(message.Chat.Id, message.From?.Id, GetAdminChatIdsSnapshot());
    }

    public void UpdateAdminChatIds(Func<long[], long[]> update)
    {
        lock (_configurationSync)
        {
            long[] updated = NormalizeAdminChatIds(update(GetAdminChatIdsSnapshot()));
            SaveAndApply(config =>
            {
                config.AdminChatIds = updated;
                config.AdminChatId = 0;
            });
            Volatile.Write(ref _adminChatIds, updated);
        }
    }

    public AppConfiguration GetConfigurationSnapshot()
    {
        lock (_configurationSync)
        {
            AppConfiguration snapshot = configuration.Clone();
            snapshot.AdminChatIds = GetAdminChatIdsSnapshot();
            return snapshot;
        }
    }

    private void SaveAndApply(Action<AppConfiguration> apply)
    {
        AppConfiguration staged = configuration.Clone();
        apply(staged);
        configurationFiles.SaveAtomic(staged);
        apply(configuration);
    }

    public bool TrySetConfigurationValue(string key, string value, out string? result)
    {
        lock (_configurationSync)
        {
            if (Settings.Setters.TryGetValue(key.ToLowerInvariant(), out SetValue? set))
            {
                if (!set(value, out Action<AppConfiguration>? apply, out result))
                {
                    return false;
                }

                SaveAndApply(apply);
                if (key.Equals(nameof(AppConfiguration.OutboxMaxSizeMB), StringComparison.OrdinalIgnoreCase))
                {
                    queueStore.ConfigurationChanged();
                }

                return true;
            }

            result = $"Unknown configuration key: {key}.";
            return false;
        }
    }

    private delegate bool SetValue(string value, [NotNullWhen(true)] out Action<AppConfiguration>? apply, out string? result);

    // Initialized on the first /config set request.
    private static class Settings
    {
        internal static readonly Dictionary<string, SetValue> Setters = new(
        [
            Boolean(nameof(AppConfiguration.AlertsEnabled), static (config, value) => config.AlertsEnabled = value),
            Boolean(nameof(AppConfiguration.AllowCrossAdminConfirmations), static (config, value) => config.AllowCrossAdminConfirmations = value),
            Boolean(nameof(AppConfiguration.PowerEventAlertsEnabled), static (config, value) => config.PowerEventAlertsEnabled = value),
            Boolean(nameof(AppConfiguration.PowerEventOutboxEnabled), static (config, value) => config.PowerEventOutboxEnabled = value),
            Boolean(nameof(AppConfiguration.PersistentMicLoopEnabled), static (config, value) => config.PersistentMicLoopEnabled = value),
            Number(nameof(AppConfiguration.OutboxMaxSizeMB), 1, 65536, static (config, value) => config.OutboxMaxSizeMB = value),
            Number(nameof(AppConfiguration.BatteryLowPercent), 1, 100, static (config, value) => config.BatteryLowPercent = value),
            Number(nameof(AppConfiguration.DiskLowPercent), 1, 100, static (config, value) => config.DiskLowPercent = value),
            Number(nameof(AppConfiguration.HealthCheckIntervalMinutes), 1, 1440, static (config, value) => config.HealthCheckIntervalMinutes = value),
            Number(nameof(AppConfiguration.PublicIpCacheMinutes), 1, 1440, static (config, value) => config.PublicIpCacheMinutes = value),
            Number(nameof(AppConfiguration.PublicIpFailureBackoffMinutes), 1, 1440, static (config, value) => config.PublicIpFailureBackoffMinutes = value),
            Number(nameof(AppConfiguration.DangerousCommandConfirmationSeconds), 10, 300, static (config, value) => config.DangerousCommandConfirmationSeconds = value),
            Number(nameof(AppConfiguration.DangerousCommandCooldownSeconds), 0, 3600, static (config, value) => config.DangerousCommandCooldownSeconds = value),
            Number(nameof(AppConfiguration.MicCooldownSeconds), 0, 3600, static (config, value) => config.MicCooldownSeconds = value),
            Number(nameof(AppConfiguration.PowerCommandPreDelaySeconds), 0, 30, static (config, value) => config.PowerCommandPreDelaySeconds = value),
            Text(nameof(AppConfiguration.GitHubOwner), static (config, value) => config.GitHubOwner = value),
            Text(nameof(AppConfiguration.GitHubRepo), static (config, value) => config.GitHubRepo = value),
            Text(nameof(AppConfiguration.GitHubReleaseAssetName), static (config, value) => config.GitHubReleaseAssetName = value)
        ], StringComparer.Ordinal);

        private static KeyValuePair<string, SetValue> Boolean(string name, Action<AppConfiguration, bool> set)
        {
            return new(name.ToLowerInvariant(), (string value, [NotNullWhen(true)] out Action<AppConfiguration>? apply, out string? result) =>
            {
                if (!TryParseBoolean(value, out bool enabled))
                {
                    apply = null;
                    result = $"{name} must be true or false.";
                    return false;
                }

                apply = config => set(config, enabled);
                result = $"{name} set to {enabled}.";
                return true;
            });
        }

        private static KeyValuePair<string, SetValue> Number(
            string name, int min, int max, Action<AppConfiguration, int> set)
        {
            return new(name.ToLowerInvariant(), (string value, [NotNullWhen(true)] out Action<AppConfiguration>? apply, out string? result) =>
            {
                if (!int.TryParse(value, out int number) || number < min || number > max)
                {
                    apply = null;
                    result = $"{name} must be a number between {min} and {max}.";
                    return false;
                }

                apply = config => set(config, number);
                result = $"{name} set to {number}.";
                return true;
            });
        }

        private static KeyValuePair<string, SetValue> Text(string name, Action<AppConfiguration, string> set)
        {
            return new(name.ToLowerInvariant(), (string value, [NotNullWhen(true)] out Action<AppConfiguration>? apply, out string? result) =>
            {
                string text = value.Trim();
                if (string.IsNullOrWhiteSpace(text))
                {
                    apply = null;
                    result = $"{name} cannot be empty.";
                    return false;
                }

                apply = config => set(config, text);
                result = $"{name} set to {text}.";
                return true;
            });
        }
    }

    public static bool TryParseBoolean(string value, out bool result)
    {
        if (bool.TryParse(value, out result))
        {
            return true;
        }

        if (value.Equals("1", StringComparison.OrdinalIgnoreCase) ||
            value.Equals("yes", StringComparison.OrdinalIgnoreCase) ||
            value.Equals("y", StringComparison.OrdinalIgnoreCase) ||
            value.Equals("on", StringComparison.OrdinalIgnoreCase))
        {
            result = true;
            return true;
        }

        if (value.Equals("0", StringComparison.OrdinalIgnoreCase) ||
            value.Equals("no", StringComparison.OrdinalIgnoreCase) ||
            value.Equals("n", StringComparison.OrdinalIgnoreCase) ||
            value.Equals("off", StringComparison.OrdinalIgnoreCase))
        {
            result = false;
            return true;
        }

        result = false;
        return false;
    }


}
