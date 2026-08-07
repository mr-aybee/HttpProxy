using Microsoft.Extensions.Configuration;

namespace Orbyn.Proxy.Core.Configuration;

internal static class CommandLineConfiguration
{
    private static readonly Dictionary<string, string> SwitchMappings = new(StringComparer.OrdinalIgnoreCase)
    {
        ["--listen"] = "Proxy:Listeners:Explicit:Address",
        ["--port"] = "Proxy:Listeners:Explicit:Port",
        ["--tls-mode"] = "Proxy:Tls:Mode",
        ["--certificate"] = "Proxy:Tls:Certificate:Path",
        ["--certificate-source"] = "Proxy:Tls:Certificate:Source",
        ["--trust-current-user"] = "Proxy:Tls:Certificate:TrustCurrentUser",
        ["--trust-local-machine"] = "Proxy:Tls:Certificate:TrustLocalMachine",
        ["--http2"] = "Proxy:Protocols:EnableHttp2",
        ["--http3"] = "Proxy:Protocols:EnableHttp3",
        ["--service"] = "Commands:Service",
        ["--background"] = "Commands:Background",
        ["--background-child"] = "Commands:BackgroundChild",
        ["--validate-config"] = "Commands:ValidateConfig",
        ["--print-config"] = "Commands:PrintConfig"
    };

    public static IConfigurationBuilder AddProxyCommandLine(
        this IConfigurationBuilder configuration,
        string[] args)
    {
        configuration.AddEnvironmentVariables("HTTPPROXY_");
        configuration.AddCommandLine(args, SwitchMappings);
        return configuration;
    }
}
