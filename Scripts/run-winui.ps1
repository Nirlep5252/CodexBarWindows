# Builds and starts the WinUI 3 shell. Mirrors Scripts/run.ps1, which does the same for the
# WinForms app.
#
# The two apps are deliberately allowed to run AT THE SAME TIME (they take different
# single-instance mutexes - see CodexBar.WinUI/ShellIdentity.cs), so this only ever stops
# CodexBar.WinUI. It must never touch CodexBarWindows: that is the shipping app, and it may be
# the one the user is actually relying on right now.
param(
    [ValidateSet("Debug", "Release")]
    [string]$Configuration = "Debug"
)

$ErrorActionPreference = "Stop"

$scriptRoot = Split-Path -Parent $MyInvocation.MyCommand.Path
$repoRoot = Split-Path -Parent $scriptRoot
$projectPath = Join-Path $repoRoot "CodexBar.WinUI\CodexBar.WinUI.csproj"
$appName = "CodexBar.WinUI"

# net10.0-windows10.0.19041.0\win-x64: the RID subfolder is there because the project is
# self-contained (WindowsAppSDKSelfContained), not because of anything this script passes.
$exePath = Join-Path $repoRoot "CodexBar.WinUI\bin\x64\$Configuration\net10.0-windows10.0.19041.0\win-x64\$appName.exe"

Get-Process -Name $appName -ErrorAction SilentlyContinue | Stop-Process -Force

# -p:Platform=x64 is mandatory: this project declares <Platforms>x64</Platforms> and there is no
# AnyCPU configuration to fall back on.
dotnet build $projectPath -c $Configuration -p:Platform=x64
if ($LASTEXITCODE -ne 0) {
    throw "Build failed with exit code $LASTEXITCODE"
}

if (-not (Test-Path -LiteralPath $exePath)) {
    throw "Built executable was not found: $exePath"
}

Start-Process -FilePath $exePath -WorkingDirectory (Split-Path -Parent $exePath)
Write-Host "Started $appName from $exePath"
