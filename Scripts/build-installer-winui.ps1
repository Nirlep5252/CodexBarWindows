# Publishes the WinUI 3 shell and packages it as an MSI. The counterpart of
# Scripts/build-installer.ps1, which does the same for the WinForms app; both can be run, and both
# MSIs can be installed, at the same time.
#
# The output is a FOLDER of roughly 240 MB rather than the WinForms app's single-file exe - see
# CodexBar.WinUI/Properties/PublishProfiles/win-x64.pubxml for why PublishSingleFile is not used.
param(
    [string]$Configuration = "Release",
    [string]$Runtime = "win-x64",
    [string]$Version,
    [switch]$SkipPublish
)

$ErrorActionPreference = "Stop"

$scriptRoot = Split-Path -Parent $MyInvocation.MyCommand.Path
$repoRoot = Split-Path -Parent $scriptRoot
$projectPath = Join-Path $repoRoot "CodexBar.WinUI\CodexBar.WinUI.csproj"
$installerProjectPath = Join-Path $repoRoot "Installer\CodexBar.WinUI.Installer.wixproj"
$publishRoot = Join-Path $repoRoot "bin\publish-winui\$Runtime"
$installerPublishRoot = "..\bin\publish-winui\$Runtime"

if ([string]::IsNullOrWhiteSpace($Version)) {
    [xml]$props = Get-Content -LiteralPath (Join-Path $repoRoot "Directory.Build.props")
    $Version = $props.Project.PropertyGroup.VersionPrefix
}

if (-not $SkipPublish) {
    Write-Host "Publishing CodexBar.WinUI $Version..."
    # No PublishSingleFile / no trimming: the Windows App SDK's native DLLs are loaded by name
    # from the app directory, and neither survives being bundled or trimmed.
    dotnet publish $projectPath `
        --configuration $Configuration `
        --runtime $Runtime `
        --self-contained:true `
        --output $publishRoot `
        -p:Platform=x64 `
        -p:Version=$Version `
        -p:AssemblyVersion="$Version.0" `
        -p:FileVersion="$Version.0" `
        -p:InformationalVersion=$Version

    if ($LASTEXITCODE -ne 0) {
        throw "Publish failed with exit code $LASTEXITCODE"
    }
}

# The publish silently omits these two without the PublishWinUiResourceIndex target in the
# csproj, and the app then fail-fasts with 0xC0000409 on its first XAML window. Fail here
# instead, where the cause is obvious.
foreach ($required in @("CodexBar.WinUI.exe", "CodexBar.WinUI.pri", "App.xbf")) {
    if (-not (Test-Path -LiteralPath (Join-Path $publishRoot $required))) {
        throw "Publish output is missing $required - the app would crash on its first window."
    }
}

Write-Host "Building MSI..."
dotnet build $installerProjectPath `
    --configuration $Configuration `
    -p:ProductVersion=$Version `
    -p:PublishDir=$installerPublishRoot

if ($LASTEXITCODE -ne 0) {
    throw "MSI build failed with exit code $LASTEXITCODE"
}

$msiPath = Join-Path $repoRoot "Installer\bin\$Configuration\CodexBar.WinUI-$Version-$Runtime.msi"
if (-not (Test-Path -LiteralPath $msiPath)) {
    throw "Expected MSI was not found: $msiPath"
}

$sizeMb = [math]::Round((Get-Item -LiteralPath $msiPath).Length / 1MB, 1)
Write-Host "Created $msiPath ($sizeMb MB)"
