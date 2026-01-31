# All Sky Camera Weather Monitor Plugin for NINA

An intelligent NINA plugin that monitors all-sky cameras and uses AI to determine weather conditions, automatically integrating with NINA's safety monitoring system to protect your equipment.

## Features

- 🌤️ **AI-Powered Weather Detection**: Analyzes all-sky camera images to detect:
  - Cloud coverage percentage
  - Clear/Cloudy/Overcast conditions
  - Rain detection
  - Fog detection
  
- 🔒 **Safety Integration**: Seamlessly integrates with NINA's safety monitoring system to automatically pause/stop imaging sequences when conditions become unsafe

- 📹 **Multiple Capture Modes**:
  - **RTSP Stream**: Connect to network cameras with continuous video monitoring
  - **INDI Camera**: Periodic captures from INDI all-sky cameras (lower resource usage)
  - **Folder Watch**: Monitor a folder for the latest image (works with any camera software)

- 🤖 **Multiple AI Providers**:
  - **Local AI**: Advanced image processing for offline weather analysis
  - **GitHub Models**: Claude 3.5, GPT-4o, Gemini (free for development)
  - **OpenAI**: GPT-4o, GPT-4o Mini (requires API key)
  - **Google Gemini**: 1.5 Flash, 1.5 Pro, 2.0 Flash (requires API key)
  - **Anthropic Claude**: 3.5 Sonnet, 3.5 Haiku, 3 Opus (requires API key)

- ⚙️ **Fully Configurable**:
  - Adjustable check intervals
  - Customizable cloud coverage thresholds
  - Easy configuration for all capture modes

## Installation

1. Download the latest release from the [Releases](../../releases) page
2. Extract the plugin files to your NINA plugins folder:
   - Default location: `C:\Users\[YourUsername]\AppData\Local\NINA\Plugins\`
3. Restart NINA
4. Navigate to **Options → Plugins** and configure the All Sky Camera plugin

## Configuration

### Capture Mode Selection

Choose how the plugin captures sky images:

#### RTSP Stream Mode
- For network cameras with RTSP support
- **RTSP Stream URL**: Enter your camera's RTSP address (e.g., `rtsp://192.168.1.100:554/stream`)
- Optional: Enter username/password if required
- Provides continuous video monitoring with real-time preview

#### INDI Camera Mode
- For INDI-compatible all-sky cameras connected to NINA
- **INDI Device Name**: Enter the name of your INDI camera device
- Lower resource usage (periodic captures vs. continuous streaming)
- Integrates directly with NINA's equipment management

#### Folder Watch Mode
- Works with any camera software that saves images to a folder
- **Folder Path**: Select the folder where your camera saves images
- Compatible with AllSky, UFOCapture, SharpCap, and other software
- Monitors for the latest image file (.jpg, .png, .bmp, .tif)

### Basic Settings

1. **Check Interval**: How often to analyze the sky (in minutes)
   - Recommended: 5-10 minutes for active monitoring
   - Lower values = more frequent checks but higher resource usage

2. **Cloud Coverage Threshold**: Maximum cloud coverage percentage considered "safe"
   - Default: 70%
   - Lower values = more conservative (stops imaging with fewer clouds)
   - Higher values = more permissive

### AI Provider Settings (Optional)

For enhanced accuracy, you can enable cloud-based AI analysis:

