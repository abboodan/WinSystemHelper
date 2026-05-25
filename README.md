# WinSystemHelper: A Hybrid .NET 8 Windows Service for Remote System Management via Telegram

WinSystemHelper is a personal Windows device management utility built as a hybrid .NET 8 executable. The same binary can run as an interactive setup tool, a silent installer/uninstaller, or a long-running Windows Worker Service controlled through a private Telegram bot.

## Features

- Hybrid self-installing executable with interactive setup, silent install, silent uninstall, and Windows Service modes.
- Small framework-dependent app package with a NativeAOT bootstrapper that installs missing .NET 8 runtime prerequisites.
- Telegram Bot integration for authenticated remote administration.
- Multi-admin command handling using the configured Telegram chat ID allowlist.
- First-run setup wizard for interactive configuration when `config.json` is missing.
- Startup and wake-from-sleep alerts.
- Shutdown, restart, and sleep transition alerts with a persistent next-boot outbox.
- Modern Standby-compatible wake detection through the Windows System event log.
- Fail-fast Telegram configuration validation to avoid endless retries on invalid credentials.
- Resilient Telegram polling with exponential backoff and strict network timeouts.
- Session 0-aware command execution for workstation lock and alert sound behavior using active-user-session process launching.
- Interactive active-user prompts using Base64-encoded PowerShell UI scripts.
- Overt active-alarm microphone recording with local visual/audio warnings.
- Optional persistent active-alarm mic loop restore after reboot, controlled by `PersistentMicLoopEnabled`.
- Admin-only OTA self-update from a Telegram ZIP document or HTTPS ZIP URL.
- GitHub Release-based OTA updates with `/update github` for public repositories.
- Confirmation gates and cooldowns for dangerous remote commands.
- Lightweight health, network, service, disk, battery, and public-IP monitoring without WMI-heavy polling.
- Runtime configuration and admin management through Telegram without reinstalling the service.
- Emoji-rich Telegram responses with a native Telegram command menu.
- Local configuration through `config.json` stored beside the executable.

## Available Commands

| Command | Description |
| --- | --- |
| `/status` | Shows the full low-overhead system, runtime, service, battery, and wake watcher dashboard. |
| `/healthcheck` | Shows a fast health summary with cached state and recent failures. |
| `/version` | Shows the installed WinSystemHelper version. |
| `/version check` | Checks the latest GitHub Release version and reports whether an update is available. |
| `/ping` | Returns a lightweight online response with machine name and timestamp. |
| `/confirm [Id]` | Confirms a pending dangerous command. |
| `/cancel [Id]` | Cancels a pending dangerous command. |
| `/lock` | Locks the active Windows workstation. |
| `/shutdown` | Requests confirmation before graceful shutdown after 10 seconds. |
| `/restart` | Requests confirmation before system restart after 10 seconds. |
| `/sleep` | Requests confirmation before putting the workstation to sleep. |
| `/ip` | Returns the cached public IP or refreshes it with a short timeout. |
| `/ip refresh` | Forces a public IP refresh subject to timeout and backoff. |
| `/net` | Shows active local adapters, local IPs, gateways, DNS, and cached public IP. |
| `/alarm` | Plays a system alert sound through the active user session. |
| `/mic [seconds]` | Triggers an overt active alarm and returns an audio recording. |
| `/mic [seconds] loop` | Starts a persistent overt active alarm loop. |
| `/mic stop` | Stops the persistent active alarm loop. |
| `/msg [text]` | Shows a warning message on the active user's screen. |
| `/ask [text]` | Shows a Yes/No question and returns the user's answer. |
| `/prompt [text]` | Forces a text response from the active user and returns it to Telegram. |
| `/speak [text]` | Speaks a message through the active user session. |
| `/screen` | Captures the active user's primary screen and returns the image. |
| `/cam` | Shows overt local warnings, captures one camera photo if available, and returns it. |
| `/tasks` | Lists the top memory-consuming processes. |
| `/kill [ProcessName]` | Terminates matching process instances by name. |
| `/startup` | Lists startup applications from machine and active-user registry run keys. |
| `/restartapp [ProcessName]` | Restarts a target app in the active user's visible session. |
| `/services` | Lists Windows services. |
| `/service status\|start\|stop\|restart [ServiceName]` | Queries or manages a Windows service. |
| `/config` | Shows safe runtime configuration without exposing the bot token. |
| `/config export` | Shows a safe `config.json` preview without exposing the bot token. |
| `/config alerts on\|off` | Enables or disables smart alerts. |
| `/config set [Key] [Value]` | Updates runtime configuration in `config.json`. |
| `/config admins` | Lists configured Telegram admins. |
| `/config admin add\|remove [ChatId]` | Adds or removes Telegram admins without reinstalling. |
| `/update [https-url]` | Applies an OTA update from an attached ZIP document or HTTPS ZIP URL. |
| `/update github` | Downloads and applies the latest GitHub Release OTA package. |
| `/restartself` | Requests confirmation before restarting the WinSystemHelper service. |
| `/repair` | Runs diagnostics, safe repairs, command-menu registration, and temp cleanup. |
| `/reinstall` | Requests confirmation before recreating the Windows Service from the current executable. |
| `/rollback` | Requests confirmation before restoring the previous persistent OTA backup. |
| `/help` | Shows the available remote commands. |
| `/stop` | Requests confirmation before stopping the WinSystemHelper service. |
| `/uninstall` | Requests confirmation before stopping and deleting the WinSystemHelper service. |

