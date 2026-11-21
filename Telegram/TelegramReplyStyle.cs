using System.Text.Json;
using Telegram.Bot.Types.Enums;
using Telegram.Bot.Types.ReplyMarkups;

namespace WinSystemHelper;

public static class TelegramReplyStyle
{
    private const string HtmlPrefix = "html:";

    public static string Html(InlineKeyboardMarkup? keyboard = null) =>
        HtmlPrefix + (keyboard is null ? string.Empty : JsonSerializer.Serialize(keyboard));

    public static (ParseMode ParseMode, ReplyMarkup? Keyboard) Read(string? markup)
    {
        if (markup is null) return (ParseMode.None, null);
        if (!markup.StartsWith(HtmlPrefix, StringComparison.Ordinal))
            return (ParseMode.None, JsonSerializer.Deserialize<ReplyKeyboardMarkup>(markup));

        string keyboard = markup[HtmlPrefix.Length..];
        return (ParseMode.Html, keyboard.Length == 0 ? null : JsonSerializer.Deserialize<InlineKeyboardMarkup>(keyboard));
    }
}
