# CodexBarWindows

A small Windows tray app for checking AI coding assistant usage limits without opening a terminal.

CodexBarWindows stays in the system tray. Left-click the tray icon to open a compact popup near the taskbar with tabs for Codex, Claude, Grok, Cursor, and OpenCode Go usage limits, including percentage used, remaining allowance, reset time, and history charts where supported.

![CodexBarWindows Codex history preview](docs/codexbarwindows-history-preview.png)

## Features

- Native Windows tray app.
- Opens instantly and refreshes usage in the background.
- Shows loading state while usage limits are fetched.
- Dynamically displays every usage window returned for one or more Codex CLI accounts.
- Shows banked Codex reset credits with their expiry, and can redeem one per account when a usage window is nearly exhausted.
- Shows Codex local history charts for estimated 30 day spend and model usage breakdowns from session logs.
- Displays 5 hour and weekly usage windows for Claude Code, plus the Fable 5 limit when Anthropic provides it.
- Displays Grok weekly credits, optional on-demand spend, and 30-day local history from Grok CLI sessions.
- Displays Cursor Total, Auto, and API usage from cursor.com when a Cursor Cookie header is configured.
- Displays OpenCode Go rolling, weekly, and monthly quota windows when available from opencode.ai.
- Follows the Windows light/dark system theme.
- Draggable popup.
- Tray context menu for settings, manual update checks, and exit.
- Settings screen for adding extra Codex CLI binary paths.
- Uses the official OpenAI symbol as the tray icon.
- Per-user install script with Windows startup registration.

## Requirements

- Windows 10/11.
- .NET SDK for development and publishing.
- Codex CLI installed and authenticated for Codex limits.
- Optional additional Codex CLI binaries or wrapper scripts for other authenticated accounts.
- Claude Code installed and authenticated for Claude limits.
- Grok CLI installed and authenticated (`grok login`) for Grok limits. Grok is off by default — turn it on in `Settings` → `Grok`.
- A Cursor Cookie header from a signed-in cursor.com browser session for Cursor limits.
- The `auth` cookie value from a signed-in OpenCode Go browser session for OpenCode Go limits.

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
msiexec /i .\Installer\bin\Release\CodexBarWindows-0.3.4-win-x64.msi /qn
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

### WinUI 3 rewrite (in progress)

A WinUI 3 shell is being built in `CodexBar.WinUI/`, side by side with this app — both build, both
can be installed, and both can run at once. It is not the default and it replaces nothing yet.

```powershell
.\Scripts\run-winui.ps1        # build and run it
.\Scripts\install-winui.ps1    # publish, build its MSI, install to %LOCALAPPDATA%\Programs\CodexBar.WinUI
.\Scripts\uninstall-winui.ps1
```

See [docs/winui3-rewrite.md](docs/winui3-rewrite.md) for what is built, what is verified, and the
exact steps to cut over.

## Project Layout

```text
Assets/                    App icon and tray logo assets
Installer/                 WiX MSI package definitions (one per shell)
Scripts/                   Local run, install, uninstall, and MSI build scripts
docs/winui3-rewrite.md     The WinUI 3 rewrite: status, verification, cutover steps
CodexBar.Core/            UI-free shared library (settings, providers, updates)
CodexBar.Core/App/        App settings, version info, single-instance guard
CodexBar.Core/Claude/     Claude Code OAuth usage reading
CodexBar.Core/Codex/      Codex CLI/RPC usage reading
CodexBar.Core/Cursor/     Cursor usage reading
CodexBar.Core/Grok/       Grok CLI billing and local history reading
CodexBar.Core/OpenCodeGo/ OpenCode Go usage reading and encrypted session settings
CodexBar.Core/Usage/      Shared provider usage models
CodexBar.Core/Updates/    GitHub Releases update checker
CodexBar.WinUI/            WinUI 3 shell (in-progress rewrite, built side by side)
Source/App/                Program entry point and tray application context
Source/UI/                 Popup form, meter control, and tray icon rendering
.github/workflows/         Release workflow for building and publishing MSI files
```

The PowerShell scripts are grouped under `Scripts/` because they are the primary local commands for development, install, uninstall, and MSI packaging.

`CodexBar.WinUI/` is the in-progress WinUI 3 replacement for the WinForms UI. It is a separate,
unpackaged self-contained app that shares `CodexBar.Core` with the shipping app, so both can run
at the same time (they use different single-instance mutex names). It is not wired into the
installer or the release workflow yet:

```powershell
dotnet build .\CodexBar.WinUI\CodexBar.WinUI.csproj -p:Platform=x64
.\CodexBar.WinUI\bin\x64\Debug\net10.0-windows10.0.19041.0\win-x64\CodexBar.WinUI.exe
```

Set `CODEXBAR_WINUI_DIAG=1` to log the flyout's show/dismiss lifecycle to
`%TEMP%\codexbar-winui.log`; `CODEXBAR_WINUI_AUTOSHOW=1` opens the flyout at startup and
`CODEXBAR_WINUI_AUTOEXIT=<seconds>` shuts the app down again, which is how the shell is verified
without a human clicking the tray icon.

