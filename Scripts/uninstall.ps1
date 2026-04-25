$ErrorActionPreference = "Stop"

$appName = "CodexBarWindows"
$installRoot = Join-Path $env:LOCALAPPDATA "Programs\$appName"
$runKeyPath = "HKCU:\Software\Microsoft\Windows\CurrentVersion\Run"

function Find-InstalledProductCodes {
    $roots = @(
        "HKCU:\Software\Microsoft\Windows\CurrentVersion\Uninstall",
        "HKLM:\Software\Microsoft\Windows\CurrentVersion\Uninstall",
        "HKLM:\Software\WOW6432Node\Microsoft\Windows\CurrentVersion\Uninstall"
    )

    foreach ($root in $roots) {
        if (-not (Test-Path -LiteralPath $root)) {
            continue
        }

        foreach ($key in Get-ChildItem -LiteralPath $root) {
            $item = Get-ItemProperty -LiteralPath $key.PSPath
            if ($item.DisplayName -ne $appName) {
                continue
            }

            if ($key.PSChildName -match "^\{[0-9A-Fa-f-]{36}\}$") {
                $key.PSChildName
                continue
            }

            if ($item.UninstallString -match "\{[0-9A-Fa-f-]{36}\}") {
                $matches[0]
            }
        }
    }
}

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

Write-Host "Stopping $appName..."
Get-Process -Name $appName -ErrorAction SilentlyContinue | Stop-Process -Force
Start-Sleep -Milliseconds 500

foreach ($productCode in (Find-InstalledProductCodes | Select-Object -Unique)) {
    Write-Host "Uninstalling MSI product $productCode..."
    $logPath = Join-Path $env:TEMP "$appName-msi-uninstall.log"
    $process = Start-Process msiexec.exe `
        -ArgumentList @("/x", $productCode, "/qn", "/norestart", "/l*v", $logPath) `
        -Wait `
        -PassThru

    if ($process.ExitCode -ne 0) {
        if (Test-Path -LiteralPath $logPath) {
            Get-Content -LiteralPath $logPath -Tail 80
        }

        throw "MSI uninstall failed with exit code $($process.ExitCode)"
    }
}

if (Test-Path -LiteralPath $runKeyPath) {
    Remove-ItemProperty -Path $runKeyPath -Name $appName -ErrorAction SilentlyContinue
}

Assert-SafeInstallPath $installRoot
if (Test-Path -LiteralPath $installRoot) {
    Remove-Item -LiteralPath $installRoot -Recurse -Force
}

Write-Host "Uninstalled $appName"
