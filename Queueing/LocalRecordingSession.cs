namespace WinSystemHelper;

/// <summary>A visible local consent window. Closing it revokes the permission it granted.</summary>
public sealed class LocalRecordingSession : IAsyncDisposable
{
    private readonly CancellationTokenSource _stop;
    private readonly FileSystemWatcher _watcher;
    private readonly Task<uint> _window;
    private readonly string _directory;
    public CancellationToken Token { get; }

    private LocalRecordingSession(string directory, IWindowsSessionProcessRunner runner, CancellationToken token,
        WindowsConsoleSession? developmentConsole)
    {
        _directory = directory;
        _stop = CancellationTokenSource.CreateLinkedTokenSource(token);
        Token = _stop.Token;
        _watcher = new FileSystemWatcher(directory, "stop") { EnableRaisingEvents = true };
        _watcher.Created += (_, _) => Stop();
        _watcher.Error += (_, _) => Stop();
        string formSetup = developmentConsole is null ? "$form = New-Object Windows.Forms.Form" : PowerShellScripts.GetDevelopmentFormSetup();
        string title = developmentConsole is null ? "Microphone recording" : "Development mode";
        string prompt = developmentConsole is null
            ? "Microphone recording requested.`nAudio is saved locally and sent to your Telegram administrator.`nRecording continues offline until its storage limit."
            : "A Telegram administrator requested development mode.`nAllow this administrator to take screenshots, camera photos and audio without asking again while this window stays open? Captures are saved and sent to that Telegram chat, even after a network interruption.";
        string allow = developmentConsole is null ? "Allow recording" : "Allow development";
        string stopLabel = developmentConsole is null ? "Stop recording" : "End development mode";
        string active = developmentConsole is null
            ? "RECORDING SESSION ACTIVE`nRecording may pause while storage is full.`nStop or close this window to end the session."
            : "DEVELOPMENT MODE ACTIVE`nThis administrator can take screenshots, photos and audio without another prompt.`nStop or close this window to revoke permission.";
        string script = PowerShellScripts.BuildLocalConsentScript(directory, formSetup, title, prompt, allow, stopLabel, active);
        _window = runner.StartEncodedPowerShellAndWaitAsync(script, TimeSpan.FromDays(30), Token, developmentConsole);
        _ = ObserveWindowAsync();
    }

    private async Task ObserveWindowAsync()
    {
        try { await _window; }
        catch (Exception) { }
        finally { Stop(); }
    }

    private void Stop() { try { _stop.Cancel(); } catch (ObjectDisposedException) { } }

    public static async Task<LocalRecordingSession> StartAsync(string root, IWindowsSessionProcessRunner runner,
        CancellationToken token, WindowsConsoleSession? developmentConsole = null)
    {
        token.ThrowIfCancellationRequested();
        string directory = Path.Combine(root, "consent", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(directory);
        QueueStore.SecureDirectory(directory, developmentConsole?.UserSid ?? runner.TryGetActiveUserSid());
        var session = new LocalRecordingSession(directory, runner, token, developmentConsole);
        try
        {
            using var deadline = CancellationTokenSource.CreateLinkedTokenSource(session.Token);
            deadline.CancelAfter(TimeSpan.FromSeconds(65));
            while (!File.Exists(Path.Combine(directory, "allow"))) await Task.Delay(200, deadline.Token);
            session.Token.ThrowIfCancellationRequested();
            return session;
        }
        catch { await session.DisposeAsync(); throw; }
    }

    public async ValueTask DisposeAsync()
    {
        _watcher.Dispose();
        try { await File.WriteAllTextAsync(Path.Combine(_directory, "done"), "done"); }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { }
        try { await _window.WaitAsync(TimeSpan.FromSeconds(3)); }
        catch (Exception) { _stop.Cancel(); }
        try { await _window; } catch (Exception) { }
        _stop.Dispose();
        try { Directory.Delete(_directory, true); } catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { }
    }
}
