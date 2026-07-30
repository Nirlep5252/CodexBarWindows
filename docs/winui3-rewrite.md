# The WinUI 3 rewrite

CodexBarWindows is being moved off Windows Forms and onto WinUI 3. The rewrite lives in
`CodexBar.WinUI/` and runs **side by side** with the shipping WinForms app: both build, both can be
installed, both can autostart, and both can run at the same time. Nothing in this branch changes
the default startup project or removes anything from `Source/UI`.

The cutover is a deliberate, separate step. [Cutting over](#cutting-over) lists it exactly.

---

## Layout

| Project | What it is |
| --- | --- |
| `CodexBar.Core/` | All UI-free logic: the provider readers, the pricing model, the usage/insights types, the scan cache, the refresh orchestration (`UsageRefreshService`), settings, single-instance, the tray tooltip string, and the self-updater. Referenced by every other project. |
| `CodexBarWindows.csproj` + `Source/` | The shipping WinForms app. Unchanged behaviour. |
| `CodexBar.WinUI/` | The WinUI 3 shell. Tray icon, flyout, settings window, usage graphs window. |
| `Tests/CodexBarWindows.Tests/` | 42 tests over `CodexBar.Core`. Both shells are covered by them because both consume the same core. |

The WinUI shell is **unpackaged** (no MSIX) and **self-contained** (the Windows App Runtime and the
.NET runtime ship in the app folder). MSIX was evaluated and rejected: it requires code signing and
it breaks the MSI-based self-updater.

### What lives where in the WinUI shell

| File | Responsibility |
| --- | --- |
| `Program.cs` | Custom `[STAThread] Main` (`DISABLE_XAML_GENERATED_MAIN`), single-instance guard, `Application.Start`. |
| `App.xaml.cs` | Tray icon and its menu, window lifetimes, the tray tooltip, and the update checks. |
| `ShellIdentity.cs` | **Every name that keeps this shell distinct from the WinForms app.** See [Cutting over](#cutting-over). |
| `Views/FlyoutWindow.xaml(.cs)` | The tray flyout: provider tabs, meters, reset credits, status line. |
| `Views/SettingsWindow.xaml(.cs)` | Settings: general, appearance, Codex accounts, Cursor. |
| `Views/GraphsWindow.xaml(.cs)` | The 30-day spend and per-model charts, on LiveCharts2. |
| `Views/ChartPrewarm.cs` | Pays LiveCharts' one-off Skia init off-screen at startup. |
| `Theming/` | Theme, backdrop, tint, flyout palette, chart palette. |

---

## Running each app

### WinForms (the shipping app)

```powershell
.\Scripts\run.ps1          # builds Debug x64 and starts it
.\Scripts\install.ps1      # publishes, builds the MSI, installs to %LOCALAPPDATA%\Programs\CodexBarWindows
.\Scripts\uninstall.ps1
```

### WinUI 3

```powershell
.\Scripts\run-winui.ps1                 # builds Debug x64 and starts it
.\Scripts\run-winui.ps1 -Configuration Release
.\Scripts\build-installer-winui.ps1     # publishes + builds Installer\bin\Release\CodexBar.WinUI-<ver>-win-x64.msi
.\Scripts\install-winui.ps1             # the above, then installs to %LOCALAPPDATA%\Programs\CodexBar.WinUI
.\Scripts\uninstall-winui.ps1
```

`run-winui.ps1` stops only `CodexBar.WinUI`. It never touches a running `CodexBarWindows`.

Publishing by hand:

```powershell
dotnet publish CodexBar.WinUI\CodexBar.WinUI.csproj -p:PublishProfile=win-x64
# -> bin\publish-winui\win-x64\  (540 files, ~238 MB)
```

`-p:Platform=x64` is mandatory for every build of `CodexBar.WinUI` — the project declares
`<Platforms>x64</Platforms>` and there is no AnyCPU configuration.

### Verification hooks

Environment variables the WinUI shell reads. All are opt-in and all are for driving it from a
script; none affect a normal run.

| Variable | Effect |
| --- | --- |
| `CODEXBAR_WINUI_DIAG` | `1` for `%TEMP%\codexbar-winui.log`, or a path. Enables the diagnostic log. |
| `CODEXBAR_WINUI_AUTOSHOW=1` | Opens the flyout ~1.5 s after start. |
| `CODEXBAR_WINUI_AUTOSETTINGS=1` | Opens the settings window ~1.5 s after start. |
| `CODEXBAR_WINUI_AUTOGRAPHS=<s>` | Opens the graphs window after `<s>` seconds. |
| `CODEXBAR_WINUI_NOPREWARM=1` | Disables the chart pre-warm, for A/B timing. |
| `CODEXBAR_WINUI_AUTOEXIT=<s>` | Exits after `<s>` seconds. |

---

## What phase 6 added

1. **Start with Windows** — the settings toggle writes `HKCU\...\Run`, under its own value name
   (`CodexBar.WinUI`). `StartupSettings` gained `IsEnabledFor`/`SetEnabledFor`; the old
   `IsEnabled`/`SetEnabled` still mean the WinForms app and are untouched.
2. **Update checking** — the tray menu item and a new **Check for updates** button on the settings
   General page share one in-flight guard on `App`. There is also an automatic check 10 s after
   start and every 6 hours after that, matching the WinForms shell. The updater is scoped to this
   shell's install folder and MSI asset name.
3. **Tray tooltip** — `UsageRefreshService.TooltipChanged` drives `TaskbarIcon.ToolTipText`. The
   string comes from `UsageTooltip.Build`, which both shells share, so both produce the same text
   and both get its 63-character shell clamp.
4. **Packaging** — a publish profile, run/build/install/uninstall scripts, and a second WiX package
   (`Installer/PackageWinUI.wxs` + `Installer/CodexBar.WinUI.Installer.wixproj`). The WinForms
   package is unchanged apart from being pinned to its own `.wxs`.

### Things that were found by running it, not by reading it

- **`dotnet publish` silently drops two files and the published app then crashes.**
  `CodexBar.WinUI.pri` and `App.xbf` are produced by the build but never copied to the publish
  folder, while every *other* `.pri` is. The app starts, logs, runs the chart pre-warm — and then
  fail-fasts with `0xC0000409` (`STATUS_STACK_BUFFER_OVERRUN`) the instant the first `x:Class`
  window is constructed. Diagnosed by diffing the folders (538 files vs 543) and confirmed by
  copying just those two in. The `PublishWinUiResourceIndex` target in the csproj fixes it, and
  `build-installer-winui.ps1` asserts both files exist before it will package anything.
- **An unrooted `DispatcherQueueTimer` is collected before it ticks.** The startup update check was
  first written as a local-variable 10-second timer and its tick never arrived — no log line, no
  error. It is now held in a field and re-arms itself at the 6-hour interval. (`MaybeAutoShow`'s
  timers survive only because 1.5 s is too soon for a collection.)
- **The tray icon's registration-time tooltip is baked into its accessible name.** The shell reads
  the notification-area button out as `"<ToolTipText at creation> <current ToolTipText>"`, so the
  placeholder has to be short. It was briefly `"CodexBarWindows: no usage data found"`, which
  screen readers would have prefixed to the live figures forever.

---

## Verified

Everything below was checked by building, running and observing the app — the tray tooltip through
UI Automation, the settings page through UI Automation and screenshots, the registry through
`Get-ItemProperty` before/after, and the MSI by installing and uninstalling it for real.

| Claim | How |
| --- | --- |
| WinForms app still builds, 0 warnings 0 errors | `dotnet build CodexBarWindows.csproj -p:Platform=x64 -c Release` |
| 42/42 tests pass | `dotnet run --project Tests\CodexBarWindows.Tests` |
| WinUI app builds, 0 warnings 0 errors | `dotnet build CodexBar.WinUI\CodexBar.WinUI.csproj -p:Platform=x64 -c Release` |
| Tray tooltip shows live usage | UIA name of the notification-area button read as `CodexBarWindows Codex 5% 7d, Claude 47% 5h, Cursor --`, and the diagnostic log shows it changing on each refresh |
| Settings **Check for updates** works | Invoked through UIA; the InfoBar reported `Update checks only run from the installed app.` for a dev build |
| Automatic update check runs | Diagnostic log, ~10.5 s after start: `update check: Skipped - Update checks only run from the installed app.` |
| The update check reaches GitHub from an installed build | Same log from `%LOCALAPPDATA%\Programs\CodexBar.WinUI`: `update check: UpToDate - CodexBarWindows is up to date.` |
| Start with Windows writes the right value | Toggled through UIA: `CodexBar.WinUI` appeared with the running exe's path and then disappeared; `CodexBarWindows` was byte-identical throughout |
| Publish output runs | 540 files / 237.6 MB; flyout, settings and charts all render |
| MSI installs, runs and uninstalls cleanly | Installed 77.4 MB MSI; app ran from the install folder; uninstall removed the folder, the Run value and the Start menu folder, and left the WinForms install, its shortcut and its Run value intact |
| Both installers still build | `build-installer.ps1` and `build-installer-winui.ps1`, both succeed |

## Not verified

- **The download-and-install half of the self-updater has never run for this shell.** The installed
  build reached GitHub and correctly reported "up to date", which exercises the installed-build
  detection, the HTTP call and the version comparison — but no release yet carries a
  `CodexBar.WinUI-*.msi`, so the asset match, the download and the `msiexec` handoff are unproven.
  The first tagged release that publishes the WinUI MSI is what tests them.
- **Upgrade-in-place of the WinUI MSI.** Only a clean install and a clean uninstall were run. The
  `MajorUpgrade` path (installing 0.7.1 over 0.7.0) has not been exercised.
- **Autostart actually starting the app at logon.** The registry value was verified to be written,
  removed, and to point at the right exe. Nobody signed out and back in.
- **The 6-hour repeat of the automatic update check.** The first (10 s) check was observed; the
  re-arm to 6 hours was not waited out.
- **Long-run behaviour.** No session has run for more than a few minutes.
- **`dotnet build CodexBarWindows.csproj -p:Platform=x64` in Debug** exits 1 on this machine, with
  only `MSB3021`/`MSB3027` apphost-copy errors, because the user's live `CodexBarWindows.exe` holds
  the file. Compilation is clean; Release builds fine.

---

## Cutting over

Nothing below is done. Each step is deliberate.

### 1. Take the WinForms app's identity

`CodexBar.WinUI/ShellIdentity.cs` is the only file that names the differences. Change all four:

| Constant | Now | At cutover |
| --- | --- | --- |
| `SingleInstanceMutexScope` | `"WinUI"` | `null` |
| `StartupRegistryValueName` | `"CodexBar.WinUI"` | `"CodexBarWindows"` (or `AppInfo.AppName`) |
| `InstallFolderName` | `"CodexBar.WinUI"` | `"CodexBarWindows"` |
| `ReleaseAssetNameHint` | `"CodexBar.WinUI"` | `"CodexBarWindows"` |

`SettingsWindow` reads the startup value through `ShellIdentity`, so changing the constant is enough
— but note that a user upgrading from the WinForms app will have a stale `CodexBar.WinUI` Run value
if they ever installed the preview. The WinUI MSI's uninstall removes it; a migration that deletes
it explicitly would be kinder.

### 2. Point the installer at the WinUI app

Either retarget `Installer/Package.wxs` at `bin\publish-winui\win-x64` and the WinUI exe (keeping
the existing `UpgradeCode`, so the MSI upgrades existing installs in place), or keep
`PackageWinUI.wxs` and give it the WinForms `UpgradeCode` `{503495F2-E909-46F8-9629-A6C504ADFF7A}`
and install folder. **One of the two must inherit that UpgradeCode**, or installed users end up with
two copies rather than an upgrade.

Also rename the MSI to `CodexBarWindows-<ver>-win-x64.msi` so the WinForms app's own updater — which
filters on the substring `CodexBarWindows` — finds it and can migrate users automatically.

### 3. Release CI

`.github/workflows/release.yml` was **not** changed, so tagged releases still ship only the WinForms
MSI. To ship the WinUI MSI alongside it during the preview, add a second job after `build-msi`:

```yaml
  build-winui-msi:
    needs: build-msi
    runs-on: windows-latest
    steps:
      - uses: actions/checkout@v4
      - uses: actions/setup-dotnet@v4
        with:
          dotnet-version: "10.0.x"
      - shell: pwsh
        run: |
          $version = "${{ github.ref_name }}".TrimStart("v")
          .\Scripts\build-installer-winui.ps1 -Version $version
          gh release upload "${{ github.ref_name }}" "Installer\bin\Release\CodexBar.WinUI-$version-win-x64.msi" --clobber
        env:
          GH_TOKEN: ${{ github.token }}
```

Keeping it a separate job matters: if the WinUI publish breaks, the WinForms release has already
been created and uploaded.

At full cutover this job replaces `build-msi` instead of following it.

### 4. Retire the WinForms UI

Only after the WinUI app has been lived with. Deleting `Source/UI` and `Source/App` and dropping
`CodexBarWindows.csproj` removes:

- `Source/UI/UsagePopupForm.cs`, `SettingsForm.cs`, `UsageGraphsForm.cs`, `UsageMeterControl.cs`,
  `TrayIconFactory.cs` and all of `Source/UI/Fluent/` — the hand-rolled Fluent control set that
  exists only because WinForms ships none of it;
- `Source/App/TrayApplicationContext.cs`, which still owns its **own copy of the refresh
  orchestration** rather than consuming `UsageRefreshService`. That duplication is the main reason
  the two shells can drift.

`Tests/CodexBarWindows.Tests` references `CodexBarWindows.csproj`; it will need to drop that
reference (everything it tests is in `CodexBar.Core`).

---

## Still missing in the WinUI shell

Carried forward from earlier phases, none of it blocking:

- **The dynamic percentage tray icon.** `Source/UI/TrayIconFactory.cs` renders the current
  percentage into the tray glyph and re-tints it when vibes are toggled. The WinUI shell shows a
  static `.ico`.
- **Flyout placement uses `DisplayArea.Nearest`**, not the cursor position, so on a multi-monitor
  setup it can open on the wrong screen's taskbar corner.
- **The graphs window has no manual refresh affordance.** Neither did the WinForms one — F5 lives on
  the flyout.
- **~155 ms of blank window when the graphs window opens**, even with the chart pre-warmed. Window
  creation plus XAML plus the first measure. Deferring `AppWindow.Show` until the chart's first
  `UpdateFinished` would fix it but needs a backstop for the no-data case.