#### GitHub Models (Recommended - Free for Development)
1. Get a GitHub Personal Access Token:
   - Go to [GitHub Settings → Tokens](https://github.com/settings/tokens)
   - Create a new token (classic) with model access
   - Copy the token
2. Enable "Use GitHub Models AI"
3. Select your preferred model (Claude 3.5 Sonnet recommended)
4. Enter your GitHub token

#### Other AI Providers
See [SETUP.md](SETUP.md) for detailed instructions on configuring OpenAI, Gemini, and Anthropic.

## Usage

### Live Preview

To see the camera feed and analysis results in NINA:

1. In NINA, go to **Equipment → Safety Monitor**
2. Select "All Sky Camera Safety Monitor"
3. Click Connect
4. The preview window will show:
   - 📷 **Live view** (RTSP mode) or latest capture (INDI/Folder modes)
   - ☁️ **Cloud coverage percentage**
   - 🌦️ **Weather condition**
   - ✅ **Safety status**
   - ☁️ **Cloud coverage** percentage with visual indicator
   - 🌤️ **Weather condition** (Clear, Cloudy, Rain, Fog, etc.)
   - ✅/❌ **Safety status** for imaging
   - 📊 **Confidence level** of the AI analysis
   - 💬 **Detailed description** from the AI

**Controls:**
- **▶️ Refresh**: Capture a new frame and analyze immediately
- **💾 Save Image**: Export the current frame to a file

### As a Safety Monitor

1. In NINA, go to **Equipment → Safety Monitor**
2. Select "All Sky Camera Safety Monitor"
3. Click Connect

The plugin will now:
- Periodically capture images from your RTSP stream
- Analyze weather conditions using AI
- Report safety status to NINA
- Automatically pause/stop sequences when unsafe conditions are detected

### Weather Conditions Detected

| Condition | Description | Safe for Imaging? |
|-----------|-------------|-------------------|
| Clear | < 20% cloud coverage | ✅ Yes |
| Partly Cloudy | 20-50% cloud coverage | ✅ Yes (configurable) |
| Mostly Cloudy | 50-80% cloud coverage | ⚠️ Depends on threshold |
| Overcast | > 80% cloud coverage | ❌ No |
| Rainy | Rain detected | ❌ No |
| Foggy | Fog detected | ❌ No |

## How It Works

### Local AI Analysis

The local AI mode uses sophisticated image processing algorithms:

1. **Brightness Analysis**: Clouds reflect ambient light, increasing sky brightness
2. **Color Distribution**: Analyzes blue content and color variance
3. **Pattern Detection**: Identifies rain streaks and fog uniformity
4. **Cloud Coverage Calculation**: Combines multiple metrics to estimate cloud percentage

### GitHub Models AI Analysis

When enabled, GitHub Models provides:
- Access to state-of-the-art vision models (Claude, GPT-4, Gemini)
- Advanced scene understanding and weather pattern recognition
- Natural language descriptions of sky conditions
- High confidence scores with detailed reasoning
- Free for development use

## Troubleshooting

### RTSP Connection Issues

- Verify the RTSP URL is correct
- Ensure your camera is accessible from your imaging computer
- Check firewall settings
- Test the stream in VLC Media Player first

### AI Analysis Not Working

- Check NINA logs in `%LOCALAPPDATA%\NINA\Logs\`
- Ensure adequate lighting for image analysis (IR cameras work best)
- Verify captured images are being saved to temp folder
- If using Azure AI, verify your credentials and quota

### Safety Monitor Not Responding

- Ensure the plugin is connected
- Check the monitoring interval isn't too long
- Verify RTSP stream is active
- Review recent weather analysis results in logs

## Development

### Building from Source

Requirements:
- Visual Studio 2022 or later
- .NET 8.0 SDK
- NINA installed (for assembly references)

Steps:
```bash
git clone https://github.com/yourusername/AllSkyCameraPlugin.git
cd AllSkyCameraPlugin
dotnet restore
dotnet build
```

### Project Structure

```
AllSkyCameraPlugin/
├── Equipment/
│   └── AllSkyCameraSafetyMonitor.cs    # NINA safety monitor integration
├── Services/
│   ├── RtspCaptureService.cs           # RTSP stream capture
│   ├── IWeatherAnalysisService.cs      # Analysis interface
│   ├── LocalWeatherAnalysisService.cs  # Local AI implementation
│   └── AzureWeatherAnalysisService.cs  # Azure AI implementation
├── Models/
│   └── WeatherAnalysisResult.cs        # Weather data model
├── AllSkyCameraPlugin.cs               # Main plugin class
├── AllSkyCameraOptions.cs              # Configuration options
└── AllSkyCameraOptionsView.xaml        # Settings UI
```

## Dependencies

- **NINA SDK**: Safety monitor and plugin infrastructure
- **Emgu.CV**: OpenCV wrapper for video capture and image processing
- **Microsoft.ML**: Machine learning and image analytics
- **OpenAI SDK**: Access to GitHub Models (Claude, GPT-4, Gemini)

## Contributing

Contributions are welcome! Please:
1. Fork the repository
2. Create a feature branch
3. Make your changes
4. Submit a pull request

## License

This project is licensed under the MIT License - see the LICENSE file for details.

## Support

- **Issues**: Report bugs on [GitHub Issues](../../issues)
- **Discussion**: Join the [NINA Discord](https://discord.gg/nighttime-imaging)
- **Documentation**: [NINA Plugin Development](https://nighttime-imaging.eu/)

## Acknowledgments

- NINA team for the excellent imaging platform
- OpenCV and Emgu.CV communities
- Microsoft Azure AI Vision team

## Roadmap

- [ ] Support for local all-sky cameras (USB/DirectShow)
- [ ] Historical weather data logging and graphs
- [ ] Advanced ML models trained on astronomy-specific sky conditions
- [ ] Integration with online weather services for correlation
- [ ] Mobile notifications for weather changes
- [ ] Support for multiple cameras with voting logic

---

**⭐ If you find this plugin useful, please star the repository!**
