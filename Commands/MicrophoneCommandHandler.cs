using System.Security;
using static WinSystemHelper.CommandUtilities;

namespace WinSystemHelper;

internal sealed class MicrophoneCommandHandler(
    ILogger<Worker> logger,
    CommandReply reply,
    QueueStore queueStore,
    IWindowsSessionProcessRunner sessions,
    MicrophoneRecorder recorder,
    CaptureAccess captureAccess,
    ServiceFiles files,
    PersistentMicLoopFile stateFile,
    RuntimeConfiguration runtime,
    CooldownService cooldown,
    CancellationTokenSource serviceStopping) : ICommandHandler, IDisposable
{
    public IReadOnlyList<string> Commands { get; } = ["/mic"];

    public Task LoopTask => _micLoopTask;

    public Task HandleAsync(CommandRequest request, CancellationToken cancellationToken)
    {
        return HandleMicCommandAsync(request.Text, cancellationToken);
    }

    public void Dispose() => recorder.Dispose();

    private const int DefaultMicRecordingSeconds = 10;

    private const int MaxMicRecordingSeconds = 60;

    private readonly object _micLoopSync = new();

    private CancellationTokenSource? _micLoopCts;

    private DateTimeOffset? _micLoopStartedAt;

    private bool _micLoopLongAlertSent;

    private async Task HandleMicCommandAsync(string commandText, CancellationToken cancellationToken)
    {
        MicCommand micCommand = ParseMicCommand(commandText);

        if (micCommand.Stop)
        {
            await StopMicAlarmLoopAsync(cancellationToken);
            return;
        }

        if (cooldown.TryGetRemaining("mic", GetMicCooldown(), out TimeSpan remaining))
        {
            await reply.SendTelegramMessageOnceAsync(
                $"⏳ Mic command cooldown active. Try again in {FormatDurationForHumans(remaining)}.",
                cancellationToken);
            return;
        }

        cooldown.Mark("mic");

        if (micCommand.Loop)
        {
            await StartMicAlarmLoopAsync(micCommand.DurationSeconds, cancellationToken);
            return;
        }

        await StartMicAlarmRecordingAsync(micCommand.DurationSeconds, cancellationToken);
    }

    private async Task StartMicAlarmRecordingAsync(int durationSeconds, CancellationToken cancellationToken)
    {
        if (_micLoopCts is not null)
        { await reply.SendTelegramMessageOnceAsync("Recording loop already active. Use /mic stop first.", cancellationToken); return; }

        if (!await recorder.IsAvailableAsync(cancellationToken))
        {
            await reply.SendTelegramMessageOnceAsync("🎤 Active Alarm recording is already running.", cancellationToken);
            return;
        }

        DevelopmentGrant? development = captureAccess.GetDevelopmentGrant();
        CancellationTokenSource? recording = null;
        bool loopActive;
        lock (_micLoopSync)
        {
            loopActive = _micLoopCts is not null;
            if (!loopActive && _singleRecordingCts is null)
            {
                recording = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken, development?.Token ?? CancellationToken.None);
                _singleRecordingCts = recording;
            }
        }

        if (recording is null)
        {
            await reply.SendTelegramMessageOnceAsync(
                loopActive ? "Recording loop already active. Use /mic stop first." : "🎤 Active Alarm recording is already running.",
                cancellationToken);
            return;
        }

        try
        {
            await reply.SendTelegramMessageOnceAsync(
                development is null
                    ? $"🎤 Recording requested ({durationSeconds}s); awaiting local consent."
                    : $"🎤 Recording requested ({durationSeconds}s) under the approved development session.", recording.Token);
        }
        catch
        {
            ReleaseSingleRecording(recording);
            throw;
        }

        await CommandBackgroundWork.TrackStartedWork(RecordOneShotAsync(durationSeconds, recording, development));
    }

    private async Task RecordOneShotAsync(int durationSeconds, CancellationTokenSource recording, DevelopmentGrant? development)
    {
        try
        {
            await recorder.RecordAndSendMicAlarmAsync(durationSeconds, recording.Token, development);
        }
        finally
        {
            ReleaseSingleRecording(recording);
        }
    }

    private void ReleaseSingleRecording(CancellationTokenSource recording)
    {
        lock (_micLoopSync)
        {
            if (ReferenceEquals(_singleRecordingCts, recording))
            {
                _singleRecordingCts = null;
            }
        }
        recording.Dispose();
    }

    private async Task StartMicAlarmLoopAsync(int durationSeconds, CancellationToken cancellationToken)
    {
        DevelopmentGrant? development = captureAccess.GetDevelopmentGrant();
        await StartMicAlarmLoopCoreAsync(
            durationSeconds,
            persistState: development is null,
            restoredFromStartup: false,
            cancellationToken,
            development);
    }

    private async Task StartMicAlarmLoopCoreAsync(
        int durationSeconds,
        bool persistState,
        bool restoredFromStartup,
        CancellationToken cancellationToken,
        DevelopmentGrant? development = null)
    {
        CancellationTokenSource? loopCts = TryCreateLoop(development, out bool singleRecordingActive);
        if (loopCts is null)
        {
            await reply.SendTelegramMessageOnceAsync(
                singleRecordingActive
                    ? "🎤 Active Alarm recording is already running."
                    : "⚠️ Active Alarm loop is already running! Send /mic stop first.",
                cancellationToken);
            return;
        }

        try
        {
            if (development is null)
            {
                recorder.LocalRecording = await LocalRecordingSession.StartAsync(queueStore.Root, sessions, loopCts.Token);
                recorder.TriggerOvertRecordingWarnings();
            }

            CancellationToken consent = development?.Token ?? recorder.LocalRecording!.Token;
            loopCts.Token.ThrowIfCancellationRequested();
            if (persistState && GetPersistentMicLoopEnabled())
            {
                await SavePersistentMicLoopStateAsync(durationSeconds, loopCts.Token);
            }

            await SendLoopStartedAsync(durationSeconds, restoredFromStartup, cancellationToken);

            _micLoopTask = Task.Run(
                async () =>
                {
                    CommandExecution.Current.Value = null;
                    await RunMicAlarmLoopAsync(durationSeconds, loopCts, consent, development);
                },
                CancellationToken.None);
        }
        catch
        {
            loopCts.Cancel();
            if (recorder.LocalRecording is not null) { await recorder.LocalRecording.DisposeAsync(); recorder.LocalRecording = null; }
            stateFile.DeletePersistentMicLoopState();
            lock (_micLoopSync)
            {
                if (ReferenceEquals(_micLoopCts, loopCts))
                {
                    _micLoopCts = null;
                    _micLoopStartedAt = null;
                }
            }

            loopCts.Cancel();
            loopCts.Dispose();
            throw;
        }
    }

    private Task SendLoopStartedAsync(int durationSeconds, bool restoredFromStartup, CancellationToken cancellationToken)
    {
        string message = restoredFromStartup
            ? $"🎤 Persistent Active Alarm restored after startup. Recording loop resumed ({durationSeconds}s cycles)."
            : $"🎤 Persistent Active Alarm loop started ({durationSeconds}s cycles).";
        return reply.SendTelegramMessageOnceAsync(message, cancellationToken);
    }

    private CancellationTokenSource? TryCreateLoop(DevelopmentGrant? development, out bool singleRecordingActive)
    {
        CancellationTokenSource? loopCts = null;

        lock (_micLoopSync)
        {
            singleRecordingActive = _singleRecordingCts is not null;
            if (_micLoopCts is null && !singleRecordingActive)
            {
                loopCts = CancellationTokenSource.CreateLinkedTokenSource(serviceStopping.Token,
                    development?.Token ?? CancellationToken.None);
                _micLoopCts = loopCts;
                _micLoopStartedAt = DateTimeOffset.Now;
                _micLoopLongAlertSent = false;
            }
        }

        return loopCts;
    }

    private async Task StopMicAlarmLoopAsync(CancellationToken cancellationToken)
    {
        CancelMicLoop();
        stateFile.DeletePersistentMicLoopState();
        if (CommandExecution.Current.Value is { } execution)
            await queueStore.CancelOlderRecordingCommandsAsync(execution.Id);

        await reply.SendTelegramMessageOnceAsync(
            "🛑 Recording stopped. Saved files remain queued for delivery.",
            cancellationToken);
    }

    public async Task RestorePersistentMicLoopAsync(CancellationToken cancellationToken)
    {
        if (!GetPersistentMicLoopEnabled())
        {
            return;
        }

        stateFile.MigrateLegacyState();
        PersistentMicLoopState? state = await stateFile.ReadPersistentMicLoopStateAsync(cancellationToken);
        if (state is null || !state.Active)
        {
            return;
        }

        if (!runtime.IsAdminChatId(state.RequestedByAdminChatId)) { stateFile.DeletePersistentMicLoopState(); return; }
        reply.CurrentChatId = state.RequestedByAdminChatId;
        int durationSeconds = Math.Clamp(
            state.DurationSeconds <= 0 ? DefaultMicRecordingSeconds : state.DurationSeconds,
            1,
            MaxMicRecordingSeconds);

        await StartMicAlarmLoopCoreAsync(
            durationSeconds,
            persistState: false,
            restoredFromStartup: true,
            cancellationToken);
    }

    private async Task SavePersistentMicLoopStateAsync(
        int durationSeconds,
        CancellationToken cancellationToken)
    {
        try
        {
            PersistentMicLoopState state = new()
            {
                Active = true,
                DurationSeconds = Math.Clamp(durationSeconds, 1, MaxMicRecordingSeconds),
                StartedAt = DateTimeOffset.Now,
                RequestedByAdminChatId = reply.ChatId
            };

            (string tempPath, string statePath) = await stateFile.WriteTemporaryAsync(state, cancellationToken);

            lock (_micLoopSync)
            {
                if (_micLoopCts is null || _micLoopCts.IsCancellationRequested || cancellationToken.IsCancellationRequested)
                { files.DeleteTempFile(tempPath); cancellationToken.ThrowIfCancellationRequested(); return; }
                File.Move(tempPath, statePath, overwrite: true);
            }
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or SecurityException)
        {
            logger.LogWarning(ex, "Failed to save persistent mic loop state.");
        }
    }

    private async Task RunMicAlarmLoopAsync(int durationSeconds, CancellationTokenSource loopCts, CancellationToken consent,
        DevelopmentGrant? development)
    {
        try
        {
            while (!loopCts.Token.IsCancellationRequested)
            {
                try
                {
                    if (consent.IsCancellationRequested || !runtime.IsAdminChatId(reply.ChatId))
                    { stateFile.DeletePersistentMicLoopState(); break; }
                    using var active = CancellationTokenSource.CreateLinkedTokenSource(loopCts.Token, consent);
                    bool cycleCompleted = await recorder.RecordAndSendMicAlarmAsync(durationSeconds, active.Token, development);
                    if (!cycleCompleted)
                    {
                        if (queueStore.Paused)
                            await queueStore.SpaceChanged.WaitAsync(TimeSpan.FromMinutes(5), active.Token);
                        else await Task.Delay(TimeSpan.FromSeconds(2), active.Token);
                    }
                }
                catch (OperationCanceledException) when (loopCts.IsCancellationRequested || consent.IsCancellationRequested)
                {
                    if (!loopCts.IsCancellationRequested) stateFile.DeletePersistentMicLoopState();
                    break;
                }
                catch (Exception ex)
                {
                    logger.LogWarning(ex, "Persistent active alarm loop cycle failed.");

                    try
                    {
                        await reply.SendTelegramMessageOnceAsync(
                            $"⚠️ Persistent Active Alarm cycle failed: {ex.Message}",
                            CancellationToken.None);
                    }
                    catch (Exception sendException)
                    {
                        logger.LogWarning(sendException, "Failed to report persistent active alarm loop failure.");
                    }

                    stateFile.DeletePersistentMicLoopState();
                    break;
                }
            }
        }
        finally
        {
            if (recorder.LocalRecording is not null) { await recorder.LocalRecording.DisposeAsync(); recorder.LocalRecording = null; }
            lock (_micLoopSync)
            {
                if (ReferenceEquals(_micLoopCts, loopCts))
                {
                    _micLoopCts = null;
                    _micLoopStartedAt = null;
                }
            }

            loopCts.Dispose();
        }
    }

    private static MicCommand ParseMicCommand(string commandText)
    {
        string[] parts = commandText.Split(' ', StringSplitOptions.RemoveEmptyEntries);
        var durationSeconds = DefaultMicRecordingSeconds;
        var loop = false;

        foreach (string part in parts.Skip(1))
        {
            if (part.Equals("stop", StringComparison.OrdinalIgnoreCase))
            {
                return new MicCommand(durationSeconds, Loop: false, Stop: true);
            }

            if (part.Equals("loop", StringComparison.OrdinalIgnoreCase))
            {
                loop = true;
                continue;
            }

            if (int.TryParse(part, out int requestedSeconds))
            {
                durationSeconds = Math.Clamp(requestedSeconds, 1, MaxMicRecordingSeconds);
            }
        }

        return new MicCommand(durationSeconds, loop, Stop: false);
    }

    private TimeSpan GetMicCooldown()
    {
        return TimeSpan.FromSeconds(Math.Clamp(runtime.Current.MicCooldownSeconds, 0, 3600));
    }

    private bool GetPersistentMicLoopEnabled()
    {
        return runtime.Current.PersistentMicLoopEnabled;
    }

    public CancellationTokenSource? CancelMicLoop()
    {
        lock (_micLoopSync)
        {
            CancellationTokenSource? loopCts = _micLoopCts;
            _micLoopLongAlertSent = false;
            _singleRecordingCts?.Cancel();
            loopCts?.Cancel();
            return loopCts;
        }
    }

    public string GetMicLoopStatus()
    {
        lock (_micLoopSync)
        {
            return _micLoopCts is null ? "Inactive" : "Active";
        }
    }

    private Task _micLoopTask = Task.CompletedTask;

    private CancellationTokenSource? _singleRecordingCts;

    public async Task CheckMicLoopAlertAsync(CancellationToken cancellationToken)
    {
        DateTimeOffset? startedAt;
        bool alreadySent;

        lock (_micLoopSync)
        {
            startedAt = _micLoopStartedAt;
            alreadySent = _micLoopLongAlertSent;
            if (startedAt.HasValue &&
                !alreadySent &&
                DateTimeOffset.Now - startedAt.Value >= TimeSpan.FromMinutes(30))
            {
                _micLoopLongAlertSent = true;
            }
            else
            {
                return;
            }
        }

        await reply.SendTelegramBroadcastOnceAsync(
            $"🎤 Persistent Active Alarm has been running since {startedAt.Value:yyyy-MM-dd HH:mm:ss zzz}.",
            cancellationToken);
    }
}
