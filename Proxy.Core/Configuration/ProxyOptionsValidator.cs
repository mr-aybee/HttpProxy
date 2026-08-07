using System.Net;
using Microsoft.Extensions.Options;

namespace Orbyn.Proxy.Core.Configuration;

internal sealed class ProxyOptionsValidator(IHostEnvironment environment) : IValidateOptions<ProxyOptions>
{
    public ValidateOptionsResult Validate(string? name, ProxyOptions options)
    {
        var errors = new List<string>();

        ValidateEndpoint("Explicit", options.Listeners.Explicit, errors);
        ValidateEndpoint("Transparent", options.Listeners.Transparent, errors);
        ValidateEndpoint("Socks", options.Listeners.Socks, errors);

        var enabledEndpoints = new[]
        {
            options.Listeners.Explicit,
            options.Listeners.Transparent,
            options.Listeners.Socks
        }.Where(endpoint => endpoint.Enabled).ToArray();

        if (enabledEndpoints.Length == 0)
        {
            errors.Add("At least one proxy listener must be enabled.");
        }

        var duplicate = enabledEndpoints
            .GroupBy(endpoint => $"{endpoint.Address}:{endpoint.Port}", StringComparer.OrdinalIgnoreCase)
            .FirstOrDefault(group => group.Count() > 1);
        if (duplicate is not null)
        {
            errors.Add($"More than one listener uses {duplicate.Key}.");
        }

        if (options.Listeners.SetExplicitAsSystemProxy && !options.Listeners.Explicit.Enabled)
        {
            errors.Add("The explicit listener must be enabled before it can be set as the system proxy.");
        }

        if (options.Listeners.SetExplicitAsSystemProxy && !OperatingSystem.IsWindows())
        {
            errors.Add("Setting the OS system proxy is supported only on Windows.");
        }

        if (options.Tls.Mode == TlsInspectionMode.Selective && options.Tls.InspectHosts.Length == 0)
        {
            errors.Add("TLS mode Selective requires at least one Proxy:Tls:InspectHosts rule.");
        }

        if (options.Tls.Mode != TlsInspectionMode.Tunnel &&
            string.IsNullOrWhiteSpace(options.Tls.Certificate.Path))
        {
            errors.Add("A certificate path is required when TLS inspection is enabled.");
        }

        if (options.Tls.Certificate.LeafCertificateValidDays <= 0 ||
            options.Tls.Certificate.LeafCertificateGraceDays < 0 ||
            options.Tls.Certificate.LeafCertificateValidDays + options.Tls.Certificate.LeafCertificateGraceDays > 398)
        {
            errors.Add("Leaf certificate validity plus grace days must be between 1 and 398 days.");
        }

        if (options.Tls.Mode != TlsInspectionMode.Tunnel &&
            options.Tls.Certificate.Source == CertificateSource.File &&
            !string.IsNullOrWhiteSpace(options.Tls.Certificate.Path))
        {
            var path = ResolvePath(options.Tls.Certificate.Path);
            if (!File.Exists(path))
            {
                errors.Add($"Configured root certificate does not exist: {path}");
            }
        }

        ValidatePositive(options.Limits.MaxDecodedHeaderListBytes, "MaxDecodedHeaderListBytes", errors);
        ValidatePositive(options.Limits.MaxBufferedBodyBytes, "MaxBufferedBodyBytes", errors);
        ValidatePositive(options.Limits.MaxWebSocketFramePayloadBytes, "MaxWebSocketFramePayloadBytes", errors);
        ValidatePositive(options.Limits.ConnectionTimeoutSeconds, "ConnectionTimeoutSeconds", errors);
        ValidatePositive(options.Limits.ConnectTimeoutSeconds, "ConnectTimeoutSeconds", errors);
        ValidatePositive(options.Limits.ClientHeaderTimeoutSeconds, "ClientHeaderTimeoutSeconds", errors);
        ValidatePositive(options.Limits.ResponseHeaderTimeoutSeconds, "ResponseHeaderTimeoutSeconds", errors);
        ValidatePositive(options.Limits.IdleReadTimeoutSeconds, "IdleReadTimeoutSeconds", errors);
        ValidatePositive(options.Limits.IdleWriteTimeoutSeconds, "IdleWriteTimeoutSeconds", errors);
        ValidateNonNegative(options.Limits.RequestTimeoutSeconds, "RequestTimeoutSeconds", errors);
        ValidatePositive(options.Limits.ShutdownDrainSeconds, "ShutdownDrainSeconds", errors);
        ValidatePositive(options.Transport.ListenerBacklog, "ListenerBacklog", errors);
        ValidateNonNegative(options.Transport.NetworkFailureRetryAttempts, "NetworkFailureRetryAttempts", errors);
        ValidatePositive(options.Tls.Certificate.CacheTimeoutMinutes, "Certificate CacheTimeoutMinutes", errors);
        ValidateNonNegative(options.Tls.Certificate.LeafRsaKeyPairBufferSize, "LeafRsaKeyPairBufferSize", errors);

        if (options.Limits.MaxConcurrentClientConnections is <= 0)
        {
            errors.Add("MaxConcurrentClientConnections must be null or greater than zero.");
        }

        if (options.Diagnostics.RequestSampleRate is < 0 or > 1)
        {
            errors.Add("RequestSampleRate must be between 0 and 1.");
        }

        if (options.Terminal.RefreshMilliseconds is < 100 or > 5000)
        {
            errors.Add("Terminal RefreshMilliseconds must be between 100 and 5000.");
        }

        if (options.Terminal.MaxActivityEntries is < 20 or > 1000)
        {
            errors.Add("Terminal MaxActivityEntries must be between 20 and 1000.");
        }

        ValidateExternalProxy("Http", options.Upstream.Http, errors);
        ValidateExternalProxy("Https", options.Upstream.Https, errors);

        var upstreamModes = (options.Upstream.UseSystemProxy ? 1 : 0) +
                            (!string.IsNullOrWhiteSpace(options.Upstream.AutoConfigurationUrl) ? 1 : 0) +
                            (options.Upstream.Http.Enabled || options.Upstream.Https.Enabled ? 1 : 0);
        if (upstreamModes > 1)
        {
            errors.Add("Choose only one upstream mode: system proxy, PAC URL, or configured HTTP/HTTPS proxies.");
        }

        if (!string.IsNullOrWhiteSpace(options.Upstream.AutoConfigurationUrl) &&
            !Uri.TryCreate(options.Upstream.AutoConfigurationUrl, UriKind.Absolute, out _))
        {
            errors.Add("Proxy:Upstream:AutoConfigurationUrl must be an absolute URI.");
        }

        if (options.Authentication.Enabled && string.IsNullOrWhiteSpace(options.Authentication.Username))
        {
            errors.Add("Proxy authentication requires a username.");
        }

        if (options.Authentication.Enabled && string.IsNullOrEmpty(ResolveSecret(
                options.Authentication.Password,
                options.Authentication.PasswordEnvironmentVariable)))
        {
            errors.Add("Proxy authentication requires a password or password environment variable.");
        }

        if (options.Protocols.EnableRfc8441 && !options.Protocols.EnableHttp2)
        {
            errors.Add("RFC 8441 support requires HTTP/2.");
        }

        if (options.Protocols.EnableQpackDynamicTable && !options.Protocols.EnableHttp3)
        {
            errors.Add("QPACK dynamic tables require HTTP/3.");
        }

        if (string.IsNullOrWhiteSpace(options.Policy.ViaHeaderPseudonym))
        {
            errors.Add("ViaHeaderPseudonym cannot be empty.");
        }

        return errors.Count == 0
            ? ValidateOptionsResult.Success
            : ValidateOptionsResult.Fail(errors);
    }

