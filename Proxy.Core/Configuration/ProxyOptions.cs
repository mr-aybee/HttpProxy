using System.Security.Authentication;
using System.Security.Cryptography.X509Certificates;

namespace Orbyn.Proxy.Core.Configuration;

public sealed class ProxyOptions
{
    public const string SectionName = "Proxy";

    public ListenerOptions Listeners { get; set; } = new();

    public TlsOptions Tls { get; set; } = new();

    public ProtocolOptions Protocols { get; set; } = new();

    public PolicyOptions Policy { get; set; } = new();

    public LimitOptions Limits { get; set; } = new();

    public TransportOptions Transport { get; set; } = new();

    public UpstreamOptions Upstream { get; set; } = new();

    public AuthenticationOptions Authentication { get; set; } = new();

    public DiagnosticsOptions Diagnostics { get; set; } = new();

    public TerminalOptions Terminal { get; set; } = new();

    public BackgroundOptions Background { get; set; } = new();
}

public sealed class ListenerOptions
{
    public EndpointOptions Explicit { get; set; } = new()
    {
        Enabled = true,
        Address = "127.0.0.1",
        Port = 12000
    };

    public EndpointOptions Transparent { get; set; } = new()
    {
        Enabled = false,
        Address = "0.0.0.0",
        Port = 12001
    };

    public EndpointOptions Socks { get; set; } = new()
    {
        Enabled = false,
        Address = "127.0.0.1",
        Port = 1080
    };

    public bool SetExplicitAsSystemProxy { get; set; }
}

public sealed class EndpointOptions
{
    public bool Enabled { get; set; }

    public string Address { get; set; } = "127.0.0.1";

    public int Port { get; set; }

    public int? MaxConcurrentClients { get; set; }

    public string GenericCertificateName { get; set; } = "localhost";
}

public sealed class TlsOptions
{
    public TlsInspectionMode Mode { get; set; } = TlsInspectionMode.Tunnel;

    public string[] InspectHosts { get; set; } = [];

    public string[] BypassHosts { get; set; } = [];

    public SslProtocols ClientProtocols { get; set; } = SslProtocols.Tls12 | SslProtocols.Tls13;

    public SslProtocols ServerProtocols { get; set; } = SslProtocols.None;

    public X509RevocationMode CheckCertificateRevocation { get; set; } = X509RevocationMode.NoCheck;

    public CertificateOptions Certificate { get; set; } = new();
}

public enum TlsInspectionMode
{
    Tunnel,
    Selective,
    Intercept
}

public sealed class CertificateOptions
{
    public CertificateSource Source { get; set; } = CertificateSource.Auto;

    public string Path { get; set; } = "certificates/orbyn-proxy-root.pfx";

    public string Password { get; set; } = string.Empty;

    public string PasswordEnvironmentVariable { get; set; } = "HTTPPROXY_CERT_PASSWORD";

    public bool TrustCurrentUser { get; set; }

    public bool TrustLocalMachine { get; set; }

    public string RootCertificateName { get; set; } = "Orbyn Proxy Root Certificate Authority";

    public string RootCertificateIssuerName { get; set; } = "Orbyn Technologies";

    public int LeafCertificateValidDays { get; set; } = 396;

    public int LeafCertificateGraceDays { get; set; } = 2;

    public int CacheTimeoutMinutes { get; set; } = 60;

    public bool SaveGeneratedHostCertificates { get; set; }

    public int LeafRsaKeyPairBufferSize { get; set; } = 8;
}

public enum CertificateSource
{
    Auto,
    File
}

public sealed class ProtocolOptions
{
    public bool EnableHttp2 { get; set; } = true;

    public bool EnableHttp3 { get; set; }

    public bool EnableQpackDynamicTable { get; set; }

    public bool EnableRfc8441 { get; set; }

    public bool Enable100ContinueBehaviour { get; set; }

    public bool CompatibilityMode100Continue { get; set; }
}

public sealed class PolicyOptions
{
    public string[] BlockedHosts { get; set; } = [];

    public string? BlockedHostsFile { get; set; } = "BlockedHosts.txt";

    public bool BlockPrivateNetworkDestinations { get; set; }

    public string ViaHeaderPseudonym { get; set; } = "orbyn-proxy";
}

public sealed class LimitOptions
{
    public int MaxDecodedHeaderListBytes { get; set; } = 64 * 1024;

    public int MaxBufferedBodyBytes { get; set; } = 4 * 1024 * 1024;

    public int MaxWebSocketFramePayloadBytes { get; set; } = 16 * 1024 * 1024;

    public int? MaxConcurrentClientConnections { get; set; }

    public int ConnectionTimeoutSeconds { get; set; } = 60;

    public int ConnectTimeoutSeconds { get; set; } = 20;

    public int ClientHeaderTimeoutSeconds { get; set; } = 30;

    public int ResponseHeaderTimeoutSeconds { get; set; } = 60;

    public int IdleReadTimeoutSeconds { get; set; } = 120;

    public int IdleWriteTimeoutSeconds { get; set; } = 120;

    public int RequestTimeoutSeconds { get; set; }

    public int ShutdownDrainSeconds { get; set; } = 30;
}

public sealed class TransportOptions
{
    public bool EnableConnectionPool { get; set; } = true;

    public bool EnableTcpServerConnectionPrefetch { get; set; } = true;

    public bool EnableTcpKeepAlive { get; set; } = true;

    public bool NoDelay { get; set; } = true;

    public bool ReuseSocket { get; set; } = true;

    public int ListenerBacklog { get; set; } = 512;

    public int NetworkFailureRetryAttempts { get; set; } = 1;

    public int TcpTimeWaitSeconds { get; set; }
}

public sealed class UpstreamOptions
{
    public bool UseSystemProxy { get; set; }

    public string? AutoConfigurationUrl { get; set; }

    public ExternalProxyOptions Http { get; set; } = new();

    public ExternalProxyOptions Https { get; set; } = new();
}

public sealed class ExternalProxyOptions
{
    public bool Enabled { get; set; }

    public ExternalProxyKind Type { get; set; } = ExternalProxyKind.Http;

    public string Host { get; set; } = string.Empty;

    public int Port { get; set; }

    public string Username { get; set; } = string.Empty;

    public string Password { get; set; } = string.Empty;

    public string PasswordEnvironmentVariable { get; set; } = string.Empty;

    public bool UseDefaultCredentials { get; set; }

    public bool BypassLocalhost { get; set; } = true;

    public bool ProxyDnsRequests { get; set; }
}

public enum ExternalProxyKind
{
    Http,
    Socks4,
    Socks5
}

public sealed class AuthenticationOptions
{
    public bool Enabled { get; set; }

    public string Realm { get; set; } = "Orbyn Proxy";

    public string Username { get; set; } = string.Empty;

    public string Password { get; set; } = string.Empty;

    public string PasswordEnvironmentVariable { get; set; } = "HTTPPROXY_AUTH_PASSWORD";
}

public sealed class DiagnosticsOptions
{
    public bool LogRequests { get; set; } = true;

    public bool EnableRequestTimingCapture { get; set; }

    public double RequestSampleRate { get; set; } = 1.0;
}

public sealed class TerminalOptions
{
    public int RefreshMilliseconds { get; set; } = 250;

    public int MaxActivityEntries { get; set; } = 200;
}

public sealed class BackgroundOptions
{
    public string LogFilePath { get; set; } = "logs/proxy-core.log";

    public long MaxFileSizeBytes { get; set; } = 10 * 1024 * 1024;

    public int MaxRolledFiles { get; set; } = 5;
}
