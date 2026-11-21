using Xunit;

namespace WinSystemHelper.Tests;

public sealed class OutboxCapacityTests : TestWorkspace
{
    [Fact(DisplayName = "Text limit reserves control capacity")]
    public async Task TextLimitReservesControlCapacity()
    {
        using (var store = Store("text-capacity"))
        {
            for (int i = 0; i < 1900; i++) await store.EnqueueTextAsync(1, "synthetic");
            bool rejected = false;
            try { await store.EnqueueTextAsync(1, "full"); } catch (QueueCapacityException) { rejected = true; }
            await store.EnqueueTextAsync(1, "control reply", priority: true);
            Assert.True(rejected && (await store.ClaimAsync(false))?.Text == "control reply");
        }
    }

    [Fact(DisplayName = "Broadcast storage is all-or-nothing")]
    public async Task BroadcastStorageIsAllOrNothing()
    {
        using (var store = Store("broadcast-atomic"))
        {
            for (int i = 0; i < 1899; i++) await store.EnqueueTextAsync(1, "synthetic");
            bool rejected = false;
            try { await store.EnqueueBroadcastAsync([1,2], "broadcast", "atomic"); } catch (QueueCapacityException) { rejected = true; }
            Assert.True(rejected && (await store.SnapshotAsync()).Pending == 1899);
        }
    }

}