    private string ResolvePath(string path) => Path.IsPathRooted(path)
        ? Path.GetFullPath(path)
        : Path.GetFullPath(path, environment.ContentRootPath);

    private static void ValidateEndpoint(string name, EndpointOptions endpoint, ICollection<string> errors)
    {
        if (!endpoint.Enabled)
        {
            return;
        }

        if (!IPAddress.TryParse(endpoint.Address, out _))
        {
            errors.Add($"{name} listener address '{endpoint.Address}' is not a valid IP address.");
        }

        if (endpoint.Port is < IPEndPoint.MinPort or > IPEndPoint.MaxPort)
        {
            errors.Add($"{name} listener port must be between 0 and 65535.");
        }

        if (endpoint.MaxConcurrentClients is <= 0)
        {
            errors.Add($"{name} MaxConcurrentClients must be null or greater than zero.");
        }
    }

    private static void ValidateExternalProxy(
        string name,
        ExternalProxyOptions proxy,
        ICollection<string> errors)
    {
        if (!proxy.Enabled)
        {
            return;
        }

        if (string.IsNullOrWhiteSpace(proxy.Host))
        {
            errors.Add($"{name} upstream proxy requires a host.");
        }

        if (proxy.Port is < 1 or > IPEndPoint.MaxPort)
        {
            errors.Add($"{name} upstream proxy port must be between 1 and 65535.");
        }
    }

    private static void ValidatePositive(int value, string name, ICollection<string> errors)
    {
        if (value <= 0)
        {
            errors.Add($"{name} must be greater than zero.");
        }
    }

    private static void ValidateNonNegative(int value, string name, ICollection<string> errors)
    {
        if (value < 0)
        {
            errors.Add($"{name} cannot be negative.");
        }
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
}
