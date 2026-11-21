using System.ComponentModel;
using System.Text;
using static WinSystemHelper.CommandUtilities;
using static WinSystemHelper.PowerShellExitCodes;
using static WinSystemHelper.PowerShellScripts;

namespace WinSystemHelper;

internal sealed class InteractionCommandHandler(
    ILogger<Worker> logger,
    CommandReply reply,
    IWindowsSessionProcessRunner sessions,
    ServiceFiles files,
    CommandBackgroundWork backgroundWork) : ICommandHandler
{
    public IReadOnlyList<string> Commands { get; } = ["/alarm", "/msg", "/ask", "/prompt", "/speak"];

    public Task HandleAsync(CommandRequest request, CancellationToken cancellationToken) => request.Word switch
    {
        "/alarm" => TriggerAlarmAsync(cancellationToken),
        "/msg" => HandleScreenMessageCommandAsync(request.Text, cancellationToken),
        "/ask" => HandleAskCommandAsync(request.Text, cancellationToken),
        "/prompt" => HandlePromptCommandAsync(request.Text, cancellationToken),
        "/speak" => HandleSpeakCommandAsync(request.Text, cancellationToken),
        _ => Task.CompletedTask
    };

    private static readonly TimeSpan AskPromptTimeout = TimeSpan.FromSeconds(60);

    private static readonly TimeSpan TextPromptTimeout = TimeSpan.FromSeconds(120);

    private async Task TriggerAlarmAsync(CancellationToken cancellationToken)
    {
        try
        {
            TriggerPowerShellBeep();

            await reply.SendTelegramMessageOnceAsync("🚨 Scare alarm triggered via PowerShell!", cancellationToken);
        }
        catch (Exception ex) when (ex is Win32Exception or InvalidOperationException)
        {
            logger.LogWarning(ex, "Unable to trigger alarm from the active user session.");
            await reply.SendTelegramMessageOnceAsync(
                $"⚠️ Alarm failed: {ex.Message}",
                cancellationToken);
        }
    }

    private async Task HandleScreenMessageCommandAsync(string commandText, CancellationToken cancellationToken)
    {
        string message = GetCommandPayload(commandText);
        if (string.IsNullOrWhiteSpace(message))
        {
            await reply.SendTelegramMessageOnceAsync("⚠️ Usage: /msg [text]", cancellationToken);
            return;
        }

        await reply.SendTelegramMessageOnceAsync("💬 Message sent to the screen.", cancellationToken);

        backgroundWork.QueueBackgroundWork(
            _ =>
            {
                ShowScreenMessage(message);
                return Task.CompletedTask;
            },
            "Screen message failed.");
    }

    private async Task HandleAskCommandAsync(string commandText, CancellationToken cancellationToken)
    {
        string question = GetCommandPayload(commandText);
        if (string.IsNullOrWhiteSpace(question))
        {
            await reply.SendTelegramMessageOnceAsync("⚠️ Usage: /ask [text]", cancellationToken);
            return;
        }

        backgroundWork.QueueBackgroundWork(
            token => AskActiveUserAsync(question, token),
            "Interactive Yes/No prompt failed.");
    }

    private async Task HandlePromptCommandAsync(string commandText, CancellationToken cancellationToken)
    {
        string prompt = GetCommandPayload(commandText);
        if (string.IsNullOrWhiteSpace(prompt))
        {
            await reply.SendTelegramMessageOnceAsync("⚠️ Usage: /prompt [text]", cancellationToken);
            return;
        }

        backgroundWork.QueueBackgroundWork(
            token => PromptActiveUserAsync(prompt, token),
            "Interactive text prompt failed.");
    }

    private async Task HandleSpeakCommandAsync(string commandText, CancellationToken cancellationToken)
    {
        string message = GetCommandPayload(commandText);
        if (string.IsNullOrWhiteSpace(message))
        {
            await reply.SendTelegramMessageOnceAsync("⚠️ Usage: /speak [text]", cancellationToken);
            return;
        }

        backgroundWork.QueueBackgroundWork(
            token => SpeakInActiveUserSessionAsync(message, token),
            "Text-to-speech command failed.");
    }

    private void ShowScreenMessage(string message)
    {
        string script = BuildMessageBoxScript(
            message,
            buttons: "OK",
            icon: "Warning",
            includeAskExitCodes: false);

        sessions.StartEncodedPowerShell(script);
    }

    private async Task AskActiveUserAsync(string question, CancellationToken cancellationToken)
    {
        try
        {
            string script = BuildMessageBoxScript(
                question,
                buttons: "YesNo",
                icon: "Question",
                includeAskExitCodes: true);

            uint exitCode = await sessions.StartEncodedPowerShellAndWaitAsync(
                script,
                AskPromptTimeout,
                cancellationToken);

            string answer = exitCode switch
            {
                AskYesExitCode => "YES",
                AskNoExitCode => "NO",
                _ => $"UNKNOWN (ExitCode {exitCode})"
            };

            await reply.SendTelegramMessageOnceAsync($"🗣️ User answered: {answer}", cancellationToken);
        }
        catch (System.TimeoutException)
        {
            await reply.SendTelegramBroadcastOnceAsync("⏳ User ignored the prompt (Timeout).", CancellationToken.None);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
        }
        catch (Exception ex)
        {
            logger.LogWarning(ex, "Interactive Yes/No prompt failed.");
            await reply.SendTelegramMessageOnceAsync($"⚠️ Ask prompt failed: {ex.Message}", CancellationToken.None);
        }
    }

    private async Task PromptActiveUserAsync(string prompt, CancellationToken cancellationToken)
    {
        string? tempFile = null;
        string? promptDirectory = null;

        try
        {
            // The dialog runs as the signed-in user, so only this one-off folder is
            // opened to that user; the rest of the service storage stays private.
            promptDirectory = Path.Combine(files.GetSharedTempDirectory("Interaction"), Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(promptDirectory);
            QueueStore.SecureDirectory(promptDirectory, sessions.TryGetActiveUserSid());
            tempFile = Path.Combine(promptDirectory, "response.txt");

            string script = BuildInputBoxScript(prompt, tempFile);

            _ = await sessions.StartEncodedPowerShellAndWaitAsync(
                script,
                TextPromptTimeout,
                cancellationToken);

            if (!File.Exists(tempFile))
            {
                await reply.SendTelegramMessageOnceAsync("⚠️ User prompt returned no response file.", cancellationToken);
                return;
            }

            string response = await ServiceFiles.ReadUntrustedTextAsync(tempFile, cancellationToken);
            await reply.SendTelegramMessageOnceAsync($"📝 User replied: {response}", cancellationToken);
        }
        catch (System.TimeoutException)
        {
            await reply.SendTelegramBroadcastOnceAsync("⏳ User ignored the prompt (Timeout).", CancellationToken.None);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
        }
        catch (Exception ex)
        {
            logger.LogWarning(ex, "Interactive text prompt failed.");
            await reply.SendTelegramMessageOnceAsync($"⚠️ Text prompt failed: {ex.Message}", CancellationToken.None);
        }
        finally
        {
            files.DeleteTempFile(tempFile);
            files.DeleteDirectorySafe(promptDirectory);
        }
    }

    /// <summary>Reads a bounded amount of text from a file an ordinary user could have written.</summary>

    private async Task SpeakInActiveUserSessionAsync(string message, CancellationToken cancellationToken)
    {
        try
        {
            sessions.StartEncodedPowerShell(BuildSpeakScript(message));
            await reply.SendTelegramMessageOnceAsync("🗣️ Audio message played.", cancellationToken);
        }
        catch (Exception ex) when (ex is Win32Exception or InvalidOperationException)
        {
            logger.LogWarning(ex, "Text-to-speech command failed.");
            await reply.SendTelegramMessageOnceAsync($"⚠️ Speak command failed: {ex.Message}", CancellationToken.None);
        }
    }

    private void TriggerPowerShellBeep()
    {
        sessions.StartWindowsPowerShell(
            PowerShellScripts.BuildInlineCommandArguments("AlarmBeep"));
    }
}
