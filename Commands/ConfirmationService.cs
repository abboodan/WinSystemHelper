using System.Collections.Concurrent;
using System.Security.Cryptography;
using Telegram.Bot.Types.ReplyMarkups;
using static WinSystemHelper.CommandUtilities;

namespace WinSystemHelper;

internal sealed class ConfirmationService(
    CommandReply reply,
    TelegramNotifier notifier,
    RuntimeConfiguration runtime,
    CooldownService cooldown,
    CommandBackgroundWork backgroundWork) : ICommandHandler
{
    public IReadOnlyList<string> Commands { get; } = ["/confirm", "/cancel"];

    public Task HandleAsync(CommandRequest request, CancellationToken cancellationToken) => request.Word switch
    {
        "/confirm" => ConfirmPendingCommandAsync(request.Text, request.ChatId, cancellationToken),
        "/cancel" => CancelPendingCommandAsync(request.Text, request.ChatId, cancellationToken),
        _ => Task.CompletedTask
    };

    private readonly ConcurrentDictionary<int, PendingConfirmation> _pendingConfirmations = new();

    public async Task RequestDangerousConfirmationAsync(
        string commandKey,
        string description,
        Func<CancellationToken, Task> operation,
        CancellationToken cancellationToken)
    {
        if (cooldown.TryGetRemaining(commandKey, GetDangerousCommandCooldown(), out TimeSpan remaining))
        {
            await reply.SendTelegramMessageOnceAsync(
                $"⏳ Command cooldown active. Try again in {FormatDurationForHumans(remaining)}.",
                cancellationToken);
            return;
        }

        RemoveExpiredConfirmations();

        int confirmationId = CreateConfirmationId();
        DateTimeOffset expiresAt = DateTimeOffset.Now.Add(GetDangerousCommandConfirmationTimeout());
        long requesterChatId = reply.ChatId;

        _pendingConfirmations[confirmationId] = new PendingConfirmation(
            confirmationId,
            commandKey,
            description,
            requesterChatId,
            expiresAt,
            operation);

        if (CommandExecution.Current.Value is { } execution) execution.ReplyExpires = expiresAt.ToUnixTimeMilliseconds();
        string confirm = $"/confirm {confirmationId}";
        string cancel = $"/cancel {confirmationId}";
        var keyboard = new InlineKeyboardMarkup(new[]
        {
            new[] { TelegramCommandMenu.Copy("✅ Copy confirmation", confirm), TelegramCommandMenu.Copy("✖️ Copy cancellation", cancel) }
        });
        await notifier.SendHtmlAsync(requesterChatId,
            TelegramReplies.Confirmation(description, confirm, cancel, expiresAt), keyboard, cancellationToken);
    }

    private async Task ConfirmPendingCommandAsync(
        string commandText,
        long chatId,
        CancellationToken cancellationToken)
    {
        if (!TryParseConfirmationId(commandText, out int confirmationId))
        {
            await reply.SendTelegramMessageOnceAsync("⚠️ Usage: /confirm [Id]", cancellationToken);
            return;
        }

        if (!_pendingConfirmations.TryRemove(confirmationId, out PendingConfirmation pending))
        {
            await reply.SendTelegramMessageOnceAsync("⚠️ Confirmation was not found or already expired.", cancellationToken);
            return;
        }

        if (pending.ExpiresAt <= DateTimeOffset.Now)
        {
            await reply.SendTelegramMessageOnceAsync("⚠️ Confirmation expired. Send the command again.", cancellationToken);
            return;
        }

        if (!GetAllowCrossAdminConfirmations() && pending.RequesterChatId != chatId)
        {
            _pendingConfirmations[confirmationId] = pending;
            await reply.SendTelegramMessageOnceAsync("⚠️ This confirmation belongs to another admin.", cancellationToken);
            return;
        }

        cooldown.Mark(pending.CommandKey);

        await reply.SendTelegramMessageOnceAsync($"✅ Confirmed: {pending.Description}.", cancellationToken);

        backgroundWork.QueueBackgroundWork(
            pending.Operation,
            $"Confirmed command failed: {pending.CommandKey}.");
    }

    private async Task CancelPendingCommandAsync(
        string commandText,
        long chatId,
        CancellationToken cancellationToken)
    {
        if (!TryParseConfirmationId(commandText, out int confirmationId))
        {
            await reply.SendTelegramMessageOnceAsync("⚠️ Usage: /cancel [Id]", cancellationToken);
            return;
        }

        if (!_pendingConfirmations.TryGetValue(confirmationId, out PendingConfirmation pending))
        {
            await reply.SendTelegramMessageOnceAsync("⚠️ Confirmation was not found or already expired.", cancellationToken);
            return;
        }

        if (!GetAllowCrossAdminConfirmations() && pending.RequesterChatId != chatId)
        {
            await reply.SendTelegramMessageOnceAsync("⚠️ This confirmation belongs to another admin.", cancellationToken);
            return;
        }

        _pendingConfirmations.TryRemove(confirmationId, out _);
        await reply.SendTelegramMessageOnceAsync($"🚫 Canceled: {pending.Description}.", cancellationToken);
    }

    private int CreateConfirmationId()
    {
        int confirmationId;
        do
        {
            confirmationId = RandomNumberGenerator.GetInt32(100000, 999999);
        }
        while (_pendingConfirmations.ContainsKey(confirmationId));

        return confirmationId;
    }

    private static bool TryParseConfirmationId(string commandText, out int confirmationId)
    {
        return int.TryParse(GetCommandPayload(commandText), out confirmationId);
    }

    public int RemoveExpiredConfirmations()
    {
        DateTimeOffset now = DateTimeOffset.Now;
        foreach (KeyValuePair<int, PendingConfirmation> pending in _pendingConfirmations)
        {
            if (pending.Value.ExpiresAt <= now)
            {
                _pendingConfirmations.TryRemove(pending.Key, out _);
            }
        }

        return _pendingConfirmations.Count;
    }

    private TimeSpan GetDangerousCommandConfirmationTimeout()
    {
        return TimeSpan.FromSeconds(Math.Clamp(runtime.Current.DangerousCommandConfirmationSeconds, 10, 300));
    }

    private TimeSpan GetDangerousCommandCooldown()
    {
        return TimeSpan.FromSeconds(Math.Clamp(runtime.Current.DangerousCommandCooldownSeconds, 0, 3600));
    }

    private bool GetAllowCrossAdminConfirmations()
    {
        return runtime.Current.AllowCrossAdminConfirmations;
    }
}
