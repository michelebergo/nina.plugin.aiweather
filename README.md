# AI Weather - All Sky Camera Monitor for NINA

A plugin for [N.I.N.A.](https://nighttime-imaging.eu/) (Nighttime Imaging 'N' Astronomy) that uses artificial intelligence to analyze images from all-sky cameras and determine real-time weather conditions. It integrates directly with NINA's safety monitoring system to automatically protect your astronomy equipment when conditions become unsafe.

## Why This Plugin?

Unattended imaging sessions are at risk from sudden weather changes. Traditional cloud sensors measure a narrow slice of sky and may miss approaching clouds or fog. An all-sky camera sees the entire sky dome, and AI can interpret that image in ways that go beyond simple brightness thresholds: it distinguishes between thin cirrus and dense overcast, detects rain droplets on the lens, and identifies fog. AI Weather bridges the gap between a camera image and an actionable safety decision, letting NINA pause or abort a sequence before your equipment is damaged.

## Features

### AI-Powered Sky Analysis

The plugin sends captured sky images to a vision-capable AI model that evaluates:

- **Cloud coverage** as a percentage (0-100%)
- **Weather condition** classification (Clear, Partly Cloudy, Mostly Cloudy, Overcast, Rainy, Foggy)
- **Rain detection** including water droplets on the lens, streaks, and condensation
- **Fog detection** based on uniform haze and low contrast
- **Confidence score** indicating how certain the AI is about its assessment
- **Natural language description** explaining what the AI sees in the image

### Multiple Capture Modes

Choose how the plugin acquires sky images based on your camera setup:

| Mode | Best For | How It Works |
|------|----------|--------------|
| **RTSP Stream** | Network IP cameras | Connects to a live RTSP video stream with real-time preview. Uses OpenCV with an automatic LibVLC fallback for maximum camera compatibility. |
| **HTTP Image** | Remote cameras, INDI devices | Periodically downloads a single image from an HTTP/HTTPS URL. Supports Basic authentication. Lower resource usage than continuous streaming. |
| **Folder Watch** | Any camera software | Monitors a local folder for the latest image file (.jpg, .png, .bmp, .tif). Compatible with AllSky, SharpCap, UFOCapture, ASI Studio, and any software that saves images to disk. |

### Multiple AI Providers

| Provider | Models | Requirements |
|----------|--------|--------------|
| **Local** | Built-in heuristic analysis | None (works offline) |
| **GitHub Models** | GPT-4o, GPT-4o Mini, Claude 3.5 Sonnet, Gemini 1.5 Flash/Pro | GitHub Personal Access Token (free) |
| **OpenAI** | GPT-4o, GPT-4o Mini | API key |
| **Google Gemini** | Gemini 1.5 Flash, 1.5 Pro, 2.0 Flash | API key |
| **Anthropic Claude** | Claude 3.5 Sonnet, Claude 3.5 Haiku, Claude 3 Opus | API key |

If a cloud AI provider fails or times out (60-second limit), the plugin automatically falls back to local analysis so that safety monitoring is never interrupted.

### Safety Monitor Integration

The plugin registers as a NINA Safety Monitor device. When connected:

- It periodically captures and analyzes sky images at a configurable interval (1-60 minutes).
- It reports **Safe** or **Unsafe** to NINA based on cloud coverage threshold, rain, and fog.
- NINA's sequencer can automatically pause or abort imaging when the status changes to Unsafe.
- An optional status file can be written for integration with ASCOM Generic File SafetyMonitor or external automation tools.

### Live Preview Panel

The preview panel in NINA shows:

- Live video (RTSP) or the latest captured image (HTTP/Folder mode)
- Safety status with color-coded indicator
- Weather condition, cloud coverage percentage, and confidence score
- Rain and fog detection flags
- Full AI description of sky conditions
- Real-time activity log of captures, analyses, and events
- Controls to force an immediate refresh or save the current image

## Installation

1. Download the latest release from the [Releases](https://github.com/michelebergo/nina.plugin.aiweather/releases) page.
2. Extract the plugin files to your NINA plugins folder:
   ```
   %LOCALAPPDATA%\NINA\Plugins\
   ```
3. Restart NINA.
4. Go to **Options > Plugins** to configure AI Weather.

## Configuration

### 1. Select a Capture Mode

In the plugin options, choose the capture mode that matches your camera:

- **RTSP Stream**: Enter the stream URL (e.g. `rtsp://192.168.1.100:554/stream`) and optional credentials.
- **HTTP Image**: Enter the image URL and optional credentials.
- **Folder Watch**: Browse to the folder where your camera software saves images.

### 2. Choose an AI Provider

- **Local** requires no setup and works offline. It uses image processing heuristics (brightness, color distribution, pattern detection) to estimate cloud coverage.
- **GitHub Models** is recommended for getting started: create a [GitHub Personal Access Token](https://github.com/settings/tokens) and paste it into the settings. This gives free access to multiple vision models.
- **OpenAI**, **Gemini**, and **Anthropic** require their respective API keys from each provider's developer portal.

### 3. Set Monitoring Parameters

- **Check Interval** (minutes): How often the plugin captures and analyzes an image. 5-10 minutes is recommended for active monitoring.
- **Cloud Coverage Threshold** (%): The maximum cloud coverage considered safe for imaging. Default is 70%. Lower values are more conservative.

### 4. Optional: ASCOM and Status File

- Enable **ASCOM SafetyMonitor** integration to query an additional hardware safety monitor alongside AI analysis.
- Enable **Write Safety Status File** to output the current status to a text file, useful for external scripts or the ASCOM Generic File SafetyMonitor driver.

## Usage

### Connecting the Safety Monitor

1. In NINA, go to **Equipment > Safety Monitor**.
2. Select **All Sky Camera Safety Monitor**.
3. Click **Connect**.

The plugin will begin periodic monitoring. The safety status is reported to NINA and any running sequence will respond according to its safety instructions.

### Using the Preview Panel

Navigate to the AI Weather preview panel to see:

- The current sky image or live video stream
- Analysis results updated after each check cycle
- An activity log showing connection events, captures, and AI responses

Use the **Refresh** button to trigger an immediate capture and analysis outside the regular interval.

### Weather Conditions Reference

| Condition | Cloud Coverage | Rain/Fog | Safe? |
|-----------|---------------|----------|-------|
| Clear | < 15% | No | Yes |
| Partly Cloudy | 15-50% | No | Yes |
| Mostly Cloudy | 50-85% | No | Depends on threshold |
| Overcast | > 85% | No | No |
| Rainy | Any | Rain detected | No |
| Foggy | Any | Fog detected | No |

Rain and fog always trigger an Unsafe status regardless of the cloud coverage threshold.

## Building from Source

**Requirements:**
- Visual Studio 2022 or later
- .NET 8.0 SDK
- NINA 3.x installed (for assembly references)

```
git clone https://github.com/michelebergo/nina.plugin.aiweather.git
cd nina.plugin.aiweather
dotnet restore
dotnet build
```

## License

MIT License. See [LICENSE](LICENSE) for details.

## Contributing

Contributions are welcome. Please fork the repository, create a feature branch, and submit a pull request.

## Support

- **Issues**: [GitHub Issues](https://github.com/michelebergo/nina.plugin.aiweather/issues)
- **NINA Community**: [NINA Discord](https://discord.gg/nighttime-imaging)
