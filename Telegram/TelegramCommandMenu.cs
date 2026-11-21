using Telegram.Bot.Types;
using Telegram.Bot.Types.ReplyMarkups;

namespace WinSystemHelper;

public enum TelegramMenuAction
{
    Home, System, Diagnostics, Capture, Settings, Maintenance,
    Status, Health, Queue, Logs, Config, Mode, Version, Ping, Help
}

public sealed record TelegramMenuPage(string Text, InlineKeyboardMarkup Keyboard);

public static class TelegramCommandMenu
{
    private const string Prefix = "wsh:menu:";

    public static TelegramMenuAction? Resolve(CallbackQuery callback, long botId, IReadOnlyCollection<long> admins)
    {
        if (callback.Message is not { From: { Id: var author } } message || author != botId ||
            message.Date <= DateTime.UnixEpoch || !CommandPolicy.IsAuthorizedSender(message.Chat.Id, callback.From?.Id, admins) ||
            callback.Data is not { } data || !data.StartsWith(Prefix, StringComparison.Ordinal))
        {
            return null;
        }

        string action = data[Prefix.Length..];
        return Enum.TryParse(action, out TelegramMenuAction result) && action == result.ToString() && Enum.IsDefined(result)
            ? result : null;
    }

    public static TelegramMenuPage? Build(TelegramMenuAction action) => action switch
    {
        TelegramMenuAction.Home => Page("""
            🧭 <b>WinSystemHelper</b>

            Choose what you'd like to do on this PC.
            """,
            [Button("🖥️ System", TelegramMenuAction.System), Button("📦 Queue & logs", TelegramMenuAction.Diagnostics)],
            [Button("🎙️ Capture & interaction", TelegramMenuAction.Capture)],
            [Button("⚙️ Settings", TelegramMenuAction.Settings), Button("🛠️ Maintenance", TelegramMenuAction.Maintenance)],
            [Button("📖 Command reference", TelegramMenuAction.Help)]),
        TelegramMenuAction.System => Page("""
            🖥️ <b>System</b>

            Check the PC or copy a command to send in this chat.
            """,
            [Button("📊 Status", TelegramMenuAction.Status), Button("💚 Health check", TelegramMenuAction.Health)],
            [Button("📶 Ping", TelegramMenuAction.Ping), Button("🏷️ Version", TelegramMenuAction.Version)],
            [Copy("📋 Copy /tasks", "/tasks"), Copy("🌐 Copy /net", "/net")],
            [Copy("🔒 Copy /lock", "/lock"), Copy("🔄 Copy /restart", "/restart")], Back()),
        TelegramMenuAction.Diagnostics => Page("""
            📦 <b>Queue &amp; logs</b>

            Check pending deliveries, inspect failures, or read the service log.
            """,
            [Button("📦 Delivery queue", TelegramMenuAction.Queue), Button("📄 Recent logs", TelegramMenuAction.Logs)],
            [Copy("📎 Copy /log file", "/log file")], Back()),
        TelegramMenuAction.Capture => Page("""
            🎙️ <b>Capture &amp; interaction</b>

            Paste and send a copied command to start. The PC applies its current consent settings.
            """,
            [Copy("📷 Copy /cam", "/cam"), Copy("🖼️ Copy /screen", "/screen")],
            [Copy("🎙️ Copy /mic", "/mic"), Copy("🛑 Copy /mic stop", "/mic stop")],
            [Copy("💬 Copy /msg", "/msg "), Copy("❓ Copy /ask", "/ask ")], Back()),
        TelegramMenuAction.Settings => Page("""
            ⚙️ <b>Settings</b>

            View the current configuration or copy a command to change modes.
            """,
            [Button("⚙️ Configuration", TelegramMenuAction.Config), Button("🔀 Current mode", TelegramMenuAction.Mode)],
            [Copy("🧪 Copy development", "/mode development"), Copy("🛡️ Copy production", "/mode production")], Back()),
        TelegramMenuAction.Maintenance => Page("""
            🛠️ <b>Maintenance</b>

            Paste and send a copied command to continue. The bot will ask for confirmation when required.
            """,
            [Copy("🩺 Copy /repair", "/repair"), Copy("🔄 Copy /restartself", "/restartself")],
            [Copy("⬆️ Copy /update github", "/update github"), Copy("↩️ Copy /rollback", "/rollback")], Back()),
        _ => null
    };

    public static InlineKeyboardMarkup QueueKeyboard() => new(new[]
    {
        new[] { Button("🔄 Refresh queue", TelegramMenuAction.Queue), Button("📄 Recent logs", TelegramMenuAction.Logs) },
        Back()
    });

    public static InlineKeyboardMarkup FailureKeyboard() => new(new[]
    {
        new[] { Button("📦 Open queue", TelegramMenuAction.Queue), Button("📄 Recent logs", TelegramMenuAction.Logs) }
    });

    public static InlineKeyboardButton Copy(string label, string command) =>
        InlineKeyboardButton.WithCopyText(label, new CopyTextButton { Text = command });

    private static TelegramMenuPage Page(string text, params InlineKeyboardButton[][] rows) => new(text, new InlineKeyboardMarkup(rows));
    private static InlineKeyboardButton[] Back() => [Button("⬅️ Main menu", TelegramMenuAction.Home)];
    private static InlineKeyboardButton Button(string label, TelegramMenuAction action) =>
        InlineKeyboardButton.WithCallbackData(label, Prefix + action);
}
