param(
    # Where your plugin source is hosted (required by the manifest schema)
    [string]$RepositoryUrl = "https://github.com/michelebergo/nina.plugin.aiweather",

    # Manifest Author field (required)
    [string]$Author = "michelebergo",

    # Optional: set to 'Beta' to create a beta-channel manifest
    [ValidateSet("Release", "Beta")]
    [string]$Channel = "Beta"
)

$ErrorActionPreference = "Stop"

$projectDir = Split-Path -Parent $MyInvocation.MyCommand.Path
Set-Location $projectDir

Write-Host "=== Prepare NINA Release ===" -ForegroundColor Cyan

# 1) Read version from AssemblyInfo.cs (AssemblyVersion)
$assemblyInfoPath = Join-Path $projectDir "Properties\AssemblyInfo.cs"
$assemblyInfo = Get-Content $assemblyInfoPath -Raw
$versionMatch = [regex]::Match($assemblyInfo, 'AssemblyVersion\("(?<v>[^\"]+)"\)')
if (!$versionMatch.Success) {
    throw "Could not find AssemblyVersion in $assemblyInfoPath"
}
$pluginVersion = $versionMatch.Groups['v'].Value

# 2) Build into a dedicated output directory to avoid any locks in bin\Release
$releaseDir = Join-Path $projectDir "release"
New-Item -ItemType Directory -Path $releaseDir -Force | Out-Null

$outDir = Join-Path $releaseDir ("out\\NINA.Plugin.AIWeather." + $pluginVersion)
if (Test-Path $outDir) { Remove-Item $outDir -Recurse -Force }
New-Item -ItemType Directory -Path $outDir -Force | Out-Null

Write-Host "Building (Release) to: $outDir" -ForegroundColor Yellow
& dotnet build .\AIWeather.csproj -c Release -o $outDir | Out-Null

$dllPath = Join-Path $outDir "NINA.Plugin.AIWeather.dll"
if (!(Test-Path $dllPath)) {
    throw "Release build output not found: $dllPath"
}

# 3) Read MinimumApplicationVersion + Identifier
$minAppMatch = [regex]::Match($assemblyInfo, 'AssemblyMetadata\("MinimumApplicationVersion",\s*"(?<v>[^\"]+)"\)')
if (!$minAppMatch.Success) {
    throw "Could not find MinimumApplicationVersion metadata in $assemblyInfoPath"
}
$minAppVersion = $minAppMatch.Groups['v'].Value

$idMatch = [regex]::Match($assemblyInfo, 'AssemblyMetadata\("Identifier",\s*"(?<v>[^\"]+)"\)')
if (!$idMatch.Success) {
    throw "Could not find Identifier metadata in $assemblyInfoPath"
}
$pluginIdentifier = $idMatch.Groups['v'].Value

# 4) Package (ZIP of all files in output folder)
$manifestRoot = Join-Path $releaseDir "manifest-for-nina.plugin.manifests"

New-Item -ItemType Directory -Path $releaseDir -Force | Out-Null
New-Item -ItemType Directory -Path $manifestRoot -Force | Out-Null

$zipName = "NINA.Plugin.AIWeather.$pluginVersion.zip"
$zipPath = Join-Path $releaseDir $zipName

if (Test-Path $zipPath) { Remove-Item $zipPath -Force }

Write-Host "Creating archive: $zipName" -ForegroundColor Yellow
Compress-Archive -Path (Join-Path $outDir '*') -DestinationPath $zipPath -Force

# 5) Hash
$hash = (Get-FileHash -Algorithm SHA256 -Path $zipPath).Hash

# 6) Build manifest.json (schema: nina.plugin.manifests/manifest.schema.json)
function Split-Version([string]$v) {
    $parts = $v.Split('.')
    while ($parts.Count -lt 4) { $parts += '0' }
    return @{ Major=$parts[0]; Minor=$parts[1]; Patch=$parts[2]; Build=$parts[3] }
}

$pluginName = "AI Weather"
$shortDescription = "AI-powered all-sky camera weather monitoring"

$pluginVersionParts = Split-Version $pluginVersion
$minAppVersionParts = Split-Version $minAppVersion

# Installer URL must point to your hosted release asset.
# Replace after you publish the GitHub Release.
$installerUrl = "${RepositoryUrl}/releases/download/v$pluginVersion/$zipName"

$manifestObj = [ordered]@{
    Name = $pluginName
    Identifier = $pluginIdentifier
    Author = $Author
    Repository = $RepositoryUrl
    License = "MIT"
    LicenseURL = "https://opensource.org/licenses/MIT"
    Version = $pluginVersionParts
    MinimumApplicationVersion = $minAppVersionParts
    Tags = @("Weather","Safety Monitor","All Sky Camera","AI","RTSP")
    Descriptions = [ordered]@{
        ShortDescription = $shortDescription
        # LongDescription is optional; NINA will show it if present.
        # You can copy/paste a longer text here if you want.
    }
    Installer = [ordered]@{
        URL = $installerUrl
        Type = "ARCHIVE"
        Checksum = $hash
        ChecksumType = "SHA256"
    }
}

if ($Channel -eq "Beta") {
    $manifestObj["Channel"] = "Beta"
}

$manifestJson = ($manifestObj | ConvertTo-Json -Depth 8)

# 7) Write manifest into the folder structure expected by nina.plugin.manifests
# manifests/<first-letter>/<plugin folder name>/<minimum nina version>/<plugin version>/manifest.json
# Convention: use a stable, space-free folder name based on the plugin assembly name.
$pluginManifestFolderName = "nina.plugin.aiweather"
$firstLetter = $pluginManifestFolderName.Substring(0,1).ToLowerInvariant()
$manifestFolder = Join-Path $manifestRoot (Join-Path "manifests\$firstLetter" (Join-Path $pluginManifestFolderName (Join-Path $minAppVersion (Join-Path $pluginVersion ""))))
New-Item -ItemType Directory -Path $manifestFolder -Force | Out-Null

$manifestPath = Join-Path $manifestFolder "manifest.json"
$manifestJson | Out-File -FilePath $manifestPath -Encoding UTF8

Write-Host "" 
Write-Host "Release artifacts:" -ForegroundColor Green
Write-Host "- ZIP: $zipPath" -ForegroundColor Green
Write-Host "- SHA256: $hash" -ForegroundColor Green
Write-Host "- Manifest: $manifestPath" -ForegroundColor Green
Write-Host "" 
Write-Host "NEXT:" -ForegroundColor Cyan
Write-Host "1) Publish a GitHub Release tag v$pluginVersion and upload $zipName" -ForegroundColor Cyan
Write-Host "2) Update RepositoryUrl/Author in the manifest if needed" -ForegroundColor Cyan
Write-Host "3) Fork nina.plugin.manifests and copy the 'manifests' folder from:" -ForegroundColor Cyan
Write-Host "   $manifestRoot" -ForegroundColor Cyan
Write-Host "4) (Optional) Validate in the manifest repo: npm install; node gather.js" -ForegroundColor Cyan
