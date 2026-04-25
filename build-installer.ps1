param(
    [string]$Configuration = "Release",
    [string]$Runtime = "win-x64",
    [string]$Version
)

$ErrorActionPreference = "Stop"

$repoRoot = Split-Path -Parent $MyInvocation.MyCommand.Path
$projectPath = Join-Path $repoRoot "CodexBarWindows.csproj"
$installerProjectPath = Join-Path $repoRoot "Installer\CodexBarWindows.Installer.wixproj"
$publishRoot = Join-Path $repoRoot "bin\publish\$Runtime"
$installerPublishRoot = "..\bin\publish\$Runtime"

if ([string]::IsNullOrWhiteSpace($Version)) {
    [xml]$props = Get-Content -LiteralPath (Join-Path $repoRoot "Directory.Build.props")
    $Version = $props.Project.PropertyGroup.VersionPrefix
}

Write-Host "Publishing CodexBarWindows $Version..."
dotnet publish $projectPath `
    --configuration $Configuration `
    --runtime $Runtime `
    --self-contained:true `
    --output $publishRoot `
    -p:Version=$Version `
    -p:AssemblyVersion="$Version.0" `
    -p:FileVersion="$Version.0" `
    -p:InformationalVersion=$Version `
    -p:PublishSingleFile=true `
    -p:IncludeNativeLibrariesForSelfExtract=true `
    -p:EnableCompressionInSingleFile=true

if ($LASTEXITCODE -ne 0) {
    throw "Publish failed with exit code $LASTEXITCODE"
}

Write-Host "Building MSI..."
dotnet build $installerProjectPath `
    --configuration $Configuration `
    -p:ProductVersion=$Version `
    -p:PublishDir=$installerPublishRoot

if ($LASTEXITCODE -ne 0) {
    throw "MSI build failed with exit code $LASTEXITCODE"
}

$msiPath = Join-Path $repoRoot "Installer\bin\$Configuration\CodexBarWindows-$Version-$Runtime.msi"
if (-not (Test-Path -LiteralPath $msiPath)) {
    throw "Expected MSI was not found: $msiPath"
}

Write-Host "Created $msiPath"
