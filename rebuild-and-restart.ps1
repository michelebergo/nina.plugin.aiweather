Write-Host "`n=== AI Weather Plugin Build & Deploy ===" -ForegroundColor Cyan

Write-Host "`nBuilding plugin..." -ForegroundColor Yellow
dotnet build AIWeather.csproj -c Release 2>&1 | Out-Null

if ($LASTEXITCODE -ne 0) {
    Write-Host "Build failed!" -ForegroundColor Red
    dotnet build AIWeather.csproj -c Release
    exit 1
}

Write-Host "Build successful" -ForegroundColor Green

$targetDir = "$env:LOCALAPPDATA\NINA\Plugins\3.0.0\AIWeather"

Write-Host "`nChecking for running NINA..." -ForegroundColor Yellow
$ninaProcess = Get-Process -Name "NINA" -ErrorAction SilentlyContinue
if ($ninaProcess) {
    Write-Host "Closing NINA..." -ForegroundColor Yellow
    $ninaProcess | Stop-Process -Force
    Start-Sleep -Seconds 2
    Write-Host "NINA closed" -ForegroundColor Green
}

Write-Host "Deploying to: $targetDir" -ForegroundColor Yellow

for ($attempt = 1; $attempt -le 3; $attempt++) {
    try {
        # Remove old deployment and recreate
        Remove-Item $targetDir -Recurse -Force -ErrorAction SilentlyContinue
        New-Item -ItemType Directory -Path $targetDir -Force | Out-Null

        # Deploy all files from build output (including dependencies)
        Copy-Item "bin\Release\net8.0-windows\*" -Destination $targetDir -Force -Recurse -ErrorAction Stop

        # Copy manifest
        Copy-Item "manifest.json" -Destination $targetDir -Force -ErrorAction Stop

        break
    }
    catch {
        if ($attempt -eq 3) {
            Write-Host "Deploy failed after 3 attempts: $($_.Exception.Message)" -ForegroundColor Red
            throw
        }

        Write-Host "Deploy attempt $attempt failed (likely file lock). Retrying..." -ForegroundColor Yellow
        Start-Sleep -Seconds 2
    }
}

Write-Host "Deployed successfully ($(((Get-ChildItem $targetDir -Recurse -File | Measure-Object).Count)) files)" -ForegroundColor Green

Write-Host "`nStarting NINA..." -ForegroundColor Yellow
Start-Process "C:\Program Files\N.I.N.A. - Nighttime Imaging 'N' Astronomy\NINA.exe"
Write-Host "NINA started" -ForegroundColor Green

Write-Host "`n=== Complete! ===" -ForegroundColor Cyan
Write-Host "Plugin 'AI Weather' is ready" -ForegroundColor Green
