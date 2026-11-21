using System.Text.Json;
using Telegram.Bot.Types;

namespace WinSystemHelper;

internal sealed class CommandQueueConsumer(
    QueueStore store,
    CommandReply reply,
    RuntimeConfiguration runtime,
    TelegramCommandDispatcher dispatcher,
    ILogger<Worker> logger,
    IHostApplicationLifetime hostLifetime)
{
    private const int MaxOrdinaryBackgroundTails = 8;

    public async Task RunCommandQueueAsync(bool priority, CancellationToken token)
    {
        List<Task<bool>>? backgroundTails = priority ? null : new(MaxOrdinaryBackgroundTails);
        try
        {
            await ConsumeCommandsAsync(priority, backgroundTails, token);
        }
        finally
        {
            if (backgroundTails is not null)
            {
                await Task.WhenAll(backgroundTails);
            }
        }
    }

    private async Task ConsumeCommandsAsync(bool priority, List<Task<bool>>? backgroundTails, CancellationToken token)
    {
        QueueSignal signal = priority ? store.ControlChanged : store.CommandsChanged;
        while (!token.IsCancellationRequested)
        {
            StoredCommand? command = null;
            string? commandWord = null;
            CommandExecution? execution = null;

            bool TrackBackgroundTail(Exception? failure = null)
            {
                if (backgroundTails is null || command is null || execution is null || execution.BackgroundTasks.IsEmpty)
                {
                    return false;
                }

                backgroundTails.Add(FinishBackgroundTailAsync(command, commandWord, execution, token, failure));
                return true;
            }

            try
            {
                if (backgroundTails is { Count: > 0 })
                {
                    if (!await WaitForTailCapacityAsync(backgroundTails, token))
                    {
                        return;
                    }
                    token.ThrowIfCancellationRequested();
                }

                command = await store.ClaimCommandAsync(priority);
                if (command is null)
                {
                    await signal.WaitAsync(TimeSpan.FromMinutes(5), token);
                    continue;
                }
                execution = new CommandExecution(command.UpdateId, priority);
                CommandExecution.Current.Value = execution;
                reply.CurrentChatId = command.ChatId;
                (Update? update, string? text) = await PrepareCommandAsync(command, execution, token);
                if (update is null)
                {
                    continue;
                }

                // Only the command word is logged; arguments can contain private text.
                commandWord = update.CallbackQuery is not null ? "/menu" : text!.StartsWith('/')
                    ? text!.Split((char[]?)null, 2, StringSplitOptions.RemoveEmptyEntries)[0] : null;
                logger.LogInformation("Command {UpdateId} {Command} from chat {ChatId}.", command.UpdateId, commandWord ?? "(text)", command.ChatId);
                await dispatcher.HandleUpdateAsync(update, token);
                if (TrackBackgroundTail())
                {
                    continue;
                }

                // The control lane still waits for its own child work before claiming another command.
                while (execution.BackgroundTasks.TryDequeue(out Task? work))
                {
                    await work;
                }
                await store.FinishCommandAsync(command.UpdateId, StoredCommandState.Completed);
            }
            catch (OperationCanceledException ex) when (token.IsCancellationRequested)
            {
                TrackBackgroundTail(ex);
                break;
            }
            catch (Exception ex)
            {
                if (TrackBackgroundTail(ex))
                {
                    continue;
                }

                LogFailure(command, ex);
                if (command is null)
                {
                    await Task.Delay(TimeSpan.FromSeconds(5), token);
                }
                else if (!await PersistFailureAsync(command, commandWord, ex))
                {
                    return;
                }
            }
            finally
            {
                CommandExecution.Current.Value = null;
                reply.CurrentChatId = null;
            }
        }
    }

    private static async Task<bool> WaitForTailCapacityAsync(List<Task<bool>> backgroundTails, CancellationToken token)
    {
        for (int index = backgroundTails.Count - 1; index >= 0; index--)
        {
            Task<bool> tail = backgroundTails[index];
            if (tail.IsCompleted)
            {
                backgroundTails.RemoveAt(index);
                if (!await tail)
                {
                    return false;
                }
            }
        }

        if (backgroundTails.Count < MaxOrdinaryBackgroundTails)
        {
            return true;
        }

        Task<bool> finished = await Task.WhenAny(backgroundTails).WaitAsync(token);
        backgroundTails.Remove(finished);
        return await finished;
    }

    private async Task<bool> FinishBackgroundTailAsync(
        StoredCommand command, string? commandWord, CommandExecution execution, CancellationToken token, Exception? failure)
    {
        bool interrupted = failure is OperationCanceledException && token.IsCancellationRequested;
        while (execution.BackgroundTasks.TryDequeue(out Task? work))
        {
            try
            {
                await work;
            }
            catch (Exception ex)
            {
                if (failure is null)
                {
                    failure = ex;
                    interrupted = ex is OperationCanceledException && token.IsCancellationRequested;
                }
            }
        }

        if (interrupted)
        {
            // Startup recovery will handle the running row, as for an interrupted handler.
            return true;
        }

        if (failure is null)
        {
            try
            {
                await store.FinishCommandAsync(command.UpdateId, StoredCommandState.Completed);
                return true;
            }
            catch (OperationCanceledException) when (token.IsCancellationRequested)
            {
                return true;
            }
            catch (Exception ex)
            {
                failure = ex;
            }
        }

        LogFailure(command, failure);
        return await PersistFailureAsync(command, commandWord, failure);
    }

    private void LogFailure(StoredCommand? command, Exception exception)
    {
        logger.LogWarning(exception, "Command {UpdateId} failed ({ErrorType}); unconfirmed work is preserved.",
            command?.UpdateId, exception.GetType().Name);
    }

    private async Task<(Update? Update, string? Text)> PrepareCommandAsync(
        StoredCommand command, CommandExecution execution, CancellationToken token)
    {
        if (!runtime.IsAdminChatId(command.ChatId))
        {
            await store.FinishCommandAsync(command.UpdateId, StoredCommandState.Failed);
            return (null, null);
        }
        Update update = JsonSerializer.Deserialize<Update>(command.Payload) ?? throw new InvalidDataException("Invalid saved update.");
        execution.SenderId = update.CallbackQuery?.From?.Id ?? update.Message?.From?.Id;
        execution.ReceivedAt = command.Created;
        string text = (update.Message?.Text ?? update.Message?.Caption ?? "").Trim();
        if (CommandPolicy.IsExpired(text, command.Created, QueueStore.Now))
        {
            await reply.SendTelegramMessageOnceAsync($"⏳ Command expired. It was queued more than 5 minutes ago. Please send it again.\nUpdate {command.UpdateId}", token);
            await store.FinishCommandAsync(command.UpdateId, StoredCommandState.Expired);
            return (null, null);
        }
        return (update, text);
    }

    private async Task<bool> PersistFailureAsync(StoredCommand command, string? commandWord, Exception exception)
    {
        try
        {
            await store.FinishCommandAsync(command.UpdateId, command.ReadOnly ? StoredCommandState.Failed : StoredCommandState.Unknown);
            try
            {
                string report = TelegramReplies.CommandFailure(command.UpdateId, commandWord, command.ReadOnly, exception);
                await store.EnqueueTextAsync(command.ChatId, report,
                    TelegramReplyStyle.Html(TelegramCommandMenu.FailureKeyboard()), priority: true);
            }
            catch (QueueCapacityException)
            {
            }

            return true;
        }
        catch (Exception persistenceError)
        {
            logger.LogError("Cannot persist command outcome ({ErrorType}); stopping without discarding the queue.", persistenceError.GetType().Name);
            hostLifetime.StopApplication();
            return false;
        }
    }
}
