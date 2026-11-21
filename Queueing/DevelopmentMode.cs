namespace WinSystemHelper;

public sealed record DevelopmentGrant(
    long ChatId, long SenderId, WindowsConsoleSession Console, long ApprovedAt, CancellationToken Token);

/// <summary>Holds the permission granted by the local development consent window.</summary>
public sealed class DevelopmentMode(string root, IWindowsSessionProcessRunner runner, Func<long, bool> isAdmin)
{
    private readonly object _sync = new();
    private CancellationTokenSource? _lifetime;
    private DevelopmentGrant? _grant;
    private int _lastProductionCommandId = int.MinValue;

    public string Name
    {
        get
        {
            lock (_sync) return _grant is { Token.IsCancellationRequested: false } ? "development" : "production";
        }
    }

    public DevelopmentGrant? GetGrant(long chatId, long senderId, long receivedAt)
    {
        DevelopmentGrant? grant;
        lock (_sync) grant = _grant;
        return grant is not null && grant.ChatId == chatId && grant.SenderId == senderId &&
            receivedAt >= grant.ApprovedAt && IsUsable(grant) ? grant : null;
    }

    public async Task<bool> EnableAsync(int commandId, long chatId, long senderId, CancellationToken cancellationToken)
    {
        if (!isAdmin(chatId) || !isAdmin(senderId) || runner.TryGetActiveConsoleSession() is not { } console)
        {
            return false;
        }

        CancellationTokenSource lifetime;
        lock (_sync)
        {
            if (_lifetime is not null || commandId <= _lastProductionCommandId)
            {
                return false;
            }

            lifetime = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            _lifetime = lifetime;
        }

        LocalRecordingSession? window = null;
        bool activated = false;
        try
        {
            window = await LocalRecordingSession.StartAsync(root, runner, lifetime.Token, console);
            var grant = new DevelopmentGrant(chatId, senderId, console, QueueStore.Now, window.Token);
            lock (_sync)
            {
                if (!ReferenceEquals(_lifetime, lifetime) || !IsUsable(grant))
                {
                    return false;
                }

                _grant = grant;
                activated = true;
            }

            _ = ObserveWindowAsync(window, grant, lifetime);
            return true;
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            return false;
        }
        finally
        {
            if (!activated)
            {
                Release(lifetime);
                if (window is not null) await window.DisposeAsync();
            }
        }
    }

    public void Disable(int? commandId = null)
    {
        lock (_sync)
        {
            if (commandId is int id)
            {
                _lastProductionCommandId = Math.Max(_lastProductionCommandId, id);
            }

            _grant = null;
            _lifetime?.Cancel();
            _lifetime = null;
        }
    }

    private bool IsUsable(DevelopmentGrant grant) => !grant.Token.IsCancellationRequested &&
        isAdmin(grant.ChatId) && isAdmin(grant.SenderId) && runner.TryGetActiveConsoleSession() == grant.Console;

    private async Task ObserveWindowAsync(LocalRecordingSession window, DevelopmentGrant grant, CancellationTokenSource lifetime)
    {
        try
        {
            while (IsUsable(grant)) await Task.Delay(TimeSpan.FromSeconds(1), grant.Token);
        }
        catch (OperationCanceledException) when (grant.Token.IsCancellationRequested) { }
        finally
        {
            Release(lifetime);
            await window.DisposeAsync();
        }
    }

    private void Release(CancellationTokenSource lifetime)
    {
        lock (_sync)
        {
            if (ReferenceEquals(_lifetime, lifetime))
            {
                _grant = null;
                _lifetime = null;
            }

            lifetime.Cancel();
            lifetime.Dispose();
        }
    }
}