## Build

Install the .NET 8 SDK, then create the small framework-dependent release package:

```powershell
.\build.cmd
```

The NativeAOT bootstrapper publish requires the Visual Studio **Desktop development with C++** workload because Windows NativeAOT needs the platform linker. If that workload is not installed, `install.ps1` remains available as the script-based fallback.

The clean release package will be under:

```text
dist\
├─ WinSystemHelper.Bootstrapper.exe
├─ WinSystemHelper.exe
├─ install.ps1
└─ README.md
```

The default package is framework-dependent to keep the app small. `WinSystemHelper.Bootstrapper.exe` checks for the .NET 8 x64 Runtime, downloads it from Microsoft if missing, installs it silently, then launches `WinSystemHelper.exe`.

If you need a fully self-contained package for offline deployment, publish manually with:

```powershell
dotnet publish .\WinSystemHelper.csproj -c Release -r win-x64 --self-contained true /p:PublishSingleFile=true /p:IncludeNativeLibrariesForSelfExtract=true
```

## Versioning

The project uses semantic versioning in `WinSystemHelper.csproj`. Each functional change should bump the version according to impact:

- Patch: small fixes and low-risk internal corrections.
- Minor: new commands, configuration options, alerts, or remote-management features.
- Major: breaking configuration changes or behavior changes that require operator action.

Current version: `1.5.8`.

## Installation

Run PowerShell or Command Prompt as Administrator, then install the service with your Telegram bot token and primary admin chat ID:

```powershell
.\WinSystemHelper.Bootstrapper.exe /install /token <YOUR_TOKEN> /chatid <YOUR_CHAT_ID>
```

The bootstrapper installs missing runtime prerequisites, preserves the existing silent install flow, writes `config.json` next to the executable using the multi-admin format, creates the `WinSystemHelper` Windows Service, and starts it automatically.

If the native bootstrapper is unavailable, use the PowerShell fallback from the same folder:

```powershell
powershell -ExecutionPolicy Bypass -File .\install.ps1 /install /token <YOUR_TOKEN> /chatid <YOUR_CHAT_ID>
```

The generated configuration stores the provided chat ID as an array:

```json
{
  "botToken": "<YOUR_TOKEN>",
  "adminChatIds": [
    987654321
  ]
}
```

## Interactive Setup

Run the executable without arguments from an elevated console on first run:

```powershell
.\WinSystemHelper.Bootstrapper.exe
```

If `config.json` is missing, the app starts the first-run setup wizard, prompts for your Telegram bot token, prompts for the primary admin chat ID, optionally accepts additional admin chat IDs, saves them to `config.json`, installs the service, and starts it.

If `config.json` already exists, the executable runs normally as the Worker Service host.

## Uninstall

Run from an elevated shell:

```powershell
.\WinSystemHelper.Bootstrapper.exe /uninstall
```

## Configuration

Runtime credentials are stored in:

```text
config.json
```

This file is intentionally excluded from Git. Do not commit bot tokens, chat IDs, appsettings files, logs, or local build output.

The current configuration format supports multiple Telegram admins:

```json
{
  "botToken": "<YOUR_TOKEN>",
  "adminChatIds": [
    111111111,
    222222222
  ]
}
```

Older single-admin `adminChatId` configurations are still accepted for compatibility, but new installs and the setup wizard write `adminChatIds`.

Runtime settings can be changed through Telegram without reinstalling:

```text
/config set BatteryLowPercent 15
/config set AlertsEnabled true
/config alerts off
/config admin add 333333333
```

Supported runtime keys include `AlertsEnabled`, `BatteryLowPercent`, `DiskLowPercent`, `HealthCheckIntervalMinutes`, `PublicIpCacheMinutes`, `PublicIpFailureBackoffMinutes`, `DangerousCommandConfirmationSeconds`, `DangerousCommandCooldownSeconds`, `MicCooldownSeconds`, and `AllowCrossAdminConfirmations`.

Dangerous commands such as shutdown, restart, sleep, update, process termination, service control, stop, and uninstall require `/confirm [Id]` before execution.

