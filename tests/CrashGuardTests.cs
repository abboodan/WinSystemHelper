using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace WinSystemHelper.Tests;

public sealed class CrashGuardTests : TestWorkspace
{
    [Fact(DisplayName = "First start reports nothing")]
    public async Task FirstStartReportsNothing()
    {
        Assert.True(await CrashGuardAsync(nameof(FirstStartReportsNothing)));
    }

    [Fact(DisplayName = "Clean shutdown is not reported as a failure")]
    public async Task CleanShutdownIsNotReportedAsAFailure()
    {
        Assert.True(await CrashGuardAsync(nameof(CleanShutdownIsNotReportedAsAFailure)));
    }

    [Fact(DisplayName = "Failed background service exits with a failure code and is logged")]
    public async Task FailedBackgroundServiceExitsWithAFailureCodeAndIsLogged()
    {
        Assert.True(await CrashGuardAsync(nameof(FailedBackgroundServiceExitsWithAFailureCodeAndIsLogged)));
    }

    [Fact(DisplayName = "Next start detects the abnormal stop")]
    public async Task NextStartDetectsTheAbnormalStop()
    {
        Assert.True(await CrashGuardAsync(nameof(NextStartDetectsTheAbnormalStop)));
    }

    [Fact(DisplayName = "Abnormal stop notice carries the recorded cause")]
    public async Task AbnormalStopNoticeCarriesTheRecordedCause()
    {
        Assert.True(await CrashGuardAsync(nameof(AbnormalStopNoticeCarriesTheRecordedCause)));
    }

    [Fact(DisplayName = "Crash cause is reported once")]
    public async Task CrashCauseIsReportedOnce()
    {
        Assert.True(await CrashGuardAsync(nameof(CrashCauseIsReportedOnce)));
    }

    [Fact(DisplayName = "Deliberate stop and normal shutdown do not trigger a failure exit")]
    public async Task DeliberateStopAndNormalShutdownDoNotTriggerAFailureExit()
    {
        Assert.True(await CrashGuardAsync(nameof(DeliberateStopAndNormalShutdownDoNotTriggerAFailureExit)));
    }

    [Fact(DisplayName = "Automatic restart is bounded to two attempts per day")]
    public async Task AutomaticRestartIsBoundedToTwoAttemptsPerDay()
    {
        Assert.True(await CrashGuardAsync(nameof(AutomaticRestartIsBoundedToTwoAttemptsPerDay)));
    }

    [Fact(DisplayName = "Log command is read-only")]
    public async Task LogCommandIsReadOnly()
    {
        Assert.True(await CrashGuardAsync(nameof(LogCommandIsReadOnly)));
    }

