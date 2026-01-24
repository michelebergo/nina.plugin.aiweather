# Quick Setup Guide for All Sky Camera Plugin

## Prerequisites

1. **Install NINA** (if not already installed)
   - Download from: https://nighttime-imaging.eu/
   - Install to default location

2. **GitHub Personal Access Token** (for AI features)
   - Go to: https://github.com/settings/tokens
   - Create new token (classic) with model access
   - Save the token for later

## Installation Steps

### Option 1: Automated Deployment (Recommended)

1. **Run the deployment script:**
   ```powershell
   .\deploy.ps1
   ```

2. The script will:
   - Find your NINA installation
   - Build the plugin
   - Deploy it to NINA's plugins folder
   - Show you next steps

### Option 2: Manual Build & Deploy

1. **Build the plugin:**
   ```powershell
   dotnet build AllSkyCameraPlugin.csproj -c Release
   ```

2. **Copy files to NINA:**
   - From: `bin\Release\net8.0-windows\`
   - To: `%LOCALAPPDATA%\NINA\Plugins\AllSkyCameraPlugin\`

3. **Copy all DLL files and runtime folders**

## Configuration in NINA

1. **Start NINA**

2. **Configure Plugin:**
   - Go to **Options → Plugins**
   - Find "All Sky Camera Weather Monitor"
   - Configure settings:
     - **RTSP URL**: Your camera stream (e.g., `rtsp://192.168.1.100:554/stream`)
     - **Check Interval**: How often to check (default: 5 minutes)
     - **Cloud Threshold**: Max cloud % for safe imaging (default: 70%)

3. **Optional - Enable AI:**
   - Check "Use GitHub Models AI"
   - Select model (GPT-4o recommended)
   - Enter your GitHub token
   - Click Save

4. **Connect Safety Monitor:**
   - Go to **Equipment → Safety Monitor**
   - Select "All Sky Camera Safety Monitor"
   - Click **Connect**

5. **View Live Preview:**
   - The preview will show your camera feed
   - Click **Refresh** to capture and analyze
   - See real-time weather conditions and safety status

## Testing Your Setup

1. **Test RTSP Connection:**
   - Open VLC Media Player
   - Media → Open Network Stream
   - Enter your RTSP URL
   - Verify you see the camera feed

2. **Test Plugin:**
   - In NINA, click "Refresh" in the preview
   - You should see:
     - Camera image appears
     - Weather analysis completes
     - Safety status updates

## Troubleshooting

### Build Errors

**"NINA assemblies not found"**
- Ensure NINA is installed
- Run `deploy.ps1` to auto-configure paths

**"NuGet restore failed"**
```powershell
dotnet nuget locals all --clear
dotnet restore AllSkyCameraPlugin.csproj
```

### Runtime Errors

**"Failed to connect to RTSP"**
- Verify RTSP URL is correct
- Check camera is accessible on network
- Test with VLC first

**"GitHub Models not working"**
- Verify GitHub token is valid
- Check internet connection
- Try Local AI mode first

**"Plugin not appearing in NINA"**
- Check NINA logs: `%LOCALAPPDATA%\NINA\Logs\`
- Ensure all DLLs copied to plugins folder
- Restart NINA completely

## File Locations

- **Plugin Files**: `%LOCALAPPDATA%\NINA\Plugins\AllSkyCameraPlugin\`
- **NINA Logs**: `%LOCALAPPDATA%\NINA\Logs\`
- **Captured Images**: `%LOCALAPPDATA%\NINA\Temp\AllSkyCameraPlugin\`
- **Settings**: `%LOCALAPPDATA%\NINA\Settings\`

## What to Expect

### First Run
1. Connect to camera (may take 5-10 seconds)
2. First capture and analysis
3. Safety status appears
4. Automatic checks begin based on interval

### During Operation
- Plugin captures frame every X minutes
- AI analyzes weather conditions
- Safety status updates automatically
- NINA pauses imaging if unsafe conditions detected

### Performance
- **Local AI**: Instant analysis, basic accuracy
- **GitHub Models**: 1-3 second analysis, high accuracy
- **Memory**: ~50-100 MB
- **Network**: Minimal (only for GitHub Models)

## Need Help?

1. Check the [README.md](README.md) for detailed documentation
2. See [GITHUB_MODELS_SETUP.md](GITHUB_MODELS_SETUP.md) for AI configuration
3. Review NINA logs for error details
4. Ensure RTSP camera is working with VLC first

## Success Indicators

✓ Plugin appears in NINA Options → Plugins
✓ Safety Monitor shows "All Sky Camera Safety Monitor"
✓ Preview shows camera feed
✓ Weather analysis displays results
✓ Safety status updates (green/red indicator)
✓ Automatic checks run periodically

---

**Ready to go!** Start capturing and let AI protect your equipment! 🌟
