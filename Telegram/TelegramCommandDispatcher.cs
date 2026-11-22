using Telegram.Bot.Types;
using Telegram.Bot.Types.Enums;

namespace WinSystemHelper;

internal sealed class TelegramCommandDispatcher(
    CommandRegistry commands,
    MenuCommandHandler menu,
    RuntimeConfiguration runtime,
    CommandReply reply)
{
    public async Task HandleUpdateAsync(Update update, CancellationToken cancellationToken)
    {
        if (update.CallbackQuery is { } callback)
        {
            await menu.HandleMenuCallbackAsync(commands, callback, update.Id, cancellationToken);
            return;
        }

        if (update.Type != UpdateType.Message || update.Message is not { } message)
        {
            return;
        }

        // Security boundary: do not inspect or respond to commands from any non-admin chat.
        long chatId = message.Chat.Id;
        if (!runtime.IsAuthorizedMessage(message))
        {
            return;
        }

        string? text = (message.Text ?? message.Caption)?.Trim();
        if (string.IsNullOrWhiteSpace(text))
        {
            return;
        }

        string command = text.Split([' ', '\r', '\n', '\t'], StringSplitOptions.RemoveEmptyEntries)[0].ToLowerInvariant();

        long? previousReplyChatId = reply.CurrentChatId;
        reply.CurrentChatId = chatId;

        try
        {
            if (commands.TryGet(command, out ICommandHandler? handler))
            {
                await handler.HandleAsync(new CommandRequest(command, text, update.Id, chatId, message), cancellationToken);
            }
        }
        finally
        {
            reply.CurrentChatId = previousReplyChatId;
        }
    }
}
