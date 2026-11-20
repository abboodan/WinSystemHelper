using System.Text.Json;
using Telegram.Bot.Types;

namespace WinSystemHelper;

public sealed partial class Worker
{
    private readonly QueueStore _queueStore;
    private Task[] _commandConsumers = [];
    private bool _queueWasOffline;
    private const long ImageReservationBytes = 10 * 1024 * 1024;
    private LocalRecordingSession? _localRecording;
    private Task _micLoopTask = Task.CompletedTask;
    private CancellationTokenSource? _singleRecordingCts;

    private async Task RunCommandQueueAsync(bool priority, CancellationToken token)
    {
        QueueSignal signal = priority ? _queueStore.ControlChanged : _queueStore.CommandsChanged;
        while (!token.IsCancellationRequested)
        {
            StoredCommand? command = null;
            try
            {
                command = await _queueStore.ClaimCommandAsync(priority);
                if (command is null) { await signal.WaitAsync(TimeSpan.FromMinutes(5), token); continue; }
                var execution = new CommandExecution(command.UpdateId, priority);
                CommandExecution.Current.Value = execution;
                _replyChatId.Value = command.ChatId;
                if (!IsAdminChatId(command.ChatId))
                { await _queueStore.FinishCommandAsync(command.UpdateId, "failed"); continue; }
                Update update = JsonSerializer.Deserialize<Update>(command.Payload) ?? throw new InvalidDataException("Invalid saved update.");
                string text = (update.Message?.Text ?? update.Message?.Caption ?? "").Trim();
                if (CommandPolicy.IsExpired(text, command.Created, QueueStore.Now))
                {
                    await SendTelegramMessageOnceAsync($"Command {command.UpdateId} expired (older than 5 minutes). Please send it again.", token);
                    await _queueStore.FinishCommandAsync(command.UpdateId, "expired");
                    continue;
                }
                await HandleUpdateAsync(update, token);
                // A command may schedule child work. Drain it before declaring completion.
                while (execution.BackgroundTasks.TryDequeue(out Task? work)) await work;
                await _queueStore.FinishCommandAsync(command.UpdateId, "completed");
            }
            catch (OperationCanceledException) when (token.IsCancellationRequested) { break; }
            catch (Exception ex)
            {
                _logger.LogWarning("Command queue failure ({ErrorType}); unconfirmed work is preserved.", ex.GetType().Name);
                if (command is not null)
                {
                    try
                    {
                        await _queueStore.FinishCommandAsync(command.UpdateId, command.ReadOnly ? "failed" : "unknown");
                        try { await _queueStore.EnqueueTextAsync(command.ChatId, $"Command {command.UpdateId} did not complete normally; effects may already have occurred. It will not be replayed automatically. Check /queue.", priority: true); }
                        catch (QueueCapacityException) { }
                    }
                    catch (Exception persistenceError)
                    {
                        _logger.LogError("Cannot persist command outcome ({ErrorType}); stopping without discarding the queue.", persistenceError.GetType().Name);
                        _hostLifetime.StopApplication();
                        return;
                    }
                }
                else await Task.Delay(TimeSpan.FromSeconds(5), token);
            }
            finally { CommandExecution.Current.Value = null; _replyChatId.Value = null; }
        }
    }

    private async Task HandleQueueCommandAsync(string text, CancellationToken token)
    {
        string[] parts = text.Split(' ', StringSplitOptions.RemoveEmptyEntries);
        long chat = GetReplyChatId();
        if (parts.Length == 1)
        {
            QueueSnapshot status = await _queueStore.SnapshotAsync(chat);
            string report = $"Queue: {status.Bytes / 1048576d:F2}/{status.Limit / 1048576} MiB\n" +
                $"Pending: {status.Pending}; failed: {status.Failed}; commands: {status.Commands}\n" +
                $"Capture: {(status.Paused ? "paused for space; resumes below 80%" : "available")}\n" +
                string.Join('\n', status.Problems.Select(i => $"{i.Id}: {i.Error}"));
            await SendTelegramMessageOnceAsync(report, token);
            return;
        }
        if (parts.Length != 3 || !Guid.TryParseExact(parts[2], "N", out _) || parts[1] is not ("retry" or "delete"))
        { await SendTelegramMessageOnceAsync("Usage: /queue | /queue retry [Id] | /queue delete [Id]", token); return; }
        string id = parts[2];
        if (parts[1] == "delete")
        {
            await RequestDangerousConfirmationAsync($"queue-delete:{id}", $"Delete queued item {id}", async ct =>
            {
                bool removed = await _queueStore.ManageAsync(id, chat, true);
                await SendTelegramMessageOnceAsync(removed ? "Queued item deleted." : "Item unavailable or currently sending.", ct);
            }, token);
        }
        else
        {
            bool changed = await _queueStore.ManageAsync(id, chat, false);
            await SendTelegramMessageOnceAsync(changed ? "Item queued for another delivery attempt." : "Item unavailable, or capture was incomplete.", token);
        }
    }

