using Orbyn.Proxy.Core.Runtime;

namespace Orbyn.Proxy.Core.Logging;

internal sealed class DashboardLoggerProvider(ProxyRuntimeState state) : ILoggerProvider
{
    public ILogger CreateLogger(string categoryName) => new DashboardLogger(categoryName, state);

    public void Dispose()
    {
    }

    private sealed class DashboardLogger(string category, ProxyRuntimeState runtimeState) : ILogger
    {
        public IDisposable? BeginScope<TState>(TState state) where TState : notnull => NullScope.Instance;

        public bool IsEnabled(LogLevel logLevel) => logLevel != LogLevel.None;

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

            var message = formatter(state, exception);
            if (exception is not null)
            {
                message = string.IsNullOrWhiteSpace(message)
                    ? exception.Message
                    : $"{message}: {exception.Message}";
            }

            if (!string.IsNullOrWhiteSpace(message))
            {
                runtimeState.AddActivity(logLevel, category, message);
            }
        }
    }

    private sealed class NullScope : IDisposable
    {
        public static NullScope Instance { get; } = new();

        public void Dispose()
        {
        }
    }
}
