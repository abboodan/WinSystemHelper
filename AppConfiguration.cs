using System.Text.Json.Serialization;

namespace WinSystemHelper;

public sealed class AppConfiguration
{
    public string BotToken { get; set; } = string.Empty;

    public long[] AdminChatIds { get; set; } = [];

    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingDefault)]
    public long AdminChatId { get; set; }

    public bool AlertsEnabled { get; set; } = true;

    public int BatteryLowPercent { get; set; } = 20;

    public int DiskLowPercent { get; set; } = 10;

    public int HealthCheckIntervalMinutes { get; set; } = 5;

    public int PublicIpCacheMinutes { get; set; } = 10;

    public int PublicIpFailureBackoffMinutes { get; set; } = 5;

    public int DangerousCommandConfirmationSeconds { get; set; } = 60;

    public int DangerousCommandCooldownSeconds { get; set; } = 60;

    public int MicCooldownSeconds { get; set; } = 30;

    public bool PersistentMicLoopEnabled { get; set; } = true;

    public bool AllowCrossAdminConfirmations { get; set; }

    public bool PowerEventAlertsEnabled { get; set; } = true;

    public bool PowerEventOutboxEnabled { get; set; } = true;

    public int PowerCommandPreDelaySeconds { get; set; } = 3;

    public string GitHubOwner { get; set; } = "abboodan";

    public string GitHubRepo { get; set; } = "WinSystemHelper";

    public string GitHubReleaseAssetName { get; set; } = "WinSystemHelper-ota.zip";

    public IReadOnlyList<long> GetEffectiveAdminChatIds()
    {
        if (AdminChatIds.Length > 0)
        {
            return AdminChatIds;
        }

        return AdminChatId == 0 ? [] : [AdminChatId];
    }
}
