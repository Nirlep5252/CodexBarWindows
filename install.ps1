param(
    [string]$Configuration = "Release",
    [string]$Runtime = "win-x64",
    [bool]$SelfContained = $true,
    [switch]$NoStart
)

$ErrorActionPreference = "Stop"

$appName = "CodexBarWindows"
$repoRoot = Split-Path -Parent $MyInvocation.MyCommand.Path
$projectPath = Join-Path $repoRoot "$appName.csproj"
$publishRoot = Join-Path $repoRoot "bin\publish\$Runtime"
$installRoot = Join-Path $env:LOCALAPPDATA "Programs\$appName"
$exePath = Join-Path $installRoot "$appName.exe"
$runKeyPath = "HKCU:\Software\Microsoft\Windows\CurrentVersion\Run"
$quotedExePath = "`"$exePath`""

function Assert-SafeInstallPath {
    param([string]$Path)

    $programsRoot = [System.IO.Path]::GetFullPath((Join-Path $env:LOCALAPPDATA "Programs"))
    $fullPath = [System.IO.Path]::GetFullPath($Path)

    if (-not $fullPath.StartsWith($programsRoot, [System.StringComparison]::OrdinalIgnoreCase)) {
        throw "Refusing to install outside the per-user Programs directory: $fullPath"
    }

    if ([System.IO.Path]::GetFileName($fullPath) -ne $appName) {
        throw "Refusing to install into an unexpected directory: $fullPath"
    }
}

function Stop-InstalledApp {
    Write-Host "Stopping any running $appName instance..."
    $processes = @(Get-Process -Name $appName -ErrorAction SilentlyContinue)
    foreach ($process in $processes) {
        Stop-Process -Id $process.Id -Force -ErrorAction SilentlyContinue
    }

    foreach ($process in $processes) {
        Wait-Process -Id $process.Id -Timeout 10 -ErrorAction SilentlyContinue
    }
}

function Remove-InstallRoot {
    if (-not (Test-Path -LiteralPath $installRoot)) {
        return
    }

    for ($attempt = 1; $attempt -le 5; $attempt++) {
        try {
            Remove-Item -LiteralPath $installRoot -Recurse -Force
            return
        }
        catch {
            if ($attempt -eq 5) {
                throw
            }

            Start-Sleep -Milliseconds 500
        }
    }
}

Assert-SafeInstallPath $installRoot

Write-Host "Publishing $appName..."
dotnet publish $projectPath `
    --configuration $Configuration `
    --runtime $Runtime `
    --self-contained:$SelfContained `
    --output $publishRoot `
    -p:PublishSingleFile=true `
    -p:IncludeNativeLibrariesForSelfExtract=true `
    -p:EnableCompressionInSingleFile=true

if ($LASTEXITCODE -ne 0) {
    throw "Publish failed with exit code $LASTEXITCODE"
}

$publishedExe = Join-Path $publishRoot "$appName.exe"
if (-not (Test-Path -LiteralPath $publishedExe)) {
    throw "Published executable was not found: $publishedExe"
}

Stop-InstalledApp
Remove-InstallRoot

New-Item -ItemType Directory -Path $installRoot | Out-Null
Get-ChildItem -LiteralPath $publishRoot -Force |
    Copy-Item -Destination $installRoot -Recurse -Force

New-Item -Path $runKeyPath -Force | Out-Null
Set-ItemProperty -Path $runKeyPath -Name $appName -Value $quotedExePath

if (-not $NoStart) {
    Start-Process -FilePath $exePath -WorkingDirectory $installRoot
}

Write-Host "Installed $appName to $installRoot"
Write-Host "Startup registered under HKCU Run: $quotedExePath"