    private async Task<bool> CrashGuardAsync(string check)
    {
        using (var store = Store("crash-guard"))
        {
            string logFolder = Path.Combine(TestRoot, "crash-logs");
            using var log = new FileLogStore(logFolder);
            var cfg = new AppConfiguration { AdminChatIds = [1] };
            var runner = new FakeProcessRunner();
            using var emptyServices = new ServiceCollection().BuildServiceProvider();
            var exits = new List<int>();
            using var guard = new CrashGuard(log, store, cfg, runner, emptyServices, NullLogger<CrashGuard>.Instance) { Exit = exits.Add };
            log.BeginRun();
            bool observed = await guard.ReportPreviousRunsAsync() && await store.ClaimAsync(false) is null;
            if (check == nameof(FirstStartReportsNothing))
            {
                return observed;
            }
            log.EndRunCleanly();
            log.BeginRun();
            observed = await guard.ReportPreviousRunsAsync() && await store.ClaimAsync(false) is null;
            if (check == nameof(CleanShutdownIsNotReportedAsAFailure))
            {
                return observed;
            }

            var failed = Task.FromException(new InvalidOperationException("synthetic crash"));
            await guard.WatchAsync([("Worker", failed)], CancellationToken.None);
            observed = exits.SequenceEqual([1]) && log.ReadTail(20, 3500).Contains("synthetic crash");
            if (check == nameof(FailedBackgroundServiceExitsWithAFailureCodeAndIsLogged))
            {
                return observed;
            }

            using var restartedLog = new FileLogStore(logFolder);
            using var restartedGuard = new CrashGuard(restartedLog, store, cfg, runner, emptyServices, NullLogger<CrashGuard>.Instance) { Exit = exits.Add };
            restartedLog.BeginRun();
            observed = await restartedGuard.ReportPreviousRunsAsync();
            if (check == nameof(NextStartDetectsTheAbnormalStop))
            {
                return observed;
            }
            string? notice = (await store.ClaimAsync(false))?.Text;
            observed = notice is not null && notice.Contains("did not shut down cleanly") && notice.Contains("synthetic crash") && notice.Contains("/log");
            if (check == nameof(AbnormalStopNoticeCarriesTheRecordedCause))
            {
                return observed;
            }
            observed = await restartedGuard.ReportPreviousRunsAsync() && await store.ClaimAsync(false) is null;
            if (check == nameof(CrashCauseIsReportedOnce))
            {
                return observed;
            }
            restartedLog.EndRunCleanly();

            exits.Clear();
            await guard.WatchAsync([("Worker", Task.CompletedTask)], CancellationToken.None);
            using var stopping = new CancellationTokenSource();
            Task watch = guard.WatchAsync([("Worker", Task.Delay(Timeout.Infinite, stopping.Token))], stopping.Token);
            stopping.Cancel();
            await watch;
            observed = exits.Count == 0;
            if (check == nameof(DeliberateStopAndNormalShutdownDoNotTriggerAFailureExit))
            {
                return observed;
            }
            observed = CrashGuard.RecoveryArguments.Contains("restart/60000/restart/300000//0") && CrashGuard.RecoveryArguments.Contains("reset= 86400");
            if (check == nameof(AutomaticRestartIsBoundedToTwoAttemptsPerDay))
            {
                return observed;
            }
            return CommandPolicy.IsReadOnly("/log") && CommandPolicy.IsReadOnly("/log file") && !CommandPolicy.IsExpired("/log", 0, 900000);
        }
    }

    [Fact(DisplayName = "A full outbox preserves old and new crash records across a clean restart")]
    public async Task AFullOutboxPreservesOldAndNewCrashRecordsAcrossACleanRestart()
    {
        Assert.True(await ReportRetryAsync(nameof(AFullOutboxPreservesOldAndNewCrashRecordsAcrossACleanRestart)));
    }

    [Fact(DisplayName = "Automatic retry queues the preserved crash causes once space is available")]
    public async Task AutomaticRetryQueuesThePreservedCrashCausesOnceSpaceIsAvailable()
    {
        Assert.True(await ReportRetryAsync(nameof(AutomaticRetryQueuesThePreservedCrashCausesOnceSpaceIsAvailable)));
    }

