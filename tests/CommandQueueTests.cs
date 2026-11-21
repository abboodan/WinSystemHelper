using System.Reflection;
using System.Text.Json;
using Telegram.Bot.Types;
using Telegram.Bot.Types.Enums;
using Xunit;

namespace WinSystemHelper.Tests;

public sealed class CommandQueueTests : TestWorkspace
{
    [Fact(DisplayName = "Telegram update round trip")]
    public async Task TelegramUpdateRoundTrip()
    {
        Assert.True(await CommandAdmissionAsync(nameof(TelegramUpdateRoundTrip)));
    }

    [Fact(DisplayName = "Atomic offset and duplicate prevention")]
    public async Task AtomicOffsetAndDuplicatePrevention()
    {
        Assert.True(await CommandAdmissionAsync(nameof(AtomicOffsetAndDuplicatePrevention)));
    }

    [Fact(DisplayName = "Control lane independent of ordinary commands")]
    public async Task ControlLaneIndependentOfOrdinaryCommands()
    {
        Assert.True(await CommandAdmissionAsync(nameof(ControlLaneIndependentOfOrdinaryCommands)));
    }

    [Fact(DisplayName = "Unauthorized payload discarded")]
    public async Task UnauthorizedPayloadDiscarded()
    {
        Assert.True(await CommandAdmissionAsync(nameof(UnauthorizedPayloadDiscarded)));
    }

    [Fact(DisplayName = "Read-only running command recovered")]
    public async Task ReadOnlyRunningCommandRecovered()
    {
        Assert.True(await CommandAdmissionAsync(nameof(ReadOnlyRunningCommandRecovered)));
    }

    [Fact(DisplayName = "Interrupted side effect is not executed again")]
    public async Task InterruptedSideEffectIsNotExecutedAgain()
    {
        Assert.True(await CommandAdmissionAsync(nameof(InterruptedSideEffectIsNotExecutedAgain)));
    }

    [Fact(DisplayName = "Unknown outcome is reported")]
    public async Task UnknownOutcomeIsReported()
    {
        Assert.True(await CommandAdmissionAsync(nameof(UnknownOutcomeIsReported)));
    }

    private async Task<bool> CommandAdmissionAsync(string check)
    {
        using (var store = Store("commands"))
        {
            var update = new Update { Id = 10, Message = new Message { Id = 10, Chat = new Chat { Id = 1, Type = ChatType.Private }, Date = DateTime.UtcNow, Text = "/ping" } };
            string serialized = JsonSerializer.Serialize(update);
            bool observed = JsonSerializer.Deserialize<Update>(serialized)?.Message?.Text == "/ping";
            if (check == nameof(TelegramUpdateRoundTrip))
            {
                return observed;
            }
            await store.AcceptAsync(10, 1, serialized, true, false, QueueStore.Now);
            await store.AcceptAsync(10, 1, serialized, true, false, QueueStore.Now);
            observed = await store.OffsetAsync() == 11 && (await store.SnapshotAsync()).Commands == 1;
            if (check == nameof(AtomicOffsetAndDuplicatePrevention))
            {
                return observed;
            }
            await store.AcceptAsync(11, 1, "control", false, true, QueueStore.Now);
            observed = (await store.ClaimCommandAsync(true))?.UpdateId == 11;
            if (check == nameof(ControlLaneIndependentOfOrdinaryCommands))
            {
                return observed;
            }
            await store.AcceptAsync(12, 0, null, false, false, QueueStore.Now);
            observed = await store.OffsetAsync() == 13 && (await store.SnapshotAsync()).Commands == 2;
            if (check == nameof(UnauthorizedPayloadDiscarded))
            {
                return observed;
            }
            await store.ClaimCommandAsync(false);
        }
        using (var store = Store("commands"))
        {
            bool observed = (await store.ClaimCommandAsync(false))?.UpdateId == 10;
            if (check == nameof(ReadOnlyRunningCommandRecovered))
            {
                return observed;
            }
            observed = await store.ClaimCommandAsync(true) is null;
            if (check == nameof(InterruptedSideEffectIsNotExecutedAgain))
            {
                return observed;
            }
            await store.ReportUnknownAsync();
            return (await store.ClaimAsync(false))?.Text.Contains("unknown") == true;
        }
    }

    [Fact(DisplayName = "Five-minute expiry boundary")]
    public void FiveMinuteExpiryBoundary()
    {
        Assert.True(QueuePolicy(nameof(FiveMinuteExpiryBoundary)));
    }

    [Fact(DisplayName = "Stop and status do not expire")]
    public void StopAndStatusDoNotExpire()
    {
        Assert.True(QueuePolicy(nameof(StopAndStatusDoNotExpire)));
    }

    private bool QueuePolicy(string check)
    {
        bool observed = CommandPolicy.IsExpired("/screen", 0, 300001) && !CommandPolicy.IsExpired("/screen", 0, 300000);
        if (check == nameof(FiveMinuteExpiryBoundary))
        {
            return observed;
        }
        return !CommandPolicy.IsExpired("/mic stop", 0, 900000) && !CommandPolicy.IsExpired("/status", 0, 900000);
    }

    [Fact(DisplayName = "Stop cancels older queued recording requests")]
    public async Task StopCancelsOlderQueuedRecordingRequests()
    {
        using (var store = Store("stop-pending"))
        {
            var pending = new Update { Id = 1, Message = new Message { Id = 1, Chat = new Chat { Id = 1, Type = ChatType.Private }, Date = DateTime.UtcNow, Text = "/mic 10 loop" } };
            await store.AcceptAsync(1, 1, JsonSerializer.Serialize(pending), false, false, QueueStore.Now);
            await store.CancelOlderRecordingCommandsAsync(2);
            Assert.True(await store.ClaimCommandAsync(false) is null);
        }
    }

    [Fact(DisplayName = "Bot identities isolate queues while token rotation preserves ownership")]
    public void BotIdentitiesIsolateQueuesWhileTokenRotationPreservesOwnership()
    {
        var rootMethod = typeof(QueueStore).GetMethod("DefaultRoot", System.Reflection.BindingFlags.Static | System.Reflection.BindingFlags.NonPublic)!;
        string RootFor(string token) => (string)rootMethod.Invoke(null, [new AppConfiguration { BotToken = token }])!;
        Assert.True(RootFor("1:synthetic") == RootFor("1:rotated") && RootFor("1:synthetic") != RootFor("2:synthetic"));
    }

    [Fact(DisplayName = "Full ordinary command lane retains stop capacity")]
    public async Task FullOrdinaryCommandLaneRetainsStopCapacity()
    {
        using (var store = Store("control-limit"))
        {
            for (int i = 0; i < 950; i++) await store.AcceptAsync(i, 1, "ordinary", false, false, QueueStore.Now);
            await store.AcceptAsync(950, 1, "rejected", false, false, QueueStore.Now);
            await store.AcceptAsync(951, 1, "/mic stop", false, true, QueueStore.Now);
            Assert.True((await store.SnapshotAsync()).Commands == 951 && (await store.ClaimCommandAsync(true))?.UpdateId == 951);
        }
    }

}
