using System.Text;
using static WinSystemHelper.CommandUtilities;
using static WinSystemHelper.PowerShellExitCodes;
using static WinSystemHelper.PowerShellScripts;

namespace WinSystemHelper;

internal sealed class ImageCommandHandler(
    CommandReply reply,
    QueueStore queueStore,
    IWindowsSessionProcessRunner sessions,
    CaptureAccess captureAccess,
    CommandBackgroundWork backgroundWork) : ICommandHandler, IDisposable
{
    public IReadOnlyList<string> Commands { get; } = ["/screen", "/cam", "/camera"];

    public Task HandleAsync(CommandRequest request, CancellationToken cancellationToken) => request.Word switch
    {
        "/screen" => HandleScreenshotCommandAsync(cancellationToken),
        "/cam" or "/camera" => HandleCameraCommandAsync(cancellationToken),
        _ => Task.CompletedTask
    };

    public void Dispose()
    {
        _screenshotLock.Dispose();
        _cameraLock.Dispose();
    }

    private readonly SemaphoreSlim _cameraLock = new(1, 1);

    private readonly SemaphoreSlim _screenshotLock = new(1, 1);

    private static readonly TimeSpan CameraCaptureTimeout = TimeSpan.FromSeconds(25);

    private static readonly TimeSpan ScreenshotTimeout = TimeSpan.FromSeconds(30);

    private async Task HandleScreenshotCommandAsync(CancellationToken cancellationToken)
    {
        if (!await _screenshotLock.WaitAsync(0, cancellationToken))
        {
            await reply.SendTelegramMessageOnceAsync(
                "⏳ Screenshot capture is still in progress. Ignoring this request.",
                cancellationToken);
            return;
        }

        bool acknowledged = false;
        try
        {
            await reply.SendTelegramMessageOnceAsync(
                "🖼️ Screenshot capture started. I will send the image when it is ready.",
                cancellationToken);
            acknowledged = true;
        }
        finally
        {
            if (!acknowledged)
            {
                _screenshotLock.Release();
            }
        }

        backgroundWork.QueueBackgroundWork(
            CaptureAndSendScreenshotAsync,
            "Screenshot command failed.",
            () => _screenshotLock.Release());
    }

    private async Task HandleCameraCommandAsync(CancellationToken cancellationToken)
    {
        if (!await _cameraLock.WaitAsync(0, cancellationToken))
        {
            await reply.SendTelegramMessageOnceAsync(
                "⏳ Camera capture is still in progress. Ignoring this request.",
                cancellationToken);
            return;
        }

        bool acknowledged = false;
        try
        {
            await reply.SendTelegramMessageOnceAsync(
                captureAccess.GetDevelopmentGrant() is null
                    ? "📷 Camera capture requested. Notifying the local user first."
                    : "📷 Camera capture requested under the approved development session.",
                cancellationToken);
            acknowledged = true;
        }
        finally
        {
            if (!acknowledged)
            {
                _cameraLock.Release();
            }
        }

        backgroundWork.QueueBackgroundWork(
            CaptureAndSendCameraAsync,
            "Camera command failed.",
            () => _cameraLock.Release());
    }

    private Task CaptureAndSendScreenshotAsync(CancellationToken token) => CaptureImageAsync(false, token);

    private Task CaptureAndSendCameraAsync(CancellationToken token) => CaptureImageAsync(true, token);

    private static async Task<string> ReadCameraFailureReasonAsync(
        string? errorFile,
        CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(errorFile) || !File.Exists(errorFile))
        {
            return "No detailed error was returned by the camera script.";
        }

        string reason = await File.ReadAllTextAsync(errorFile, Encoding.UTF8, cancellationToken);
        return string.IsNullOrWhiteSpace(reason)
            ? "No detailed error was returned by the camera script."
            : TruncateForTelegram(NormalizeWhitespace(reason), 900);
    }

    private void TriggerOvertCameraWarning()
    {
        sessions.StartProcess(
            "msg.exe",
            "* \"📷 WinSystemHelper notice: camera capture is now ACTIVE and will be transmitted to the administrator.\"");

        sessions.StartWindowsPowerShell(
            PowerShellScripts.BuildInlineCommandArguments("CameraWarning"));
    }

    private const long ImageReservationBytes = 10 * 1024 * 1024;

    private async Task CaptureImageAsync(bool camera, CancellationToken token)
    {
        DevelopmentGrant? development = captureAccess.GetDevelopmentGrant();
        if (development is null && !await captureAccess.RequestLocalCaptureConsentAsync(camera ? "Camera photo requested." : "Screenshot requested.", token))
        { await reply.SendTelegramMessageOnceAsync("Capture declined locally.", token); return; }

        using var permission = CancellationTokenSource.CreateLinkedTokenSource(token, development?.Token ?? CancellationToken.None);
        CaptureReservation? capture = await captureAccess.ReserveCaptureAsync(QueueItemKind.Photo, ImageReservationBytes, 0, permission.Token, development?.Console);
        if (capture is null) return;
        string? failure = null;
        try
        {
            if (camera && development is null) TriggerOvertCameraWarning();
            string script = camera ? BuildCameraCaptureScript(capture.Path) : BuildScreenshotScript(capture.Path);
            uint code = await sessions.StartEncodedPowerShellAndWaitAsync(script,
                camera ? CameraCaptureTimeout : ScreenshotTimeout, permission.Token, development?.Console);
            if (code != 0)
            {
                failure = camera ? code switch
                {
                    CameraInitializationFailedExitCode => "The camera could not be initialized. Check Windows camera access and whether another app is using it.",
                    CameraCaptureFailedExitCode => "The camera opened, but the photo could not be captured.",
                    CameraSaveFailedExitCode => "The photo could not be saved to the local capture queue.",
                    _ => $"Camera helper exited unexpectedly (code {code})."
                } : "Screenshot capture did not complete.";
                throw new IOException(failure);
            }
            permission.Token.ThrowIfCancellationRequested();
            await queueStore.CommitCaptureAsync(capture);
        }
        catch
        {
            await queueStore.FailCaptureAsync(capture.Id, failure);
            throw;
        }
    }
}
