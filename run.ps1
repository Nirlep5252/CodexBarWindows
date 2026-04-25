$ErrorActionPreference = "Stop"

$repoRoot = Split-Path -Parent $MyInvocation.MyCommand.Path
$projectPath = Join-Path $repoRoot "CodexBarWindows.csproj"
$appName = "CodexBarWindows"
$exePath = Join-Path $repoRoot "bin\x64\Debug\net10.0-windows\$appName.exe"

Get-Process -Name $appName -ErrorAction SilentlyContinue | Stop-Process -Force

dotnet build $projectPath -p:Platform=x64
if ($LASTEXITCODE -ne 0) {
    throw "Build failed with exit code $LASTEXITCODE"
}

if (-not (Test-Path -LiteralPath $exePath)) {
    throw "Built executable was not found: $exePath"
}

Start-Process -FilePath $exePath -WorkingDirectory (Split-Path -Parent $exePath)
Write-Host "Started $appName from $exePath"
