# NINA Plugin Beta Release Checklist

## Release Information
- **Plugin Name**: AI Weather
- **Version**: 1.0.0.0
- **Channel**: Beta
- **Archive**: NINA.Plugin.AIWeather.1.0.0.0.zip
- **SHA256**: BD36FED504032708F1BDC1B1A346032A596364BB0059788BA1766C84C0EFC65A
- **Archive Size**: 297 MB (991 files)

## ✅ Pre-Release Verification

### Dependencies Included
- ✓ NINA.Plugin.AIWeather.dll (187 KB)
- ✓ LibVLCSharp + LibVLC runtime (194 KB + 2.8 MB core + 465 plugins)
- ✓ Emgu.CV for image processing (891 KB)
- ✓ Microsoft.ML libraries (ML.Core, ML.Data, ML.ImageAnalytics, etc.)
- ✓ ONNX Runtime (10.9 MB)
- ✓ OpenAI SDK (2.2 MB)
- ✓ Azure.AI.OpenAI (324 KB)
- ✓ System.Text.Json, Newtonsoft.Json, Nito.AsyncEx utilities
- ✓ ai_weather_logo.png (embedded as WPF Resource in DLL)

### Manifest Configuration
- ✓ Beta channel enabled (`"Channel": "Beta"`)
- ✓ Repository URL: https://github.com/michelebergo/nina.plugin.aiweather
- ✓ Installer URL: https://github.com/michelebergo/nina.plugin.aiweather/releases/download/v1.0.0.0/NINA.Plugin.AIWeather.1.0.0.0.zip
- ✓ Manifest path: `manifests/n/nina.plugin.aiweather/3.0.0.0/1.0.0.0/manifest.json`
- ✓ License: MIT
- ✓ Minimum NINA version: 3.0.0.0

## 📋 Steps to Publish

### 1. Create GitHub Repository (if not exists)
```bash
# Create a new repository at https://github.com/michelebergo/nina.plugin.aiweather
# Initialize it with this workspace code
cd C:\Users\miche\Desktop\NINA_ai_allskycamera
git init
git add .
git commit -m "Initial release v1.0.0.0"
git remote add origin https://github.com/michelebergo/nina.plugin.aiweather.git
git push -u origin main
```

### 2. Create GitHub Release
1. Go to: https://github.com/michelebergo/nina.plugin.aiweather/releases/new
2. Set tag: `v1.0.0.0` (or `1.0.0.0`)
3. Release title: `v1.0.0.0 - Beta Release`
4. Description:
   ```markdown
   ## AI Weather Plugin - Beta Release
   
   AI-powered all-sky camera weather monitoring for NINA.
   
   ### Features
   - Live RTSP stream preview
   - AI weather analysis (GitHub Models, OpenAI, Gemini, Anthropic)
   - Offline fallback using local image processing
   - Safety monitor integration for automatic imaging pause/resume
   
   ### Installation
   This is a **BETA** release. Install via NINA Plugin Manager by adding the beta channel:
   
   1. In NINA: Options → General → Plugin Repositories
   2. Click `+` and add: `https://nighttime-imaging.eu/wp-json/nina/v1/beta`
   3. Go to Plugin Manager → Available and install "AI Weather"
   
   Or download the ZIP below and extract to `%LOCALAPPDATA%\NINA\Plugins\3.0.0\`
   ```

5. **Upload the release asset**:
   - File: `C:\Users\miche\Desktop\NINA_ai_allskycamera\release\NINA.Plugin.AIWeather.1.0.0.0.zip`
   - Checksum: `BD36FED504032708F1BDC1B1A346032A596364BB0059788BA1766C84C0EFC65A`

6. Check "Set as a pre-release" (for beta)
7. Publish

### 3. Fork and Submit to nina.plugin.manifests

#### Fork the Manifest Repository
1. Go to: https://github.com/isbeorn/nina.plugin.manifests
2. Click "Fork" → Create fork under `michelebergo/nina.plugin.manifests`

#### Clone Your Fork
```bash
git clone https://github.com/michelebergo/nina.plugin.manifests.git
cd nina.plugin.manifests
```

#### Copy Manifest
```powershell
# Copy the generated manifest folder into your fork
Copy-Item -Path "C:\Users\miche\Desktop\NINA_ai_allskycamera\release\manifest-for-nina.plugin.manifests\manifests\n\nina.plugin.aiweather" -Destination ".\manifests\n\" -Recurse -Force
```

Your manifest should be at:
```
manifests/
  n/
    nina.plugin.aiweather/
      3.0.0.0/
        1.0.0.0/
          manifest.json
```

#### (Optional) Validate Manifest
If you have Node.js installed:
```bash
npm install
node gather.js
```
Check that your manifest shows as valid.

#### Commit and Push
```bash
git add manifests/n/nina.plugin.aiweather
git commit -m "Add nina.plugin.aiweather 1.0.0.0 beta manifest"
git push origin main
```

#### Create Pull Request
1. Go to: https://github.com/isbeorn/nina.plugin.manifests
2. Click "Pull requests" → "New pull request"
3. Click "compare across forks"
4. Head repository: `michelebergo/nina.plugin.manifests`
5. Base repository: `isbeorn/nina.plugin.manifests` (base: `main`)
6. Title: `Add nina.plugin.aiweather 1.0.0.0 beta manifest`
7. Description:
   ```markdown
   Adding beta manifest for AI Weather plugin v1.0.0.0
   
   - Plugin: AI Weather
   - Channel: Beta
   - Repository: https://github.com/michelebergo/nina.plugin.aiweather
   - License: MIT
   
   AI-powered all-sky camera weather monitoring with RTSP support.
   ```
8. Create pull request

### 4. Wait for Review
- The NINA maintainers will review your PR
- They may request changes or approve/merge it
- Once merged, your plugin will appear in the beta channel within a few hours

## 🔄 Future Releases (Automated with GitHub Actions)

The workflow `.github/workflows/build-and-release-nina.yaml` is already configured.

**For automated releases:**

1. **Set up PAT secret** (optional, for auto-PR to manifest repo):
   - Create a Personal Access Token: https://github.com/settings/tokens
   - Scope: `repo` + `workflow`
   - Add to your plugin repo secrets as `PAT`
   - Name: `PAT`

2. **Push a version tag**:
   ```bash
   git tag 1.0.1.0
   git push origin 1.0.1.0
   ```

3. The workflow will:
   - Build the plugin
   - Create ZIP + manifest via CreateManifest.ps1
   - Create GitHub Release with both assets
   - (If PAT exists) Auto-open PR to isbeorn/nina.plugin.manifests

## 📝 Notes

- **Beta Channel URL**: `https://nighttime-imaging.eu/wp-json/nina/v1/beta`
- Users must opt-in by adding this URL to their Plugin Repositories in NINA
- For production releases, change `Channel = "Beta"` to `Channel = "Release"` in `prepare-nina-release.ps1`

## 🔗 Useful Links

- NINA Plugin Manifest Repo: https://github.com/isbeorn/nina.plugin.manifests
- NINA Plugin Template: https://github.com/isbeorn/nina.plugin.template
- NINA Discord: https://discord.gg/nighttime-imaging
- Your Manifest Fork: https://github.com/michelebergo/nina.plugin.manifests
- Your Plugin Repo: https://github.com/michelebergo/nina.plugin.aiweather

---

**Generated**: January 24, 2026
**Plugin Version**: 1.0.0.0 Beta
