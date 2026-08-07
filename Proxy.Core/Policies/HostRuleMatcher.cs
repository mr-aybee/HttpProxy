using System.Globalization;
using System.Net;

namespace Orbyn.Proxy.Core.Policies;

internal sealed class HostRuleMatcher
{
    private readonly HashSet<string> exactRules = new(StringComparer.OrdinalIgnoreCase);
    private readonly string[] suffixRules;

    public HostRuleMatcher(IEnumerable<string> rules)
    {
        suffixRules = BuildRules(rules);
    }

    public bool IsMatch(string? host)
    {
        var normalized = NormalizeHost(host);
        if (normalized is null)
        {
            return false;
        }

        return exactRules.Contains(normalized) ||
               suffixRules.Any(suffix => normalized.EndsWith(suffix, StringComparison.OrdinalIgnoreCase));
    }

    private string[] BuildRules(IEnumerable<string> rules)
    {
        var suffixes = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        foreach (var rawRule in rules)
        {
            var rule = rawRule.Trim();
            if (rule.Length == 0 || rule.StartsWith('#'))
            {
                continue;
            }

            var isSuffix = rule.StartsWith("*.", StringComparison.Ordinal);
            var normalized = NormalizeHost(isSuffix ? rule[2..] : rule);
            if (normalized is null)
            {
                continue;
            }

            if (isSuffix)
            {
                exactRules.Add(normalized);
                suffixes.Add('.' + normalized);
            }
            else
            {
                exactRules.Add(normalized);
            }
        }

        return suffixes.ToArray();
    }

    private static string? NormalizeHost(string? value)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            return null;
        }

        var candidate = value.Trim();
        if (candidate.Contains("://", StringComparison.Ordinal) &&
            Uri.TryCreate(candidate, UriKind.Absolute, out var uri))
        {
            candidate = uri.Host;
        }

        if (candidate.StartsWith('[') && candidate.Contains(']'))
        {
            candidate = candidate[1..candidate.IndexOf(']')];
        }
        else if (candidate.Count(character => character == ':') == 1)
        {
            candidate = candidate[..candidate.IndexOf(':')];
        }

        candidate = candidate.TrimEnd('.');
        if (candidate.Length == 0)
        {
            return null;
        }

        if (IPAddress.TryParse(candidate, out var address))
        {
            return address.ToString();
        }

        try
        {
            return new IdnMapping().GetAscii(candidate).ToLowerInvariant();
        }
        catch (ArgumentException)
        {
            return null;
        }
    }
}
