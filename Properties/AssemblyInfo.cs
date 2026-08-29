using System.Reflection;
using System.Runtime.InteropServices;

// General Information about an assembly is controlled through the following
// set of attributes. Change these attribute values to modify the information
// associated with an assembly.
[assembly: AssemblyTitle("AI Weather")]
[assembly: AssemblyDescription("AI-powered all-sky camera weather monitoring with automatic safety protection")]
[assembly: AssemblyConfiguration("")]
[assembly: AssemblyCompany("Michele Bergo")]
[assembly: AssemblyProduct("NINA.Plugins")]
[assembly: AssemblyCopyright("Copyright © 2026")]
[assembly: AssemblyTrademark("")]
[assembly: AssemblyCulture("")]

// Setting ComVisible to false makes the types in this assembly not visible
// to COM components.  If you need to access a type in this assembly from
// COM, set the ComVisible attribute to true on that type.
[assembly: ComVisible(false)]

// Plugin identifier used by N.I.N.A. plugin manager manifests (CreateManifest.ps1 reads GuidAttribute)
[assembly: Guid("A1B2C3D4-E5F6-4A5B-8C9D-0E1F2A3B4C5D")]

// The minimum Version of N.I.N.A. that this plugin is compatible with
[assembly: AssemblyMetadata("MinimumApplicationVersion", "3.0.0.0")]

// Required by the N.I.N.A. community plugin manifest repository
[assembly: AssemblyMetadata("Identifier", "AI Weather")]
[assembly: AssemblyMetadata("Author", "Michele Bergo")]
[assembly: AssemblyMetadata("Repository", "https://github.com/michelebergo/nina.plugin.aiweather")]

// Version information
[assembly: AssemblyVersion("1.15.2.0")]
[assembly: AssemblyFileVersion("1.15.2.0")]

// The license your plugin code is using
[assembly: AssemblyMetadata("License", "MIT")]

// The url to the license
[assembly: AssemblyMetadata("LicenseURL", "https://opensource.org/licenses/MIT")]

// Common tags that quickly describe your plugin
[assembly: AssemblyMetadata("Tags", "Weather,Safety Monitor,All Sky Camera,AI,RTSP")]

// Optional metadata used by N.I.N.A.'s plugin manager UI
[assembly: AssemblyMetadata("Homepage", "https://github.com/michelebergo/nina.plugin.aiweather")]
[assembly: AssemblyMetadata("ChangelogURL", "https://github.com/michelebergo/nina.plugin.aiweather/releases")]

// Featured logo displayed next to the plugin in the plugin list
[assembly: AssemblyMetadata("FeaturedImageURL", "https://raw.githubusercontent.com/michelebergo/nina.plugin.aiweather/main/icon.png")]

// Optional screenshots (leave empty if not available)
[assembly: AssemblyMetadata("ScreenshotURL", "")]
[assembly: AssemblyMetadata("AltScreenshotURL", "")]

// Short description shown in plugin list
[assembly: AssemblyMetadata("ShortDescription", "AI-powered all-sky camera weather monitoring with automatic safety protection for unattended imaging sessions")]

