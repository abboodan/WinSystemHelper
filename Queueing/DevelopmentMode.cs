using System.Text;

namespace WinSystemHelper;

public sealed record DevelopmentGrant(
    long ChatId, long SenderId, WindowsConsoleSession Console, long ApprovedAt, CancellationToken Token);

/// <summary>Holds the permission granted by development mode.</summary>
public sealed class DevelopmentMode(string root, IWindowsSessionProcessRunner runner, Func<long, bool> isAdmin)
{
    private readonly object _sync = new();
    private CancellationTokenSource? _lifetime;
    private DevelopmentGrant? _grant;
    private int _lastProductionCommandId = int.MinValue;
    private readonly string _statePath = Path.Combine(root, "development-mode.state");
    private bool _initialized;

    public string Name
    {
        get
        {
            EnsureInitialized();
            lock (_sync) return _grant is { Token.IsCancellationRequested: false } ? "development" : "production";
        }
    }

    public DevelopmentGrant? GetGrant(long chatId, long senderId, long receivedAt)
    {
        EnsureInitialized();
        lock (_sync)
        {
            if (_grant is null || _grant.ChatId != chatId || _grant.SenderId != senderId ||
                receivedAt < _grant.ApprovedAt || _grant.Token.IsCancellationRequested ||
                !isAdmin(chatId) || !isAdmin(senderId))
            {
                return null;
            }

            WindowsConsoleSession? currentConsole = runner.TryGetActiveConsoleSession();
            if (currentConsole is not null && _grant.Console != currentConsole)
            {
                _grant = _grant with { Console = currentConsole };
            }

            return _grant;
        }
    }

    public async Task<bool> EnableAsync(int commandId, long chatId, long senderId, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        EnsureInitialized();
        if (!isAdmin(chatId) || !isAdmin(senderId))
        {
            return false;
        }

        lock (_sync)
        {
            if ((_lifetime is not null && !_lifetime.IsCancellationRequested) || commandId <= _lastProductionCommandId)
            {
                return false;
            }

            var lifetime = new CancellationTokenSource();
            _lifetime = lifetime;

            WindowsConsoleSession console = runner.TryGetActiveConsoleSession() ?? new WindowsConsoleSession(0, string.Empty);
            var grant = new DevelopmentGrant(chatId, senderId, console, QueueStore.Now, lifetime.Token);
            _grant = grant;

            try
            {
                Directory.CreateDirectory(root);
                File.WriteAllText(_statePath, $"{chatId}:{senderId}:{grant.ApprovedAt}", new UTF8Encoding(false));
            }
            catch
            {
            }
        }

        return await Task.FromResult(true);
    }

    public void Shutdown()
    {
        lock (_sync)
        {
            _grant = null;
            _lifetime?.Cancel();
            _lifetime?.Dispose();
            _lifetime = null;
        }
    }

    public void Disable(int? commandId = null)
    {
        EnsureInitialized();
        lock (_sync)
        {
            if (commandId is int id)
            {
                _lastProductionCommandId = Math.Max(_lastProductionCommandId, id);
            }

            _grant = null;
            _lifetime?.Cancel();
            _lifetime?.Dispose();
            _lifetime = null;

            try
            {
                if (File.Exists(_statePath))
                {
                    File.Delete(_statePath);
                }
            }
            catch
            {
            }
        }
    }

    private void EnsureInitialized()
    {
        if (_initialized)
        {
            return;
        }

        lock (_sync)
        {
            if (_initialized)
            {
                return;
            }

            _initialized = true;
            TryRestorePersistedState();
        }
    }

    private void TryRestorePersistedState()
    {
        if (!File.Exists(_statePath))
        {
            return;
        }

        try
        {
            string text = File.ReadAllText(_statePath, Encoding.UTF8);
            string[] parts = text.Split(':', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
            if (parts.Length >= 3 &&
                long.TryParse(parts[0], out long chatId) &&
                long.TryParse(parts[1], out long senderId) &&
                long.TryParse(parts[2], out long approvedAt) &&
                isAdmin(chatId) &&
                isAdmin(senderId))
            {
                var lifetime = new CancellationTokenSource();
                _lifetime = lifetime;
                WindowsConsoleSession console = runner.TryGetActiveConsoleSession() ?? new WindowsConsoleSession(0, string.Empty);
                _grant = new DevelopmentGrant(chatId, senderId, console, approvedAt, lifetime.Token);
            }
        }
        catch
        {
        }
    }
}
