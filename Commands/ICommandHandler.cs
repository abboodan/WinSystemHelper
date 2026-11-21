using Telegram.Bot.Types;

namespace WinSystemHelper;

internal interface ICommandHandler
{
    IReadOnlyList<string> Commands { get; }

    Task HandleAsync(CommandRequest request, CancellationToken cancellationToken);
}

internal readonly record struct CommandRequest(string Word, string Text, int UpdateId, long ChatId, Message Message);
