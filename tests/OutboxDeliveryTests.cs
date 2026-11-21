using Microsoft.Extensions.Logging.Abstractions;
using Telegram.Bot.Exceptions;
using Telegram.Bot.Types;
using Xunit;

namespace WinSystemHelper.Tests;

public sealed class OutboxDeliveryTests : TestWorkspace
{
    [Fact(DisplayName = "Network failure retains file")]
    public async Task NetworkFailureRetainsFile()
    {
        Assert.True(await DeliveryAsync(nameof(NetworkFailureRetainsFile)));
    }

    [Fact(DisplayName = "Reconnect drains preserved file")]
    public async Task ReconnectDrainsPreservedFile()
    {
        Assert.True(await DeliveryAsync(nameof(ReconnectDrainsPreservedFile)));
    }

    [Fact(DisplayName = "Removed admin receives no media")]
    public async Task RemovedAdminReceivesNoMedia()
    {
        Assert.True(await DeliveryAsync(nameof(RemovedAdminReceivesNoMedia)));
    }

    [Fact(DisplayName = "Expired confirmation never sent")]
    public async Task ExpiredConfirmationNeverSent()
    {
        Assert.True(await DeliveryAsync(nameof(ExpiredConfirmationNeverSent)));
    }

    [Fact(DisplayName = "Idempotent result enqueue")]
    public async Task IdempotentResultEnqueue()
    {
        Assert.True(await DeliveryAsync(nameof(IdempotentResultEnqueue)));
    }

    private async Task<bool> DeliveryAsync(string check)
    {
        using (var store = Store("delivery"))
        {
            var transport = new FakeTransport();
            var cfg = new AppConfiguration { AdminChatIds = [1] };
            var sender = new OutboxSender(store, transport, cfg, NullLogger<OutboxSender>.Instance);
            var media = await Capture(store);
            transport.Fail = true;
            await sender.DeliverOneAsync(true, CancellationToken.None);
            bool observed = (await store.SnapshotAsync()).Bytes > 0;
            if (check == nameof(NetworkFailureRetainsFile))
            {
                return observed;
            }
            transport.Fail = false;
            await store.ConnectedAsync();
            await sender.DeliverOneAsync(true, CancellationToken.None);
            observed = (await store.SnapshotAsync()).Bytes == 0 && transport.Delivered == 1;
            if (check == nameof(ReconnectDrainsPreservedFile))
            {
                return observed;
            }
            await Capture(store);
            cfg.AdminChatIds = [2];
            await sender.DeliverOneAsync(true, CancellationToken.None);
            observed = (await store.SnapshotAsync()).Failed == 1 && transport.Delivered == 1;
            if (check == nameof(RemovedAdminReceivesNoMedia))
            {
                return observed;
            }
            await store.EnqueueTextAsync(1, "expired confirmation", expires: QueueStore.Now - 1);
            observed = await store.ClaimAsync(false) is null;
            if (check == nameof(ExpiredConfirmationNeverSent))
            {
                return observed;
            }
            await store.EnqueueTextAsync(1, "one", dedupe: "one");
            await store.EnqueueTextAsync(1, "one", dedupe: "one");
            return (await store.ClaimAsync(false))?.Text == "one" && await store.ClaimAsync(false) is null;
        }
    }

    [Fact(DisplayName = "Oldest media is claimed first")]
    public async Task OldestMediaIsClaimedFirst()
    {
        Assert.True(await MediaOrderingAsync(nameof(OldestMediaIsClaimedFirst)));
    }

    [Fact(DisplayName = "Later media cannot overtake retrying media")]
    public async Task LaterMediaCannotOvertakeRetryingMedia()
    {
        Assert.True(await MediaOrderingAsync(nameof(LaterMediaCannotOvertakeRetryingMedia)));
    }

    [Fact(DisplayName = "Reconnection wakes the oldest pending file")]
    public async Task ReconnectionWakesTheOldestPendingFile()
    {
        Assert.True(await MediaOrderingAsync(nameof(ReconnectionWakesTheOldestPendingFile)));
    }

    [Fact(DisplayName = "Cleanup failure cannot resurrect an acknowledged delivery")]
    public async Task CleanupFailureCannotResurrectAnAcknowledgedDelivery()
    {
        Assert.True(await MediaOrderingAsync(nameof(CleanupFailureCannotResurrectAnAcknowledgedDelivery)));
    }

