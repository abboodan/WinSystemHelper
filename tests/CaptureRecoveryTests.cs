using Xunit;

namespace WinSystemHelper.Tests;

public sealed class CaptureRecoveryTests : TestWorkspace
{
    [Fact(DisplayName = "In-progress media counts toward capacity")]
    public async Task InProgressMediaCountsTowardCapacity()
    {
        Assert.True(await CapacityAsync(nameof(InProgressMediaCountsTowardCapacity)));
    }

    [Fact(DisplayName = "Quota pause is visible")]
    public async Task QuotaPauseIsVisible()
    {
        Assert.True(await CapacityAsync(nameof(QuotaPauseIsVisible)));
    }

    [Fact(DisplayName = "Raising limit resumes below hysteresis threshold")]
    public async Task RaisingLimitResumesBelowHysteresisThreshold()
    {
        Assert.True(await CapacityAsync(nameof(RaisingLimitResumesBelowHysteresisThreshold)));
    }

    [Fact(DisplayName = "Lowering limit preserves existing media")]
    public async Task LoweringLimitPreservesExistingMedia()
    {
        Assert.True(await CapacityAsync(nameof(LoweringLimitPreservesExistingMedia)));
    }

    [Fact(DisplayName = "Queue status preserves the capture failure reason")]
    public async Task QueueStatusPreservesTheCaptureFailureReason()
    {
        Assert.True(await CapacityAsync(nameof(QueueStatusPreservesTheCaptureFailureReason)));
    }

    [Fact(DisplayName = "Freeing capacity resumes production")]
    public async Task FreeingCapacityResumesProduction()
    {
        Assert.True(await CapacityAsync(nameof(FreeingCapacityResumesProduction)));
    }

    private async Task<bool> CapacityAsync(string check)
    {
        int limit = 1;
        using (var store = Store("capacity", () => limit))
        {
            var first = await store.ReserveAsync(1, "voice", 900000, 1, null);
            bool observed = first is not null && await store.ReserveAsync(1, "voice", 200000, 1, null) is null;
            if (check == nameof(InProgressMediaCountsTowardCapacity))
            {
                return observed;
            }
            observed = (await store.SnapshotAsync()).Paused;
            if (check == nameof(QuotaPauseIsVisible))
            {
                return observed;
            }
            limit = 2; store.ConfigurationChanged();
            observed = await store.ReserveAsync(1, "voice", 1000, 1, null) is not null;
            if (check == nameof(RaisingLimitResumesBelowHysteresisThreshold))
            {
                return observed;
            }
            limit = 1;
            observed = await store.ReserveAsync(1, "voice", 200000, 1, null) is null && (await store.SnapshotAsync()).Bytes == 901000;
            if (check == nameof(LoweringLimitPreservesExistingMedia))
            {
                return observed;
            }
            await store.FailCaptureAsync(first!.Id, "Synthetic capture failed while saving");
            observed = (await store.SnapshotAsync(1)).Problems.Single().Error == "Synthetic capture failed while saving";
            if (check == nameof(QueueStatusPreservesTheCaptureFailureReason))
            {
                return observed;
            }
            await store.ManageAsync(first.Id, 1, true);
            return await store.ReserveAsync(1, "voice", 1000, 1, null) is not null;
        }
    }

    [Fact(DisplayName = "Completed capture recovers after process restart")]
    public async Task CompletedCaptureRecoversAfterProcessRestart()
    {
        Assert.True(await RecoveryAsync(nameof(CompletedCaptureRecoversAfterProcessRestart)));
    }

    [Fact(DisplayName = "Interrupted upload becomes eligible again")]
    public async Task InterruptedUploadBecomesEligibleAgain()
    {
        Assert.True(await RecoveryAsync(nameof(InterruptedUploadBecomesEligibleAgain)));
    }

    [Fact(DisplayName = "Delete follows durable delivery confirmation")]
    public async Task DeleteFollowsDurableDeliveryConfirmation()
    {
        Assert.True(await RecoveryAsync(nameof(DeleteFollowsDurableDeliveryConfirmation)));
    }

    private async Task<bool> RecoveryAsync(string check)
    {
        string recovered;
        using (var store = Store("recovery")) { recovered = (await Capture(store, commit: false)).Id; }
        using (var store = Store("recovery"))
        {
            var item = await store.ClaimAsync(true);
            bool observed = item?.Id == recovered && File.Exists(item.Path);
            if (check == nameof(CompletedCaptureRecoversAfterProcessRestart))
            {
                return observed;
            }
        }
        using (var store = Store("recovery"))
        {
            var item = await store.ClaimAsync(true);
            bool observed = item?.Id == recovered;
            if (check == nameof(InterruptedUploadBecomesEligibleAgain))
            {
                return observed;
            }
            await store.CompleteAsync(recovered);
            return !File.Exists(item!.Path) && (await store.SnapshotAsync()).Bytes == 0;
        }
    }

    [Fact(DisplayName = "Incomplete capture is quarantined")]
    public async Task IncompleteCaptureIsQuarantined()
    {
        using (var store = Store("incomplete"))
        {
            var r = await store.ReserveAsync(1, "voice", 100, 1, null);
            await File.WriteAllBytesAsync(r!.Path, new byte[50]);
        }
        using (var store = Store("incomplete"))
        {
            Assert.True(await store.ClaimAsync(true) is null && (await store.SnapshotAsync()).Failed == 1);
        }
    }

    [Fact(DisplayName = "Filesystem failure preserves capture and reservation")]
    public async Task FilesystemFailurePreservesCaptureAndReservation()
    {
        Assert.True(await LockedCaptureAsync(nameof(FilesystemFailurePreservesCaptureAndReservation)));
    }

    [Fact(DisplayName = "Capture can recover after filesystem failure")]
    public async Task CaptureCanRecoverAfterFilesystemFailure()
    {
        Assert.True(await LockedCaptureAsync(nameof(CaptureCanRecoverAfterFilesystemFailure)));
    }

    private async Task<bool> LockedCaptureAsync(string check)
    {
        using (var store = Store("locked-file"))
        {
            var capture = await Capture(store, commit: false);
            bool failed = false;
            await using (var locked = new FileStream(capture.Path, FileMode.Open, FileAccess.ReadWrite, FileShare.None))
            {
                try { await store.CommitCaptureAsync(capture); } catch (IOException) { failed = true; }
            }
            bool observed = failed && File.Exists(capture.Path) && (await store.SnapshotAsync()).Bytes > 0;
            if (check == nameof(FilesystemFailurePreservesCaptureAndReservation))
            {
                return observed;
            }
            await store.CommitCaptureAsync(capture);
            return (await store.ClaimAsync(true))?.Id == capture.Id;
        }
    }

    [Fact(DisplayName = "Quota hysteresis survives restart above 80 percent")]
    public async Task QuotaHysteresisSurvivesRestartAbove80Percent()
    {
        using (var store = Store("persisted-pause", () => 1))
        {
            await store.ReserveAsync(1, "voice", 900000, 1, null);
            await store.ReserveAsync(1, "voice", 200000, 1, null);
        }
        using (var store = Store("persisted-pause", () => 1))
            Assert.True(await store.ReserveAsync(1, "voice", 1000, 1, null) is null && (await store.SnapshotAsync()).Paused);
    }

}
