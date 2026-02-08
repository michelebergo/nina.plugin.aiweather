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
[assembly: AssemblyMetadata("Identifier", "A1B2C3D4-E5F6-4A5B-8C9D-0E1F2A3B4C5D")]
[assembly: AssemblyMetadata("Author", "Michele Bergo")]
[assembly: AssemblyMetadata("Repository", "https://github.com/michelebergo/nina.plugin.aiweather")]

// Version information
[assembly: AssemblyVersion("1.1.0.0")]
[assembly: AssemblyFileVersion("1.1.0.0")]

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
// This points to an embedded WPF resource.
[assembly: AssemblyMetadata("FeaturedImageURL", "pack://application:,,,/NINA.Plugin.AIWeather;component/ai_weather_logo.png")]

// Optional screenshots (leave empty if not available)
[assembly: AssemblyMetadata("ScreenshotURL", "")]
[assembly: AssemblyMetadata("AltScreenshotURL", "")]

// Long description displayed in the plugin manager
[assembly: AssemblyMetadata("LongDescription", @"AI Weather analyzes all-sky camera images using artificial intelligence to determine real-time weather conditions and protect your equipment during unattended imaging sessions.

How It Works:
The plugin periodically captures an image from your all-sky camera, sends it to a vision-capable AI model, and receives a structured weather assessment including cloud coverage percentage, weather condition, and rain/fog detection. Based on configurable thresholds, it reports a Safe or Unsafe status to NINA's safety monitor, which can automatically pause or abort an imaging sequence.

Capture Modes:
- RTSP Stream: live video from network IP cameras with real-time preview
- HTTP Image: periodic download from a URL (indi-allsky, AllSky, web cameras)
- Folder Watch: monitors a local folder for the latest image saved by any camera software

AI Providers:
- Local: offline heuristic analysis using brightness, color, and pattern detection
- GitHub Models: free access to GPT-4o, Claude, Gemini via GitHub token
- OpenAI: GPT-4o and GPT-4o Mini
- Google Gemini: Gemini 1.5 Flash, 1.5 Pro, 2.0 Flash
- Anthropic Claude: Claude 3.5 Sonnet, 3.5 Haiku, 3 Opus

Safety Features:
- Detects cloud coverage, rain (including lens droplets), and fog
- Rain and fog trigger Unsafe regardless of cloud threshold
- Automatic fallback to local analysis if the AI provider fails or times out
- Optional status file output for ASCOM Generic File SafetyMonitor integration

Connect the safety monitor under Equipment > Safety Monitor > All Sky Camera Safety Monitor to start protecting your imaging sessions.
")]