// Long description displayed in the plugin manager
[assembly: AssemblyMetadata("LongDescription", @"Protect your equipment and imaging sessions with intelligent, real-time weather monitoring powered by AI vision models. AI Weather watches the sky so you don't have to.

🌩️ REAL-TIME WEATHER MONITORING:
• Automatic Sky Analysis: AI vision models analyze your all-sky camera images to determine cloud coverage, detect rain and fog
• Continuous Protection: Periodic image capture and analysis runs in the background during your entire imaging session
• Instant Alerts: Weather status updates in real-time with detailed condition reports and cloud coverage percentages
• Live Preview: See exactly what the AI sees with the built-in camera preview panel

📷 3 FLEXIBLE CAPTURE MODES:
• RTSP Stream: Live video from network IP cameras (Dahua, Hikvision, etc.) with real-time preview and snapshot extraction
• HTTP Image Download: Periodic image download from any URL — works with indi-allsky, AllSky, web cameras, and any HTTP-accessible image
• Folder Watch: Monitors a local directory for the latest image saved by any camera software — perfect for USB cameras or custom setups

🤖 5 AI PROVIDERS (Free to Advanced):
• Local (FREE, Offline): Built-in heuristic analysis using brightness, color distribution, and edge detection — no internet needed, no API costs
• Google Gemini (free API tier): fast and capable Flash/Pro vision models
• Ollama / Custom (FREE, Local): Any vision model on a local OpenAI-compatible server (Ollama, LM Studio, llama.cpp, LocalAI) — no API key, full privacy
• OpenAI: GPT-4o and GPT-4o Mini for high-accuracy cloud and weather analysis
• Anthropic Claude: excellent at detailed image understanding
(GitHub Models was retired by GitHub on July 30, 2026 and no longer works.)

🛡️ SAFETY FEATURES:
• Two Cloud Thresholds: a high one that turns the state Unsafe and a low one that lets it return to Safe, so a sky hovering around a single number does not flip the sequence back and forth
• Rain Detection: Rain (including lens droplets) immediately triggers Unsafe — regardless of cloud threshold
• Fog Detection: Fog conditions immediately trigger Unsafe — protects optics and prevents wasted exposures
• Automatic Fallback: If the cloud AI provider fails, times out, or loses connectivity, the plugin falls back to local offline analysis
• 60-Second Timeout: All AI providers have a 60-second timeout to prevent indefinite hangs during analysis
• ASCOM SafetyMonitor Integration: Outputs a status file compatible with the ASCOM Generic File SafetyMonitor for third-party software integration
• Fail-Safe Data Expiry: the verdict expires. If no analysis succeeds within the Maximum data age (configurable; automatic means three check intervals and never below 10 minutes), the monitor reports Unsafe instead of holding the last known state - a camera that dies while the sky is clear no longer reports Safe indefinitely. Disconnecting clears the verdict too, so a reconnect starts Unsafe until the first successful analysis
• External ASCOM Safety Monitor: pick a second safety device with the ASCOM chooser and its verdict is combined with AND - imaging is Safe only when both the sky analysis and that device say so. Pair all-sky cloud, rain and fog detection with a rain sensor or a humidity and dew-point monitor. Any failure to reach it reports Unsafe
• It says WHY: the panel names the reason for the current state - a cloudy sky, data too old with the age and the limit, or an unreachable external device - instead of leaving it in the log

🧠 IT LEARNS YOUR SITE:
• Site notes: a free-text field for what the AI cannot guess about your location, in your own words - ""clouds to the south are lit orange by the city and can look like overcast"", ""in daylight the sun reflects off the dome and creates a hazy halo"". Sent with every analysis, for every provider. It steers interpretation only: the thresholds, the safety rules and the response format stay with the built-in prompt
• Daily sky digest into the shared knowledge wiki: what the sky did is filed into the same local markdown wiki the AI Assistant plugin reads, so a question about last night can be answered from what was actually observed
• Disable model thinking (Ollama, on by default): newer local models reason at length before answering, which took analyses from 14 to 78 seconds on the same image and past the timeout. Requests ask them not to

⚙️ EASY SETUP:
1. Configure your all-sky camera source (RTSP URL, HTTP URL, or watched folder path)
2. Choose an AI provider and enter your API key (or use Local for zero-config offline analysis)
3. Set your cloud coverage safety threshold
4. Connect the safety monitor under Equipment → Safety Monitor → All Sky Camera Safety Monitor
5. Start monitoring — the plugin automatically protects your sequences

💡 BEGINNER-FRIENDLY:
• Start with Local (offline) mode — no API keys needed, works out of the box
• Upgrade to Google Gemini (free API tier) or a local Ollama vision model for AI-powered analysis
• Detailed activity log shows every analysis result for easy troubleshooting
• Works with any all-sky camera that provides RTSP, HTTP, or file-based output

⚡ PRO FEATURES:
• Custom Analysis Intervals: Configure how often the sky is analyzed (seconds between captures)
• Multiple Camera Support: Point to different camera sources as needed
• Robust Error Handling: Automatic recovery from network failures, API errors, and camera disconnects
• Detailed Logging: Every analysis result is logged with timestamp, provider, cloud percentage, and safety status
• Seamless NINA Integration: Works directly with NINA's safety monitor system to pause or abort sequences when conditions deteriorate

Transform your all-sky camera into an intelligent weather guardian. Focus on imaging while AI Weather keeps watch over your equipment and data.")]
