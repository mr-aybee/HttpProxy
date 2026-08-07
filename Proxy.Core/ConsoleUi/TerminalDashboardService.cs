using System.Runtime.InteropServices;
using System.Text;
using Microsoft.Extensions.Options;
using Orbyn.Proxy.Core.Configuration;
using Orbyn.Proxy.Core.Runtime;

namespace Orbyn.Proxy.Core.ConsoleUi;

internal sealed class TerminalDashboardService(
    ProxyRuntimeState state,
    IOptions<ProxyOptions> options,
    IHostApplicationLifetime applicationLifetime) : BackgroundService
{
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        state.SetActivityCapacity(options.Value.Terminal.MaxActivityEntries);
        using var terminal = TerminalSession.Enter();
        var refresh = TimeSpan.FromMilliseconds(options.Value.Terminal.RefreshMilliseconds);

        try
        {
            while (!stoppingToken.IsCancellationRequested)
            {
                terminal.Render(state.GetSnapshot());
                HandleInput();
                await Task.Delay(refresh, stoppingToken);
            }
        }
        catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
        {
        }
        finally
        {
            terminal.Render(state.GetSnapshot());
        }
    }

    private void HandleInput()
    {
        try
        {
            while (Console.KeyAvailable)
            {
                var key = Console.ReadKey(intercept: true).Key;
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
            }
        }
        catch (InvalidOperationException)
        {
            // Input can disappear when a parent terminal is detached during shutdown.
        }
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

        public void Render(ProxyRuntimeSnapshot snapshot)
        {
            if (disposed)
            {
                return;
            }

            var width = GetConsoleWidth();
            var height = GetConsoleHeight();
            var frame = BuildFrame(snapshot, width, height);

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
            int height)
        {
            var innerWidth = width - 2;
            var body = new List<DashboardLine>
            {
                Row(" ORBYN TECHNOLOGIES", Brand),
                Row($" Proxy Core {BrandConsole.GetVersion()}  /  HTTP(S) Gateway", Muted),
                Divider(innerWidth),
                Row(BuildStatus(snapshot), StatusColor(snapshot.Status)),
                Row(BuildMetrics(snapshot), Foreground),
                Divider(innerWidth),
                Row(" LISTENERS", Brand)
            };

            var listenerLimit = Math.Max(1, Math.Min(snapshot.Listeners.Count, 4));
            if (snapshot.Listeners.Count == 0)
            {
                body.Add(Row("   Waiting for listeners...", Muted));
            }
            else
            {
                foreach (var listener in snapshot.Listeners.Take(listenerLimit))
                {
                    body.Add(Row($"   > {listener}", Cyan));
                }
            }

            body.Add(Divider(innerWidth));
            body.Add(Row(" RECENT ACTIVITY", Brand));

            var footer = new[]
            {
                Divider(innerWidth),
                Row(" [Q] Quit    [C] Clear activity    [Ctrl+C] Stop", Muted)
            };
            var activitySlots = Math.Max(0, height - body.Count - footer.Length - 2);
            var visibleActivity = snapshot.Activity.TakeLast(activitySlots).ToArray();

            if (visibleActivity.Length == 0 && activitySlots > 0)
            {
                body.Add(Row("   No activity yet", Muted));
            }
            else
            {
                foreach (var entry in visibleActivity)
                {
                    body.Add(Row(FormatActivity(entry), LogColor(entry.Level)));
                }
            }

            body.AddRange(footer);
            while (body.Count > height - 2 && body.Count > 0)
            {
                body.RemoveAt(body.Count - footer.Length - 1);
            }

            var lines = new List<DashboardLine>(body.Count + 2)
            {
                new("┌" + new string('─', innerWidth) + "┐", Brand)
            };
            lines.AddRange(body.Select(line => new DashboardLine(
                "│" + Fit(line.Text, innerWidth) + "│",
                line.Color)));
            lines.Add(new DashboardLine("└" + new string('─', innerWidth) + "┘", Brand));
            return lines;
        }

        private static DashboardLine Row(string text, string color) => new(text, color);

        private static DashboardLine Divider(int width) => new(new string('─', width), Muted);

        private static string BuildStatus(ProxyRuntimeSnapshot snapshot)
        {
            var uptime = snapshot.StartedAt is null
                ? "--:--:--"
                : FormatDuration(DateTimeOffset.UtcNow - snapshot.StartedAt.Value);
            return $" {StatusMarker(snapshot.Status)} {snapshot.Status.ToString().ToUpperInvariant(),-9}  " +
                   $"{snapshot.StatusMessage}    UPTIME {uptime}";
        }

        private static string BuildMetrics(ProxyRuntimeSnapshot snapshot) =>
            $" TLS {snapshot.TlsMode,-9}  CONNECTIONS {snapshot.ActiveConnections,5:N0}  " +
            $"REQUESTS {snapshot.Requests,7:N0}  TUNNELS {snapshot.Tunnels,7:N0}  " +
            $"BLOCKED {snapshot.Blocked,5:N0}  ERRORS {snapshot.Errors,4:N0}";

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
            return $" {entry.Timestamp:HH:mm:ss}  {level}  {entry.Source,-24} {entry.Message}";
        }

        private static string FormatDuration(TimeSpan duration) => duration.TotalDays >= 1
            ? $"{(int)duration.TotalDays:00}.{duration:hh\\:mm\\:ss}"
            : duration.ToString("hh\\:mm\\:ss");

        private static string StatusMarker(ProxyRunStatus status) => status switch
        {
            ProxyRunStatus.Running => "●",
            ProxyRunStatus.Faulted => "!",
            ProxyRunStatus.Stopped => "■",
            _ => "◆"
        };

        private static string StatusColor(ProxyRunStatus status) => status switch
        {
            ProxyRunStatus.Running => Green,
            ProxyRunStatus.Faulted => Red,
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
