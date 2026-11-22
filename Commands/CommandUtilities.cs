using System.Text.Json;
using System.Text;

namespace WinSystemHelper;

internal static class CommandUtilities
{
    internal static string GetCommandPayload(string commandText)
    {
        int separatorIndex = commandText.IndexOfAny([' ', '\r', '\n', '\t']);
        if (separatorIndex < 0 || separatorIndex == commandText.Length - 1)
        {
            return string.Empty;
        }

        return commandText[(separatorIndex + 1)..].Trim();
    }

    internal static CancellationTokenSource CreateTimeoutTokenSource(
        CancellationToken cancellationToken,
        TimeSpan timeout)
    {
        CancellationTokenSource timeoutTokenSource =
            CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);

        timeoutTokenSource.CancelAfter(timeout);
        return timeoutTokenSource;
    }

    internal static string GetUsefulExceptionMessage(Exception exception)
    {
        List<string> messages = [];
        Exception? current = exception;

        while (current is not null)
        {
            if (!string.IsNullOrWhiteSpace(current.Message))
            {
                messages.Add(current.Message.Trim());
            }

            current = current.InnerException;
        }

        if (exception is AggregateException aggregateException)
        {
            foreach (Exception innerException in aggregateException.Flatten().InnerExceptions)
            {
                if (!string.IsNullOrWhiteSpace(innerException.Message))
                {
                    messages.Add(innerException.Message.Trim());
                }
            }
        }

        string message = string.Join(" | ", messages.Distinct(StringComparer.Ordinal));
        if (!string.IsNullOrWhiteSpace(message))
        {
            return TruncateForTelegram(message, 1200);
        }

        string exceptionText = exception.ToString();
        if (!string.IsNullOrWhiteSpace(exceptionText))
        {
            return TruncateForTelegram(exceptionText.Split(Environment.NewLine)[0], 1200);
        }

        return $"{exception.GetType().FullName} was thrown without a message.";
    }

    internal static string TruncateForTelegram(string value, int maxLength)
    {
        if (value.Length <= maxLength)
        {
            return value;
        }

        return value[..Math.Max(0, maxLength - 3)] + "...";
    }

    internal static string FormatPublicIpSnapshot(PublicIpSnapshot snapshot)
    {
        if (string.IsNullOrWhiteSpace(snapshot.PublicIp))
        {
            return snapshot.NextAttemptAt.HasValue
                ? $"Unavailable; next attempt {snapshot.NextAttemptAt.Value:HH:mm:ss zzz}"
                : "Unavailable";
        }

        string fetchedAt = snapshot.FetchedAt.HasValue
            ? snapshot.FetchedAt.Value.ToString("HH:mm:ss zzz")
            : "unknown time";

        return $"{snapshot.PublicIp} fetched at {fetchedAt}";
    }

    internal static string FormatDurationForHumans(TimeSpan duration)
    {
        if (duration.TotalMinutes >= 1)
        {
            return $"{Math.Ceiling(duration.TotalMinutes):N0} minute(s)";
        }

        return $"{Math.Ceiling(duration.TotalSeconds):N0} second(s)";
    }

    internal static readonly JsonSerializerOptions ConfigurationJsonOptions = new(JsonSerializerDefaults.Web)
    {
        WriteIndented = true
    };

    internal static string NormalizeWhitespace(string value)
    {
        return string.Join(' ', value.Split(default(string[]), StringSplitOptions.RemoveEmptyEntries));
    }

    internal static TimeSpan CalculateBackoff(
        int attempt,
        TimeSpan initialDelay,
        TimeSpan maxDelay)
    {
        if (attempt <= 1)
        {
            return initialDelay;
        }

        int exponent = Math.Min(attempt - 1, 16);
        double delayMilliseconds = initialDelay.TotalMilliseconds * Math.Pow(2, exponent);

        return TimeSpan.FromMilliseconds(Math.Min(delayMilliseconds, maxDelay.TotalMilliseconds));
    }
}
