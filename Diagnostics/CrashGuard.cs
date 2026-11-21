namespace WinSystemHelper;

/// <summary>
/// Makes failures visible and recoverable.
///
/// By default a background service that throws is stopped "cleanly" by the host:
/// Windows sees a normal stop, never restarts the service, and nobody is told.
/// This guard records the reason, exits with a failure code so the Service Control
/// Manager's recovery actions apply, and reports the abnormal stop on the next start.
/// </summary>
public sealed class CrashGuard(
    FileLogStore log,
    QueueStore queue,
    AppConfiguration configuration,
    IProcessRunner processRunner,
    IServiceProvider services,
    ILogger<CrashGuard> logger) : BackgroundService
{
    // First failure: restart after 1 minute. Second: after 5 minutes. After that Windows
    // leaves the service stopped, so a build that crashes on start cannot loop forever.
    // The failure count resets after a day without failures.
    public const string RecoveryArguments =
        "failure " + ServiceConstants.ServiceName + " reset= 86400 actions= restart/60000/restart/300000//0";

    private bool _hasPendingNotices;

    /// <summary>Replaceable so tests can observe the exit instead of ending the test process.</summary>
    public Action<int> Exit { get; set; } = Environment.Exit;

    public override async Task StartAsync(CancellationToken cancellationToken)
    {
        logger.LogInformation(
            "Service starting. Version {Version}, process {ProcessId}.",
            typeof(CrashGuard).Assembly.GetName().Version, Environment.ProcessId);

        if (!log.IsAvailable)
        {
            logger.LogWarning("Private log storage is unavailable. File logging and crash notices are disabled.");
        }

        log.BeginRun();
        _hasPendingNotices = !await ReportPreviousRunsAsync();
        await base.StartAsync(cancellationToken);
    }

    public override async Task StopAsync(CancellationToken cancellationToken)
    {
        await base.StopAsync(cancellationToken);
        logger.LogInformation("Service stopped cleanly.");
        log.EndRunCleanly();
    }

    /// <summary>Returns true once all previous-run notices have been queued.</summary>
    public async Task<bool> ReportPreviousRunsAsync()
    {
        try
        {
            foreach (FileLogStore.UncleanRun run in log.ReadPreviousRuns())
            {
                string notice = string.Join(
                    Environment.NewLine,
                    $"⚠️ WinSystemHelper on {Environment.MachineName} did not shut down cleanly.",
                    $"Previous run started: {run.Started:yyyy-MM-dd HH:mm:ss zzz}",
                    run.Cause.Length == 0
                        ? "No error was recorded: the process was killed, or the machine lost power or was forced off."
                        : $"Recorded error: {Truncate(run.Cause, 600)}",
                    "The service is running again. Send /log for details.");

                await queue.EnqueueBroadcastAsync(
                    configuration.GetEffectiveAdminChatIds().ToArray(), notice, $"unclean-stop:{run.Id}", priority: true);
                run.Acknowledge();
                logger.LogWarning(
                    "Previous run (started {PreviousStart}) did not shut down cleanly. Crash notice queued.", run.Started);
            }

            return true;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or InvalidOperationException or Microsoft.Data.Sqlite.SqliteException)
        {
            logger.LogWarning(ex, "Could not finish reporting previous runs. Pending notices will be retried.");
            return false;
        }
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        await ConfigureRecoveryAsync(stoppingToken);

        // Registered last, so every other background service has already been started.
        (string Name, Task Task)[] watched = services.GetServices<IHostedService>()
            .OfType<BackgroundService>()
            .Where(service => !ReferenceEquals(service, this) && service.ExecuteTask is not null)
            .Select(service => (service.GetType().Name, service.ExecuteTask!))
            .ToArray();

        await Task.WhenAll(WatchAsync(watched, stoppingToken), RetryPreviousRunsAsync(stoppingToken));
    }

    private async Task RetryPreviousRunsAsync(CancellationToken stoppingToken)
    {
        if (!_hasPendingNotices)
        {
            return;
        }

        using var timer = new PeriodicTimer(TimeSpan.FromSeconds(5));
        while (await timer.WaitForNextTickAsync(stoppingToken))
        {
            if (await ReportPreviousRunsAsync())
            {
                return;
            }
        }
    }

    /// <summary>Waits until one of the tasks fails, then records the cause and exits with a failure code.</summary>
    public async Task WatchAsync(IReadOnlyCollection<(string Name, Task Task)> watched, CancellationToken stoppingToken)
    {
        var remaining = watched.ToList();
        var stopping = Task.Delay(Timeout.Infinite, stoppingToken);

        while (remaining.Count > 0)
        {
            Task finished = await Task.WhenAny(remaining.Select(item => item.Task).Append(stopping));
            if (ReferenceEquals(finished, stopping) || stoppingToken.IsCancellationRequested)
            {
                return;
            }

            (string name, Task task) = remaining.First(item => ReferenceEquals(item.Task, finished));
            remaining.RemoveAll(item => ReferenceEquals(item.Task, finished));

            if (!task.IsFaulted)
            {
                // Returning normally is a deliberate stop (for example invalid credentials).
                continue;
            }

            Exception cause = task.Exception!.GetBaseException();
            log.RecordCrash(name, cause);
            Exit(1);
            return;
        }
    }

    private async Task ConfigureRecoveryAsync(CancellationToken cancellationToken)
    {
        try
        {
            ProcessCaptureResult result = await processRunner.CaptureAsync(
                "sc.exe", RecoveryArguments, TimeSpan.FromSeconds(10), cancellationToken);

            if (result.ExitCode == 0)
            {
                logger.LogInformation("Automatic restart after failure is configured.");
            }
            else
            {
                logger.LogWarning("Could not configure automatic restart (sc.exe exit {ExitCode}).", result.ExitCode);
            }
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception ex)
        {
            logger.LogWarning(ex, "Could not configure automatic restart.");
        }
    }

    private static string Truncate(string value, int maxLength)
    {
        return value.Length <= maxLength ? value : value[..maxLength] + "...";
    }
}
