using Microsoft.Extensions.Options;
using Orbyn.Proxy.Core.Configuration;
using Orbyn.Proxy.Core.ConsoleUi;
using Orbyn.Proxy.Core.Logging;
using Orbyn.Proxy.Core.Policies;
using Orbyn.Proxy.Core.Runtime;
using Orbyn.Proxy.Core.Services;

namespace Orbyn.Proxy.Core;

internal static class Program
{
    public static async Task<int> Main(string[] args)
    {
        if (args.Any(IsHelpArgument))
        {
            BrandConsole.WriteHelp();
            return 0;
        }

        if (args.Any(argument => argument.Equals("--version", StringComparison.OrdinalIgnoreCase)))
        {
            Console.WriteLine(BrandConsole.GetVersion());
            return 0;
        }

        var normalizedArgs = NormalizeBooleanSwitches(args);

        try
        {
            var builder = Host.CreateApplicationBuilder(new HostApplicationBuilderSettings
            {
                Args = normalizedArgs,
                ContentRootPath = AppContext.BaseDirectory
            });
            builder.Configuration.AddProxyCommandLine(normalizedArgs);

            var serviceMode = builder.Configuration.GetValue<bool>("Commands:Service");
            var controlCommand = builder.Configuration.GetValue<bool>("Commands:PrintConfig") ||
                                 builder.Configuration.GetValue<bool>("Commands:ValidateConfig");
            var terminalMode = !serviceMode && !controlCommand && CanOwnTerminal();
            var runtimeState = new ProxyRuntimeState();

            builder.Services.AddWindowsService(serviceOptions =>
                serviceOptions.ServiceName = "Orbyn Proxy Core");
            builder.Services.AddSystemd();

            if (terminalMode)
            {
                builder.Logging.ClearProviders();
                builder.Logging.AddProvider(new DashboardLoggerProvider(runtimeState));
                builder.Services.Configure<ConsoleLifetimeOptions>(lifetime =>
                    lifetime.SuppressStatusMessages = true);
            }

            builder.Services
                .AddOptions<ProxyOptions>()
                .Bind(builder.Configuration.GetRequiredSection(ProxyOptions.SectionName), binder =>
                    binder.ErrorOnUnknownConfiguration = true)
                .ValidateOnStart();
            builder.Services.AddSingleton<IValidateOptions<ProxyOptions>, ProxyOptionsValidator>();
            builder.Services.AddSingleton(runtimeState);
            builder.Services.AddSingleton<ProxyPolicyProvider>();
            if (terminalMode)
            {
                builder.Services.AddHostedService<TerminalDashboardService>();
            }

            builder.Services.AddHostedService<TitaniumProxyHostedService>();

            var drainSeconds = builder.Configuration.GetValue<int>(
                "Proxy:Limits:ShutdownDrainSeconds",
                30);
            builder.Services.Configure<HostOptions>(hostOptions =>
                hostOptions.ShutdownTimeout = TimeSpan.FromSeconds(Math.Max(5, drainSeconds + 5)));

            using var host = builder.Build();
            var options = host.Services.GetRequiredService<IOptions<ProxyOptions>>().Value;

            if (builder.Configuration.GetValue<bool>("Commands:PrintConfig"))
            {
                ConfigurationPrinter.WriteRedacted(options, serviceMode);
                return 0;
            }

            if (builder.Configuration.GetValue<bool>("Commands:ValidateConfig"))
            {
                Console.WriteLine("Configuration is valid.");
                return 0;
            }

            await host.RunAsync();
            return 0;
        }
        catch (OptionsValidationException exception)
        {
            Console.Error.WriteLine("Configuration is invalid:");
            foreach (var failure in exception.Failures)
            {
                Console.Error.WriteLine($"  - {failure}");
            }

            return 2;
        }
        catch (Exception exception)
        {
            Console.Error.WriteLine($"Proxy.Core failed: {exception.Message}");
            return 1;
        }
    }

    private static string[] NormalizeBooleanSwitches(string[] args) => args
        .Select(argument => argument.Equals("--validate-config", StringComparison.OrdinalIgnoreCase) ||
                            argument.Equals("--print-config", StringComparison.OrdinalIgnoreCase) ||
                            argument.Equals("--service", StringComparison.OrdinalIgnoreCase)
            ? argument + "=true"
            : argument)
        .ToArray();

    private static bool IsHelpArgument(string argument) =>
        argument.Equals("--help", StringComparison.OrdinalIgnoreCase) ||
        argument.Equals("-h", StringComparison.OrdinalIgnoreCase) ||
        argument.Equals("/?", StringComparison.OrdinalIgnoreCase);

    private static bool CanOwnTerminal() =>
        Environment.UserInteractive &&
        !Console.IsInputRedirected &&
        !Console.IsOutputRedirected;
}
