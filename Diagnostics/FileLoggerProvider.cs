namespace WinSystemHelper;

/// <summary>Routes the service's normal logging into <see cref="FileLogStore"/>.</summary>
public sealed class FileLoggerProvider(FileLogStore store) : ILoggerProvider
{
    public ILogger CreateLogger(string categoryName) => new FileLogger(store, categoryName);

    public void Dispose()
    {
    }

    private sealed class FileLogger(FileLogStore store, string category) : ILogger
    {
        // Framework chatter (per-request HTTP lines, host lifetime) is only kept when it signals trouble.
        private readonly LogLevel _minimum =
            category.StartsWith("Microsoft", StringComparison.Ordinal) ||
            category.StartsWith("System", StringComparison.Ordinal)
                ? LogLevel.Warning
                : LogLevel.Information;

        private readonly string _shortCategory = category[(category.LastIndexOf('.') + 1)..];

        public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;

        public bool IsEnabled(LogLevel logLevel) => logLevel != LogLevel.None && logLevel >= _minimum;

        public void Log<TState>(
            LogLevel logLevel,
            EventId eventId,
            TState state,
            Exception? exception,
            Func<TState, Exception?, string> formatter)
        {
            if (!IsEnabled(logLevel))
            {
                return;
            }

            string level = logLevel switch
            {
                LogLevel.Critical => "CRT",
                LogLevel.Error => "ERR",
                LogLevel.Warning => "WRN",
                LogLevel.Information => "INF",
                _ => "DBG"
            };

            store.Write(level, _shortCategory, formatter(state, exception), exception);
        }
    }
}
