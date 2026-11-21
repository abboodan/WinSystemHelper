using System.ComponentModel;

namespace WinSystemHelper;

internal sealed class PowerCommandHandler(
    ILogger<Worker> logger,
    CommandReply reply,
    IWindowsSessionProcessRunner windowsSessionProcessRunner,
    ConfirmationService confirmations,
    PowerEventService events) : ICommandHandler
{
    public IReadOnlyList<string> Commands { get; } = ["/lock", "/shutdown", "/restart", "/sleep"];

    public Task HandleAsync(CommandRequest request, CancellationToken cancellationToken) => request.Word switch
    {
        "/lock" => LockWorkstationAsync(cancellationToken),
        "/shutdown" => confirmations.RequestDangerousConfirmationAsync(
            "shutdown", "Shut down this PC in 10 seconds", ShutdownAsync, cancellationToken),
        "/restart" => confirmations.RequestDangerousConfirmationAsync(
            "restart", "Restart this PC in 10 seconds", RestartAsync, cancellationToken),
        "/sleep" => confirmations.RequestDangerousConfirmationAsync(
            "sleep", "Put this PC to sleep", SleepAsync, cancellationToken),
        _ => Task.CompletedTask
    };

    private async Task LockWorkstationAsync(CancellationToken cancellationToken)
    {
        try
        {
            windowsSessionProcessRunner.StartProcess("rundll32.exe", "user32.dll,LockWorkStation");
            await reply.SendTelegramMessageOnceAsync("🔒 Workstation locked.", cancellationToken);
        }
        catch (Exception ex) when (ex is Win32Exception or InvalidOperationException)
        {
            logger.LogWarning(ex, "Unable to lock workstation from the active user session.");
            await reply.SendTelegramMessageOnceAsync(
                $"⚠️ Lock failed: {ex.Message}",
                cancellationToken);
        }
    }

    private async Task ShutdownAsync(CancellationToken cancellationToken)
    {
        await events.ExecutePowerCommandAsync(
            PowerEventKind.Shutdown,
            "🛑 Shutdown requested. The laptop will shut down in 10 seconds.",
            "shutdown.exe",
            "-s -t 10",
            cancellationToken);
    }

    private async Task RestartAsync(CancellationToken cancellationToken)
    {
        await events.ExecutePowerCommandAsync(
            PowerEventKind.Restart,
            "🔄 Restart requested. The laptop will restart in 10 seconds.",
            "shutdown.exe",
            "-r -t 10",
            cancellationToken);
    }

    private async Task SleepAsync(CancellationToken cancellationToken)
    {
        await events.ExecutePowerCommandAsync(
            PowerEventKind.Sleep,
            "🌙 Sleep requested. The laptop is entering sleep mode.",
            "rundll32.exe",
            "powrprof.dll,SetSuspendState 0,1,0",
            cancellationToken);
    }
}
