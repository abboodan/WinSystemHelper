using System.Diagnostics.CodeAnalysis;

namespace WinSystemHelper;

internal sealed class CommandRegistry
{
    private readonly Dictionary<string, ICommandHandler> _handlers = new(StringComparer.Ordinal);

    public CommandRegistry(IEnumerable<ICommandHandler> handlers)
    {
        foreach (ICommandHandler handler in handlers)
        {
            foreach (string command in handler.Commands)
            {
                _handlers.Add(command, handler);
            }
        }
    }

    public bool TryGet(string command, [NotNullWhen(true)] out ICommandHandler? handler)
    {
        return _handlers.TryGetValue(command, out handler);
    }
}
