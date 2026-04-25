$ErrorActionPreference = "Stop"

$appName = "CodexBarWindows"
$installRoot = Join-Path $env:LOCALAPPDATA "Programs\$appName"
$runKeyPath = "HKCU:\Software\Microsoft\Windows\CurrentVersion\Run"

function Assert-SafeInstallPath {
    param([string]$Path)

    $programsRoot = [System.IO.Path]::GetFullPath((Join-Path $env:LOCALAPPDATA "Programs"))
    $fullPath = [System.IO.Path]::GetFullPath($Path)

    if (-not $fullPath.StartsWith($programsRoot, [System.StringComparison]::OrdinalIgnoreCase)) {
        throw "Refusing to remove a directory outside the per-user Programs directory: $fullPath"
    }

    if ([System.IO.Path]::GetFileName($fullPath) -ne $appName) {
        throw "Refusing to remove an unexpected directory: $fullPath"
    }
}

Assert-SafeInstallPath $installRoot

Write-Host "Stopping $appName..."
Get-Process -Name $appName -ErrorAction SilentlyContinue | Stop-Process -Force

if (Test-Path -LiteralPath $runKeyPath) {
    Remove-ItemProperty -Path $runKeyPath -Name $appName -ErrorAction SilentlyContinue
}

if (Test-Path -LiteralPath $installRoot) {
    Remove-Item -LiteralPath $installRoot -Recurse -Force
}

Write-Host "Uninstalled $appName"
