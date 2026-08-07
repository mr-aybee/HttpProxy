using System.Text;

namespace Orbyn.Proxy.Core.Logging;

internal sealed class RollingFileLoggerProvider : ILoggerProvider
{
    private readonly Lock sync = new();
    private readonly string filePath;
    private readonly long maxFileSizeBytes;
    private readonly int maxRolledFiles;
    private StreamWriter writer;
    private bool disposed;

    public RollingFileLoggerProvider(string filePath, long maxFileSizeBytes, int maxRolledFiles)
    {
        this.filePath = Path.GetFullPath(filePath);
        this.maxFileSizeBytes = maxFileSizeBytes;
        this.maxRolledFiles = maxRolledFiles;
        Directory.CreateDirectory(Path.GetDirectoryName(this.filePath)!);
        writer = OpenWriter();
    }

    public ILogger CreateLogger(string categoryName) => new FileLogger(categoryName, this);

    public void Dispose()
    {
        lock (sync)
        {
            if (disposed)
            {
                return;
            }

            writer.Dispose();
            disposed = true;
        }
    }

    private void Write(LogLevel level, string category, string message, Exception? exception)
    {
        lock (sync)
        {
            if (disposed)
            {
                return;
            }

            RollIfRequired();
            var source = category[(category.LastIndexOf('.') + 1)..];
            var exceptionText = exception is null ? string.Empty : $" | {exception}";
            var line = $"{DateTimeOffset.Now:yyyy-MM-dd HH:mm:ss.fff zzz} " +
                       $"{LevelCode(level)} {source} {message}{exceptionText}";
            writer.WriteLine(SingleLine(line));
            writer.Flush();
        }
    }

    private void RollIfRequired()
    {
        if (writer.BaseStream.Length < maxFileSizeBytes)
        {
            return;
        }

        writer.Dispose();
        for (var index = maxRolledFiles - 1; index >= 1; index--)
        {
            var source = $"{filePath}.{index}";
            if (File.Exists(source))
            {
                File.Move(source, $"{filePath}.{index + 1}", overwrite: true);
            }
        }

        if (File.Exists(filePath))
        {
            File.Move(filePath, $"{filePath}.1", overwrite: true);
        }

        writer = OpenWriter();
    }

    private StreamWriter OpenWriter() => new(
        new FileStream(filePath, FileMode.Append, FileAccess.Write, FileShare.ReadWrite),
        new UTF8Encoding(encoderShouldEmitUTF8Identifier: false));

    private static string LevelCode(LogLevel level) => level switch
    {
        LogLevel.Trace => "TRC",
        LogLevel.Debug => "DBG",
        LogLevel.Information => "INF",
        LogLevel.Warning => "WRN",
        LogLevel.Error => "ERR",
        LogLevel.Critical => "CRT",
        _ => "---"
    };

    private static string SingleLine(string value) => value
        .Replace('\r', ' ')
        .Replace('\n', ' ')
        .Trim();

    private sealed class FileLogger(string category, RollingFileLoggerProvider provider) : ILogger
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
            if (IsEnabled(logLevel))
            {
                provider.Write(logLevel, category, formatter(state, exception), exception);
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
