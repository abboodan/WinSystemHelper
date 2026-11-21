using System.Diagnostics;
using static WinSystemHelper.WindowsProcess;

namespace WinSystemHelper;

internal sealed class MicrophoneRecorder(
    ILogger<Worker> logger,
    CommandReply reply,
    QueueStore queueStore,
    IWindowsSessionProcessRunner sessions,
    CaptureAccess captureAccess,
    RuntimeConfiguration runtime) : IDisposable
{
    private readonly SemaphoreSlim _micRecordingLock = new(1, 1);

    public LocalRecordingSession? LocalRecording { get; set; }

    public void TriggerOvertRecordingWarnings()
    {
        sessions.StartProcess(
            "msg.exe",
            "* \"🚨 WinSystemHelper: audio recording is active. Files are saved locally and sent to your administrator.\"");

        sessions.StartWindowsPowerShell(
            PowerShellScripts.BuildInlineCommandArguments("RecordingWarning"));
    }

    public async Task<bool> IsAvailableAsync(CancellationToken cancellationToken)
    {
        if (!await _micRecordingLock.WaitAsync(0, cancellationToken))
        {
            return false;
        }

        _micRecordingLock.Release();
        return true;
    }

    public void Dispose() => _micRecordingLock.Dispose();

    private async Task RunMicRecorderHelperAsync(
        string outputPath,
        int durationSeconds,
        CancellationToken cancellationToken,
        WindowsConsoleSession? console)
    {
        string commandLine = string.Join(
            " ",
            "/recordmic-helper",
            $"/seconds {durationSeconds}",
            $"/out \"{outputPath}\"");

        uint exitCode = await sessions.StartProcessAndWaitAsync(
            GetCurrentExecutablePath(),
            commandLine,
            TimeSpan.FromSeconds(durationSeconds + 15),
            cancellationToken,
            console);

        if (exitCode != 0)
        {
            throw new MicRecorderFailure(MicRecorderFailure.Describe(exitCode));
        }

        FileInfo outputFile = new(outputPath);
        if (!outputFile.Exists || outputFile.Length == 0)
        {
            throw new MicRecorderFailure("The microphone recorder reported success without producing an audio file.");
        }
    }

    public async Task<bool> RecordAndSendMicAlarmAsync(int durationSeconds, CancellationToken cancellationToken,
        DevelopmentGrant? development = null)
    {
        if (!runtime.IsAdminChatId(reply.ChatId)) throw new OperationCanceledException("Recording administrator is no longer authorized.");
        if (!await _micRecordingLock.WaitAsync(0, cancellationToken)) return false;
        CaptureReservation? capture = null;
        LocalRecordingSession? oneShot = null;
        try
        {
            if (development is null && LocalRecording is null)
            {
                oneShot = await LocalRecordingSession.StartAsync(queueStore.Root, sessions, cancellationToken);
                TriggerOvertRecordingWarnings();
            }
            CancellationToken consent = development?.Token ?? (oneShot ?? LocalRecording)!.Token;
            using var active = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken, consent);
            capture = await captureAccess.ReserveCaptureAsync(QueueItemKind.Voice, durationSeconds * 32000L + 128, durationSeconds, active.Token, development?.Console);
            if (capture is null) return false;
            await RunMicRecorderHelperAsync(capture.Path, durationSeconds, active.Token, development?.Console);
            active.Token.ThrowIfCancellationRequested();
            await queueStore.CommitCaptureAsync(capture);
            return true;
        }
        catch (Exception ex)
        {
            if (capture is not null)
            {
                string reason = ex switch
                {
                    MicRecorderFailure failure => failure.Message,
                    System.ComponentModel.Win32Exception launchFailure =>
                        $"The microphone recorder could not start in the active Windows session (Win32 error {launchFailure.NativeErrorCode}).",
                    TimeoutException => "The microphone recorder helper did not finish before its timeout.",
                    OperationCanceledException => "Microphone recording was cancelled before completion.",
                    _ => "The microphone recording could not be completed. See /log for details."
                };
                logger.LogWarning("Microphone capture {CaptureId} failed. {Reason}", capture.Id, reason);
                await queueStore.FailCaptureAsync(capture.Id, reason);
            }
            throw;
        }
        finally
        {
            if (oneShot is not null) await oneShot.DisposeAsync();
            _micRecordingLock.Release();
        }
    }
}
