using Microsoft.Extensions.Logging;

namespace Orbyn.Proxy.Core.Runtime;

internal enum ProxyRunStatus
{
    Starting,
    Running,
    Stopping,
    Stopped,
    Faulted
}

internal sealed record ProxyActivity(
    DateTimeOffset Timestamp,
    LogLevel Level,
    string Source,
    string Message);

internal sealed record ProxyRuntimeSnapshot(
    ProxyRunStatus Status,
    string StatusMessage,
    DateTimeOffset? StartedAt,
    string TlsMode,
    IReadOnlyList<string> Listeners,
    int ActiveConnections,
    long Requests,
    long Tunnels,
    long Blocked,
    long Errors,
    IReadOnlyList<ProxyActivity> Activity);

internal sealed class ProxyRuntimeState
{
    private readonly Lock sync = new();
    private readonly List<ProxyActivity> activity = [];
    private ProxyRunStatus status = ProxyRunStatus.Starting;
    private string statusMessage = "Initializing worker";
    private DateTimeOffset? startedAt;
    private string tlsMode = "-";
    private string[] listeners = [];
    private int activityCapacity = 200;
    private int activeConnections;
    private long requests;
    private long tunnels;
    private long blocked;
    private long errors;

    public void MarkStarting(string message = "Starting proxy listeners")
    {
        lock (sync)
        {
            status = ProxyRunStatus.Starting;
            statusMessage = message;
        }
    }

    public void MarkRunning(string mode, IEnumerable<string> endpointDescriptions)
    {
        lock (sync)
        {
            status = ProxyRunStatus.Running;
            statusMessage = "Accepting connections";
            startedAt = DateTimeOffset.UtcNow;
            tlsMode = mode;
            listeners = endpointDescriptions.ToArray();
        }
    }

    public void MarkStopping()
    {
        lock (sync)
        {
            status = ProxyRunStatus.Stopping;
            statusMessage = "Draining active connections";
        }
    }

    public void MarkStopped()
    {
        lock (sync)
        {
            status = ProxyRunStatus.Stopped;
            statusMessage = "Proxy stopped";
            activeConnections = 0;
        }
    }

    public void MarkFaulted(Exception exception)
    {
        Interlocked.Increment(ref errors);
        lock (sync)
        {
            status = ProxyRunStatus.Faulted;
            statusMessage = Sanitize(exception.Message);
        }
    }

    public void SetActivityCapacity(int capacity)
    {
        lock (sync)
        {
            activityCapacity = capacity;
            TrimActivity();
        }
    }

    public void AddActivity(LogLevel level, string source, string message)
    {
        if (level == LogLevel.None)
        {
            return;
        }

        lock (sync)
        {
            activity.Add(new ProxyActivity(
                DateTimeOffset.Now,
                level,
                ShortenSource(source),
                Sanitize(message)));
            TrimActivity();
        }

        if (level >= LogLevel.Error)
        {
            Interlocked.Increment(ref errors);
        }
    }

    public void ClearActivity()
    {
        lock (sync)
        {
            activity.Clear();
        }
    }

    public void UpdateConnections(int count) => Volatile.Write(ref activeConnections, count);

    public void CountRequest() => Interlocked.Increment(ref requests);

    public void CountTunnel() => Interlocked.Increment(ref tunnels);

    public void CountBlocked() => Interlocked.Increment(ref blocked);

    public ProxyRuntimeSnapshot GetSnapshot()
    {
        lock (sync)
        {
            return new ProxyRuntimeSnapshot(
                status,
                statusMessage,
                startedAt,
                tlsMode,
                listeners.ToArray(),
                Volatile.Read(ref activeConnections),
                Interlocked.Read(ref requests),
                Interlocked.Read(ref tunnels),
                Interlocked.Read(ref blocked),
                Interlocked.Read(ref errors),
                activity.ToArray());
        }
    }

    private void TrimActivity()
    {
        var excess = activity.Count - activityCapacity;
        if (excess > 0)
        {
            activity.RemoveRange(0, excess);
        }
    }

    private static string ShortenSource(string source)
    {
        if (source.StartsWith("Titanium.Web.Proxy", StringComparison.Ordinal))
        {
            return "Titanium";
        }

        if (source.StartsWith("Microsoft.Hosting", StringComparison.Ordinal))
        {
            return "Host";
        }

        var separator = source.LastIndexOf('.');
        return separator >= 0 ? source[(separator + 1)..] : source;
    }

    private static string Sanitize(string value) => value
        .Replace('\r', ' ')
        .Replace('\n', ' ')
        .Replace("\u001b", string.Empty, StringComparison.Ordinal)
        .Trim();
}
