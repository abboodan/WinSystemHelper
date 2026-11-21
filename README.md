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
- Persistent command and delivery queues, offline media capture up to a configurable quota, and a visible local recording consent/stop window.
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
| `/menu` | Opens the interactive command menu in the current chat. |
| `/status` | Shows the full low-overhead system, runtime, service, battery, and wake watcher dashboard. |
| `/mode [production\|development]` | Shows the operating mode or asks to change it. |
| `/healthcheck` | Shows a fast health summary with cached state and recent failures. |
| `/log [lines]` | Shows the latest service log lines (default 40, up to 200). |
| `/log file` | Sends the service log as a file. |
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
| `/mic stop` | Stops recording; queued media continues uploading. |
| `/queue` | Shows storage usage, pending commands, and failed delivery IDs. |
| `/queue retry [Id]` | Retries a failed, completed file or message belonging to the requester. |
| `/queue delete [Id]` | Requests confirmation before deleting a queued item. |
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

Use `/menu` to browse command categories with Telegram buttons. Status and diagnostic actions can be selected directly. Copy buttons put the displayed command on the clipboard so you can review and send it. `/help` still shows the text command reference and reply keyboard. Queue reports show recent failures first, with the reason above the item ID.

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

Current version: `1.6.0`.

## Installation

Run PowerShell or Command Prompt as Administrator, then install the service with your Telegram bot token and primary admin chat ID:

```powershell
.\WinSystemHelper.Bootstrapper.exe /install /token <YOUR_TOKEN> /chatid <YOUR_CHAT_ID>
```

The bootstrapper installs missing runtime prerequisites, then the installer copies the application to `%ProgramFiles%\WinSystemHelper`, writes `config.json` there using the multi-admin format, creates the `WinSystemHelper` Windows Service with a quoted executable path, and starts it automatically. The folder you launched the installer from is no longer used and can be deleted.

The service runs as LocalSystem, so it must not live in a folder ordinary users can modify. A copy that is already somewhere under `%ProgramFiles%` is installed in place; anything else is relocated. `/repair` reports an install folder that non-administrators can write to.

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

It sits in the install folder and holds the bot token in clear text, so it is written with an access list limited to SYSTEM and Administrators (existing installs are tightened on the next service start). Installer logs never record command-line values.

This file is intentionally excluded from Git. Do not commit bot tokens, chat IDs, appsettings files, logs, or local build output.

### Who can send commands

A command is accepted only when **both** the chat and the person who sent it are in `adminChatIds`. In a private chat those are the same number. If you list a group, also list the user ID of each person allowed to control the machine; other group members and anonymous group admins are ignored. Alerts are still delivered to every listed chat.

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

## Production and development modes

WinSystemHelper starts in `production`. A configured Telegram administrator can request `/mode development`. One window on the active Windows desktop explains that the requesting administrator and chat can take screenshots, camera photos, and audio without separate prompts after the local user approves. The window stays visible while development is active, with an **End development mode** button.

Use `/mode production`, click the button, or close the window to revoke permission. Locking, disconnecting, or signing out of that Windows session also ends it. Each activation requires fresh local approval. Development recordings stop when permission ends, and development mode starts fresh in production after a service restart.

Telegram notifications and command results are available in both modes. Commands that explicitly ask the computer to show a message, play sound, or collect an answer still do so. Check the current mode with `/mode`, `/status`, or `/config`. Set `"allowDevelopmentMode": false` in the local `config.json` and restart the service to disable development requests on a device.

## Power Event Alerts

Shutdown, restart, and sleep commands sent through Telegram broadcast an alert before the system action starts. The service also watches the Windows System event log for local shutdown/restart requests and sleep transitions, then attempts to notify all configured admins.

If the machine powers off before Telegram delivery completes, a small JSON report is stored under the shared `PowerEvents` temp folder and sent on the next service startup. Power event alerts can be controlled remotely:

```text
/config set PowerEventAlertsEnabled false
/config set PowerEventOutboxEnabled true
/config set PowerCommandPreDelaySeconds 3
```

## Persistent Active Alarm Mic Loop

A local user must approve a recording session. A visible window remains open with a Stop button; closing it stops recording. Camera photos and screenshots also require local approval. An audible/visual notice is issued when the session starts, not for every audio chunk.

With `PersistentMicLoopEnabled`, an active loop can be restored after a service restart, but **local approval is requested again**. Local Stop, closing the recording window, or `/mic stop` disables automatic resumption. The service never replays an interrupted capture command automatically.

### Offline storage and delivery

Completed recordings and images are durably queued in `%ProgramData%/WinSystemHelper/Queue/<bot-scope>/`, outside the repository, temporary cleanup folders, and installation directory. SQLite holds metadata only; media is streamed from separate files. Access is limited to the service and administrators, with temporary per-capture access for the active local user. A different bot uses a separate store.

```text
/config set OutboxMaxSizeMB 512
/queue
/queue retry <Id>
/queue delete <Id>
/mic stop
```

`OutboxMaxSizeMB` defaults to **512 MiB** (one unit is 1,048,576 bytes), accepts 1–65536, and can be changed without restarting. It includes completed, failed, and reserved in-progress media. Small queue metadata/control files are additional. Images reserve up to 10 MiB; a smaller configured quota cannot accept image capture. Audio reserves its uncompressed 16 kHz/16-bit mono size before recording.

At capacity, production pauses without discarding existing files. An approved loop resumes below 80% usage when its next chunk fits. Lowering the quota never deletes files; raising it wakes the loop. Uploading does not block recording or the independent control lane. No compression/conversion is added. Capture failure or withdrawal of local approval stops the loop; incomplete files are retained for review/deletion, not uploaded as complete recordings.

