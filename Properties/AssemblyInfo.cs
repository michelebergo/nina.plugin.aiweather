using System.Reflection;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;

[assembly: InternalsVisibleTo("AIWeather.DatasetSmoke")]
[assembly: InternalsVisibleTo("NINA.Plugin.AIWeather.Test")]

// General Information about an assembly is controlled through the following
// set of attributes. Change these attribute values to modify the information
// associated with an assembly.
[assembly: AssemblyTitle("AI Weather")]
[assembly: AssemblyDescription("AI-powered all-sky camera weather monitoring with bundled site-trained ONNX inference")]
[assembly: AssemblyConfiguration("")]
[assembly: AssemblyCompany("Michele Bergo / sedirk")]
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
[assembly: AssemblyMetadata("Author", "Michele Bergo / sedirk")]
[assembly: AssemblyMetadata("Repository", "https://github.com/sedirk/nina.plugin.aiweather-advanced")]

// Version information
[assembly: AssemblyVersion("1.35.0.0")]
[assembly: AssemblyFileVersion("1.35.0.0")]
[assembly: AssemblyInformationalVersion("1.35.0.0")]

// The license your plugin code is using
[assembly: AssemblyMetadata("License", "MIT")]

// The url to the license
[assembly: AssemblyMetadata("LicenseURL", "https://opensource.org/licenses/MIT")]

// Common tags that quickly describe your plugin
[assembly: AssemblyMetadata("Tags", "Weather,Safety Monitor,All Sky Camera,AI,RTSP")]

// Optional metadata used by N.I.N.A.'s plugin manager UI
[assembly: AssemblyMetadata("Homepage", "https://github.com/sedirk/nina.plugin.aiweather-advanced")]
[assembly: AssemblyMetadata("ChangelogURL", "https://github.com/sedirk/nina.plugin.aiweather-advanced/releases")]

// Featured logo displayed next to the plugin in the plugin list
[assembly: AssemblyMetadata("FeaturedImageURL", "https://raw.githubusercontent.com/sedirk/nina.plugin.aiweather-advanced/advanced/icon.png")]

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

🤖 6 AI PROVIDER POLICIES (Free to Advanced):
• Local (FREE, Offline): Bundled site-trained MobileNetV3 ONNX model — CPU inference, no internet needed, no API costs
• Google Gemini: one operator-selected model and one request, with no automatic switching, retry, downgrade or backoff; intended for billed projects
• Google Gemini Free: reorderable Flash/Lite pool with per-model quota circuits and configurable complete cycles; intended for free-tier projects
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
• Fail-Safe Data Expiry: the sky input expires after the Maximum data age (automatic means three check intervals and never below 10 minutes). Default AND or no external device then reports Unsafe; explicit OR may rely on a readable safe external device without refreshing the old image timestamp. Disconnection, stopped monitoring and daylight suspension always report Unsafe
• External ASCOM Safety Monitor: choose a second safety device and how it combines with the sky verdict. AND (default) requires both inputs to be safe. Explicit OR permits either input to carry the verdict, except fresh rain, fog or unreadable sky remains Unsafe. Unreachable devices count as unsafe. A following LAN replica cannot use OR to bypass an unsafe or unreachable primary; external device settings stay local to each computer
• It says WHY: the panel names the reason for the current state - a cloudy sky, data too old with the age and the limit, or an unreachable external device - instead of leaving it in the log

🧠 IT LEARNS YOUR SITE:
• Camera view and site darkness: tell it whether the camera is an all-sky fisheye or a fixed camera looking at part of the sky, and whether the site is light-polluted (clouds look bright) or dark (clouds are black holes in the star field). The prompt is composed from the answers, so a black sky at a dark site reads as overcast instead of clear. A frame with no readable sky at all is reported as Unknown and treated as unsafe, and noise specks are not counted as stars
• Site notes: a free-text field for what the AI cannot guess about your location, in your own words - ""clouds to the south are lit orange by the city and can look like overcast"", ""in daylight the sun reflects off the dome and creates a hazy halo"". Sent with every analysis, for every provider. It steers interpretation only: the thresholds, the safety rules and the response format stay with the built-in prompt
• Daily sky digest into the shared knowledge wiki: what the sky did is filed into the same local markdown wiki the AI Assistant plugin reads, so a question about last night can be answered from what was actually observed
• Disable model thinking (Ollama, on by default): newer local models reason at length before answering, which took analyses from 14 to 78 seconds on the same image and past the timeout. Requests ask them not to

⚙️ EASY SETUP:
1. Configure your all-sky camera source (RTSP URL, HTTP URL, or watched folder path)
2. Choose an AI provider and enter your API key (or use Local for zero-config offline analysis)
3. Set both cloud coverage thresholds: High turns the state Unsafe, Low lets it return to Safe, and between them the state does not change
4. Connect the safety monitor under Equipment → Safety Monitor → All Sky Camera Safety Monitor
5. Start monitoring — the plugin automatically protects your sequences

💡 BEGINNER-FRIENDLY:
• Start with Local (offline) mode — no API keys needed, works out of the box
• Use Google Gemini Free for a best-effort free-project pool, strict Google Gemini for one billed-project model, or a local Ollama vision model
• Detailed activity log shows every analysis result for easy troubleshooting
• Works with any all-sky camera that provides RTSP, HTTP, or file-based output

⚡ PRO FEATURES:
• Custom Analysis Intervals: Configure how often the sky is analyzed (seconds between captures)
• Multiple Camera Support: Point to different camera sources as needed
• Robust Error Handling: Automatic recovery from network failures, API errors, and camera disconnects
• Detailed Logging: Every analysis result is logged with timestamp, provider, cloud percentage, and safety status
• Seamless NINA Integration: Works directly with NINA's safety monitor system to pause or abort sequences when conditions deteriorate

Transform your all-sky camera into an intelligent weather guardian. Focus on imaging while AI Weather keeps watch over your equipment and data.")]
