Write-Host "`n=== AI Weather Plugin Deploy ===" -ForegroundColor Cyan

$ErrorActionPreference = "Stop"

Write-Host "`nBuilding plugin (Release)..." -ForegroundColor Yellow
dotnet build AIWeather.csproj -c Release 2>&1 | Out-Null

if ($LASTEXITCODE -ne 0) {
    Write-Host "Build failed." -ForegroundColor Red
    dotnet build AIWeather.csproj -c Release
    exit 1
}

Write-Host "Build successful." -ForegroundColor Green

Write-Host "`nStopping NINA (if running)..." -ForegroundColor Yellow
$ninaProcess = Get-Process -Name "NINA" -ErrorAction SilentlyContinue
if ($ninaProcess) {
    $ninaProcess | Stop-Process -Force
    try {
        $ninaProcess | Wait-Process -Timeout 10
    } catch {
        Start-Sleep -Seconds 2
    }
}

$targetDir = "$env:LOCALAPPDATA\NINA\Plugins\3.0.0\AIWeather"
Write-Host "`nDeploying to: $targetDir" -ForegroundColor Yellow

Remove-Item $targetDir -Recurse -Force -ErrorAction SilentlyContinue
New-Item -ItemType Directory -Path $targetDir -Force | Out-Null

Copy-Item "bin\Release\net8.0-windows\*" -Destination $targetDir -Force -Recurse

if (Test-Path "manifest.json") {
    Copy-Item "manifest.json" -Destination $targetDir -Force
}

$fileCount = (Get-ChildItem $targetDir -Recurse -File | Measure-Object).Count
Write-Host "Deployed successfully ($fileCount files)." -ForegroundColor Green

Write-Host "`n(Optional) Restarting NINA..." -ForegroundColor Yellow
$ninaExe = "C:\Program Files\N.I.N.A. - Nighttime Imaging 'N' Astronomy\NINA.exe"
if (Test-Path $ninaExe) {
    Start-Process $ninaExe
    Write-Host "NINA started." -ForegroundColor Green
} else {
    Write-Host "NINA.exe not found at: $ninaExe" -ForegroundColor Yellow
}

Write-Host "`n=== Done ===" -ForegroundColor Cyan
