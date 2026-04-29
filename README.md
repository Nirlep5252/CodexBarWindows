# CodexBarWindows

A small Windows tray app for checking AI coding assistant usage limits without opening a terminal.

CodexBarWindows stays in the system tray. Left-click the tray icon to open a compact popup near the taskbar with tabs for Codex and Claude usage limits, including percentage used, remaining allowance, and reset time.

## Features

- Native Windows tray app.
- Opens instantly and refreshes usage in the background.
- Shows loading state while usage limits are fetched.
- Displays 5 hour and weekly usage windows for Codex.
- Displays 5 hour and weekly usage windows for Claude Code when local Claude credentials are available.
- Follows the Windows light/dark system theme.
- Draggable popup.
- Tray context menu for settings, manual update checks, and exit.
- Uses the official OpenAI symbol as the tray icon.
- Per-user install script with Windows startup registration.

## Requirements

- Windows 10/11.
- .NET SDK for development and publishing.
- Codex CLI installed and authenticated for Codex limits.
- Claude Code installed and authenticated for Claude limits.

The installer publishes a self-contained Windows build, so the installed app does not require a separate .NET runtime.

## Install

From PowerShell:

```powershell
.\Scripts\install.ps1
```

This publishes the app, installs it to:

```text
%LOCALAPPDATA%\Programs\CodexBarWindows
```

It also creates a Start Menu shortcut so `CodexBarWindows` appears in Windows search, and registers the app to start automatically at Windows login using:

```text
HKCU\Software\Microsoft\Windows\CurrentVersion\Run
```

## MSI Installer

Build the MSI locally:

```powershell
.\Scripts\build-installer.ps1
```

The MSI is written to:

```text
Installer\bin\Release\CodexBarWindows-<version>-win-x64.msi
```

Install it silently:

```powershell
msiexec /i .\Installer\bin\Release\CodexBarWindows-0.1.5-win-x64.msi /qn
```

## Uninstall

```powershell
.\Scripts\uninstall.ps1
```

This stops the app, removes the startup entry, and deletes the per-user install folder.

## Development

Run a local debug build:

```powershell
.\Scripts\run.ps1
```

Build manually:

```powershell
dotnet build -c Release -r win-x64
```

Publish manually:

```powershell
dotnet publish .\CodexBarWindows.csproj -c Release -r win-x64 --self-contained:true
```

## Project Layout

```text
Assets/                    App icon and tray logo assets
Installer/                 WiX MSI package definition
Scripts/                   Local run, install, uninstall, and MSI build scripts
Source/App/                Program entry point and tray application context
Source/Claude/             Claude Code OAuth usage reading
Source/Codex/              Codex CLI/RPC usage reading
Source/Usage/              Shared provider usage models
Source/UI/                 Popup form, meter control, and tray icon rendering
Source/Updates/            GitHub Releases update checker
.github/workflows/         Release workflow for building and publishing MSI files
```

The PowerShell scripts are grouped under `Scripts/` because they are the primary local commands for development, install, uninstall, and MSI packaging.

## Versioning

The app version is defined in [Directory.Build.props](Directory.Build.props):

```xml
<VersionPrefix>0.1.5</VersionPrefix>
```

Use semantic versions in the form `major.minor.patch`. MSI upgrades use this version, and the updater compares it against GitHub release tags.

To publish a new version:

```powershell
git tag v0.1.5
git push origin v0.1.5
```

The GitHub Actions release workflow builds an MSI and attaches it to a GitHub Release.

## Auto Updates

Installed builds check GitHub Releases on startup and then every 6 hours. If a newer release exists and includes a `.msi` asset, the app downloads it, exits, installs the update silently, and restarts.

You can also right-click the tray icon and choose `Check for updates`.

Release requirements:

- Tags must look like `v0.1.5`.
- The release must include an MSI asset, for example `CodexBarWindows-0.1.5-win-x64.msi`.
- The new version must be greater than the installed assembly version.

For private repositories, GitHub release checks require authentication. The app checks these sources in order:

- `CODEXBAR_GITHUB_TOKEN`
- `GITHUB_TOKEN`
- `gh auth token`

If the repository is made public later, no token is required.

## How Usage Is Read

For Codex, the app first asks the local Codex CLI for rate limits using the CLI app-server RPC endpoint. If live RPC data is unavailable, it falls back to local Codex session data where possible.

No Codex account token is stored by this app. Authentication remains managed by the Codex CLI.

For Claude, the app reads the local Claude Code OAuth credential file at `%USERPROFILE%\.claude\.credentials.json` and calls Anthropic's OAuth usage endpoint. Tokens are read from Claude Code's existing local auth state and refreshed in memory only; this app does not write credentials back to disk.

## Assets

The tray icon is generated from the OpenAI symbol SVG and includes light/dark variants for Windows tray visibility.
