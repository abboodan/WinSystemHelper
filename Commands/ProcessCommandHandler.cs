using System.ComponentModel;
using System.Diagnostics;
using System.Text;
using static WinSystemHelper.CommandUtilities;
using static WinSystemHelper.PowerShellScripts;
using static WinSystemHelper.WindowsProcess;

namespace WinSystemHelper;

internal sealed class ProcessCommandHandler(
    ILogger<Worker> logger,
    CommandReply replies,
    IWindowsSessionProcessRunner sessions,
    CommandBackgroundWork backgroundWork,
    ConfirmationService confirmations,
    StartupApplications startup) : ICommandHandler
{
    public IReadOnlyList<string> Commands { get; } = ["/tasks", "/kill", "/startup", "/restartapp"];

    public Task HandleAsync(CommandRequest request, CancellationToken cancellationToken) => request.Word switch
    {
        "/tasks" => HandleTasksCommand(),
        "/kill" => HandleKillCommandRequestAsync(request.Text, cancellationToken),
        "/startup" => SendStartupAppsAsync(cancellationToken),
        "/restartapp" => HandleRestartAppCommandRequestAsync(request.Text, cancellationToken),
        _ => Task.CompletedTask
    };

    private Task HandleTasksCommand()
    {
        backgroundWork.QueueBackgroundWork(
            SendTopProcessesAsync,
            "Process list command failed.");
        return Task.CompletedTask;
    }

    private async Task HandleKillCommandRequestAsync(string commandText, CancellationToken cancellationToken)
    {
        string processName = GetCommandPayload(commandText);
        if (string.IsNullOrWhiteSpace(processName))
        {
            await replies.SendTelegramMessageOnceAsync("⚠️ Usage: /kill [ProcessName]", cancellationToken);
            return;
        }

        await confirmations.RequestDangerousConfirmationAsync(
            $"kill:{NormalizeProcessName(processName)}",
            $"Terminate process {NormalizeProcessName(processName)}",
            token => KillProcessesByNameAsync(processName, token),
            cancellationToken);
    }

    private async Task SendStartupAppsAsync(CancellationToken cancellationToken)
    {
        IReadOnlyList<StartupEntry> entries = startup.GetStartupEntries();

        if (entries.Count == 0)
        {
            await replies.SendTelegramMessageOnceAsync(
                "🚀 Startup applications: none found.",
                cancellationToken);
            return;
        }

        StringBuilder message = new();
        message.AppendLine("🚀 Startup applications:");
        message.AppendLine();

        foreach (StartupEntry entry in entries.OrderBy(entry => entry.Name, StringComparer.OrdinalIgnoreCase))
        {
            string line = $"[{entry.Name}] - {TruncateForTelegram(entry.Command, 260)}";
            if (message.Length + line.Length + Environment.NewLine.Length > 3900)
            {
                message.AppendLine("...output truncated.");
                break;
            }

            message.AppendLine(line);
        }

        await replies.SendTelegramMessageOnceAsync(message.ToString().TrimEnd(), cancellationToken);
    }

    private async Task HandleRestartAppCommandRequestAsync(string commandText, CancellationToken cancellationToken)
    {
        string requestedProcessName = GetCommandPayload(commandText);
        if (string.IsNullOrWhiteSpace(requestedProcessName))
        {
            await replies.SendTelegramMessageOnceAsync("⚠️ Usage: /restartapp [ProcessName]", cancellationToken);
            return;
        }

        await confirmations.RequestDangerousConfirmationAsync(
            $"restartapp:{NormalizeProcessName(requestedProcessName)}",
            $"Restart app {NormalizeProcessName(requestedProcessName)} in the active session",
            token => RestartApplicationAsync(requestedProcessName, token),
            cancellationToken);
    }

    private async Task RestartApplicationAsync(
        string requestedProcessName,
        CancellationToken cancellationToken)
    {
        string processName = NormalizeProcessName(requestedProcessName);
        if (string.IsNullOrWhiteSpace(processName))
        {
            await replies.SendTelegramMessageOnceAsync("⚠️ Usage: /restartapp [ProcessName]", cancellationToken);
            return;
        }

        Process[] processes = Process.GetProcessesByName(processName);
        try
        {
            AppLaunchInfo? launchInfo = TryGetLaunchInfoFromRunningProcesses(processes);
            bool wasRunning = processes.Length > 0;

            if (launchInfo is null)
            {
                launchInfo = startup.TryFindStartupLaunchInfo(processName, startup.GetStartupEntries());
            }

            if (launchInfo is null)
            {
                await replies.SendTelegramMessageOnceAsync(
                    $"⚠️ Could not determine a launch path for {processName}.",
                    cancellationToken);
                return;
            }

            if (wasRunning)
            {
                int terminatedCount = await TerminateProcesses(processes, processName, cancellationToken);
                if (terminatedCount == 0)
                {
                    await replies.SendTelegramMessageOnceAsync(
                        $"⚠️ {processName} was found, but no matching process could be terminated.",
                        cancellationToken);
                    return;
                }
            }

            cancellationToken.ThrowIfCancellationRequested();
            sessions.StartEncodedPowerShell(BuildLaunchApplicationScript(launchInfo.Value));

            string reply = wasRunning
                ? $"🔄 {processName} was killed and restarted successfully in the active session."
                : $"🔄 {processName} was not running, but was started successfully in the active session.";

            await replies.SendTelegramMessageOnceAsync(reply, cancellationToken);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception ex)
        {
            logger.LogWarning(ex, "Application restart command failed for {ProcessName}.", processName);
            await replies.SendTelegramMessageOnceAsync(
                $"⚠️ Restart failed for {processName}: {ex.Message}",
                CancellationToken.None);
        }
        finally
        {
            foreach (Process process in processes)
            {
                process.Dispose();
            }
        }
    }

    private AppLaunchInfo? TryGetLaunchInfoFromRunningProcesses(IEnumerable<Process> processes)
    {
        foreach (Process process in processes)
        {
            try
            {
                string? filePath = process.MainModule?.FileName;
                if (!string.IsNullOrWhiteSpace(filePath))
                {
                    return new AppLaunchInfo(filePath, Arguments: string.Empty);
                }
            }
            catch (Exception ex) when (ex is Win32Exception or InvalidOperationException or NotSupportedException)
            {
                logger.LogDebug(
                    ex,
                    "Unable to read executable path for process {ProcessId}.",
                    SafeProcessId(process));
            }
        }

        return null;
    }

    private async Task<int> TerminateProcesses(
        IEnumerable<Process> processes, string processName, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var terminatedCount = 0;

        foreach (Process process in processes)
        {
            try
            {
                if (process.HasExited)
                {
                    continue;
                }

                cancellationToken.ThrowIfCancellationRequested();
                process.Kill(entireProcessTree: true);

                using var exitTimeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
                exitTimeout.CancelAfter(TimeSpan.FromSeconds(10));
                try
                {
                    await process.WaitForExitAsync(exitTimeout.Token);
                }
                catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
                {
                    logger.LogWarning(
                        "Process {ProcessName} with PID {ProcessId} did not exit before timeout.",
                        processName,
                        SafeProcessId(process));
                    continue;
                }

                terminatedCount++;
            }
            catch (Exception ex) when (ex is Win32Exception or InvalidOperationException or NotSupportedException)
            {
                logger.LogWarning(
                    ex,
                    "Failed to terminate process {ProcessName} with PID {ProcessId}.",
                    processName,
                    SafeProcessId(process));
            }
        }

        return terminatedCount;
    }

    private async Task SendTopProcessesAsync(CancellationToken cancellationToken)
    {
        try
        {
            string[] lines = Process.GetProcesses()
                .Select(process =>
                {
                    try
                    {
                        return new
                        {
                            process.ProcessName,
                            process.Id,
                            process.WorkingSet64
                        };
                    }
                    catch
                    {
                        return null;
                    }
                    finally
                    {
                        process.Dispose();
                    }
                })
                .Where(process => process is not null)
                .OrderByDescending(process => process!.WorkingSet64)
                .Take(10)
                .Select((process, index) =>
                    $"{index + 1}. {process!.ProcessName} | PID {process.Id} | {process.WorkingSet64 / 1024 / 1024:N0} MB")
                .ToArray();

            string message = string.Join(
                Environment.NewLine,
                ["📋 Top memory-consuming processes:", .. lines]);

            await replies.SendTelegramMessageOnceAsync(message, cancellationToken);
        }
        catch (Exception ex)
        {
            logger.LogWarning(ex, "Process list command failed.");
            await replies.SendTelegramMessageOnceAsync($"⚠️ Task list failed: {ex.Message}", CancellationToken.None);
        }
    }

    private async Task KillProcessesByNameAsync(string requestedProcessName, CancellationToken cancellationToken)
    {
        string processName = NormalizeProcessName(requestedProcessName);
        if (string.IsNullOrWhiteSpace(processName))
        {
            await replies.SendTelegramMessageOnceAsync("⚠️ Usage: /kill [ProcessName]", cancellationToken);
            return;
        }

        var matchedProcessCount = 0;
        var terminatedProcessCount = 0;

        foreach (Process process in Process.GetProcesses())
        {
            try
            {
                if (!process.ProcessName.Equals(processName, StringComparison.OrdinalIgnoreCase))
                {
                    continue;
                }

                matchedProcessCount++;
                process.Kill();
                terminatedProcessCount++;
            }
            catch (Exception ex) when (ex is Win32Exception or InvalidOperationException or NotSupportedException)
            {
                logger.LogWarning(ex, "Failed to terminate process {ProcessName}.", processName);
            }
            finally
            {
                process.Dispose();
            }
        }

        if (matchedProcessCount == 0)
        {
            await replies.SendTelegramMessageOnceAsync(
                $"⚠️ Process {processName} not found.",
                cancellationToken);
            return;
        }

        if (terminatedProcessCount == 0)
        {
            await replies.SendTelegramMessageOnceAsync(
                $"⚠️ Process {processName} was found but could not be terminated.",
                cancellationToken);
            return;
        }

        await replies.SendTelegramMessageOnceAsync(
            $"🔪 Process {processName} terminated.",
            cancellationToken);
    }

    private static int SafeProcessId(Process process)
    {
        try
        {
            return process.Id;
        }
        catch
        {
            return 0;
        }
    }
}
