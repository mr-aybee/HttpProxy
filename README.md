# Orbyn Proxy Core

`Proxy.Core` is a .NET 10 Worker Service from Orbyn Technologies using Titanium.Web.Proxy 5.2.0. It provides a full-screen terminal control surface, a detachable background mode, and a headless host for Windows Service, systemd, containers, and process supervisors.

The original .NET Framework WinForms application remains in `ProxyServer`; new development should target `Proxy.Core`.

## Run

```powershell
dotnet run --project Proxy.Core/Proxy.Core.csproj
```

An interactive launch takes ownership of the terminal and displays live status, listeners, connections, traffic counters, and recent activity without scrolling the console. Available controls:

- `R` safely restarts the proxy and reloads the current configuration.
- `B` opens confirmation to move the proxy into a detached background process.
- `S` displays service and headless-hosting guidance.
- `C` clears recent activity.
- `Q`, `Escape`, or `Ctrl+C` performs a graceful shutdown.

The safe default listens on `127.0.0.1:12000`, tunnels HTTPS without decrypting it, does not alter the system proxy, and does not trust or generate a certificate until TLS inspection is enabled.

Common overrides:

```powershell
# Different address and port
dotnet run --project Proxy.Core/Proxy.Core.csproj -- --listen 0.0.0.0 --port 8080

# Selective TLS inspection; generates the configured PFX when it is missing
dotnet run --project Proxy.Core/Proxy.Core.csproj -- `
  --tls-mode Selective `
  --Proxy:Tls:InspectHosts:0=example.com `
  --Proxy:Tls:InspectHosts:1=*.example.org

# Inspect all HTTPS except matching bypass rules
dotnet run --project Proxy.Core/Proxy.Core.csproj -- `
  --tls-mode Intercept `
  --Proxy:Tls:BypassHosts:0=*.bank.example

# Inspect the merged settings without exposing passwords
dotnet run --project Proxy.Core/Proxy.Core.csproj -- --print-config
```

Run `dotnet run --project Proxy.Core/Proxy.Core.csproj -- --help` for the short CLI reference.

### Secure remote listener on Linux

The default `127.0.0.1` listener accepts only local connections. To accept remote connections, bind to all interfaces and require authentication:

```bash
export HTTPPROXY_AUTH_PASSWORD='use-a-strong-password'

./Proxy.Core \
  --listen 0.0.0.0 \
  --port 12000 \
  --Proxy:Authentication:Enabled=true \
  --Proxy:Authentication:Username=proxyuser \
  --Proxy:Policy:BlockPrivateNetworkDestinations=true
```

Replace the example password and restrict inbound TCP port `12000` in both the Linux firewall and hosting-provider firewall to trusted client IP addresses. Never expose an unauthenticated proxy publicly.

## Background mode

Press `B` in the terminal dashboard or launch directly with `--background`:

```bash
./Proxy.Core --background
```

Proxy.Core starts a detached headless child, reports its process ID, and writes rolling logs to `logs/proxy-core.log`. Configure the path and retention through `Proxy:Background` in `appsettings.json`.

Background mode is intended for temporary detached execution. Use systemd or Windows Service for boot startup, health supervision, automatic recovery, and OS-native service logs.

## Service mode

`--service` disables the terminal dashboard and uses headless logging. When hosted by Windows Service Control Manager, logs are sent to Windows Event Log. Under systemd, logs and lifecycle notifications integrate with the journal. When `--service` is started manually or by another supervisor, concise single-line logs are written to standard output.

Windows Service support activates automatically when Service Control Manager launches the executable. Publish first, then run the following from an elevated PowerShell prompt, replacing the example path:

```powershell
dotnet publish Proxy.Core/Proxy.Core.csproj -c Release -r win-x64 `
  --self-contained true -p:PublishSingleFile=true `
  -o C:\Services\OrbynProxyCore

sc.exe create OrbynProxyCore `
  binPath= '"C:\Services\OrbynProxyCore\Proxy.Core.exe" --service' `
  start= auto `
  DisplayName= 'Orbyn Proxy Core'

sc.exe start OrbynProxyCore
```