## Power Event Alerts

Shutdown, restart, and sleep commands sent through Telegram broadcast an alert before the system action starts. The service also watches the Windows System event log for local shutdown/restart requests and sleep transitions, then attempts to notify all configured admins.

If the machine powers off before Telegram delivery completes, a small JSON report is stored under the shared `PowerEvents` temp folder and sent on the next service startup. Power event alerts can be controlled remotely:

```text
/config set PowerEventAlertsEnabled false
/config set PowerEventOutboxEnabled true
/config set PowerCommandPreDelaySeconds 3
```

## Persistent Active Alarm Mic Loop

When `PersistentMicLoopEnabled` is true, `/mic [seconds] loop` stores only the active loop state under the shared `Audio` folder. If the laptop shuts down or restarts while the loop is active, the service restores the overt recording loop after startup and continues until an admin sends:

```text
/mic stop
```

The feature does not persist audio files. Each recording cycle still shows the local visual warning and plays the audible warning before recording.

## OTA Self-Update

Authorized admins can update an endpoint remotely with a ZIP package:

```text
/update https://example.com/update.zip
```

For a public GitHub repository with Releases enabled, admins can update from the latest GitHub Release:

```text
/update github
```

The default GitHub Release settings are:

```json
{
  "gitHubOwner": "abboodan",
  "gitHubRepo": "WinSystemHelper",
  "gitHubReleaseAssetName": "WinSystemHelper-ota.zip"
}
```

These can be changed remotely without reinstalling:

```text
/config set GitHubOwner abboodan
/config set GitHubRepo WinSystemHelper
/config set GitHubReleaseAssetName WinSystemHelper-ota.zip
```

You can also check the latest available Release without updating:

```text
/version check
```

Or attach a `.zip` file in Telegram with this caption:

```text
/update
```

The ZIP may contain the published files directly at its root or inside one top-level folder. For the small framework-dependent package, zip the contents of `dist\`, including `WinSystemHelper.exe`, `WinSystemHelper.Bootstrapper.exe`, and `install.ps1`. The service stages and extracts the package, preserves the existing `config.json`, stops itself, copies the new files into `AppContext.BaseDirectory`, restarts the `WinSystemHelper` service, and reports the result to all configured admins on the next startup.

Telegram document updates are intended for small packages only. If the ZIP is larger than the Telegram Bot API download limit, use `/update github` or `/update https://...` instead of attaching the file in Telegram.

Updates use the existing admin chat allowlist as the trust boundary. The updater creates a temporary backup before copying files and refreshes a persistent `Backups\previous` copy that `/rollback` can use later. If an update copy fails, the updater still attempts immediate rollback from the temporary backup.

## Maintenance

WinSystemHelper includes admin-only maintenance commands for service recovery:

```text
/repair
/restartself
/reinstall
/rollback
```

`/repair` is non-destructive: it validates configuration, service path, runtime availability, shared folders, Telegram command menu registration, and cleans old temporary maintenance/update artifacts. `/restartself`, `/reinstall`, and `/rollback` require confirmation and run through detached maintenance scripts so the service can stop itself safely and report the result on startup.

### GitHub Releases

The repository includes a GitHub Actions workflow that runs when a tag beginning with `v` is pushed. It builds the framework-dependent release package, creates `artifacts\WinSystemHelper-ota.zip`, publishes a GitHub Release, and uploads the ZIP as a Release asset.

The build script includes size guardrails so CI fails instead of publishing a large self-contained `WinSystemHelper.exe` by accident.

Create a release by pushing a tag:

```powershell
git tag v1.5.8
git push origin v1.5.8
```

For public repositories, `/update github` reads:

```text
https://api.github.com/repos/abboodan/WinSystemHelper/releases/latest
```

Then it downloads the configured `WinSystemHelper-ota.zip` asset and passes it through the same OTA safety checks and confirmation flow as manual ZIP/URL updates.

### OTA Safety Checks

Before stopping the service, `/update` verifies that the endpoint is safe to restart:

- `config.json` must exist beside the currently running service executable and contain a bot token plus at least one admin chat ID.
- The Windows Service `binPath` must match the currently running `WinSystemHelper.exe`.
- The service must not be running from a build output folder such as `bin\Debug` or `bin\Release`; install from a clean deployment folder first.
- The .NET 8 x64 Runtime must be installed because the default package is framework-dependent.
- The ZIP must contain a non-empty `WinSystemHelper.exe` at the payload root and must not include `config.json`.

If any check fails, the bot rejects the update with a clear warning and keeps the existing service running.

## Security Disclaimer

This project is intended only for personal remote management of devices you own and administer. Keep your Telegram bot token private, restrict access to your admin chat ID, and understand the operational impact of remote commands such as shutdown, stop, and uninstall before enabling the service.