    private async Task<CaptureReservation?> ReserveCaptureAsync(string kind, long bytes, int duration, CancellationToken token)
    {
        token.ThrowIfCancellationRequested();
        var reservation = await _queueStore.ReserveAsync(GetReplyChatId(), kind, bytes, duration, _windowsSessionProcessRunner.TryGetActiveUserSid());
        if (reservation is null)
            await _queueStore.EnqueueTextAsync(GetReplyChatId(), "Capture paused: queue capacity reached. Existing files are preserved. Check /queue.",
                dedupe: $"space:{GetReplyChatId()}:{QueueStore.Now / 3600000}", priority: true);
        return reservation;
    }

    private async Task<bool> RequestLocalCaptureConsentAsync(string description, CancellationToken token)
    {
        uint result = await _windowsSessionProcessRunner.StartEncodedPowerShellAndWaitAsync(
            BuildMessageBoxScript(description + "\nThe result will be saved locally and sent to your configured Telegram administrator. Allow?",
                "YesNo", "Question", true), TimeSpan.FromSeconds(60), token);
        return result == AskYesExitCode;
    }

    private async Task ImportLegacyNoticeAsync(string path, string text, CancellationToken token)
    {
        token.ThrowIfCancellationRequested();
        string identity = Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(System.Text.Encoding.UTF8.GetBytes(path + text)));
        foreach (long chat in GetAdminChatIdsSnapshot())
            await _queueStore.EnqueueTextAsync(chat, text, dedupe: $"legacy:{identity}:{chat}");
        DeleteTempFile(path);
    }

    private async Task<bool> QueuePowerReportAsync(PowerEventReport report, CancellationToken token)
    {
        token.ThrowIfCancellationRequested();
        foreach (long chat in GetAdminChatIdsSnapshot())
            await _queueStore.EnqueueTextAsync(chat, report.Message, dedupe: $"power:{report.Id}:{chat}");
        return true;
    }

    private async Task CaptureImageAsync(bool camera, CancellationToken token)
    {
        if (!await RequestLocalCaptureConsentAsync(camera ? "Camera photo requested." : "Screenshot requested.", token))
        { await SendTelegramMessageOnceAsync("Capture declined locally.", token); return; }
        CaptureReservation? capture = await ReserveCaptureAsync("photo", ImageReservationBytes, 0, token);
        if (capture is null) return;
        try
        {
            if (camera) TriggerOvertCameraWarning();
            string script = camera ? BuildCameraCaptureScript(capture.Path, capture.Path + ".error") : BuildScreenshotScript(capture.Path);
            uint code = await _windowsSessionProcessRunner.StartEncodedPowerShellAndWaitAsync(script,
                camera ? CameraCaptureTimeout : ScreenshotTimeout, token);
            if (code != 0) throw new IOException("Image capture did not complete.");
            await _queueStore.CommitCaptureAsync(capture);
        }
        catch
        {
            await _queueStore.FailCaptureAsync(capture.Id);
            throw;
        }
    }

    private async Task<bool> RecordAndSendMicAlarmAsync(int durationSeconds, CancellationToken cancellationToken)
    {
        if (!IsAdminChatId(GetReplyChatId())) throw new OperationCanceledException("Recording administrator is no longer authorized.");
        if (!await _micRecordingLock.WaitAsync(0, cancellationToken)) return false;
        CaptureReservation? capture = null;
        LocalRecordingSession? oneShot = null;
        try
        {
            if (_localRecording is null)
            {
                oneShot = await LocalRecordingSession.StartAsync(_queueStore.Root, _windowsSessionProcessRunner, cancellationToken);
                TriggerOvertRecordingWarnings();
            }
            using var active = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken, (oneShot ?? _localRecording)!.Token);
            capture = await ReserveCaptureAsync("voice", durationSeconds * 32000L + 128, durationSeconds, active.Token);
            if (capture is null) return false;
            await RunMicRecorderHelperAsync(capture.Path, durationSeconds, active.Token);
            await _queueStore.CommitCaptureAsync(capture);
            return true;
        }
        catch
        {
            if (capture is not null) await _queueStore.FailCaptureAsync(capture.Id);
            throw;
        }
        finally
        {
            if (oneShot is not null) await oneShot.DisposeAsync();
            _micRecordingLock.Release();
        }
    }
}
