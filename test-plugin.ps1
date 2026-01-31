# AI Weather Plugin Test Script
Write-Host "`n=== AI Weather Plugin Test ===" -ForegroundColor Cyan

# Check if plugin is deployed
$pluginPath = "$env:LOCALAPPDATA\NINA\Plugins\3.0.0\AIWeather"
Write-Host "`n1. Checking plugin deployment..." -ForegroundColor Yellow
if (Test-Path $pluginPath) {
    Write-Host "   ✓ Plugin folder exists" -ForegroundColor Green
    $dllCount = (Get-ChildItem $pluginPath -Filter "*.dll" -Recurse | Measure-Object).Count
    Write-Host "   ✓ Found $dllCount DLL files" -ForegroundColor Green
} else {
    Write-Host "   ✗ Plugin not deployed!" -ForegroundColor Red
    exit
}

# Check if NINA is running
Write-Host "`n2. Checking NINA status..." -ForegroundColor Yellow
$nina = Get-Process -Name "NINA" -ErrorAction SilentlyContinue
if ($nina) {
    Write-Host "   ✓ NINA is running (PID: $($nina.Id))" -ForegroundColor Green
} else {
    Write-Host "   ! NINA is not running" -ForegroundColor Yellow
}

# Check logs for plugin loading
Write-Host "`n3. Checking NINA logs..." -ForegroundColor Yellow
$logMatch = Get-Content "$env:LOCALAPPDATA\NINA\Logs\*.log" -ErrorAction SilentlyContinue | 
    Select-String "AI Weather" | 
    Select-Object -Last 1

if ($logMatch) {
    Write-Host "   ✓ Plugin found in logs:" -ForegroundColor Green
    Write-Host "   $logMatch" -ForegroundColor Gray
} else {
    Write-Host "   ! No plugin logs found" -ForegroundColor Yellow
}

# Display configuration
Write-Host "`n4. Current Settings:" -ForegroundColor Yellow
$configPath = "$env:LOCALAPPDATA\NINA\NINA.Settings\user.config"
if (Test-Path $configPath) {
    Write-Host "   Settings file exists" -ForegroundColor Green
} else {
    Write-Host "   Using default settings (not configured yet)" -ForegroundColor Yellow
}

Write-Host "`n=== Test Results ===" -ForegroundColor Cyan
Write-Host ""
Write-Host "Next Steps:" -ForegroundColor Green
Write-Host "1. Open NINA → Options → Plugins → AI Weather" -ForegroundColor White
Write-Host "2. Configure RTSP URL and settings" -ForegroundColor White
Write-Host "3. Go to Equipment → Safety Monitor → Select 'AI Weather Safety Monitor'" -ForegroundColor White
Write-Host "4. Click Connect to see live preview" -ForegroundColor White
Write-Host ""

# Test RTSP connection (if URL is configured)
Write-Host "Test RTSP Connection:" -ForegroundColor Yellow
Write-Host "If you have an all-sky camera, enter RTSP URL to test:" -ForegroundColor White
Write-Host "Format: rtsp://username:password@192.168.1.100:554/stream" -ForegroundColor Gray
Write-Host "(Leave empty to skip)" -ForegroundColor Gray
$rtspUrl = Read-Host "RTSP URL"

if ($rtspUrl) {
    Write-Host "`nTesting connection to $rtspUrl..." -ForegroundColor Yellow
    try {
        # Simple ping test to camera IP
        $ip = ([System.Uri]$rtspUrl).Host
        $ping = Test-Connection -ComputerName $ip -Count 1 -Quiet
        if ($ping) {
            Write-Host "✓ Camera host is reachable" -ForegroundColor Green
        } else {
            Write-Host "✗ Cannot reach camera host" -ForegroundColor Red
        }
    } catch {
        Write-Host "✗ Invalid URL format" -ForegroundColor Red
    }
}

Write-Host ""
Write-Host "=== Test Complete ===" -ForegroundColor Cyan