The WinUI flyout reads real usage through `UsageRefreshService` in `CodexBar.Core`, which is the
UI-free port of the WinForms tray context's refresh orchestration: per-provider readers, the
`KeepLastGood` stale-data retention, the banked-reset redemption flow, and the visibility-gated
poll timer. **Nothing refreshes while no window is showing usage** — opening the flyout or the
graphs window starts the one-minute timer, closing the last one stops it. The WinForms app still
runs its own copy of that logic in `Source/App/TrayApplicationContext.cs`; the two converge at
cutover.

## Versioning

The app version is defined in [Directory.Build.props](Directory.Build.props):

```xml
<VersionPrefix>0.5.0</VersionPrefix>
```

Use semantic versions in the form `major.minor.patch`. MSI upgrades use this version, and the updater compares it against GitHub release tags.

To publish a new version:

```powershell
git tag v0.5.0
git push origin v0.5.0
```

The GitHub Actions release workflow builds an MSI and attaches it to a GitHub Release.

## Auto Updates

Installed builds check GitHub Releases on startup and then every 6 hours. If a newer release exists and includes a `.msi` asset, the app downloads it, exits, installs the update silently, and restarts.

You can also right-click the tray icon and choose `Check for updates`.

Release requirements:

- Tags must look like `v0.5.0`.
- The release must include an MSI asset, for example `CodexBarWindows-0.5.0-win-x64.msi`.
- The new version must be greater than the installed assembly version.

For private repositories, GitHub release checks require authentication. The app checks these sources in order:

- `CODEXBAR_GITHUB_TOKEN`
- `GITHUB_TOKEN`
- `gh auth token`

If the repository is made public later, no token is required.

## How Usage Is Read

For Codex, the app asks the local Codex CLI for live rate limits using the CLI app-server RPC endpoint. It establishes an initial consensus across independent live samples, rejects isolated conflicting windows, and retains the last confirmed snapshot when a refresh fails. Session logs are not used as a rate-limit fallback because they cannot be reliably attributed to the currently selected Codex account.

Codex history charts are local estimates from `%USERPROFILE%\.codex\sessions` and `%USERPROFILE%\.codex\archived_sessions`, or from `%CODEX_HOME%` when set. They also include pi agent sessions from `%USERPROFILE%\.pi\agent\sessions`, or `%PI_HOME%\agent\sessions` when set, for `openai-codex` usage. They summarize recent token rows into a 30 day spend chart and model usage breakdown. Cost values use built-in API-rate estimates and may differ from billing.

The built-in Codex tab uses `CODEX_BINARY` or `PATH`, matching the existing behavior. Extra Codex accounts can be added from the tray icon's `Settings` window by choosing another `codex.exe`, `codex.cmd`, `.bat`, or wrapper script path. Each configured binary is queried separately and appears as its own Codex tab in the tray popup.

No Codex account token is stored by this app. Authentication remains managed by the Codex CLI.

For Claude, the app reads the local Claude Code OAuth credential file at `%USERPROFILE%\.claude\.credentials.json` and calls Anthropic's OAuth usage endpoint. Tokens are read from Claude Code's existing local auth state and refreshed in memory only; this app does not write credentials back to disk.

For Grok, the app reads the local Grok CLI session file at `%USERPROFILE%\.grok\auth.json` (or `%GROK_HOME%\auth.json`) and calls the cli-chat-proxy billing endpoint used by Grok's `/usage` command. Tokens are refreshed in memory only; this app does not write credentials back to disk. Grok history charts are local estimates from `%USERPROFILE%\.grok\sessions` turn ledgers (`updates.jsonl`), preferring server-stamped `costUsdTicks` when present and falling back to published rates otherwise.

Unlike Codex and Claude, Grok history is **limited to the last 30 days** and is not written to the usage ledger, so `Import history` does not cover it and Grok has no hourly breakdown or past-month view. Grok is scan-only until a ledger writer exists for it.

For Cursor, the app calls cursor.com usage endpoints using a manually configured Cookie header. Paste the header in `Settings` → `Cursor`. The header is stored encrypted for the current Windows user. Stage 1 does not automatically import browser cookies, and Cursor does not currently have a local token/cost history chart.

For OpenCode Go, the app reads the signed-in workspace dashboard at opencode.ai and extracts the rolling five-hour window plus the optional weekly and monthly windows. Paste only the `auth` cookie value in `Settings` → `OpenCode Go`; the app constructs the request cookie internally, and the optional `wrk_…` workspace field can be left blank for automatic discovery. The session value is stored with Windows DPAPI for the current user. An OpenCode API key can make model requests, but OpenCode does not publish a quota-reading API for these subscription windows, so it cannot replace the dashboard session here.

## Assets

The tray icon is generated from the OpenAI symbol SVG and includes light/dark variants for Windows tray visibility.
