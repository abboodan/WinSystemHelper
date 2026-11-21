using System.Text.Json;
using Telegram.Bot;
using Telegram.Bot.Exceptions;
using Telegram.Bot.Types;
using Telegram.Bot.Types.Enums;
using Telegram.Bot.Types.ReplyMarkups;
using static WinSystemHelper.CommandUtilities;

namespace WinSystemHelper;

internal sealed class MenuCommandHandler(
    ITelegramBotClient botClient,
    QueueStore queueStore,
    TelegramNotifier notifier,
    CommandReply reply,
    RuntimeConfiguration runtime,
    QueueCommandHandler queue) : ICommandHandler
{
    public IReadOnlyList<string> Commands { get; } = ["/start", "/menu", "/help"];

    public Task HandleAsync(CommandRequest request, CancellationToken cancellationToken) => request.Word switch
    {
        "/start" or "/menu" => SendCommandMenuAsync(cancellationToken),
        "/help" => SendHelpAsync(cancellationToken),
        _ => Task.CompletedTask
    };

    private Task SendCommandMenuAsync(CancellationToken token)
    {
        TelegramMenuPage page = TelegramCommandMenu.Build(TelegramMenuAction.Home)!;
        return notifier.SendHtmlAsync(reply.ChatId, page.Text, page.Keyboard, token);
    }

    public async Task HandleMenuCallbackAsync(CommandRegistry commands, CallbackQuery callback, int updateId, CancellationToken token)
    {
        if (TelegramCommandMenu.Resolve(callback, botClient.BotId, runtime.GetAdminChatIdsSnapshot()) is not { } action)
        {
            return;
        }

        Message message = callback.Message!;
        TelegramMenuPage? page = action == TelegramMenuAction.Queue
            ? await queue.BuildQueuePageAsync(message.Chat.Id)
            : TelegramCommandMenu.Build(action);
        if (page is not null)
        {
            await EditMenuPageAsync(message, page, token);
            return;
        }

        switch (action)
        {
            case TelegramMenuAction.Status: await DispatchMenuCommandAsync(commands, "/status", updateId, message, token); break;
            case TelegramMenuAction.Health: await DispatchMenuCommandAsync(commands, "/healthcheck", updateId, message, token); break;
            case TelegramMenuAction.Logs: await DispatchMenuCommandAsync(commands, "/log", updateId, message, token); break;
            case TelegramMenuAction.Config: await DispatchMenuCommandAsync(commands, "/config", updateId, message, token); break;
            case TelegramMenuAction.Mode: await DispatchMenuCommandAsync(commands, "/mode", updateId, message, token); break;
            case TelegramMenuAction.Version: await DispatchMenuCommandAsync(commands, "/version", updateId, message, token); break;
            case TelegramMenuAction.Ping: await DispatchMenuCommandAsync(commands, "/ping", updateId, message, token); break;
            case TelegramMenuAction.Help: await SendHelpAsync(token); break;
        }
    }

    private static Task DispatchMenuCommandAsync(CommandRegistry commands, string command, int updateId, Message message, CancellationToken token)
    {
        return commands.TryGet(command, out ICommandHandler? handler)
            ? handler.HandleAsync(new CommandRequest(command, command, updateId, message.Chat.Id, message), token)
            : Task.CompletedTask;
    }

    private async Task EditMenuPageAsync(Message message, TelegramMenuPage page, CancellationToken token)
    {
        using var timeout = CreateTimeoutTokenSource(token, TimeSpan.FromSeconds(5));
        try
        {
            await botClient.EditMessageText(message.Chat.Id, message.Id, page.Text, parseMode: ParseMode.Html,
                replyMarkup: page.Keyboard, cancellationToken: timeout.Token);
        }
        catch (ApiRequestException ex) when (ex.ErrorCode == 400 && ex.Message.Contains("message is not modified", StringComparison.OrdinalIgnoreCase))
        {
            // Refreshing an unchanged report leaves the existing message in place.
        }
        catch (Exception ex) when (!token.IsCancellationRequested && (ex is RequestException or HttpRequestException or OperationCanceledException))
        {
            await notifier.SendHtmlAsync(message.Chat.Id, page.Text, page.Keyboard, token);
        }
    }

    private async Task SendHelpAsync(CancellationToken cancellationToken)
    {
        string helpText = string.Join(
            Environment.NewLine,
            "📜 WinSystemHelper Commands",
            "🧭 /menu - Browse the interactive menu in this chat.",
            "",
            "📊 Status & Monitoring",
            "/status - Show service, machine, network, and timestamp details.",
            "/queue - Show queued files and storage capacity; retry or delete by Id.",
            "/healthcheck - Show fast service health and cached state.",
            "/log [lines] - Show the latest service log lines (default 40).",
            "/log file - Send the service log as a file.",
            "/version - Show the installed WinSystemHelper version.",
            "/version check - Check the latest GitHub Release version.",
            "/ping - Lightweight bot responsiveness check.",
            "/ip - Show the cached or refreshed public IP address.",
            "/ip refresh - Force a public IP refresh with timeout/backoff.",
            "/net - Show local network adapters, IPs, gateways, and DNS.",
            "/tasks - 📋 Show the top 10 memory-consuming processes.",
            "/startup - 🚀 List configured startup applications.",
            "/services - 🧩 List Windows services.",
            "",
            "✅ Confirmation",
            "/confirm [Id] - Confirm a pending dangerous command.",
            "/cancel [Id] - Cancel a pending dangerous command.",
            "",
            "🖥️ System Control",
            "/lock - Lock the Windows workstation.",
            "/shutdown - Request shutdown confirmation.",
            "/restart - 🔄 Request restart confirmation.",
            "/sleep - 🌙 Request sleep confirmation.",
            "/alarm - Play a system alert sound.",
            "/kill [ProcessName] - 🔪 Terminate matching processes.",
            "/restartapp [ProcessName] - 🔄 Restart an app in the active session.",
            "/service status|start|stop|restart [ServiceName] - 🧩 Manage a Windows service.",
            "",
            "🎙️ Interactive & Session 0 Tools",
            "/mic [seconds] - Trigger an overt alarm and record audio for up to 60 seconds.",
            "/mic [seconds] loop - Start a persistent overt active alarm loop.",
            "/mic stop - Stop the persistent active alarm loop.",
            "/msg [text] - Show a warning message on the active user's screen.",
            "/ask [text] - Ask the active user a Yes/No question.",
            "/prompt [text] - Force a text response from the active user.",
            "/cam - 📷 Notify the local user, capture one camera photo, and return it.",
            "/speak [text] - 🗣️ Speak a message through the active session.",
            "/screen - 🖼️ Capture and return the primary screen.",
            "",
            "⚙️ Configuration",
            "/config - ⚙️ Show safe runtime configuration.",
            "/mode [production|development] - Show or change the operating mode.",
            "/config export - ⚙️ Show a safe config.json preview.",
            "/config alerts on|off - ⚙️ Enable or disable smart alerts.",
            "/config set [Key] [Value] - ⚙️ Update runtime configuration.",
            "/config admins - 👥 List configured admins.",
            "/config admin add|remove [ChatId] - 👥 Manage admins without reinstall.",
            "",
            "🔄 Service & Updates",
            "/update [https-url] - 🔄 Apply an OTA update from a ZIP file or attached document.",
            "/update github - 🔄 Apply the latest GitHub Release OTA package.",
            "",
            "🛠️ Maintenance",
            "/restartself - Restart the WinSystemHelper service.",
            "/repair - Run diagnostics and safe repairs.",
            "/reinstall - Recreate the Windows Service from the current executable.",
            "/rollback - Restore the previous installed version.",
            "/stop - Request WinSystemHelper service stop.",
            "/uninstall - Request service uninstall.",
            "/help - Show this command list.");

        await SendTelegramHelpAsync(helpText, cancellationToken);
    }

    private async Task SendTelegramHelpAsync(string text, CancellationToken cancellationToken)
    {
        ReplyKeyboardMarkup keyboard = new(
            new[]
            {
                new KeyboardButton[] { "/menu", "/help", "/ping" },
                new KeyboardButton[] { "/config", "/update" },
                new KeyboardButton[] { "/repair", "/restartself" }
            })
        {
            ResizeKeyboard = true,
            OneTimeKeyboard = false,
            IsPersistent = true
        };

        await reply.SendTelegramMessageOnceAsync(text, cancellationToken);
        await queueStore.EnqueueTextAsync(reply.ChatId, "Command menu", JsonSerializer.Serialize(keyboard));
    }
}
