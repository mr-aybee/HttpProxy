using System.Reflection;

namespace Orbyn.Proxy.Core.ConsoleUi;

internal static class BrandConsole
{
    public static void WriteBanner()
    {
        var original = Console.ForegroundColor;
        Console.ForegroundColor = ConsoleColor.Blue;
        Console.Write("Orbyn Technologies");
        Console.ForegroundColor = original;
        Console.WriteLine($"  Proxy.Core {GetVersion()}");
    }

    public static void WriteHelp()
    {
        WriteBanner();
        Console.WriteLine("Usage: Proxy.Core [options] [--Proxy:<section>:<setting>=<value>]");
        Console.WriteLine();
        Console.WriteLine("Common options:");
        Console.WriteLine("  --listen <ip>                  Explicit listener address");
        Console.WriteLine("  --port <port>                  Explicit listener port");
        Console.WriteLine("  --tls-mode <mode>              Tunnel, Selective, or Intercept");
        Console.WriteLine("  --certificate <path>           Root CA PFX path");
        Console.WriteLine("  --certificate-source <source>  Auto or File");
        Console.WriteLine("  --trust-current-user <bool>     Trust generated CA for current user");
        Console.WriteLine("  --trust-local-machine <bool>    Trust generated CA for local machine");
        Console.WriteLine("  --http2 <bool>                 Enable HTTP/2");
        Console.WriteLine("  --http3 <bool>                 Enable experimental HTTP/3");
        Console.WriteLine("  --service                      Run headless with OS-service logging");
        Console.WriteLine("  --validate-config              Validate and exit");
        Console.WriteLine("  --print-config                 Print merged redacted configuration and exit");
        Console.WriteLine("  --help                         Show this help");
        Console.WriteLine("  --version                      Show the application version");
        Console.WriteLine();
        Console.WriteLine("Advanced example:");
        Console.WriteLine("  --Proxy:Limits:MaxBufferedBodyBytes=4194304");
    }

    public static string GetVersion() =>
        Assembly.GetExecutingAssembly().GetName().Version?.ToString(3) ?? "1.0.0";
}