    private async Task<bool> ReportRetryAsync(string check)
    {
        using (var store = Store("crash-notice-retry"))
        {
            string folder = Path.Combine(TestRoot, "retry-crash-logs");
            string legacyMarker = Path.Combine(folder, "running.marker");
            string legacyCrash = Path.Combine(folder, "last-crash.txt");
            using (var failedRun = new FileLogStore(folder))
            {
                File.WriteAllText(legacyMarker, DateTimeOffset.Now.AddDays(-1).ToString("o"));
                File.WriteAllText(legacyCrash, "older synthetic crash");
                failedRun.BeginRun();
                failedRun.RecordCrash("Worker", new InvalidOperationException("newer synthetic crash"));
            }

            long expired = QueueStore.Now - 1;
            for (int i = 0; i < 2000; i++) await store.EnqueueTextAsync(1, "filler", expires: expired, priority: true);
            var cfg = new AppConfiguration { AdminChatIds = [1] };
            using var services = new ServiceCollection().BuildServiceProvider();
            var runner = new FakeProcessRunner();
            using (var nextRun = new FileLogStore(folder))
            using (var guard = new CrashGuard(nextRun, store, cfg, runner, services, NullLogger<CrashGuard>.Instance))
            {
                await guard.StartAsync(CancellationToken.None);
                await guard.StopAsync(CancellationToken.None);
            }

            bool observed = File.Exists(legacyMarker) && File.Exists(legacyCrash) && Directory.GetFiles(folder, "running-*.marker").Length == 1;
            if (check == nameof(AFullOutboxPreservesOldAndNewCrashRecordsAcrossACleanRestart))
            {
                return observed;
            }

            using var retryRun = new FileLogStore(folder);
            using var retryGuard = new CrashGuard(retryRun, store, cfg, runner, services, NullLogger<CrashGuard>.Instance);
            await retryGuard.StartAsync(CancellationToken.None);
            try
            {
                await store.ClaimAsync(false); // Drop the expired filler and free queue capacity.

                using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(15));
                while (File.Exists(legacyMarker) || Directory.GetFiles(folder, "running-*.marker").Length > 1)
                {
                    await Task.Delay(100, timeout.Token);
                }

                string?[] notices = [(await store.ClaimAsync(false))?.Text, (await store.ClaimAsync(false))?.Text];
                return !File.Exists(legacyCrash) && notices.Any(text => text?.Contains("older synthetic crash") == true) &&
                    notices.Any(text => text?.Contains("newer synthetic crash") == true) && await store.ClaimAsync(false) is null;
            }
            finally
            {
                await retryGuard.StopAsync(CancellationToken.None);
            }
        }
    }

    [Fact(DisplayName = "Hosted service failure reaches the guard through the real host")]
    public async Task HostedServiceFailureReachesTheGuardThroughTheRealHost()
    {
        Assert.True(await HostedFailureAsync(nameof(HostedServiceFailureReachesTheGuardThroughTheRealHost)));
    }

    [Fact(DisplayName = "Service recovery is configured at start")]
    public async Task ServiceRecoveryIsConfiguredAtStart()
    {
        Assert.True(await HostedFailureAsync(nameof(ServiceRecoveryIsConfiguredAtStart)));
    }

    private async Task<bool> HostedFailureAsync(string check)
    {
        using (var store = Store("host-guard"))
        {
            // The same wiring the service uses: a real host, failures not swallowed, guard registered last.
            using var log = new FileLogStore(Path.Combine(TestRoot, "host-logs"));
            var runner = new FakeProcessRunner();
            var exited = new TaskCompletionSource<int>(TaskCreationOptions.RunContinuationsAsynchronously);
            using IHost host = Host.CreateDefaultBuilder()
                .ConfigureLogging(logging => { logging.ClearProviders(); logging.AddProvider(new FileLoggerProvider(log)); })
                .ConfigureServices(services =>
                {
                    services.Configure<HostOptions>(options => options.BackgroundServiceExceptionBehavior = BackgroundServiceExceptionBehavior.Ignore);
                    services.AddHostedService<FailingService>();
                    services.AddHostedService(provider => new CrashGuard(log, store, new AppConfiguration { AdminChatIds = [1] }, runner, provider,
                        provider.GetRequiredService<ILogger<CrashGuard>>()) { Exit = code => exited.TrySetResult(code) });
                })
                .Build();
            await host.StartAsync();
            int exitCode = await exited.Task.WaitAsync(TimeSpan.FromSeconds(10));
            await host.StopAsync();
            bool observed = exitCode == 1 && log.ReadTail(50, 3500).Contains("synthetic host failure");
            if (check == nameof(HostedServiceFailureReachesTheGuardThroughTheRealHost))
            {
                return observed;
            }
            return runner.Calls.Count == 1 && runner.Calls[0].StartsWith("sc.exe failure WinSystemHelper");
        }
    }
}