Temporary delivery failures retry with increasing delay (2 seconds to 5 minutes). Telegram rate-limit deadlines are respected. Permanent errors appear in `/queue`; they do not block unrelated messages. Acknowledged files are deleted only after success is stored. If Telegram accepted a file but its acknowledgement was lost, a retry can produce a duplicate bearing the same capture ID and original timestamp.

### Command recovery

Authorized commands are saved before advancing Telegram's polling offset. Time-sensitive commands older than five minutes are rejected; read-only requests, `/mic stop`, and `/cancel` remain valid. Pending confirmations expire and are invalidated on service restart. Commands interrupted during a possible side effect are reported as having an unknown outcome and are not automatically repeated. Read-only interrupted work can be retried.

The command queue holds up to 1000 active entries (50 reserved for control), and the text queue up to 2000 entries (100 reserved for control replies). At ordinary capacity, new work is rejected with a coalesced notice. Completed history is pruned incrementally after 24 hours and bounded to 10000 rows per history table. Metadata remains bounded independently of media; retention cleanup does not discard undelivered files.

Legacy power/update notices are imported idempotently. The old active-loop marker is moved into private storage. Repair and OTA operations do not remove the durable queue. Keep the store when updating; if storage initialization fails, the service stops rather than resetting the queue.

### Validation and public publishing

```powershell
dotnet test tests/Queue.Tests.csproj -c Release
dotnet run --project tests/Queue.Tests.csproj -c Release -- --benchmark
powershell -NoProfile -File tests/PublishSafety.Tests.ps1
powershell -NoProfile -File scripts/Check-PublishSafety.ps1 -Mode WorkingTree
powershell -NoProfile -File scripts/Check-PublishSafety.ps1 -Mode Index
powershell -NoProfile -File scripts/Check-PublishSafety.ps1 -Mode History
```

Tests use synthetic media and fake Telegram/session transports; they do not activate hardware or send messages. `/queue-storage-check` on the published executable checks bundled SQLite without installing or starting the service. See [queue validation](docs/queue-validation.md) for measurements and remaining interactive checks.

The publishing helper checks staged content and history, printing only file locations and categories for potential secrets. Failed inspection blocks publication. Release CI enforces the four-file package allowlist. Runtime configs, media, logs, database files, keys, and local build products must stay untracked. Author identity and license attribution are intentionally public. Pattern checks supplement, rather than replace, review of the final diff and files being pushed.

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

### Signed updates

An update is code that runs as LocalSystem, so being able to send a command should not be enough to install one. Release packages carry a detached ECDSA P-256 signature (`WinSystemHelper-ota.zip.sig`), and a build with a pinned public key refuses any package without a valid signature, before anything is extracted:

- `/update github` reads the `.sig` asset from the same Release.
- `/update https://host/file.zip` fetches `https://host/file.zip.sig`.
- A ZIP attached in Telegram takes the signature in the caption: `/update <signature>`.

One-time setup for maintainers (and for forks, which need their own key):

```powershell
.\WinSystemHelper.exe /ota-keygen C:\secure\ota-signing.key
```

Store the file's content as the GitHub Actions secret `OTA_SIGNING_KEY`, keep the file out of the repository, and set `OtaSignature.PinnedPublicKey` in `Updates/OtaSignature.cs` to the printed public key. From then on the release workflow signs the package and fails if the signature does not match the key compiled into the build.

Until a key is pinned, the build cannot verify packages: it still accepts updates, says so in the update message and in `/repair`, and the release workflow prints a warning. Rotating the key requires shipping a release, signed with the old key, that pins the new one.

Admin-chat authorization, confirmation, and cooldown still apply on top of the signature. The updater creates a temporary backup before copying files and refreshes a persistent `Backups\previous` copy that `/rollback` can use later. If an update copy fails, the updater still attempts immediate rollback from the temporary backup.

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
git tag v1.6.0
git push origin v1.6.0
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

## Diagnostics and crash recovery

The service keeps a rotating log in `%ProgramData%\WinSystemHelper\Service\Logs` (`service.log`, 1 MiB per file, two older files kept). It records startup and shutdown, each command word with its chat ID, warnings, and errors with their stack traces. Command arguments are not logged, and the bot token is removed from every line.

```text
/log            latest 40 lines
/log 120        latest 120 lines (up to 200, trimmed to fit one message)
/log file       all retained log files as one document
```

If a background task fails, the cause is written to the log and the process exits with a failure code instead of stopping quietly. Windows then restarts the service: after 1 minute on the first failure and after 5 minutes on the second; a third failure within a day leaves it stopped, so a build that crashes on start cannot loop. The service applies this recovery policy to itself on every start, so OTA-updated installs get it too.

After an abnormal stop, the next start queues a notice with the recorded error for every admin. If the queue is temporarily unavailable or full, the service retains the notice and retries. A normal stop, restart, update, or Windows shutdown produces no notice. If private log storage cannot be opened at startup, file logging and crash notices are disabled for that run.

## Service storage

Updater and maintenance scripts, staged update payloads, the rollback backup, and pending power-event reports live under `%ProgramData%\WinSystemHelper\Service`, restricted to SYSTEM and Administrators. Earlier versions used `C:\Users\Public\Documents\WinSystemHelper`, which any local user can modify; nothing is executed or restored from that folder any more, and it can be deleted after upgrading. Because the old rollback backup is not trusted, `/rollback` becomes available again after the first update applied by this version.

## Security Disclaimer

This project is intended only for personal remote management of devices you own and administer. Keep your Telegram bot token private, restrict access to your admin chat ID, and understand the operational impact of remote commands such as shutdown, stop, and uninstall before enabling the service.
