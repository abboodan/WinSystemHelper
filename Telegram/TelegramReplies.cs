using System.Text;

namespace WinSystemHelper;

public static class TelegramReplies
{
    public static string Queue(QueueSnapshot queue)
    {
        var report = new StringBuilder("📦 <b>Delivery queue</b>\n\n")
            .AppendLine($"<b>Storage</b>  {queue.Bytes / 1048576d:F2} / {queue.Limit / 1048576} MiB")
            .AppendLine($"<b>Pending</b>  {queue.Pending}   ·   <b>Failed</b>  {queue.Failed}")
            .AppendLine($"<b>Commands pending or running</b>  {queue.Commands}")
            .AppendLine(queue.Paused
                ? "🟠 <b>Capture paused</b>  Resumes below 80% storage."
                : "🟢 <b>Capture available</b>");

        if (queue.Problems.Count == 0)
        {
            report.AppendLine().Append("No failed items in this chat.");
            return report.ToString();
        }

        report.AppendLine().AppendLine("⚠️ <b>Latest failures in this chat</b>");
        foreach (OutboxItem item in queue.Problems)
        {
            string label = item.Kind switch
            {
                QueueItemKind.Voice => "🎙️ Microphone recording",
                QueueItemKind.Photo => "🖼️ Image capture",
                QueueItemKind.Text => "💬 Message delivery",
                _ => "📎 File delivery"
            };
            string reason = item.Error switch
            {
                "Capture did not complete" => "The capture ended before a complete file was saved.",
                null or "" => "No error description was recorded.",
                _ => item.Error
            };
            string entry = $"\n<b>{label}</b>\n{Escape(Shorten(reason, 350))}\nID  <code>{Escape(item.Id)}</code>\n";
            if (report.Length + entry.Length > 3800)
            {
                report.AppendLine().Append("More failures are stored in the queue.");
                break;
            }

            report.Append(entry);
        }

        return report.ToString().TrimEnd();
    }

    public static string CommandFailure(int updateId, string? command, bool readOnly, Exception error)
    {
        string reason = error switch
        {
            MicRecorderFailure recorder => recorder.Message,
            TimeoutException => "The operation timed out.",
            OperationCanceledException => "The operation was cancelled before it finished.",
            _ => "The operation ended before completion. See /log for details."
        };
        string operation = string.IsNullOrWhiteSpace(command) ? "Command" : $"Command  <code>{Escape(Shorten(command, 64))}</code>";
        string outcome = readOnly ? string.Empty : "\n\nSome changes may already have happened. The bot won't run this command again automatically.";
        return $"❌ <b>Command failed</b>\n\n{operation}\n<b>Reason</b>  {Escape(Shorten(reason, 650))}{outcome}\n\nUpdate  <code>{updateId}</code>";
    }

    public static string Confirmation(string description, string confirm, string cancel, DateTimeOffset expiresAt) =>
        $"⚠️ <b>Confirmation required</b>\n\n{Escape(Shorten(description, 350))}\n\n" +
        $"✅ <code>{Escape(confirm)}</code>\n✖️ <code>{Escape(cancel)}</code>\n\n" +
        $"<b>Expires</b>  {expiresAt:HH:mm:ss zzz}\nCopy, paste, and send one command.";

    public static string Log(string machine, string tail)
    {
        if (tail.Length == 0) return "📄 <b>Service log</b>\n\nNo entries yet.";

        string header = $"📄 <b>Service log · {Escape(Shorten(machine, 64))}</b>\n\n<pre>";
        const string footer = "</pre>\n\nUse /log file for the complete saved log.";
        int remaining = 3800 - header.Length - footer.Length;
        string[] lines = tail.Split('\n');
        var output = new Stack<string>();
        for (int index = lines.Length - 1; index >= 0; index--)
        {
            string escaped = Escape(lines[index].TrimEnd('\r'));
            if (escaped.Length + 1 > remaining)
            {
                // Keep part of a single long newest line without cutting an HTML entity.
                if (output.Count == 0) output.Push(Escape(Shorten(lines[index], (remaining - 1) / 5)));
                break;
            }

            output.Push(escaped);
            remaining -= escaped.Length + 1;
        }

        return header + string.Join('\n', output) + footer;
    }

    public static string Escape(string text) => text.Replace("&", "&amp;", StringComparison.Ordinal)
        .Replace("<", "&lt;", StringComparison.Ordinal).Replace(">", "&gt;", StringComparison.Ordinal);

    private static string Shorten(string text, int maxLength)
    {
        if (text.Length <= maxLength) return text;
        if (char.IsHighSurrogate(text[maxLength - 1])) maxLength--;
        return text[..maxLength] + "…";
    }
}
