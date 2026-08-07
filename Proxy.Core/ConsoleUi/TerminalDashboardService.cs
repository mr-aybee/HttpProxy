using System.Runtime.InteropServices;
using System.Text;
using Microsoft.Extensions.Options;
using Orbyn.Proxy.Core.Configuration;
using Orbyn.Proxy.Core.Hosting;
using Orbyn.Proxy.Core.Runtime;
using Orbyn.Proxy.Core.Services;

namespace Orbyn.Proxy.Core.ConsoleUi;

internal sealed class TerminalDashboardService(
    ProxyRuntimeState state,
    IOptions<ProxyOptions> options,
    IHostApplicationLifetime applicationLifetime,
    TitaniumProxyHostedService proxyService,
    ApplicationArguments applicationArguments,
    ExecutionTransitionState transitionState,
    ILogger<TerminalDashboardService> logger) : BackgroundService
{
    private DashboardView view = DashboardView.Main;
    private Task? activeAction;

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        state.SetActivityCapacity(options.Value.Terminal.MaxActivityEntries);
        using var terminal = TerminalSession.Enter();
        var refresh = TimeSpan.FromMilliseconds(options.Value.Terminal.RefreshMilliseconds);

        try
        {
            while (!stoppingToken.IsCancellationRequested)
            {
                terminal.Render(
                    state.GetSnapshot(),
                    view,
                    activeAction is not null,
                    options.Value.Background.LogFilePath);
                ObserveCompletedAction();
                HandleInput(stoppingToken);
                await Task.Delay(refresh, stoppingToken);
            }
        }
        catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
        {
        }
        finally
        {
            terminal.Render(
                state.GetSnapshot(),
                view,
                activeAction is not null,
                options.Value.Background.LogFilePath);
        }
    }

    private void HandleInput(CancellationToken stoppingToken)
    {
        try
        {
            while (Console.KeyAvailable)
            {
                var key = Console.ReadKey(intercept: true).Key;
                if (view != DashboardView.Main)
                {
                    HandleDialogInput(key, stoppingToken);
                    continue;
                }

                if (key is ConsoleKey.Q or ConsoleKey.Escape)
                {
                    state.MarkStopping();
                    applicationLifetime.StopApplication();
                    return;
                }

                if (key == ConsoleKey.C)
                {
                    state.ClearActivity();
                }

                if (key == ConsoleKey.R && activeAction is null)
                {
                    activeAction = RestartProxyAsync(stoppingToken);
                }

                if (key == ConsoleKey.B)
                {
                    view = DashboardView.BackgroundConfirmation;
                }

                if (key == ConsoleKey.S)
                {
                    view = DashboardView.ServiceInfo;
                }
            }
        }
        catch (InvalidOperationException)
        {
            // Input can disappear when a parent terminal is detached during shutdown.
        }
    }

    private void HandleDialogInput(ConsoleKey key, CancellationToken stoppingToken)
    {
        if (key == ConsoleKey.Escape ||
            (view == DashboardView.BackgroundConfirmation && key == ConsoleKey.B) ||
            (view == DashboardView.ServiceInfo && key == ConsoleKey.S))
        {
            view = DashboardView.Main;
            return;
        }

        if (view == DashboardView.BackgroundConfirmation &&
            key == ConsoleKey.Enter &&
            activeAction is null)
        {
            activeAction = MoveToBackgroundAsync(stoppingToken);
            view = DashboardView.Main;
        }

        if (view == DashboardView.ServiceInfo && key == ConsoleKey.B)
        {
            view = DashboardView.BackgroundConfirmation;
        }
    }

    private async Task RestartProxyAsync(CancellationToken cancellationToken)
    {
        try
        {
            await proxyService.RestartAsync(cancellationToken);
            logger.LogInformation("Proxy restart completed");
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
        }
        catch (Exception exception)
        {
            logger.LogError(exception, "Proxy restart failed");
        }
    }

    private async Task MoveToBackgroundAsync(CancellationToken cancellationToken)
    {
        try
        {
            logger.LogInformation("Moving Proxy.Core to the background");
            await proxyService.StopAsync(cancellationToken);
            var result = BackgroundProcessLauncher.Start(applicationArguments.Values);
            if (!result.Started)
            {
                logger.LogError("Could not start the background process: {Error}", result.Error);
                await proxyService.StartAsync(cancellationToken);
                return;
            }

            transitionState.ExitMessage =
                $"Proxy.Core continues in background (PID {result.ProcessId}). " +
                $"Log: {ResolveLogPath(options.Value.Background.LogFilePath)}";
            applicationLifetime.StopApplication();
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
        }
        catch (Exception exception)
        {
            logger.LogError(exception, "Could not move Proxy.Core to the background");
        }
    }

    private void ObserveCompletedAction()
    {
        if (activeAction?.IsCompleted == true)
        {
            activeAction = null;
        }
    }

    private static string ResolveLogPath(string path) => Path.IsPathRooted(path)
        ? Path.GetFullPath(path)
        : Path.GetFullPath(path, AppContext.BaseDirectory);

    private enum DashboardView
    {
        Main,
        BackgroundConfirmation,
        ServiceInfo
    }

    private sealed class TerminalSession : IDisposable
    {
        private const int StandardOutputHandle = -11;
        private const uint EnableVirtualTerminalProcessing = 0x0004;
        private const string Reset = "\u001b[0m";
        private const string Background = "\u001b[48;2;7;20;47m";
        private const string Foreground = "\u001b[38;2;226;232;240m";
        private const string Brand = "\u001b[38;2;37;99;235m";
        private const string Muted = "\u001b[38;2;148;163;184m";
        private const string Green = "\u001b[38;2;34;197;94m";
        private const string Yellow = "\u001b[38;2;250;204;21m";
        private const string Red = "\u001b[38;2;248;113;113m";
        private const string Cyan = "\u001b[38;2;34;211;238m";

        private readonly bool ansi;
        private readonly nint outputHandle;
        private readonly uint originalOutputMode;
        private readonly string? originalTitle;
        private bool disposed;

        private TerminalSession(bool ansi, nint outputHandle, uint originalOutputMode, string? originalTitle)
        {
            this.ansi = ansi;
            this.outputHandle = outputHandle;
            this.originalOutputMode = originalOutputMode;
            this.originalTitle = originalTitle;
        }

        public static TerminalSession Enter()
        {
            var originalTitle = TryGetTitle();
            TrySetTitle("Orbyn Proxy Core");

            var handle = nint.Zero;
            uint originalMode = 0;
            var ansi = !OperatingSystem.IsWindows();
            if (OperatingSystem.IsWindows())
            {
                handle = GetStdHandle(StandardOutputHandle);
                if (handle != nint.Zero && handle != new nint(-1) && GetConsoleMode(handle, out originalMode))
                {
                    ansi = SetConsoleMode(handle, originalMode | EnableVirtualTerminalProcessing);
                }
            }

            try
            {
                Console.CursorVisible = false;
                if (ansi)
                {
                    Console.Write("\u001b[?1049h\u001b[?25l\u001b[2J\u001b[H");
                }
                else
                {
                    Console.Clear();
                }
            }
            catch (IOException)
            {
            }

            return new TerminalSession(ansi, handle, originalMode, originalTitle);
        }

        public void Render(
            ProxyRuntimeSnapshot snapshot,
            DashboardView view,
            bool busy,
            string backgroundLogPath)
        {
            if (disposed)
            {
                return;
            }

            var width = GetConsoleWidth();
            var height = GetConsoleHeight();
            var frame = BuildFrame(snapshot, width, height, view, busy, backgroundLogPath);

            try
            {
                if (ansi)
                {
                    Console.Write("\u001b[H");
                    foreach (var line in frame)
                    {
                        Console.Write(Background);
                        Console.Write(line.Color);
                        Console.Write(line.Text);
                        Console.Write(Foreground);
                        Console.WriteLine();
                    }

                    Console.Write("\u001b[J");
                }
                else
                {
                    Console.SetCursorPosition(0, 0);
                    foreach (var line in frame)
                    {
                        Console.ForegroundColor = ToConsoleColor(line.Color);
                        Console.WriteLine(line.Text);
                    }
                }
            }
            catch (IOException)
            {
            }
            catch (ArgumentOutOfRangeException)
            {
                // A concurrent terminal resize can temporarily invalidate cursor coordinates.
            }
        }

        public void Dispose()
        {
            if (disposed)
            {
                return;
            }

            disposed = true;
            try
            {
                if (ansi)
                {
                    Console.Write($"{Reset}\u001b[?25h\u001b[?1049l");
                }
                else
                {
                    Console.ResetColor();
                    Console.Clear();
                    Console.CursorVisible = true;
                }

                if (OperatingSystem.IsWindows() && outputHandle != nint.Zero)
                {
                    SetConsoleMode(outputHandle, originalOutputMode);
                }

                if (originalTitle is not null)
                {
                    TrySetTitle(originalTitle);
                }
            }
            catch (IOException)
            {
            }
        }

        private static IReadOnlyList<DashboardLine> BuildFrame(
            ProxyRuntimeSnapshot snapshot,
            int width,
            int height,
            DashboardView view,
            bool busy,
            string backgroundLogPath)
        {
            var innerWidth = width - 2;
            var body = BuildHeader(innerWidth);

            if (view == DashboardView.BackgroundConfirmation)
            {
                body.AddRange(BuildBackgroundDialog(backgroundLogPath));
            }
            else if (view == DashboardView.ServiceInfo)
            {
                body.AddRange(BuildServiceDialog());
            }
            else
            {
                body.Add(Row(BuildStatus(snapshot), StatusColor(snapshot.Status)));
                body.Add(Row(BuildRuntime(snapshot), Foreground));
                body.Add(Divider(innerWidth));
                body.Add(Row("  LIVE TRAFFIC", Brand));
                body.Add(Row(BuildTraffic(snapshot), Foreground));
                body.Add(Divider(innerWidth));
                body.Add(Row("  ACTIVE LISTENERS", Brand));

                if (snapshot.Listeners.Count == 0)
                {
                    body.Add(Row("    ◌  Waiting for listeners...", Muted));
                }
                else
                {
                    foreach (var listener in snapshot.Listeners.Take(4))
                    {
                        body.Add(Row($"    ●  {listener}", Cyan));
                    }
                }

                body.Add(Divider(innerWidth));
                body.Add(Row("  RECENT ACTIVITY", Brand));

                var footerSize = 2;
                var activitySlots = Math.Max(0, height - body.Count - footerSize - 2);
                var visibleActivity = snapshot.Activity.TakeLast(activitySlots).ToArray();
                if (visibleActivity.Length == 0 && activitySlots > 0)
                {
                    body.Add(Row("    Nothing to report — proxy is ready", Muted));
                }
                else
                {
                    foreach (var entry in visibleActivity)
                    {
                        body.Add(Row(FormatActivity(entry), LogColor(entry.Level)));
                    }
                }
            }

            body.Add(Divider(innerWidth));
            body.Add(Row(BuildFooter(view, busy), busy ? Yellow : Muted));
            while (body.Count > height - 2 && body.Count > 0)
            {
                body.RemoveAt(body.Count - 3);
            }

            var lines = new List<DashboardLine>(body.Count + 2)
            {
                new("╭" + new string('─', innerWidth) + "╮", Brand)
            };
            lines.AddRange(body.Select(line => new DashboardLine(
                "│" + Fit(line.Text, innerWidth) + "│",
                line.Color)));
            lines.Add(new DashboardLine("╰" + new string('─', innerWidth) + "╯", Brand));
            return lines;
        }

        private static List<DashboardLine> BuildHeader(int width) =>
        [
            Row("  ORBYN TECHNOLOGIES                                      SECURE EDGE", Brand),
            Row($"  PROXY CORE  v{BrandConsole.GetVersion()}    HTTP(S) · SOCKS · POLICY ENGINE", Foreground),
            Divider(width)
        ];

        private static IEnumerable<DashboardLine> BuildBackgroundDialog(string logPath) =>
        [
            Row("", Foreground),
            Row("  MOVE TO BACKGROUND", Brand),
            Row("", Foreground),
            Row("  The active listener will be restarted in a detached headless process.", Foreground),
            Row($"  Activity will be written to: {logPath}", Cyan),
            Row("  Use systemd or Windows Service when automatic startup and recovery are required.", Muted),
            Row("", Foreground),
            Row("  Press Enter to continue, or B / Escape to cancel.", Yellow)
        ];

        private static IEnumerable<DashboardLine> BuildServiceDialog() =>
        [
            Row("", Foreground),
            Row("  SERVICE & HEADLESS MODE", Brand),
            Row("", Foreground),
            Row("  Direct:   Proxy.Core --service", Cyan),
            Row("  Linux:    install the documented systemd unit, then use systemctl.", Foreground),
            Row("  Windows:  install the published executable with Service Control Manager.", Foreground),
            Row("", Foreground),
            Row("  Managed services provide boot startup, restart policies, and OS-native logs.", Muted),
            Row("  Press B to run detached now, or S / Escape to return.", Yellow)
        ];

        private static DashboardLine Row(string text, string color) => new(text, color);

        private static DashboardLine Divider(int width) => new(new string('─', width), Muted);

        private static string BuildStatus(ProxyRuntimeSnapshot snapshot)
        {
            var uptime = snapshot.StartedAt is null
                ? "--:--:--"
                : FormatDuration(DateTimeOffset.UtcNow - snapshot.StartedAt.Value);
            return $"  {StatusMarker(snapshot.Status)}  {snapshot.Status.ToString().ToUpperInvariant(),-10} " +
                   $"{snapshot.StatusMessage,-40} UPTIME  {uptime}";
        }

        private static string BuildRuntime(ProxyRuntimeSnapshot snapshot) =>
            $"     TLS MODE  {snapshot.TlsMode,-12}    ACTIVE CONNECTIONS  {snapshot.ActiveConnections,8:N0}";

        private static string BuildTraffic(ProxyRuntimeSnapshot snapshot) =>
            $"     REQUESTS  {snapshot.Requests,10:N0}    TUNNELS  {snapshot.Tunnels,10:N0}    " +
            $"BLOCKED  {snapshot.Blocked,8:N0}    ERRORS  {snapshot.Errors,6:N0}";

        private static string BuildFooter(DashboardView view, bool busy)
        {
            if (busy)
            {
                return "  ◆  WORKING…   controls are temporarily locked";
            }

            return view == DashboardView.Main
                ? "  [R] Restart    [B] Background    [S] Service    [C] Clear    [Q] Quit"
                : "  [Escape] Back";
        }

        private static string FormatActivity(ProxyActivity entry)
        {
            var level = entry.Level switch
            {
                LogLevel.Trace => "TRC",
                LogLevel.Debug => "DBG",
                LogLevel.Information => "INF",
                LogLevel.Warning => "WRN",
                LogLevel.Error => "ERR",
                LogLevel.Critical => "CRT",
                _ => "---"
            };
            return $"    {entry.Timestamp:HH:mm:ss}  {level}  {entry.Source,-22}  {entry.Message}";
        }

        private static string FormatDuration(TimeSpan duration) => duration.TotalDays >= 1
            ? $"{(int)duration.TotalDays:00}.{duration:hh\\:mm\\:ss}"
            : duration.ToString("hh\\:mm\\:ss");

        private static string StatusMarker(ProxyRunStatus status) => status switch
        {
            ProxyRunStatus.Running => "●",
            ProxyRunStatus.Faulted => "!",
            ProxyRunStatus.Stopped => "■",
            ProxyRunStatus.Restarting => "↻",
            _ => "◆"
        };

        private static string StatusColor(ProxyRunStatus status) => status switch
        {
            ProxyRunStatus.Running => Green,
            ProxyRunStatus.Faulted => Red,
            ProxyRunStatus.Restarting => Yellow,
            ProxyRunStatus.Stopping => Yellow,
            ProxyRunStatus.Stopped => Muted,
            _ => Cyan
        };

        private static string LogColor(LogLevel level) => level switch
        {
            >= LogLevel.Error => Red,
            LogLevel.Warning => Yellow,
            LogLevel.Debug or LogLevel.Trace => Muted,
            _ => Foreground
        };

        private static ConsoleColor ToConsoleColor(string ansiColor) => ansiColor switch
        {
            Brand => ConsoleColor.Blue,
            Green => ConsoleColor.Green,
            Yellow => ConsoleColor.Yellow,
            Red => ConsoleColor.Red,
            Cyan => ConsoleColor.Cyan,
            Muted => ConsoleColor.DarkGray,
            _ => ConsoleColor.Gray
        };

        private static string Fit(string value, int width)
        {
            if (value.Length > width)
            {
                return width <= 1 ? value[..width] : value[..(width - 1)] + "…";
            }

            return value.PadRight(width);
        }

        private static int GetConsoleWidth()
        {
            try
            {
                return Math.Min(Math.Max(Console.WindowWidth - 1, 4), 159);
            }
            catch (IOException)
            {
                return 99;
            }
        }

        private static int GetConsoleHeight()
        {
            try
            {
                return Math.Min(Math.Max(Console.WindowHeight - 1, 4), 59);
            }
            catch (IOException)
            {
                return 29;
            }
        }

        private static string? TryGetTitle()
        {
            if (!OperatingSystem.IsWindows())
            {
                return null;
            }

            try
            {
                return Console.Title;
            }
            catch (IOException)
            {
                return null;
            }
        }

        private static void TrySetTitle(string title)
        {
            if (!OperatingSystem.IsWindows())
            {
                return;
            }

            try
            {
                Console.Title = title;
            }
            catch (IOException)
            {
            }
        }

        [DllImport("kernel32.dll", SetLastError = true)]
        [return: MarshalAs(UnmanagedType.Bool)]
        private static extern bool GetConsoleMode(nint consoleHandle, out uint mode);

        [DllImport("kernel32.dll", SetLastError = true)]
        private static extern nint GetStdHandle(int standardHandle);

        [DllImport("kernel32.dll", SetLastError = true)]
        [return: MarshalAs(UnmanagedType.Bool)]
        private static extern bool SetConsoleMode(nint consoleHandle, uint mode);

        private sealed record DashboardLine(string Text, string Color);
    }
}
