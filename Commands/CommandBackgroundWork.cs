namespace WinSystemHelper;

internal sealed class CommandBackgroundWork(ILogger<Worker> logger, CancellationTokenSource serviceStopping)
{
    public static Task TrackStartedWork(Task work)
    {
        if (CommandExecution.Current.Value is not { } execution)
        {
            return work;
        }

        // The caller starts the work in handler order so it can register cancellation before yielding.
        execution.BackgroundTasks.Enqueue(work);
        return Task.CompletedTask;
    }

    public void QueueBackgroundWork(
        Func<CancellationToken, Task> operation,
        string failureMessage,
        Action? onCompleted = null,
        bool waitForCompletion = true)
    {
        if (waitForCompletion && CommandExecution.Current.Value is { } execution)
        {
            execution.BackgroundTasks.Enqueue(Start(operation, onCompleted));
            return;
        }
        try
        {
            _ = Start(async token =>
            {
                try
                {
                    await operation(token);
                }
                catch (OperationCanceledException) when (serviceStopping.IsCancellationRequested)
                {
                }
                catch (Exception ex)
                {
                    logger.LogError(ex, failureMessage);
                }
            }, onCompleted);
        }
        catch (Exception ex)
        {
            logger.LogError(ex, failureMessage);
        }
    }

    private Task Start(Func<CancellationToken, Task> operation, Action? onCompleted)
    {
        Task work;
        try
        {
            CancellationToken token = serviceStopping.Token;
            work = Task.Run(() => operation(token), token);
        }
        catch
        {
            onCompleted?.Invoke();
            throw;
        }

        if (onCompleted is null)
        {
            return work;
        }

        // The delegate can be skipped if cancellation wins before it starts.
        return work.ContinueWith(completed =>
        {
            onCompleted();
            return completed;
        }, CancellationToken.None, TaskContinuationOptions.ExecuteSynchronously, TaskScheduler.Default).Unwrap();
    }
}
