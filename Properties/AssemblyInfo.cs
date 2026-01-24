using System.Reflection;
using System.Runtime.InteropServices;

// General Information about an assembly is controlled through the following
// set of attributes. Change these attribute values to modify the information
// associated with an assembly.
[assembly: AssemblyTitle("AI Weather")]
[assembly: AssemblyDescription("AI-powered all-sky camera weather monitoring")]
[assembly: AssemblyConfiguration("")]
[assembly: AssemblyCompany("michelebergo")]
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
[assembly: AssemblyMetadata("Author", "michelebergo")]
[assembly: AssemblyMetadata("Repository", "https://github.com/michelebergo/nina.plugin.aiweather")]

// Version information
[assembly: AssemblyVersion("1.0.0.0")]
[assembly: AssemblyFileVersion("1.0.0.0")]

// The license your plugin code is using
[assembly: AssemblyMetadata("License", "MIT")]

// The url to the license
[assembly: AssemblyMetadata("LicenseURL", "https://opensource.org/licenses/MIT")]

// Common tags that quickly describe your plugin
[assembly: AssemblyMetadata("Tags", "Weather,Safety Monitor,All Sky Camera,AI,RTSP")]

// Optional metadata used by N.I.N.A.'s plugin manager UI
[assembly: AssemblyMetadata("Homepage", "")]
[assembly: AssemblyMetadata("ChangelogURL", "")]

// Featured logo displayed next to the plugin in the plugin list
// This points to an embedded WPF resource.
[assembly: AssemblyMetadata("FeaturedImageURL", "pack://application:,,,/NINA.Plugin.AIWeather;component/ai_weather_logo.png")]

// Optional screenshots (leave empty if not available)
[assembly: AssemblyMetadata("ScreenshotURL", "")]
[assembly: AssemblyMetadata("AltScreenshotURL", "")]

// Long description displayed in the plugin manager
[assembly: AssemblyMetadata("LongDescription", @"All Sky Camera Weather Monitor

Monitor weather conditions using an all-sky camera with AI-powered analysis:

Features:
- RTSP stream capture from all-sky cameras
- AI-powered weather analysis using GitHub Models (Claude, GPT-4, Gemini)
- Offline fallback using local image processing
- Automatic imaging pause/resume based on weather conditions
- Live preview with real-time analysis
- Configurable cloud coverage thresholds
- Periodic monitoring at customizable intervals

The plugin integrates with NINA's safety monitor system to automatically protect your equipment
from adverse weather conditions by analyzing all-sky camera images in real-time.

Supports:
- GitHub Models API (Claude 3.5 Sonnet, GPT-4o, GPT-4o Mini, Gemini 1.5 Flash/Pro)
- Local image analysis as offline fallback
- RTSP camera streams
- Configurable monitoring intervals (1-60 minutes)
")]
