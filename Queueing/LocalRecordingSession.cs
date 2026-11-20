using System.Text;

namespace WinSystemHelper;

/// <summary>A visible, locally approved recording session. Closing the window cancels recording.</summary>
public sealed class LocalRecordingSession : IAsyncDisposable
{
    private readonly CancellationTokenSource _stop;
    private readonly FileSystemWatcher _watcher;
    private readonly Task<uint> _window;
    private readonly string _directory;
    public CancellationToken Token => _stop.Token;

    private LocalRecordingSession(string directory, IWindowsSessionProcessRunner runner, CancellationToken token)
    {
        _directory = directory;
        _stop = CancellationTokenSource.CreateLinkedTokenSource(token);
        _watcher = new FileSystemWatcher(directory, "stop") { EnableRaisingEvents = true };
        _watcher.Created += (_, _) => Stop();
        _watcher.Error += (_, _) => Stop();
        string encoded = Convert.ToBase64String(Encoding.UTF8.GetBytes(directory));
        string script = $$"""
            Add-Type -AssemblyName System.Windows.Forms
            Add-Type -AssemblyName System.Drawing
            $dir = [Text.Encoding]::UTF8.GetString([Convert]::FromBase64String('{{encoded}}'))
            $form = New-Object Windows.Forms.Form
            $form.Text = 'WinSystemHelper - Microphone recording'
            $form.Size = New-Object Drawing.Size(470,200)
            $form.TopMost = $true
            $form.StartPosition = 'CenterScreen'
            $label = New-Object Windows.Forms.Label
            $label.Text = "Microphone recording requested.`nAudio is saved locally and sent to your Telegram administrator.`nRecording continues offline until its storage limit."
            $label.SetBounds(15,15,425,65)
            $start = New-Object Windows.Forms.Button
            $start.Text = 'Allow recording'
            $start.SetBounds(20,100,160,35)
            $stop = New-Object Windows.Forms.Button
            $stop.Text = 'Stop / Decline'
            $stop.SetBounds(240,100,160,35)
            $start.Add_Click({
                [IO.File]::WriteAllText((Join-Path $dir 'allow'),'yes')
                $start.Enabled = $false
                $label.Text = "RECORDING SESSION ACTIVE`nRecording may pause while storage is full.`nStop or close this window to end the session."
            })
            $stop.Add_Click({ $form.Close() })
            $form.Add_FormClosing({ [IO.File]::WriteAllText((Join-Path $dir 'stop'),'stop') })
            $timer = New-Object Windows.Forms.Timer
            $timer.Interval = 1000
            $deadline = [DateTime]::UtcNow.AddSeconds(60)
            $timer.Add_Tick({
                if ((Test-Path -LiteralPath (Join-Path $dir 'done')) -or ($start.Enabled -and [DateTime]::UtcNow -gt $deadline)) { $form.Close() }
            })
            $form.Controls.AddRange(@($label,$start,$stop))
            $timer.Start()
            [Windows.Forms.Application]::Run($form)
            $timer.Dispose()
            $form.Dispose()
            """;
        _window = runner.StartEncodedPowerShellAndWaitAsync(script, TimeSpan.FromDays(30), _stop.Token);
        _ = ObserveWindowAsync();
    }

    private async Task ObserveWindowAsync()
    {
        try { await _window; }
        catch (Exception) { }
        finally { Stop(); }
    }

    private void Stop() { try { _stop.Cancel(); } catch (ObjectDisposedException) { } }

    public static async Task<LocalRecordingSession> StartAsync(string root, IWindowsSessionProcessRunner runner, CancellationToken token)
    {
        string directory = Path.Combine(root, "consent", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(directory);
        QueueStore.SecureDirectory(directory, runner.TryGetActiveUserSid());
        var session = new LocalRecordingSession(directory, runner, token);
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
        catch (IOException) { }
        try { await _window.WaitAsync(TimeSpan.FromSeconds(3)); }
        catch (Exception) { _stop.Cancel(); }
        try { await _window; } catch (Exception) { }
        _stop.Dispose();
        try { Directory.Delete(_directory, true); } catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { }
    }
}
