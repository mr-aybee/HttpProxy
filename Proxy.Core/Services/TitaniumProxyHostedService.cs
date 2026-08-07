using System.Net;
using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;
using System.Text;
using Microsoft.Extensions.Options;
using Orbyn.Proxy.Core.Configuration;
using Orbyn.Proxy.Core.Policies;
using Orbyn.Proxy.Core.Runtime;
using Titanium.Web.Proxy;
using Titanium.Web.Proxy.EventArguments;
using Titanium.Web.Proxy.Logging;
using Titanium.Web.Proxy.Models;
using Titanium.Web.Proxy.Network;

namespace Orbyn.Proxy.Core.Services;

internal sealed class TitaniumProxyHostedService(
    IOptionsMonitor<ProxyOptions> options,
    ProxyPolicyProvider policies,
    ProxyRuntimeState runtimeState,
    IHostEnvironment environment,
    ILoggerFactory loggerFactory,
    ILogger<TitaniumProxyHostedService> logger) : IHostedService, IDisposable
{
    private const string BlockedResponse = """
        <!doctype html>
        <html lang="en">
        <head><meta charset="utf-8"><title>Request blocked</title></head>
        <body><h1>Request blocked</h1><p>Blocked by Orbyn Proxy Core policy.</p></body>
        </html>
        """;

    private readonly SemaphoreSlim lifecycleGate = new(1, 1);
    private ProxyServer? proxyServer;
    private ExplicitProxyEndPoint? explicitEndPoint;
    private ProxyOptions? activeSettings;
    private bool changedSystemProxy;
    private bool disposed;

    public void Dispose()
    {
        if (disposed)
        {
            return;
        }

        proxyServer?.Dispose();
        proxyServer = null;
        lifecycleGate.Dispose();
        disposed = true;
    }

    public async Task StartAsync(CancellationToken cancellationToken)
    {
        await lifecycleGate.WaitAsync(cancellationToken);
        try
        {
            StartCore(cancellationToken);
        }
        finally
        {
            lifecycleGate.Release();
        }
    }

    public async Task RestartAsync(CancellationToken cancellationToken)
    {
        await lifecycleGate.WaitAsync(cancellationToken);
        try
        {
            runtimeState.MarkRestarting();
            logger.LogInformation("Restarting proxy and reloading configuration");
            await StopCoreAsync(cancellationToken, finalStop: false);
            StartCore(cancellationToken);
        }
        catch (Exception exception)
        {
            runtimeState.MarkFaulted(exception);
            throw;
        }
        finally
        {
            lifecycleGate.Release();
        }
    }

    public async Task StopAsync(CancellationToken cancellationToken)
    {
        await lifecycleGate.WaitAsync(cancellationToken);
        try
        {
            await StopCoreAsync(cancellationToken, finalStop: true);
        }
        finally
        {
            lifecycleGate.Release();
        }
    }

    private void StartCore(CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        runtimeState.MarkStarting();

        var settings = options.CurrentValue;
        activeSettings = settings;
        policies.Load(settings);

        var server = CreateServer(settings);
        try
        {
            AddConfiguredEndPoints(server, settings);
            ConfigureCertificate(server.CertificateManager, settings);

            server.BeforeRequest += OnBeforeRequest;
            server.ClientConnectionCountChanged += OnClientConnectionCountChanged;
            server.Start(changeSystemProxySettings: false);

            if (settings.Listeners.SetExplicitAsSystemProxy)
            {
                server.SetAsSystemHttpProxy(explicitEndPoint!);
                changedSystemProxy = true;
                server.SetAsSystemHttpsProxy(explicitEndPoint!);
            }

            proxyServer = server;
            runtimeState.UpdateConnections(server.ClientConnectionCount);
            runtimeState.MarkRunning(
                settings.Tls.Mode.ToString(),
                server.ProxyEndPoints.Select(endpoint =>
                    $"{GetEndPointName(endpoint)}  {endpoint.IpAddress}:{endpoint.Port}"));
            LogStartup(settings, server);
        }
        catch (Exception exception)
        {
            runtimeState.MarkFaulted(exception);
            if (changedSystemProxy)
            {
                server.RestoreOriginalProxySettings();
                changedSystemProxy = false;
            }

            server.Dispose();
            explicitEndPoint = null;
            activeSettings = null;
            throw;
        }
    }

    private async Task StopCoreAsync(CancellationToken cancellationToken, bool finalStop)
    {
        var server = proxyServer;
        if (server is null)
        {
            if (finalStop)
            {
                runtimeState.MarkStopped();
            }

            return;
        }

        if (finalStop)
        {
            logger.LogInformation("Stopping Orbyn Proxy Core; draining active connections");
            runtimeState.MarkStopping();
        }

        if (changedSystemProxy)
        {
            server.RestoreOriginalProxySettings();
            changedSystemProxy = false;
        }

        server.BeforeRequest -= OnBeforeRequest;
        server.ClientConnectionCountChanged -= OnClientConnectionCountChanged;
        if (explicitEndPoint is not null)
        {
            explicitEndPoint.BeforeTunnelConnectRequest -= OnBeforeTunnelConnectRequest;
        }

        try
        {
            var drainTimeout = TimeSpan.FromSeconds(
                (activeSettings ?? options.CurrentValue).Limits.ShutdownDrainSeconds);
            await server.StopAsync(drainTimeout).WaitAsync(cancellationToken);
        }
        catch (OperationCanceledException)
        {
            server.Stop();
            throw;
        }
        finally
        {
            server.Dispose();
            proxyServer = null;
            explicitEndPoint = null;
            activeSettings = null;
            if (finalStop)
            {
                runtimeState.MarkStopped();
            }
        }

        if (finalStop)
        {
            logger.LogInformation("Orbyn Proxy Core stopped");
        }
    }

    private ProxyServer CreateServer(ProxyOptions settings)
    {
        var certificate = settings.Tls.Certificate;
        var server = new ProxyServer(
            certificate.RootCertificateName,
            certificate.RootCertificateIssuerName,
            userTrustRootCertificate: false,
            machineTrustRootCertificate: false,
            trustRootCertificateAsAdmin: false)
        {
            NetworkFailureRetryAttempts = settings.Transport.NetworkFailureRetryAttempts,
            ForwardToUpstreamGateway = settings.Upstream.UseSystemProxy,
            UpstreamProxyConfigurationScript = ParseOptionalUri(settings.Upstream.AutoConfigurationUrl),
            EnableHttp2 = settings.Protocols.EnableHttp2,
            EnableRfc8441 = settings.Protocols.EnableRfc8441,
#pragma warning disable TWP001
            EnableHttp3 = settings.Protocols.EnableHttp3,
            EnableQpackDynamicTable = settings.Protocols.EnableQpackDynamicTable,
#pragma warning restore TWP001
            CheckCertificateRevocation = settings.Tls.CheckCertificateRevocation,
            Enable100ContinueBehaviour = settings.Protocols.Enable100ContinueBehaviour,
            CompatibilityMode100Continue = settings.Protocols.CompatibilityMode100Continue,
            MaxDecodedHeaderListBytes = settings.Limits.MaxDecodedHeaderListBytes,
            MaxBufferedBodyBytes = settings.Limits.MaxBufferedBodyBytes,
            MaxWebSocketFramePayloadBytes = settings.Limits.MaxWebSocketFramePayloadBytes,
            ViaHeaderPseudonym = settings.Policy.ViaHeaderPseudonym,
            EnableConnectionPool = settings.Transport.EnableConnectionPool,
            EnableTcpServerConnectionPrefetch = settings.Transport.EnableTcpServerConnectionPrefetch,
            NoDelay = settings.Transport.NoDelay,
            ConnectionTimeOutSeconds = settings.Limits.ConnectionTimeoutSeconds,
            ConnectTimeOutSeconds = settings.Limits.ConnectTimeoutSeconds,
            ClientHeaderTimeoutSeconds = settings.Limits.ClientHeaderTimeoutSeconds,
            ResponseHeaderTimeoutSeconds = settings.Limits.ResponseHeaderTimeoutSeconds,
            IdleReadTimeoutSeconds = settings.Limits.IdleReadTimeoutSeconds,
            IdleWriteTimeoutSeconds = settings.Limits.IdleWriteTimeoutSeconds,
            RequestTimeoutSeconds = settings.Limits.RequestTimeoutSeconds,
            TcpTimeWaitSeconds = settings.Transport.TcpTimeWaitSeconds,
            EnableTcpKeepAlive = settings.Transport.EnableTcpKeepAlive,
            ListenerBackLog = settings.Transport.ListenerBacklog,
            ReuseSocket = settings.Transport.ReuseSocket,
            MaxConcurrentClientConnections = settings.Limits.MaxConcurrentClientConnections,
            ProxyAuthenticationRealm = settings.Authentication.Realm,
            SupportedSslProtocols = settings.Tls.ClientProtocols,
            SupportedServerSslProtocols = settings.Tls.ServerProtocols,
            BlockPrivateNetworkDestinations = settings.Policy.BlockPrivateNetworkDestinations,
            UpStreamHttpProxy = CreateExternalProxy(settings.Upstream.Http),
            UpStreamHttpsProxy = CreateExternalProxy(settings.Upstream.Https),
            EnableRequestTimingCapture = settings.Diagnostics.EnableRequestTimingCapture
        };

        server.Logging = new ProxyLoggingOptions
        {
            Enabled = true,
            EnableConsole = false,
            LoggerFactory = loggerFactory
        };
        server.ApplyLoggingConfiguration();

        if (settings.Authentication.Enabled)
        {
            var expectedUser = settings.Authentication.Username;
            var expectedPassword = ResolveSecret(
                settings.Authentication.Password,
                settings.Authentication.PasswordEnvironmentVariable);

            server.ProxyBasicAuthenticateFunc = (_, username, password) =>
                Task.FromResult(FixedTimeEquals(username, expectedUser) &&
                                FixedTimeEquals(password, expectedPassword));
        }

        return server;
    }

    private void AddConfiguredEndPoints(ProxyServer server, ProxyOptions settings)
    {
        var decryptSsl = settings.Tls.Mode != TlsInspectionMode.Tunnel;

        if (settings.Listeners.Explicit.Enabled)
        {
            explicitEndPoint = new ExplicitProxyEndPoint(
                IPAddress.Parse(settings.Listeners.Explicit.Address),
                settings.Listeners.Explicit.Port,
                decryptSsl)
            {
                MaxConcurrentClients = settings.Listeners.Explicit.MaxConcurrentClients
            };
            explicitEndPoint.BeforeTunnelConnectRequest += OnBeforeTunnelConnectRequest;
            server.AddEndPoint(explicitEndPoint);
        }

        if (settings.Listeners.Transparent.Enabled)
        {
            var endpoint = new TransparentProxyEndPoint(
                IPAddress.Parse(settings.Listeners.Transparent.Address),
                settings.Listeners.Transparent.Port,
                decryptSsl)
            {
                MaxConcurrentClients = settings.Listeners.Transparent.MaxConcurrentClients,
                GenericCertificateName = settings.Listeners.Transparent.GenericCertificateName
            };
            endpoint.BeforeSslAuthenticate += OnBeforeSslAuthenticate;
            server.AddEndPoint(endpoint);
        }

        if (settings.Listeners.Socks.Enabled)
        {
            var endpoint = new SocksProxyEndPoint(
                IPAddress.Parse(settings.Listeners.Socks.Address),
                settings.Listeners.Socks.Port,
                decryptSsl)
            {
                MaxConcurrentClients = settings.Listeners.Socks.MaxConcurrentClients,
                GenericCertificateName = settings.Listeners.Socks.GenericCertificateName
            };

            endpoint.BeforeSslAuthenticate += OnBeforeSslAuthenticate;
            if (settings.Authentication.Enabled)
            {
                var expectedUser = settings.Authentication.Username;
                var expectedPassword = ResolveSecret(
                    settings.Authentication.Password,
                    settings.Authentication.PasswordEnvironmentVariable);
                endpoint.AuthenticateUserFunc = args => Task.FromResult(
                    FixedTimeEquals(args.UserName, expectedUser) &&
                    FixedTimeEquals(args.Password, expectedPassword));
            }

            server.AddEndPoint(endpoint);
        }
    }

    private void ConfigureCertificate(CertificateManager manager, ProxyOptions settings)
    {
        if (settings.Tls.Mode == TlsInspectionMode.Tunnel)
        {
            return;
        }

        var certificate = settings.Tls.Certificate;
        var path = ResolvePath(certificate.Path);
        var password = ResolveSecret(certificate.Password, certificate.PasswordEnvironmentVariable);

        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        manager.PfxFilePath = path;
        manager.PfxPassword = password;
        manager.OverwritePfxFile = false;
        manager.StorageFlag = X509KeyStorageFlags.Exportable | X509KeyStorageFlags.PersistKeySet;
        manager.CertificateValidDays = certificate.LeafCertificateValidDays;
        manager.CertificateGraceDays = certificate.LeafCertificateGraceDays;
        manager.CertificateCacheTimeOutMinutes = certificate.CacheTimeoutMinutes;
        manager.SaveFakeCertificates = certificate.SaveGeneratedHostCertificates;
        CertificateManager.LeafRsaKeyPairBufferSize = certificate.LeafRsaKeyPairBufferSize;

        if (File.Exists(path))
        {
            if (!manager.LoadRootCertificate(path, password, overwritePfXFile: false, manager.StorageFlag))
            {
                throw new InvalidOperationException($"The root certificate could not be loaded: {path}");
            }

            logger.LogInformation("Loaded root CA {Thumbprint} from {Path}",
                manager.RootCertificate?.Thumbprint,
                path);
        }
        else
        {
            if (certificate.Source == CertificateSource.File)
            {
                throw new FileNotFoundException("The configured root certificate was not found.", path);
            }

            if (!manager.CreateRootCertificate(persistToFile: true))
            {
                throw new InvalidOperationException($"A root certificate could not be generated at {path}");
            }

            logger.LogInformation("Generated root CA {Thumbprint} at {Path}",
                manager.RootCertificate?.Thumbprint,
                path);
        }

        manager.EnsureRootCertificate(
            certificate.TrustCurrentUser,
            certificate.TrustLocalMachine,
            trustRootCertificateAsAdmin: false);
    }

    private Task OnBeforeTunnelConnectRequest(object sender, TunnelConnectSessionEventArgs args)
    {
        runtimeState.CountTunnel();
        var host = args.HttpClient.Request.Host;
        if (policies.IsBlocked(host))
        {
            runtimeState.CountBlocked();
            args.DenyConnect = true;
            logger.LogWarning("Blocked CONNECT request for {Host}", host);
            return Task.CompletedTask;
        }

        args.DecryptSsl = policies.ShouldInspect(host, GetActiveSettings().Tls.Mode);
        LogRequest("CONNECT", host);
        return Task.CompletedTask;
    }

    private Task OnBeforeSslAuthenticate(object sender, BeforeSslAuthenticateEventArgs args)
    {
        if (policies.IsBlocked(args.SniHostName))
        {
            runtimeState.CountBlocked();
            logger.LogWarning("Blocked transparent TLS request for {Host}", args.SniHostName);
            args.TerminateSession();
            return Task.CompletedTask;
        }

        args.DecryptSsl = policies.ShouldInspect(args.SniHostName, GetActiveSettings().Tls.Mode);
        LogRequest("TLS", args.SniHostName);
        return Task.CompletedTask;
    }

    private Task OnBeforeRequest(object sender, SessionEventArgs args)
    {
        runtimeState.CountRequest();
        var request = args.HttpClient.Request;
        var host = string.IsNullOrWhiteSpace(request.Host)
            ? request.RequestUri.Host
            : request.Host;
        if (policies.IsBlocked(host))
        {
            runtimeState.CountBlocked();
            args.GenericResponse(BlockedResponse, HttpStatusCode.Forbidden, closeServerConnection: true);
            logger.LogWarning("Blocked {Method} request for {Host}", request.Method, host);
            return Task.CompletedTask;
        }

        LogRequest(request.Method, request.Url);
        return Task.CompletedTask;
    }

    private void LogRequest(string method, string? target)
    {
        var diagnostics = GetActiveSettings().Diagnostics;
        if (!diagnostics.LogRequests || Random.Shared.NextDouble() > diagnostics.RequestSampleRate)
        {
            return;
        }

        logger.LogInformation("{Method} {Target}", method, target);
    }

    private void LogStartup(ProxyOptions settings, ProxyServer server)
    {
        foreach (var endpoint in server.ProxyEndPoints)
        {
            logger.LogInformation(
                "Listening on {Type} {Address}:{Port} (TLS inspection: {DecryptSsl})",
                endpoint.GetType().Name,
                endpoint.IpAddress,
                endpoint.Port,
                endpoint.DecryptSsl);
        }

        logger.LogInformation(
            "Orbyn Proxy Core started in {TlsMode} mode. Press Ctrl+C to stop",
            settings.Tls.Mode);
    }

    private void OnClientConnectionCountChanged(object? sender, EventArgs args)
    {
        if (sender is ProxyServer server)
        {
            runtimeState.UpdateConnections(server.ClientConnectionCount);
        }
    }

    private static string GetEndPointName(ProxyEndPoint endpoint) => endpoint switch
    {
        ExplicitProxyEndPoint => "HTTP(S)",
        SocksProxyEndPoint => "SOCKS",
        TransparentProxyEndPoint => "TRANSPARENT",
        _ => endpoint.GetType().Name
    };

    private string ResolvePath(string path) => Path.IsPathRooted(path)
        ? Path.GetFullPath(path)
        : Path.GetFullPath(path, environment.ContentRootPath);

    private ProxyOptions GetActiveSettings() => activeSettings ?? options.CurrentValue;

    private static Uri? ParseOptionalUri(string? value) => string.IsNullOrWhiteSpace(value)
        ? null
        : new Uri(value, UriKind.Absolute);

    private static ExternalProxy? CreateExternalProxy(ExternalProxyOptions settings)
    {
        if (!settings.Enabled)
        {
            return null;
        }

        return new ExternalProxy(settings.Host, settings.Port)
        {
            ProxyType = settings.Type switch
            {
                ExternalProxyKind.Http => ExternalProxyType.Http,
                ExternalProxyKind.Socks4 => ExternalProxyType.Socks4,
                ExternalProxyKind.Socks5 => ExternalProxyType.Socks5,
                _ => throw new ArgumentOutOfRangeException(nameof(settings.Type))
            },
            UserName = settings.Username,
            Password = ResolveSecret(settings.Password, settings.PasswordEnvironmentVariable),
            UseDefaultCredentials = settings.UseDefaultCredentials,
            BypassLocalhost = settings.BypassLocalhost,
            ProxyDnsRequests = settings.ProxyDnsRequests
        };
    }

    private static string ResolveSecret(string configuredValue, string environmentVariable)
    {
        if (!string.IsNullOrWhiteSpace(environmentVariable))
        {
            var environmentValue = Environment.GetEnvironmentVariable(environmentVariable);
            if (environmentValue is not null)
            {
                return environmentValue;
            }
        }

        return configuredValue;
    }

    private static bool FixedTimeEquals(string actual, string expected)
    {
        var actualHash = SHA256.HashData(Encoding.UTF8.GetBytes(actual));
        var expectedHash = SHA256.HashData(Encoding.UTF8.GetBytes(expected));
        return CryptographicOperations.FixedTimeEquals(actualHash, expectedHash);
    }
}
