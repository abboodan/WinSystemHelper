using Telegram.Bot;

namespace WinSystemHelper;

public sealed class TelegramNotifier
{
    private readonly ITelegramBotClient _botClient;
    private readonly ILogger<TelegramNotifier> _logger;

    public TelegramNotifier(
        ITelegramBotClient botClient,
        ILogger<TelegramNotifier> logger)
    {
        _botClient = botClient;
        _logger = logger;
    }

    public async Task SendMessageAsync(
        long chatId,
        string text,
        TimeSpan timeout,
        CancellationToken cancellationToken)
    {
        using CancellationTokenSource timeoutToken =
            CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeoutToken.CancelAfter(timeout);

        await _botClient.SendMessage(
            chatId: chatId,
            text: text,
            cancellationToken: timeoutToken.Token);
    }

    public async Task<bool> BroadcastAsync(
        IReadOnlyCollection<long> adminChatIds,
        string text,
        TimeSpan timeout,
        CancellationToken cancellationToken)
    {
        var anyDelivered = false;

        foreach (long adminChatId in adminChatIds)
        {
            try
            {
                await SendMessageAsync(adminChatId, text, timeout, cancellationToken);
                anyDelivered = true;
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
                throw;
            }
            catch (Exception ex)
            {
                _logger.LogWarning(
                    ex,
                    "Telegram broadcast delivery failed for admin chat {AdminChatId}.",
                    adminChatId);
            }
        }

        return anyDelivered;
    }
}