Example systemd unit at `/etc/systemd/system/orbyn-proxy-core.service`:

```ini
[Unit]
Description=Orbyn Proxy Core
After=network-online.target
Wants=network-online.target

[Service]
Type=notify
WorkingDirectory=/opt/orbyn/proxy-core
ExecStart=/opt/orbyn/proxy-core/Proxy.Core --service
Restart=always
RestartSec=5
User=orbyn-proxy
Environment=DOTNET_ENVIRONMENT=Production

[Install]
WantedBy=multi-user.target
```

The service account needs write access to the configured certificate directory when automatic TLS certificate generation is enabled.

## Configuration precedence

Every property under `Proxy` in [appsettings.json](Proxy.Core/appsettings.json) can be overridden with a hierarchical CLI parameter or environment variable. Highest precedence wins:

1. CLI, such as `--Proxy:Limits:MaxBufferedBodyBytes=8388608`
2. `HTTPPROXY_`-prefixed environment variables, such as `HTTPPROXY_Proxy__Tls__Mode=Intercept`
3. Standard .NET environment variables and command-line configuration
4. `appsettings.{Environment}.json`
5. `appsettings.json`

Use double underscores for nested environment keys. Keep secrets out of command history and JSON by using the configured secret environment variables:

- `HTTPPROXY_CERT_PASSWORD`
- `HTTPPROXY_AUTH_PASSWORD`
- `HTTPPROXY_UPSTREAM_HTTP_PASSWORD`
- `HTTPPROXY_UPSTREAM_HTTPS_PASSWORD`

Validate merged settings without opening a listener:

```powershell
dotnet run --project Proxy.Core/Proxy.Core.csproj -- --validate-config
```

## HTTPS certificates

`Proxy:Tls:Certificate:Source` defaults to `Auto`. In `Selective` or `Intercept` mode, Proxy.Core loads the configured PFX if present; otherwise it generates and reuses `certificates/orbyn-proxy-root.pfx`. `Source=File` requires an existing PFX. Tunnel mode needs no certificate.

Certificate trust is deliberately opt-in. Set `TrustCurrentUser=true` only on machines you administer. `TrustLocalMachine=true` requires sufficient OS permissions. Other clients must import the generated root CA themselves before they can accept intercepted HTTPS traffic. Never distribute the PFX private key as a client trust certificate.

## Host policies

Add block rules to [BlockedHosts.txt](Proxy.Core/BlockedHosts.txt) or `Proxy:Policy:BlockedHosts`. Rules are exact host names or `*.example.com`; a wildcard rule includes both the root domain and its subdomains. The same syntax applies to TLS inspect and bypass rules.

`BlockPrivateNetworkDestinations=true` prevents proxy clients from reaching private network targets and is recommended when exposing a listener beyond localhost. When binding to `0.0.0.0`, also enable proxy authentication and apply firewall restrictions.

## Publish

Framework-dependent:

```powershell
dotnet publish Proxy.Core/Proxy.Core.csproj -c Release
```

Self-contained single-file for Windows x64:

```powershell
dotnet publish Proxy.Core/Proxy.Core.csproj -c Release -r win-x64 `
  --self-contained true -p:PublishSingleFile=true
```

Self-contained single-file for Linux x64, when running from the repository root (`D:\Repos\HttpProxy`):

```bash
dotnet publish Proxy.Core/Proxy.Core.csproj -c Release -r linux-x64 \
  --self-contained true -p:PublishSingleFile=true \
  -o ./publish/linux-x64
```

When running from the project directory (`D:\Repos\HttpProxy\Proxy.Core`) in PowerShell:

```powershell
dotnet publish .\Proxy.Core.csproj -c Release -r linux-x64 `
  --self-contained true -p:PublishSingleFile=true `
  -o .\publish\linux-x64
```

After copying the published files to Linux, ensure the executable can run:

```bash
chmod +x /opt/orbyn/proxy-core/Proxy.Core
```

The same published executable supports both the terminal dashboard and service hosting; the runtime environment and `--service` flag select the experience.
