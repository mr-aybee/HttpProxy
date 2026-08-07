using Orbyn.Proxy.Core.Configuration;

namespace Orbyn.Proxy.Core.Policies;

internal sealed class ProxyPolicyProvider
{
    private readonly ILogger<ProxyPolicyProvider> logger;
    private HostRuleMatcher blockedHosts = new([]);
    private HostRuleMatcher inspectedHosts = new([]);
    private HostRuleMatcher bypassedHosts = new([]);

    public ProxyPolicyProvider(IHostEnvironment environment, ILogger<ProxyPolicyProvider> logger)
    {
        ContentRootPath = environment.ContentRootPath;
        this.logger = logger;
    }

    private string ContentRootPath { get; }

    public void Load(ProxyOptions options)
    {
        var blockedRules = new List<string>(options.Policy.BlockedHosts);
        var blockFile = ResolveOptionalPath(options.Policy.BlockedHostsFile);

        if (blockFile is not null && File.Exists(blockFile))
        {
            blockedRules.AddRange(File.ReadLines(blockFile));
            logger.LogInformation("Loaded blocked-host rules from {Path}", blockFile);
        }
        else if (blockFile is not null)
        {
            logger.LogDebug("Blocked-host file {Path} was not found; inline rules remain active", blockFile);
        }

        blockedHosts = new HostRuleMatcher(blockedRules);
        inspectedHosts = new HostRuleMatcher(options.Tls.InspectHosts);
        bypassedHosts = new HostRuleMatcher(options.Tls.BypassHosts);
    }

    public bool IsBlocked(string? host) => blockedHosts.IsMatch(host);

    public bool ShouldInspect(string? host, TlsInspectionMode mode) => mode switch
    {
        TlsInspectionMode.Tunnel => false,
        TlsInspectionMode.Selective => inspectedHosts.IsMatch(host) && !bypassedHosts.IsMatch(host),
        TlsInspectionMode.Intercept => !bypassedHosts.IsMatch(host),
        _ => false
    };

    private string? ResolveOptionalPath(string? path)
    {
        if (string.IsNullOrWhiteSpace(path))
        {
            return null;
        }

        return Path.IsPathRooted(path)
            ? Path.GetFullPath(path)
            : Path.GetFullPath(path, ContentRootPath);
    }
}
