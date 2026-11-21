using Telegram.Bot.Types.ReplyMarkups;

namespace WinSystemHelper;

/// <summary>Success means durably queued, not delivered to Telegram.</summary>
public sealed class TelegramNotifier(QueueStore store)
{
    public Task SendHtmlAsync(long chatId, string html, InlineKeyboardMarkup? keyboard, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        CommandExecution? execution = CommandExecution.Current.Value;
        return store.EnqueueTextAsync(chatId, html, TelegramReplyStyle.Html(keyboard),
            dedupe: execution?.NextKey(), expires: execution?.ReplyExpires, priority: execution?.Priority ?? false);
    }

    public async Task SendMessageAsync(long chatId, string text, TimeSpan timeout, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        CommandExecution? execution = CommandExecution.Current.Value;
        string key = execution?.NextKey() ?? Guid.NewGuid().ToString("N");
        for (int offset = 0, part = 0; offset < text.Length; part++)
        {
            int length = Math.Min(4000, text.Length - offset);
            if (char.IsHighSurrogate(text[offset + length - 1])) length--;
            await store.EnqueueTextAsync(chatId, text.Substring(offset, length),
                dedupe: $"{key}:{chatId}:{part}", expires: execution?.ReplyExpires, priority: execution?.Priority ?? false);
            offset += length;
        }
    }

    public async Task<bool> BroadcastAsync(IReadOnlyCollection<long> adminChatIds, string text,
        TimeSpan timeout, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        CommandExecution? execution = CommandExecution.Current.Value;
        await store.EnqueueBroadcastAsync(adminChatIds, text, execution?.NextKey() ?? Guid.NewGuid().ToString("N"),
            execution?.ReplyExpires, execution?.Priority ?? false);
        return adminChatIds.Count > 0;
    }
}
