namespace WinSystemHelper;

internal sealed class QueueCommandHandler(
    QueueStore queueStore,
    CommandReply reply,
    TelegramNotifier notifier,
    ConfirmationService confirmations) : ICommandHandler
{
    public IReadOnlyList<string> Commands { get; } = ["/queue"];

    public Task HandleAsync(CommandRequest request, CancellationToken cancellationToken)
    {
        return HandleQueueCommandAsync(request.Text, cancellationToken);
    }

    private async Task HandleQueueCommandAsync(string text, CancellationToken token)
    {
        string[] parts = text.Split(' ', StringSplitOptions.RemoveEmptyEntries);
        long chat = reply.ChatId;
        if (parts.Length == 1)
        {
            TelegramMenuPage page = await BuildQueuePageAsync(chat);
            await notifier.SendHtmlAsync(chat, page.Text, page.Keyboard, token);
            return;
        }
        if (parts.Length != 3 || !Guid.TryParseExact(parts[2], "N", out _) || parts[1] is not ("retry" or "delete"))
        { await reply.SendTelegramMessageOnceAsync("ℹ️ Queue commands\n/queue — Show the queue\n/queue retry [Id] — Retry delivery\n/queue delete [Id] — Request deletion", token); return; }
        string id = parts[2];
        if (parts[1] == "delete")
        {
            await confirmations.RequestDangerousConfirmationAsync($"queue-delete:{id}", $"Delete queued item {id}", async ct =>
            {
                bool removed = await queueStore.ManageAsync(id, chat, true);
                await reply.SendTelegramMessageOnceAsync(removed ? "✅ Queue item deleted." : "⚠️ This item is unavailable or currently being sent.", ct);
            }, token);
        }
        else
        {
            bool changed = await queueStore.ManageAsync(id, chat, false);
            await reply.SendTelegramMessageOnceAsync(changed ? "✅ Item queued for another delivery attempt." : "⚠️ Retry unavailable. The item is missing or its capture was incomplete.", token);
        }
    }

    public async Task<TelegramMenuPage> BuildQueuePageAsync(long chatId)
    {
        QueueSnapshot status = await queueStore.SnapshotAsync(chatId);
        return new TelegramMenuPage(TelegramReplies.Queue(status), TelegramCommandMenu.QueueKeyboard());
    }
}
