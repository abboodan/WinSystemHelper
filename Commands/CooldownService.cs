using System.Collections.Concurrent;

namespace WinSystemHelper;

public sealed class CooldownService
{
    private readonly ConcurrentDictionary<string, DateTimeOffset> _cooldowns =
        new(StringComparer.OrdinalIgnoreCase);

    public bool TryGetRemaining(
        string key,
        TimeSpan cooldown,
        out TimeSpan remaining)
    {
        remaining = TimeSpan.Zero;
        if (cooldown <= TimeSpan.Zero)
        {
            return false;
        }

        if (!_cooldowns.TryGetValue(key, out DateTimeOffset lastRunAt))
        {
            return false;
        }

        TimeSpan elapsed = DateTimeOffset.Now - lastRunAt;
        if (elapsed >= cooldown)
        {
            _cooldowns.TryRemove(key, out _);
            return false;
        }

        remaining = cooldown - elapsed;
        return true;
    }

    public void Mark(string key)
    {
        _cooldowns[key] = DateTimeOffset.Now;
    }
}
