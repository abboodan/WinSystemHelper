using System.Text.Json;
using Telegram.Bot;
using Telegram.Bot.Exceptions;
using Telegram.Bot.Types;
using Telegram.Bot.Types.ReplyMarkups;

namespace WinSystemHelper;

public interface IOutboxTransport
{
    Task SendAsync(OutboxItem item, CancellationToken token);
}

public sealed class TelegramOutboxTransport(ITelegramBotClient client) : IOutboxTransport
{
    public async Task SendAsync(OutboxItem item, CancellationToken token)
    {
        if (item.Kind == "text")
        {
            await client.SendMessage(item.ChatId, item.Text,
                replyMarkup: item.Markup is null ? null : JsonSerializer.Deserialize<ReplyKeyboardMarkup>(item.Markup), cancellationToken: token);
            return;
        }
        await using var stream = new FileStream(item.Path!, FileMode.Open, FileAccess.Read, FileShare.Read, 65536, FileOptions.Asynchronous | FileOptions.SequentialScan);
        var input = InputFile.FromStream(stream, Path.GetFileName(item.Path!));
        if (item.Kind == "voice")
            await client.SendVoice(item.ChatId, input, caption: item.Text, duration: item.Duration, cancellationToken: token);
        else if (item.Kind == "photo")
            await client.SendPhoto(item.ChatId, input, caption: item.Text, cancellationToken: token);
        else
            await client.SendDocument(item.ChatId, input, caption: item.Text, cancellationToken: token);
    }
}

public sealed class OutboxSender(QueueStore store, IOutboxTransport transport,
    AppConfiguration configuration, ILogger<OutboxSender> logger) : BackgroundService
{
    private readonly SemaphoreSlim _rateLock = new(1, 1);
    private readonly Dictionary<long, long> _lastSent = [];
    private long _telegramBlockedUntil;
    private readonly long[] _laneBlockedUntil = new long[2];
    private readonly long[] _laneGeneration = new long[2];

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        await store.OffsetAsync();
        await Task.WhenAll(RunLaneAsync(false, stoppingToken), RunLaneAsync(true, stoppingToken));
    }

    public async Task RunLaneAsync(bool media, CancellationToken token)
    {
        QueueSignal signal = media ? store.MediaChanged : store.TextChanged;
        while (!token.IsCancellationRequested)
        {
            try
            {
                int lane = media ? 1 : 0;
                if (_laneGeneration[lane] != store.ConnectionGeneration) _laneBlockedUntil[lane] = 0;
                long pause = Math.Max(_laneBlockedUntil[lane], Interlocked.Read(ref _telegramBlockedUntil)) - QueueStore.Now;
                if (pause > 0)
                { await signal.WaitAsync(TimeSpan.FromMilliseconds(pause), token); continue; }
                if (await DeliverOneAsync(media, token)) continue;
                await store.CleanupAsync();
                await signal.WaitAsync(await store.NextDelayAsync(media), token);
            }
            catch (OperationCanceledException) when (token.IsCancellationRequested) { break; }
            catch (Exception ex)
            {
                logger.LogWarning("Queue worker paused after {ErrorType}; files are preserved.", ex.GetType().Name);
                await Task.Delay(TimeSpan.FromSeconds(30), token);
            }
        }
    }

    public async Task<bool> DeliverOneAsync(bool media, CancellationToken token)
    {
        OutboxItem? item = await store.ClaimAsync(media);
        if (item is null) return false;
        try
        {
            if (!configuration.GetEffectiveAdminChatIds().Contains(item.ChatId))
            {
                await store.RetryLaterAsync(item.Id, TimeSpan.Zero, "Recipient is no longer authorized", true);
                return true;
            }
            await WaitForRateAsync(item.ChatId, token);
            if (!configuration.GetEffectiveAdminChatIds().Contains(item.ChatId))
            {
                await store.RetryLaterAsync(item.Id, TimeSpan.Zero, "Recipient is no longer authorized", true);
                return true;
            }
            if (item.Expires is long expiry && expiry <= QueueStore.Now)
            { await store.CompleteAsync(item.Id); return true; }
            using var timeout = CancellationTokenSource.CreateLinkedTokenSource(token);
            timeout.CancelAfter(media ? TimeSpan.FromMinutes(2) : TimeSpan.FromSeconds(15));
            await transport.SendAsync(item, timeout.Token);
            await store.CompleteAsync(item.Id);
        }
        catch (OperationCanceledException) when (token.IsCancellationRequested)
        {
            await store.RetryLaterAsync(item.Id, TimeSpan.Zero, "network", false);
            throw;
        }
        catch (Exception ex)
        {
            int? code = (ex as ApiRequestException)?.ErrorCode;
            int? retryAfter = (ex as ApiRequestException)?.Parameters?.RetryAfter;
            bool permanent = code is >= 400 and < 500 && code != 429 || ex is FileNotFoundException or UnauthorizedAccessException;
            var delay = retryAfter is > 0 ? TimeSpan.FromSeconds(retryAfter.Value)
                : TimeSpan.FromSeconds(Math.Min(300, 2 * Math.Pow(2, Math.Min(item.Attempts, 8))));
            string category = code == 429 ? "rate-limit" : code.HasValue ? $"Telegram {code}" : "network";
            if (code == 429) Interlocked.Exchange(ref _telegramBlockedUntil, QueueStore.Now + (long)delay.TotalMilliseconds);
            await store.RetryLaterAsync(item.Id, delay, category, permanent);
            if (permanent) logger.LogWarning("Outbox item {Id} needs review ({Category}).", item.Id, category);
            if (!permanent)
            {
                int lane = media ? 1 : 0;
                _laneGeneration[lane] = store.ConnectionGeneration;
                _laneBlockedUntil[lane] = QueueStore.Now + (long)delay.TotalMilliseconds;
            }
        }
        return true;
    }

    private async Task WaitForRateAsync(long chat, CancellationToken token)
    {
        long delay;
        await _rateLock.WaitAsync(token);
        try
        {
            long next = Math.Max(QueueStore.Now, Interlocked.Read(ref _telegramBlockedUntil));
            if (_lastSent.TryGetValue(chat, out long previous)) next = Math.Max(next, previous + 1100);
            delay = next - QueueStore.Now;
            if (_lastSent.Count > 1000) _lastSent.Clear();
            _lastSent[chat] = next;
        }
        finally { _rateLock.Release(); }
        if (delay > 0) await Task.Delay(TimeSpan.FromMilliseconds(delay), token);
        // A concurrent lane may have received Retry-After while this lane was waiting.
        while ((delay = Interlocked.Read(ref _telegramBlockedUntil) - QueueStore.Now) > 0)
            await Task.Delay(TimeSpan.FromMilliseconds(delay), token);
    }
}
