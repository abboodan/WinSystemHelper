using System.Text.Json.Serialization;

namespace WinSystemHelper;

public sealed class AppConfiguration
{
    public string BotToken { get; init; } = string.Empty;

    public long[] AdminChatIds { get; init; } = [];

    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingDefault)]
    public long AdminChatId { get; init; }

    public IReadOnlyList<long> GetEffectiveAdminChatIds()
    {
        if (AdminChatIds.Length > 0)
        {
            return AdminChatIds;
        }

        return AdminChatId == 0 ? [] : [AdminChatId];
    }
}
