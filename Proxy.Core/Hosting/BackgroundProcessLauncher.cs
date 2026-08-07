using System.ComponentModel;
using System.Diagnostics;

namespace Orbyn.Proxy.Core.Hosting;

internal sealed record BackgroundLaunchResult(bool Started, int? ProcessId, string? Error);

internal static class BackgroundProcessLauncher
{
    public static BackgroundLaunchResult Start(IEnumerable<string> originalArguments)
    {
        try
        {
            var executable = Environment.ProcessPath;
            if (string.IsNullOrWhiteSpace(executable))
            {
                return new BackgroundLaunchResult(false, null, "The current executable path is unavailable.");
            }

            var childArguments = BuildChildArguments(executable, originalArguments);
            var startInfo = CreateStartInfo(executable, childArguments);
            using var process = Process.Start(startInfo);
            return process is null
                ? new BackgroundLaunchResult(false, null, "The background process could not be created.")
                : new BackgroundLaunchResult(true, process.Id, null);
        }
        catch (Win32Exception exception)
        {
            return new BackgroundLaunchResult(false, null, exception.Message);
        }
        catch (InvalidOperationException exception)
        {
            return new BackgroundLaunchResult(false, null, exception.Message);
        }
    }

    private static List<string> BuildChildArguments(
        string executable,
        IEnumerable<string> originalArguments)
    {
        var arguments = new List<string>();
        if (IsDotnetHost(executable))
        {
            var managedApplication = Environment.GetCommandLineArgs().FirstOrDefault();
            if (string.IsNullOrWhiteSpace(managedApplication) ||
                !managedApplication.EndsWith(".dll", StringComparison.OrdinalIgnoreCase))
            {
                throw new InvalidOperationException("The managed application path is unavailable.");
            }

            arguments.Add(Path.GetFullPath(managedApplication));
        }

        arguments.AddRange(originalArguments.Where(argument => !IsModeArgument(argument)));
        arguments.Add("--service=true");
        arguments.Add("--background-child=true");
        return arguments;
    }

    private static ProcessStartInfo CreateStartInfo(string executable, IReadOnlyList<string> arguments)
    {
        ProcessStartInfo startInfo;
        if (OperatingSystem.IsLinux())
        {
            startInfo = new ProcessStartInfo("setsid");
            startInfo.ArgumentList.Add(executable);
        }
        else
        {
            startInfo = new ProcessStartInfo(executable);
        }

        foreach (var argument in arguments)
        {
            startInfo.ArgumentList.Add(argument);
        }

        startInfo.WorkingDirectory = AppContext.BaseDirectory;
        startInfo.CreateNoWindow = true;
        if (OperatingSystem.IsWindows())
        {
            startInfo.UseShellExecute = true;
            startInfo.WindowStyle = ProcessWindowStyle.Hidden;
        }
        else
        {
            startInfo.UseShellExecute = false;
            startInfo.RedirectStandardInput = true;
            startInfo.RedirectStandardOutput = true;
            startInfo.RedirectStandardError = true;
        }

        return startInfo;
    }

    private static bool IsDotnetHost(string executable) =>
        Path.GetFileNameWithoutExtension(executable).Equals("dotnet", StringComparison.OrdinalIgnoreCase);

    private static bool IsModeArgument(string argument) =>
        argument.StartsWith("--background", StringComparison.OrdinalIgnoreCase) ||
        argument.StartsWith("--service", StringComparison.OrdinalIgnoreCase) ||
        argument.StartsWith("--validate-config", StringComparison.OrdinalIgnoreCase) ||
        argument.StartsWith("--print-config", StringComparison.OrdinalIgnoreCase);
}
