namespace WinSystemHelper;

internal sealed class ModeCommandHandler(
    CommandReply reply,
    RuntimeConfiguration runtime,
    DevelopmentMode developmentMode) : ICommandHandler
{
    public IReadOnlyList<string> Commands { get; } = ["/mode"];

    public Task HandleAsync(CommandRequest request, CancellationToken cancellationToken)
    {
        return HandleModeCommandAsync(request.Text, request.UpdateId, request.ChatId, request.Message.From?.Id ?? 0, cancellationToken);
    }

    private async Task HandleModeCommandAsync(string text, int updateId, long chatId, long senderId, CancellationToken token)
    {
        string[] parts = text.Split(' ', StringSplitOptions.RemoveEmptyEntries);
        if (parts.Length == 1)
        {
            await reply.SendTelegramMessageOnceAsync($"Mode: {developmentMode.Name}", token);
            return;
        }

        if (parts.Length == 2 && parts[1].Equals("production", StringComparison.OrdinalIgnoreCase))
        {
            developmentMode.Disable(updateId);
            await reply.SendTelegramMessageOnceAsync("Production mode active. Development permission has ended.", token);
            return;
        }

        if (parts.Length != 2 || !parts[1].Equals("development", StringComparison.OrdinalIgnoreCase))
        {
            await reply.SendTelegramMessageOnceAsync("Usage: /mode [production|development]", token);
            return;
        }

        if (!runtime.Current.AllowDevelopmentMode)
        {
            await reply.SendTelegramMessageOnceAsync("Development mode is disabled in the local configuration.", token);
            return;
        }

        if (developmentMode.Name == "development")
        {
            await reply.SendTelegramMessageOnceAsync("Development mode is already active for the approved Telegram administrator and chat.", token);
            return;
        }

        await reply.SendTelegramMessageOnceAsync("Waiting for local approval to enable development mode.", token);
        await CommandBackgroundWork.TrackStartedWork(CompleteDevelopmentRequestAsync(updateId, chatId, senderId, token));
    }

    private async Task CompleteDevelopmentRequestAsync(int updateId, long chatId, long senderId, CancellationToken token)
    {
        bool approved = await developmentMode.EnableAsync(updateId, chatId, senderId, token);
        await reply.SendTelegramMessageOnceAsync(approved
            ? "Development mode active for this administrator and chat. Use /mode production to end it."
            : "Development mode was not approved or the request ended. Production mode is active.", token);
    }
}
