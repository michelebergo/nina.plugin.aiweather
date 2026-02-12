# prepare-nina-release.ps1
# Local release helper that uses the official CreateManifest.ps1 from
# https://github.com/isbeorn/nina.plugin.manifests/blob/main/tools/CreateManifest.ps1
#
# Usage:
#   .\prepare-nina-release.ps1           # Build + generate manifest (release)
#   .\prepare-nina-release.ps1 -Beta    # Build + generate manifest (beta channel)

param(
    [string]$RepositoryUrl = "https://github.com/michelebergo/nina.plugin.aiweather",
    [switch]$Beta = $false
)

$ErrorActionPreference = "Stop"

$projectDir = Split-Path -Parent $MyInvocation.MyCommand.Path
Set-Location $projectDir

Write-Host "=== Prepare NINA Release ===" -ForegroundColor Cyan

# ---------------------------------------------------------------------------
# 1) Read version from AssemblyInfo.cs
# ---------------------------------------------------------------------------
$assemblyInfoPath = Join-Path $projectDir "Properties\AssemblyInfo.cs"
$assemblyInfo     = Get-Content $assemblyInfoPath -Raw
$versionMatch     = [regex]::Match($assemblyInfo, 'AssemblyVersion\("(?<v>[^\"]+)"\)')
if (!$versionMatch.Success) { throw "Could not find AssemblyVersion in $assemblyInfoPath" }
$pluginVersion = $versionMatch.Groups['v'].Value

Write-Host "Plugin version: $pluginVersion" -ForegroundColor Yellow

# ---------------------------------------------------------------------------
# 2) Build into a staging folder
# ---------------------------------------------------------------------------
$releaseDir = Join-Path $projectDir "release"
$outDir     = Join-Path $releaseDir "packages\AI Weather"
if (Test-Path $outDir) { Remove-Item $outDir -Recurse -Force }
New-Item -ItemType Directory -Path $outDir -Force | Out-Null

Write-Host "Building (Release) to: $outDir" -ForegroundColor Yellow
& dotnet build .\AIWeather.csproj -c Release -p:PostBuildEvent= -p:Version=$pluginVersion -o $outDir
if ($LASTEXITCODE -ne 0) { throw "Build failed" }

$dllPath = Join-Path $outDir "NINA.Plugin.AIWeather.dll"
if (!(Test-Path $dllPath)) { throw "DLL not found: $dllPath" }

# ---------------------------------------------------------------------------
# 3) Download the official CreateManifest.ps1 (if not already cached)
# ---------------------------------------------------------------------------
$createManifestPath = Join-Path $releaseDir "CreateManifest.ps1"
$manifestScriptUrl  = "https://raw.githubusercontent.com/isbeorn/nina.plugin.manifests/refs/heads/main/tools/CreateManifest.ps1"

if (!(Test-Path $createManifestPath)) {
    Write-Host "Downloading official CreateManifest.ps1 ..." -ForegroundColor Yellow
    Invoke-WebRequest -Uri $manifestScriptUrl -OutFile $createManifestPath -UseBasicParsing
} else {
    Write-Host "Using cached CreateManifest.ps1" -ForegroundColor DarkGray
}

# ---------------------------------------------------------------------------
# 4) Run CreateManifest.ps1 — it reads ALL metadata from the DLL
# ---------------------------------------------------------------------------
$installerUrl = "$RepositoryUrl/releases/download/$pluginVersion/NINA.Plugin.AIWeather.zip"

$cmArgs = @(
    "-file",          $dllPath,
    "-installerUrl",  $installerUrl,
    "-createArchive",
    "-includeAll"
)
if ($Beta) { $cmArgs += "-beta" }

Write-Host "Running CreateManifest.ps1 ..." -ForegroundColor Yellow
Push-Location $releaseDir
try {
    & pwsh -File $createManifestPath @cmArgs
    if ($LASTEXITCODE -ne 0) { throw "CreateManifest.ps1 failed" }
} finally {
    Pop-Location
}

# ---------------------------------------------------------------------------
# 5) Show results
# ---------------------------------------------------------------------------
$zipFile      = Get-ChildItem -Path $releaseDir -Filter "NINA.Plugin.AIWeather*.zip" | Sort-Object LastWriteTime -Descending | Select-Object -First 1
$manifestFile = Get-ChildItem -Path $releaseDir -Filter "manifest.json" -ErrorAction SilentlyContinue

Write-Host ""
Write-Host "Release artifacts:" -ForegroundColor Green
if ($zipFile)      { Write-Host "  ZIP:      $($zipFile.FullName)" -ForegroundColor Green }
if ($manifestFile) { Write-Host "  Manifest: $($manifestFile.FullName)" -ForegroundColor Green }
Write-Host ""
Write-Host "NEXT STEPS:" -ForegroundColor Cyan
Write-Host "  1) Push a git tag:  git tag $pluginVersion && git push origin $pluginVersion" -ForegroundColor Cyan
Write-Host "     The GitHub Action will build, release, and create a manifest PR automatically." -ForegroundColor Cyan
Write-Host ""
Write-Host "  OR manually:" -ForegroundColor Cyan
Write-Host "  1) Create a GitHub Release for tag $pluginVersion and upload the ZIP" -ForegroundColor Cyan
Write-Host "  2) Copy manifest.json to your fork of nina.plugin.manifests:" -ForegroundColor Cyan
Write-Host "     manifests/n/nina.plugin.aiweather/manifest.$pluginVersion.json" -ForegroundColor Cyan
Write-Host "  3) Open a PR to isbeorn/nina.plugin.manifests" -ForegroundColor Cyan