    private async Task<bool> MediaOrderingAsync(string check)
    {
        using (var store = Store("fifo"))
        {
            var first = await Capture(store);
            await Task.Delay(2);
            await Capture(store);
            bool observed = (await store.ClaimAsync(true))?.Id == first.Id;
            if (check == nameof(OldestMediaIsClaimedFirst))
            {
                return observed;
            }
            await store.RetryLaterAsync(first.Id, TimeSpan.FromMinutes(1), "network", false);
            observed = await store.ClaimAsync(true) is null;
            if (check == nameof(LaterMediaCannotOvertakeRetryingMedia))
            {
                return observed;
            }
            await store.ConnectedAsync();
            observed = (await store.ClaimAsync(true))?.Id == first.Id;
            if (check == nameof(ReconnectionWakesTheOldestPendingFile))
            {
                return observed;
            }
            await store.CompleteAsync(first.Id);
            await store.RetryLaterAsync(first.Id, TimeSpan.Zero, "network", false);
            return (await store.ClaimAsync(true))?.Id != first.Id;
        }
    }

    [Fact(DisplayName = "Reconnection does not override Telegram Retry-After")]
    public async Task ReconnectionDoesNotOverrideTelegramRetryAfter()
    {
        Assert.True(await RetryPolicyAsync(nameof(ReconnectionDoesNotOverrideTelegramRetryAfter)));
    }

    [Fact(DisplayName = "Permanent API errors stop automatic retries")]
    public async Task PermanentAPIErrorsStopAutomaticRetries()
    {
        Assert.True(await RetryPolicyAsync(nameof(PermanentAPIErrorsStopAutomaticRetries)));
    }

    private async Task<bool> RetryPolicyAsync(string check)
    {
        using (var store = Store("retry-policy"))
        {
            var transport = new FakeTransport { Error = new ApiRequestException("synthetic", 429, new ResponseParameters { RetryAfter = 2 }) };
            var sender = new OutboxSender(store, transport, new AppConfiguration { AdminChatIds = [1] }, NullLogger<OutboxSender>.Instance);
            await Capture(store);
            await sender.DeliverOneAsync(true, CancellationToken.None);
            await store.ConnectedAsync();
            bool observed = await store.ClaimAsync(true) is null;
            if (check == nameof(ReconnectionDoesNotOverrideTelegramRetryAfter))
            {
                return observed;
            }
            transport.Error = new ApiRequestException("synthetic", 400);
            await store.EnqueueTextAsync(1, "bad request");
            await sender.DeliverOneAsync(false, CancellationToken.None);
            return (await store.SnapshotAsync()).Failed == 1;
        }
    }

    [Fact(DisplayName = "Lost acknowledgement retains the local file")]
    public async Task LostAcknowledgementRetainsTheLocalFile()
    {
        Assert.True(await LostAcknowledgementAsync(nameof(LostAcknowledgementRetainsTheLocalFile)));
    }

    [Fact(DisplayName = "Lost acknowledgement may duplicate delivery but does not lose the file")]
    public async Task LostAcknowledgementMayDuplicateDeliveryButDoesNotLoseTheFile()
    {
        Assert.True(await LostAcknowledgementAsync(nameof(LostAcknowledgementMayDuplicateDeliveryButDoesNotLoseTheFile)));
    }

    private async Task<bool> LostAcknowledgementAsync(string check)
    {
        using (var store = Store("lost-ack"))
        {
            var transport = new FakeTransport { LoseAcknowledgement = true };
            var sender = new OutboxSender(store, transport, new AppConfiguration { AdminChatIds = [1] }, NullLogger<OutboxSender>.Instance);
            await Capture(store);
            await sender.DeliverOneAsync(true, CancellationToken.None);
            bool observed = transport.Delivered == 1 && (await store.SnapshotAsync()).Bytes > 0;
            if (check == nameof(LostAcknowledgementRetainsTheLocalFile))
            {
                return observed;
            }
            await store.ConnectedAsync();
            await sender.DeliverOneAsync(true, CancellationToken.None);
            return transport.Delivered == 2 && (await store.SnapshotAsync()).Bytes == 0;
        }
    }

    [Fact(DisplayName = "Text lane remains available during a blocked upload")]
    public async Task TextLaneRemainsAvailableDuringABlockedUpload()
    {
        using (var store = Store("blocked-upload"))
        {
            var transport = new FakeTransport { HoldMedia = new(TaskCreationOptions.RunContinuationsAsynchronously) };
            var sender = new OutboxSender(store, transport, new AppConfiguration { AdminChatIds = [1,2] }, NullLogger<OutboxSender>.Instance);
            await Capture(store);
            var upload = sender.DeliverOneAsync(true, CancellationToken.None);
            bool textDelivered;
            try
            {
                await transport.MediaStarted.Task.WaitAsync(TimeSpan.FromSeconds(5));
                await store.EnqueueTextAsync(2, "control response", priority: true);
                await sender.DeliverOneAsync(false, CancellationToken.None).WaitAsync(TimeSpan.FromSeconds(1));
                textDelivered = transport.Delivered == 1 && !upload.IsCompleted;
            }
            finally
            {
                transport.HoldMedia.SetResult();
                await upload;
            }
            Assert.True(textDelivered);
        }
    }

}
