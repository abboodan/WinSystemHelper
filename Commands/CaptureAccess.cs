using static WinSystemHelper.PowerShellScripts;
using static WinSystemHelper.PowerShellExitCodes;

namespace WinSystemHelper;

internal sealed class CaptureAccess(
    CommandReply reply,
    QueueStore queueStore,
    IWindowsSessionProcessRunner sessions,
    DevelopmentMode developmentMode)
{

    public async Task<CaptureReservation?> ReserveCaptureAsync(string kind, long bytes, int duration, CancellationToken token,
        WindowsConsoleSession? console = null)
    {
        token.ThrowIfCancellationRequested();
        string? userSid = console?.UserSid ?? sessions.TryGetActiveUserSid();
        var reservation = await queueStore.ReserveAsync(reply.ChatId, kind, bytes, duration, userSid);
        if (reservation is null)
            await queueStore.EnqueueTextAsync(reply.ChatId, "Capture paused: queue capacity reached. Existing files are preserved. Check /queue.",
                dedupe: $"space:{reply.ChatId}:{QueueStore.Now / 3600000}", priority: true);
        return reservation;
    }

    public async Task<bool> RequestLocalCaptureConsentAsync(string description, CancellationToken token)
    {
        uint result = await sessions.StartEncodedPowerShellAndWaitAsync(
            BuildMessageBoxScript(description + "\nThe result will be saved locally and sent to your configured Telegram administrator. Allow?",
                "YesNo", "Question", true), TimeSpan.FromSeconds(60), token);
        return result == AskYesExitCode;
    }

    public DevelopmentGrant? GetDevelopmentGrant() =>
        CommandExecution.Current.Value is { SenderId: { } senderId } command
            ? developmentMode.GetGrant(reply.ChatId, senderId, command.ReceivedAt)
            : null;
}
