# WinSystemHelper: A Hybrid .NET 8 Windows Service for Remote System Management via Telegram

WinSystemHelper is a personal Windows device management utility built as a hybrid .NET 8 executable. The same binary can run as an interactive setup tool, a silent installer/uninstaller, or a long-running Windows Worker Service controlled through a private Telegram bot.

## Features

- Hybrid self-installing executable with interactive setup, silent install, silent uninstall, and Windows Service modes.
- Telegram Bot integration for authenticated remote administration.
- Admin-only command handling using the configured Telegram chat ID.
- Startup and wake-from-sleep alerts.
- Modern Standby-compatible wake detection through the Windows System event log.
- Fail-fast Telegram configuration validation to avoid endless retries on invalid credentials.
- Resilient Telegram polling with exponential backoff and strict network timeouts.
- Session 0-aware command execution for workstation lock and alert sound behavior using active-user-session process launching.
- Interactive active-user prompts using Base64-encoded PowerShell UI scripts.
- Overt active-alarm microphone recording with local visual/audio warnings.
- Emoji-rich Telegram responses with a native Telegram command menu.
- Local configuration through `config.json` stored beside the executable.

## Available Commands

| Command | Description |
| --- | --- |
| `/status` | Shows machine, user, service uptime, network state, and timestamp. |
| `/lock` | Locks the active Windows workstation. |
| `/shutdown` | Initiates a graceful shutdown after 10 seconds. |
| `/restart` | Initiates a system restart after 10 seconds. |
| `/sleep` | Puts the workstation to sleep. |
| `/ip` | Returns the current public IP address. |
| `/alarm` | Plays a system alert sound through the active user session. |
| `/mic [seconds]` | Triggers an overt active alarm and returns an audio recording. |
| `/mic [seconds] loop` | Starts a persistent overt active alarm loop. |
| `/mic stop` | Stops the persistent active alarm loop. |
| `/msg [text]` | Shows a warning message on the active user's screen. |
| `/ask [text]` | Shows a Yes/No question and returns the user's answer. |
| `/prompt [text]` | Forces a text response from the active user and returns it to Telegram. |
| `/help` | Shows the available remote commands. |
| `/stop` | Stops the WinSystemHelper service. |
| `/uninstall` | Stops and deletes the WinSystemHelper service. |

## Build

Install the .NET 8 SDK, then publish a single-file Windows executable:

```powershell
dotnet publish .\WinSystemHelper.csproj -c Release -r win-x64 --self-contained true /p:PublishSingleFile=true /p:IncludeNativeLibrariesForSelfExtract=true
```

The published executable will be under:

```text
bin\Release\net8.0-windows\win-x64\publish\
```

## Installation

Run PowerShell or Command Prompt as Administrator, then install the service with your Telegram bot token and admin chat ID:

```powershell
WinSystemHelper.exe /install /token <YOUR_TOKEN> /chatid <YOUR_CHAT_ID>
```

This writes `config.json` next to the executable, creates the `WinSystemHelper` Windows Service, and starts it automatically.

## Interactive Setup

Run the executable without arguments from an elevated console:

```powershell
WinSystemHelper.exe
```

If the service is not installed, the app prompts for your Telegram bot token and admin chat ID, saves them to `config.json`, installs the service, and starts it.

If the service is already installed, the app offers an uninstall option.

## Uninstall

Run from an elevated shell:

```powershell
WinSystemHelper.exe /uninstall
```

## Configuration

Runtime credentials are stored in:

```text
config.json
```

This file is intentionally excluded from Git. Do not commit bot tokens, chat IDs, appsettings files, logs, or local build output.

## Security Disclaimer

This project is intended only for personal remote management of devices you own and administer. Keep your Telegram bot token private, restrict access to your admin chat ID, and understand the operational impact of remote commands such as shutdown, stop, and uninstall before enabling the service.
