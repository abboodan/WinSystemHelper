using Telegram.Bot.Types;
using static WinSystemHelper.CommandUtilities;

namespace WinSystemHelper;

internal sealed class UpdateCommandHandler(
    CommandReply reply,
    ConfirmationService confirmations,
    OtaUpdateService update) : ICommandHandler
{
    public IReadOnlyList<string> Commands { get; } = ["/update"];

    public Task HandleAsync(CommandRequest request, CancellationToken cancellationToken)
    {
        return HandleUpdateCommandRequestAsync(request.Message, request.Text, cancellationToken);
    }

    private async Task HandleUpdateCommandRequestAsync(
        Message message,
        string commandText,
        CancellationToken cancellationToken)
    {
        if (message.Document is null && string.IsNullOrWhiteSpace(GetCommandPayload(commandText)))
        {
            await reply.SendTelegramMessageOnceAsync(
                "⚠️ Usage: send /update github, /update https://example.com/update.zip, or attach a .zip file with caption /update.",
                cancellationToken);
            return;
        }

        string? preflightError = await update.ValidateOtaPreflightAsync(cancellationToken);
        if (!string.IsNullOrWhiteSpace(preflightError))
        {
            await reply.SendTelegramMessageOnceAsync(preflightError, cancellationToken);
            return;
        }

        await confirmations.RequestDangerousConfirmationAsync(
            "update",
            "Apply an OTA update and restart the service",
            token => update.ApplyAsync(message, commandText, token),
            cancellationToken);
    }
}
