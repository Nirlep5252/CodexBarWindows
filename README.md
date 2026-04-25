# CodexBarWindows

A small Windows tray app for checking Codex usage limits without opening a terminal.

CodexBarWindows stays in the system tray. Left-click the tray icon to open a compact popup near the taskbar with your current 5 hour and weekly Codex usage limits, including percentage used, remaining allowance, and reset time.

## Features

- Native Windows tray app.
- Opens instantly and refreshes usage in the background.
- Shows loading state while Codex limits are fetched.
- Displays 5 hour and weekly usage windows.
- Follows the Windows light/dark system theme.
- Draggable popup.
- Minimal tray context menu with only `Exit`.
- Uses the official OpenAI symbol as the tray icon.
- Per-user install script with Windows startup registration.

## Requirements

- Windows 10/11.
- .NET SDK for development and publishing.
- Codex CLI installed and authenticated.

The installer publishes a self-contained Windows build, so the installed app does not require a separate .NET runtime.

## Install

From PowerShell:

```powershell
.\install.ps1
```

This publishes the app, installs it to:

```text
%LOCALAPPDATA%\Programs\CodexBarWindows
```

It also registers the app to start automatically at Windows login using:

```text
HKCU\Software\Microsoft\Windows\CurrentVersion\Run
```

## Uninstall

```powershell
.\uninstall.ps1
```

This stops the app, removes the startup entry, and deletes the per-user install folder.

## Development

Run a local debug build:

```powershell
.\run.ps1
```

Build manually:

```powershell
dotnet build -c Release -r win-x64
```

Publish manually:

```powershell
dotnet publish .\CodexBarWindows.csproj -c Release -r win-x64 --self-contained:true
```

## How Usage Is Read

The app first asks the local Codex CLI for rate limits using the CLI app-server RPC endpoint. If live RPC data is unavailable, it falls back to local Codex session data where possible.

No Codex account token is stored by this app. Authentication remains managed by the Codex CLI.

## Assets

The tray icon is generated from the OpenAI symbol SVG and includes light/dark variants for Windows tray visibility.
