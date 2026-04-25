param(
    [string]$Configuration = "Release",
    [string]$Runtime = "win-x64",
    [string]$Version,
    [switch]$NoStart
)

$ErrorActionPreference = "Stop"

$repoRoot = Split-Path -Parent $MyInvocation.MyCommand.Path
$appName = "CodexBarWindows"
$installerScript = Join-Path $repoRoot "build-installer.ps1"

function Find-InstalledProductCodes {
    $roots = @(
        "HKCU:\Software\Microsoft\Windows\CurrentVersion\Uninstall",
        "HKLM:\Software\Microsoft\Windows\CurrentVersion\Uninstall",
        "HKLM:\Software\WOW6432Node\Microsoft\Windows\CurrentVersion\Uninstall"
    )

    $productCodes = [System.Collections.Generic.List[string]]::new()
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
                $productCodes.Add($key.PSChildName)
                continue
            }

            if ($item.UninstallString -match "\{[0-9A-Fa-f-]{36}\}") {
                $productCodes.Add($matches[0])
            }
        }
    }

    return $productCodes | Select-Object -Unique
}

function Uninstall-ExistingProducts {
    foreach ($productCode in Find-InstalledProductCodes) {
        Write-Host "Removing existing MSI product $productCode..."
        $uninstallLogPath = Join-Path $env:TEMP "$appName-msi-uninstall.log"
        $process = Start-Process msiexec.exe `
            -ArgumentList @("/x", $productCode, "/qn", "/norestart", "/l*v", $uninstallLogPath) `
            -Wait `
            -PassThru

        if ($process.ExitCode -ne 0) {
            if (Test-Path -LiteralPath $uninstallLogPath) {
                Get-Content -LiteralPath $uninstallLogPath -Tail 80
            }

            throw "Existing MSI uninstall failed with exit code $($process.ExitCode)"
        }
    }
}

$buildArgs = @{
    Configuration = $Configuration
    Runtime = $Runtime
}

if (-not [string]::IsNullOrWhiteSpace($Version)) {
    $buildArgs.Version = $Version
}

& $installerScript @buildArgs
if ($LASTEXITCODE -ne 0) {
    throw "MSI build failed with exit code $LASTEXITCODE"
}

if ([string]::IsNullOrWhiteSpace($Version)) {
    [xml]$props = Get-Content -LiteralPath (Join-Path $repoRoot "Directory.Build.props")
    $Version = $props.Project.PropertyGroup.VersionPrefix
}

$msiPath = Join-Path $repoRoot "Installer\bin\$Configuration\$appName-$Version-$Runtime.msi"
if (-not (Test-Path -LiteralPath $msiPath)) {
    throw "MSI was not found: $msiPath"
}

Write-Host "Stopping any running $appName instance..."
Get-Process -Name $appName -ErrorAction SilentlyContinue | Stop-Process -Force
Start-Sleep -Milliseconds 500
Uninstall-ExistingProducts

Write-Host "Installing $msiPath..."
$logPath = Join-Path $env:TEMP "$appName-msi-install.log"
$process = Start-Process msiexec.exe `
    -ArgumentList @("/i", $msiPath, "/qn", "/norestart", "/l*v", $logPath) `
    -Wait `
    -PassThru

if ($process.ExitCode -ne 0) {
    if (Test-Path -LiteralPath $logPath) {
        Get-Content -LiteralPath $logPath -Tail 80
    }

    throw "MSI install failed with exit code $($process.ExitCode)"
}

$installRoot = Join-Path $env:LOCALAPPDATA "Programs\$appName"
$exePath = Join-Path $installRoot "$appName.exe"

if (-not $NoStart -and (Test-Path -LiteralPath $exePath)) {
    Start-Process -FilePath $exePath -WorkingDirectory $installRoot
}

Write-Host "Installed $appName $Version from MSI"
