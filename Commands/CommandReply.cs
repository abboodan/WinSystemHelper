namespace WinSystemHelper;

internal sealed class CommandReply(TelegramNotifier notifier, RuntimeConfiguration runtime)
{
    internal static readonly TimeSpan SendTimeout = TimeSpan.FromSeconds(15);
    private readonly AsyncLocal<long?> _chatId = new();

    public long? CurrentChatId
    {
        get => _chatId.Value;
        set => _chatId.Value = value;
    }

    public long ChatId => CurrentChatId ?? runtime.GetAdminChatIdsSnapshot().First();

    public Task SendTelegramMessageOnceAsync(string text, CancellationToken cancellationToken)
    {
        return notifier.SendMessageAsync(ChatId, text, SendTimeout, cancellationToken);
    }

    public async Task<bool> SendTelegramBroadcastOnceAsync(
        string text,
        CancellationToken cancellationToken)
    {
        return await notifier.BroadcastAsync(
            runtime.GetAdminChatIdsSnapshot(),
            text,
            SendTimeout,
            cancellationToken);
    }
}
