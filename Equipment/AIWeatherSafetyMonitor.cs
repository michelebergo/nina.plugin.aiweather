using NINA.Equipment.Interfaces;
using NINA.Core.Utility;
using NINA.Profile.Interfaces;
using NINA.Image.Interfaces;
using System;
using System.Collections.Generic;
using System.ComponentModel.Composition;
using System.Drawing;
using System.IO;
using System.Threading;
using System.Threading.Tasks;
using AIWeather.Models;
using AIWeather.Services;

namespace AIWeather.Equipment
{
    /// <summary>
    /// All Sky Camera Weather Monitor
    /// Monitors weather conditions and writes status to file
    /// </summary>
    [System.Runtime.Versioning.SupportedOSPlatform("windows")]
    public class AIWeatherSafetyMonitor : BaseINPC, ISafetyMonitor
    {
        private static AIWeatherSafetyMonitor? _instance;
        public static AIWeatherSafetyMonitor Instance => _instance ??= new AIWeatherSafetyMonitor();

        private readonly UnifiedCaptureService _captureService;
        private IWeatherAnalysisService _analysisService;
        private Timer? _monitoringTimer;
        private WeatherAnalysisResult? _lastResult;
        private Bitmap? _lastImage;
        private bool _isMonitoring = false;
        private bool _isCurrentlySafe = false;
        private CancellationTokenSource? _cts;
        private readonly SemaphoreSlim _checkGate = new SemaphoreSlim(1, 1);
        private IProfileService? _profileService;

        // When the last analysis actually succeeded. The sky verdict expires: a monitor that
        // keeps answering with the last known state after its camera died reports SAFE all
        // night on data from before the failure, which is the one thing a safety monitor
        // must never do.
        private DateTime _lastAnalysisUtc = DateTime.MinValue;
        private bool _staleLogged;

        // Optional external ASCOM safety monitor, ANDed with the sky verdict so the two
        // protections are independent: this plugin watches the sky, the external device
        // watches whatever it was built to watch (humidity, dew point, rain sensor).
        private readonly AscomSafetyMonitorClient _externalMonitor = new AscomSafetyMonitorClient();
        private readonly object _externalGate = new object();
        private bool _externalSafeCached;
        private DateTime _externalReadUtc = DateTime.MinValue;
        private DateTime _externalConnectAttemptUtc = DateTime.MinValue;
        private bool _externalFailureLogged;

        /// <summary>IsSafe is polled often; a COM read per poll would hammer the driver.</summary>
        private static readonly TimeSpan ExternalReadCacheDuration = TimeSpan.FromSeconds(5);

        /// <summary>How long to wait before retrying a driver that failed to connect or read.</summary>
        private static readonly TimeSpan ExternalReconnectInterval = TimeSpan.FromSeconds(30);

        /// <summary>Floor for the automatic data-age limit, whatever the check interval.</summary>
        private static readonly TimeSpan MinimumAutomaticDataAge = TimeSpan.FromMinutes(10);

        /// <summary>
        /// Fired after Connect succeeds and periodic monitoring has started.
        /// </summary>
        public event EventHandler? MonitoringStarted;

        public AIWeatherSafetyMonitor()
        {
            _captureService = new UnifiedCaptureService(cameraMediator: null);
            _analysisService = new LocalWeatherAnalysisService();
            
            // Subscribe to settings changes
            Properties.Settings.Default.PropertyChanged += (s, e) =>
            {
                if (e.PropertyName == nameof(Properties.Settings.Default.UseGitHubModels)
                    || e.PropertyName == nameof(Properties.Settings.Default.AnalysisProvider)
                    || e.PropertyName == nameof(Properties.Settings.Default.SelectedModel)
                    || e.PropertyName == nameof(Properties.Settings.Default.GitHubToken)
                    || e.PropertyName == nameof(Properties.Settings.Default.OpenAIKey)
                    || e.PropertyName == nameof(Properties.Settings.Default.GeminiKey)
                    || e.PropertyName == nameof(Properties.Settings.Default.AnthropicKey))
                {
                    UpdateAnalysisService();
                }
            };
        }

        /// <summary>
        /// Injects NINA's image data factory for proper FITS/TIFF loading with debayering and stretching.
        /// Called from the MEF-constructed provider.
        /// </summary>
        public void SetImageDataFactory(IImageDataFactory imageDataFactory)
        {
            _captureService.SetImageDataFactory(imageDataFactory);
        }

        /// <summary>
        /// Injects NINA's profile service for accessing observer location (lat/lon/elevation).
        /// Called from the MEF-constructed provider.
        /// </summary>
        public void SetProfileService(IProfileService profileService)
        {
            _profileService = profileService;
        }

        private void UpdateAnalysisService()
        {
            var provider = Properties.Settings.Default.AnalysisProvider;
            if (string.IsNullOrWhiteSpace(provider))
            {
                provider = Properties.Settings.Default.UseGitHubModels ? "GitHubModels" : "Local";
            }

            provider = provider.Trim();
            var model = Properties.Settings.Default.SelectedModel;

            if (string.Equals(provider, "GitHubModels", StringComparison.OrdinalIgnoreCase))
            {
                _analysisService = new GitHubModelsAnalysisService(
                    Properties.Settings.Default.GitHubToken,
                    model);
                return;
            }

            if (string.Equals(provider, "OpenAI", StringComparison.OrdinalIgnoreCase))
            {
                _analysisService = new OpenAIAnalysisService(
                    Properties.Settings.Default.OpenAIKey,
                    model);
                return;
            }

            if (string.Equals(provider, "Gemini", StringComparison.OrdinalIgnoreCase))
            {
                _analysisService = new GeminiAnalysisService(
                    Properties.Settings.Default.GeminiKey,
                    model);
                return;
            }

            if (string.Equals(provider, "Anthropic", StringComparison.OrdinalIgnoreCase))
            {
                _analysisService = new AnthropicAnalysisService(
                    Properties.Settings.Default.AnthropicKey,
                    model);
                return;
            }

            if (string.Equals(provider, "Ollama", StringComparison.OrdinalIgnoreCase))
            {
                _analysisService = new OllamaAnalysisService(
                    Properties.Settings.Default.OllamaBaseUrl,
                    model,
                    Properties.Settings.Default.OllamaDisableThinking);
                return;
            }

            _analysisService = new LocalWeatherAnalysisService();
        }

        #region ISafetyMonitor Implementation

        public string Category => "All Sky Camera";
        public bool HasSetupDialog => true;
        public string Id => "AIWeatherSafetyMonitor";
        public string Name => "All Sky Camera Safety Monitor";
        public string Description => "Monitors all-sky camera for weather conditions and provides safety status for imaging";
        public string DriverInfo => "All Sky Camera Plugin v1.0";
        public string DriverVersion => "1.0.0";

        private bool _connected = false;
        public bool Connected
        {
            get => _connected;
            private set
            {
                _connected = value;
                RaisePropertyChanged();
            }
        }

        public async Task<bool> Connect(CancellationToken token)
        {
            try
            {
                Logger.Info("Connecting to All Sky Camera Safety Monitor");

                // Get capture mode from settings
                var captureMode = (CaptureMode)Properties.Settings.Default.CaptureMode;
                _captureService.CurrentMode = captureMode;
                Logger.Info($"Safety Monitor - Capture Mode: {captureMode}");

                bool success = false;

                if (captureMode == CaptureMode.RTSPStream)
                {
                    // RTSP mode
                    var rtspUrl = Properties.Settings.Default.RtspUrl;
                    var username = Properties.Settings.Default.RtspUsername;
                    var password = Properties.Settings.Default.RtspPassword;

                    Logger.Info($"Safety Monitor - RTSP URL: '{rtspUrl}'");
                    _captureService.ConfigureRTSP(rtspUrl ?? "", username, password);
                    success = !string.IsNullOrWhiteSpace(rtspUrl);
                }
                else if (captureMode == CaptureMode.INDICamera)
                {
                    // HTTP Image Download mode
                    var imageUrl = Properties.Settings.Default.INDIDeviceName;
                    var username = Properties.Settings.Default.RtspUsername;
                    var password = Properties.Settings.Default.RtspPassword;
                    
                    Logger.Info($"Safety Monitor - HTTP Image URL: '{imageUrl}'");
                    _captureService.ConfigureINDI(imageUrl ?? "", username, password);
                    success = !string.IsNullOrWhiteSpace(imageUrl);
                }
                else if (captureMode == CaptureMode.FolderWatch)
                {
                    // Folder Watch mode
                    var folderPath = Properties.Settings.Default.FolderPath;
                    Logger.Info($"Safety Monitor - Folder Path: '{folderPath}'");
                    _captureService.ConfigureFolderWatch(folderPath ?? "");
                    success = !string.IsNullOrWhiteSpace(folderPath) && Directory.Exists(folderPath);
                }

                if (!success)
                {
                    Logger.Error($"Safety Monitor - Failed to configure {captureMode} mode");
                    return false;
                }

                // Initialize analysis service
                UpdateAnalysisService();
                var analysisReady = await _analysisService.InitializeAsync(token);
                if (!analysisReady)
                {
                    Logger.Warning("Selected analysis provider failed to initialize; falling back to local analysis");
                    _analysisService = new LocalWeatherAnalysisService();
                    await _analysisService.InitializeAsync(token);
                }

                // A fresh connection starts with no verdict at all: unsafe until the first
                // analysis succeeds, never inheriting the state of a previous session.
                _lastAnalysisUtc = DateTime.MinValue;
                _isCurrentlySafe = false;
                _staleLogged = false;

                // Best-effort: the lazy path in IsExternalMonitorSafe retries on its own
                // schedule, but connecting here surfaces a wrong ProgID in the log at once.
                if (Properties.Settings.Default.UseAscomSafetyMonitor)
                {
                    var progId = Properties.Settings.Default.AscomSafetyMonitorProgId;
                    lock (_externalGate)
                    {
                        _externalConnectAttemptUtc = DateTime.UtcNow;
                        _externalSafeCached = _externalMonitor.TryConnect(progId ?? string.Empty)
                                              && _externalMonitor.TryGetIsSafe(out var s) && s;
                        _externalReadUtc = DateTime.UtcNow;
                    }
                    Logger.Info($"External ASCOM safety monitor enabled ('{progId}'): " +
                                $"{(_externalMonitor.Connected ? "connected" : "NOT connected - the monitor will report UNSAFE until it is")}");
                }

                // Mark as connected BEFORE starting periodic monitoring
                // so that UI handlers can see Connected=true when the first check completes
                Connected = true;
                Logger.Info($"All Sky Camera Safety Monitor connected using {captureMode} mode");

                // Start periodic monitoring (first check runs immediately)
                StartPeriodicMonitoring();

                MonitoringStarted?.Invoke(this, EventArgs.Empty);
                return true;
            }
            catch (Exception ex)
            {
                Logger.Error($"Error connecting to safety monitor: {ex.Message}", ex);
                return false;
            }
        }

        public void Disconnect()
        {
            try
            {
                Logger.Info("Disconnecting All Sky Camera Safety Monitor");

                StopPeriodicMonitoring();
                _captureService.Dispose();

                // Values are no longer being refreshed: blank the sequencer symbols so an
                // expression cannot keep acting on a stale reading.
                SequencerSymbolPublisher.ClearValues();

                // Drop the verdict with the connection, so a reconnect cannot start from a
                // SAFE inherited from before the disconnect.
                _lastAnalysisUtc = DateTime.MinValue;
                _isCurrentlySafe = false;

                lock (_externalGate)
                {
                    _externalMonitor.Disconnect();
                    _externalSafeCached = false;
                    _externalReadUtc = DateTime.MinValue;
                }

                Connected = false;
                Logger.Info("All Sky Camera Safety Monitor disconnected");
            }
            catch (Exception ex)
            {
                Logger.Error($"Error disconnecting: {ex.Message}", ex);
            }
        }

        /// <summary>
        /// The state NINA acts on. Three independent conditions, all of which must hold:
        /// the sky verdict from the latest analysis, that verdict still being recent enough
        /// to describe the current sky, and the external ASCOM safety monitor (when one is
        /// configured). Anything unknown counts as unsafe — a missing answer is not a
        /// permission to keep imaging.
        /// </summary>
        public bool IsSafe => _isCurrentlySafe && IsAnalysisFresh() && IsExternalMonitorSafe();

        /// <summary>The sky verdict alone, without freshness or the external monitor.</summary>
        public bool IsSkyConditionSafe => _isCurrentlySafe;

        /// <summary>
        /// Why the monitor is reporting what it reports, in one line for the panel. Until
        /// now this only existed in the log, which meant a user seeing UNSAFE on a clear
        /// night had no way to tell a cloudy verdict from a dead camera or an unreachable
        /// external device. Conditions are reported in the order they are evaluated, so the
        /// first thing that is actually wrong is the thing shown.
        /// </summary>
        public string SafetyStateReason
        {
            get
            {
                if (!Connected)
                {
                    return "Not connected";
                }

                if (_lastAnalysisUtc == DateTime.MinValue)
                {
                    return "Waiting for the first sky analysis";
                }

                if (!IsAnalysisFresh())
                {
                    var age = (DateTime.UtcNow - _lastAnalysisUtc).TotalMinutes;
                    return $"Stale data: last analysis {age:F0} min ago, limit {MaxDataAge().TotalMinutes:F0} min — check the camera source";
                }

                if (Properties.Settings.Default.UseAscomSafetyMonitor && !IsExternalMonitorSafe())
                {
                    return _externalMonitor.Connected
                        ? "External safety monitor reports unsafe"
                        : "External safety monitor cannot be connected or read";
                }

                if (!_isCurrentlySafe)
                {
                    var r = _lastResult;
                    if (r == null)
                    {
                        return "No usable analysis";
                    }
                    if (r.RainDetected)
                    {
                        return "Rain detected";
                    }
                    if (r.FogDetected)
                    {
                        return "Fog detected";
                    }
                    return $"Cloud coverage {r.CloudCoverage:F0}% (safe below {Properties.Settings.Default.CloudCoverageSafeThreshold}%)";
                }

                return "Sky clear and data current";
            }
        }

        /// <summary>
        /// Maximum age of the latest successful analysis. Configurable; 0 means automatic
        /// (three check intervals, never below ten minutes) so a long polling interval
        /// cannot make the state permanently stale by construction.
        /// </summary>
        private static TimeSpan MaxDataAge()
        {
            var configured = Properties.Settings.Default.MaxDataAgeMinutes;
            if (configured > 0)
            {
                return TimeSpan.FromMinutes(configured);
            }

            var interval = Math.Max(1, Properties.Settings.Default.CheckIntervalMinutes);
            var automatic = TimeSpan.FromMinutes(interval * 3);
            return automatic < MinimumAutomaticDataAge ? MinimumAutomaticDataAge : automatic;
        }

        /// <summary>
        /// Whether the latest analysis is recent enough to be acted upon. Logged once per
        /// transition rather than per call: NINA polls IsSafe continuously.
        /// </summary>
        private bool IsAnalysisFresh()
        {
            if (_lastAnalysisUtc == DateTime.MinValue)
            {
                return false; // nothing analysed yet since connecting
            }

            var age = DateTime.UtcNow - _lastAnalysisUtc;
            if (age <= MaxDataAge())
            {
                return true;
            }

            if (!_staleLogged)
            {
                _staleLogged = true;
                Logger.Warning($"Safety monitor reporting UNSAFE: no successful sky analysis for {age.TotalMinutes:F1} minutes " +
                               $"(limit {MaxDataAge().TotalMinutes:F0} min). Check the all-sky camera source.");
            }
            return false;
        }

        /// <summary>
        /// The external ASCOM safety monitor's verdict, or true when the feature is off.
        /// Every failure mode - not configured, driver missing, connection lost, read error -
        /// reports unsafe, because an external monitor that cannot be read is exactly the
        /// situation its user installed it to be protected from. Reads are cached briefly
        /// and reconnects are rate-limited so a polled property cannot hammer a COM driver.
        /// </summary>
        private bool IsExternalMonitorSafe()
        {
            if (!Properties.Settings.Default.UseAscomSafetyMonitor)
            {
                return true;
            }

            lock (_externalGate)
            {
                var now = DateTime.UtcNow;
                if (now - _externalReadUtc < ExternalReadCacheDuration)
                {
                    return _externalSafeCached;
                }
                _externalReadUtc = now;

                var progId = Properties.Settings.Default.AscomSafetyMonitorProgId;
                if (string.IsNullOrWhiteSpace(progId))
                {
                    _externalSafeCached = false;
                    LogExternalFailureOnce("the external ASCOM safety monitor is enabled but no driver is selected");
                    return false;
                }

                if (!_externalMonitor.Connected || !string.Equals(_externalMonitor.ProgId, progId, StringComparison.OrdinalIgnoreCase))
                {
                    if (now - _externalConnectAttemptUtc < ExternalReconnectInterval)
                    {
                        _externalSafeCached = false;
                        return false;
                    }

                    _externalConnectAttemptUtc = now;
                    if (!_externalMonitor.TryConnect(progId))
                    {
                        _externalSafeCached = false;
                        LogExternalFailureOnce($"cannot connect to the external ASCOM safety monitor '{progId}'");
                        return false;
                    }
                }

                if (!_externalMonitor.TryGetIsSafe(out var externalSafe))
                {
                    // The driver answered before and does not now: drop the connection so the
                    // next cycle rebuilds it instead of polling a dead object forever.
                    _externalMonitor.Disconnect();
                    _externalSafeCached = false;
                    LogExternalFailureOnce($"cannot read IsSafe from the external ASCOM safety monitor '{progId}'");
                    return false;
                }

                if (_externalFailureLogged)
                {
                    _externalFailureLogged = false;
                    Logger.Info($"External ASCOM safety monitor '{progId}' is readable again");
                }

                _externalSafeCached = externalSafe;
                return externalSafe;
            }
        }

        private void LogExternalFailureOnce(string message)
        {
            if (_externalFailureLogged)
            {
                return;
            }
            _externalFailureLogged = true;
            Logger.Warning($"Safety monitor reporting UNSAFE: {message}");
        }

        private void UpdateSafetyState(WeatherAnalysisResult result)
        {
            if (result == null)
            {
                _isCurrentlySafe = false;
                return;
            }

            var unsafeThreshold = Properties.Settings.Default.CloudCoverageThreshold;
            var safeThreshold = Properties.Settings.Default.CloudCoverageSafeThreshold;

            bool baseConditionsSafe = result.IsSafeForImaging && !result.RainDetected && !result.FogDetected;

            if (!baseConditionsSafe)
            {
                _isCurrentlySafe = false;
            }
            else
            {
                // Hysteresis logic
                if (_isCurrentlySafe)
                {
                    // Stay safe until coverage exceeds the high/unsafe threshold
                    if (result.CloudCoverage >= unsafeThreshold)
                    {
                        _isCurrentlySafe = false;
                    }
                }
                else
                {
                    // Stay unsafe until coverage drops below the low/safe threshold
                    if (result.CloudCoverage < safeThreshold)
                    {
                        _isCurrentlySafe = true;
                    }
                }
            }

            Logger.Debug($"Safety check: {(_isCurrentlySafe ? "SAFE" : "UNSAFE")} - " +
                       $"Cloud coverage: {result.CloudCoverage:F1}%, " +
                       $"Safe Threshold: {safeThreshold}%, Unsafe Threshold: {unsafeThreshold}%, " +
                       $"Rain: {result.RainDetected}, Fog: {result.FogDetected}, " +
                       $"Condition: {result.Condition}");
        }

        // IDevice methods required by interface
        public string Action(string actionName, string actionParameters)
        {
            return string.Empty;
        }

        public string SendCommandString(string command, bool raw = true)
        {
            return string.Empty;
        }

        public bool SendCommandBool(string command, bool raw = true)
        {
            return false;
        }

        public void SendCommandBlind(string command, bool raw = true)
        {
            // No-op
        }

        public string DisplayName
        {
            get => Name;
            set { }
        }

        public IList<string> SupportedActions => new List<string>();

        #endregion

        private void StartPeriodicMonitoring()
        {
            if (_isMonitoring) return;

            _cts = new CancellationTokenSource();
            _isMonitoring = true;

            var intervalMinutes = Properties.Settings.Default.CheckIntervalMinutes;
            var interval = TimeSpan.FromMinutes(intervalMinutes);

            var captureMode = (CaptureMode)Properties.Settings.Default.CaptureMode;
            Logger.Debug($"Starting periodic monitoring every {intervalMinutes} minutes (Mode: {captureMode})");

            _monitoringTimer = new Timer(_ =>
            {
                var currentMode = (CaptureMode)Properties.Settings.Default.CaptureMode;
                Logger.Debug($"Timer fired - Interval: {intervalMinutes} min, Mode: {currentMode}");
                
                if (_cts?.Token.IsCancellationRequested ?? true)
                {
                    Logger.Warning("Timer fired but cancellation was requested - skipping");
                    return;
                }

                try
                {
                    Logger.Debug($"Launching weather check task from timer (Mode: {currentMode})");
                    Task.Run(async () =>
                    {
                        try
                        {
                            Logger.Debug($"Executing periodic weather check (Mode: {currentMode})");
                            await PerformWeatherCheckAsync(_cts.Token);
                            Logger.Debug($"Weather check complete - next check in {intervalMinutes} min");
                        }
                        catch (Exception ex)
                        {
                            Logger.Error($"Error in periodic weather check: {ex.Message}", ex);
                        }
                    });
                }
                catch (Exception ex)
                {
                    Logger.Error($"Failed to start weather check task: {ex.Message}", ex);
                }
            }, null, TimeSpan.Zero, interval);
            
            Logger.Debug($"Timer created and started - first check will run immediately");
        }

        private void StopPeriodicMonitoring()
        {
            _isMonitoring = false;
            _cts?.Cancel();
            _monitoringTimer?.Dispose();
            _monitoringTimer = null;
            Logger.Info("Stopped periodic monitoring");
        }

        private async Task PerformWeatherCheckAsync(CancellationToken cancellationToken)
        {
            await _checkGate.WaitAsync(cancellationToken);
            try
            {
                var captureMode = (CaptureMode)Properties.Settings.Default.CaptureMode;
                Logger.Debug($"PerformWeatherCheckAsync - Mode: {captureMode}");

                Bitmap? frame = null;

                // Capture image from all modes
                Logger.Debug($"Capturing image from {captureMode} source");
                frame = await _captureService.CaptureImageAsync(cancellationToken);

                if (frame == null)
                {
                    // No new data. The state is deliberately NOT flipped here on a single
                    // miss - a dropped frame on an RTSP stream would make the monitor flap
                    // and abort sequences - but it is not silently kept either: the analysis
                    // ages, and IsSafe turns unsafe once it passes the maximum data age.
                    Logger.Warning($"Failed to capture image from {captureMode} source; " +
                                   $"last successful analysis is {LastAnalysisAgeDescription()} old");
                    RaisePropertyChanged(nameof(IsSafe));
                RaisePropertyChanged(nameof(SafetyStateReason));
                    return;
                }

                Logger.Debug($"Image captured from {captureMode}, size: {frame.Width}x{frame.Height}");

                // Analyze the frame
                Logger.Debug($"Starting AI analysis using {_analysisService.GetType().Name}");

                // Compute astronomical context from observer location
                AstroContext? astroContext = null;
                try
                {
                    if (_profileService != null)
                    {
                        var astro = _profileService.ActiveProfile.AstrometrySettings;
                        astroContext = AstroContext.Compute(
                            astro.Latitude, astro.Longitude, astro.Elevation, DateTime.UtcNow);
                        Logger.Info($"Astro context: Sun {astroContext.SunAltitude:F1}° ({astroContext.SunState}), " +
                                   $"Moon {astroContext.MoonIllumination:F0}% {astroContext.MoonPhase} at {astroContext.MoonAltitude:F1}°");
                    }
                }
                catch (Exception ex)
                {
                    Logger.Warning($"Failed to compute astronomical context: {ex.Message}");
                }

                var result = await _analysisService.AnalyzeImageAsync(frame, astroContext, cancellationToken);
                Logger.Debug("AI analysis completed");
                _lastResult = result;

                // The clock the freshness check runs against. Set only on a real result:
                // every provider falls back to the offline local analyzer internally, so
                // reaching this line means the sky was actually assessed.
                _lastAnalysisUtc = DateTime.UtcNow;
                _staleLogged = false;

                // Store a copy of the image for UI restoration
                _lastImage?.Dispose();
                _lastImage = new Bitmap(frame);

                // Update Safety State (Hysteresis)
                UpdateSafetyState(result);

                // Expose the reading to the Advanced Sequencer's Symbols sidebar (N.I.N.A. 3.3+).
                // The published Safe symbol is the composite state, so an expression in the
                // sequencer sees the same verdict NINA's safety monitor sees.
                SequencerSymbolPublisher.Publish(result, IsSafe);

                // Log the results
                Logger.Info($"Weather Analysis - Condition: {result.Condition}, " +
                          $"Cloud Coverage: {result.CloudCoverage:F1}%, " +
                          $"Safe: {result.IsSafeForImaging}, " +
                          $"Confidence: {result.Confidence:F1}%");

                if (Properties.Settings.Default.UseAscomSafetyMonitor)
                {
                    Logger.Info($"Safety state - sky: {(_isCurrentlySafe ? "SAFE" : "UNSAFE")}, " +
                                $"external ASCOM monitor: {(IsExternalMonitorSafe() ? "SAFE" : "UNSAFE")}, " +
                                $"combined: {(IsSafe ? "SAFE" : "UNSAFE")}");
                }

                // Append state changes to the shared LLM wiki daily digest (raw/)
                LlmWikiRawWriter.RecordAnalysis(result);

                // Raise property changed to notify NINA of safety status change
                RaisePropertyChanged(nameof(IsSafe));
                RaisePropertyChanged(nameof(SafetyStateReason));

                // Write safety status to file if enabled
                WriteSafetyStatusFile();

                // Save frame for debugging/logging (optional)
                var captureFolder = Path.Combine(CoreUtil.APPLICATIONTEMPPATH, "AllSkyCameraPlugin");
                var imagePath = Path.Combine(
                    captureFolder,
                    $"capture_{DateTime.Now:yyyyMMdd_HHmmss}.jpg");

                // Save image (HTTP/Folder modes only, RTSP handled above)
                await _captureService.SaveImageAsync(frame, imagePath, cancellationToken);

                PruneCaptureFolder(captureFolder);

                frame.Dispose();
            }
            catch (Exception ex)
            {
                // Same contract as a failed capture: no new verdict, so the existing one
                // keeps ageing toward the freshness limit rather than being trusted forever.
                Logger.Error($"Error performing weather check: {ex.Message}", ex);
                RaisePropertyChanged(nameof(IsSafe));
                RaisePropertyChanged(nameof(SafetyStateReason));
            }
            finally
            {
                _checkGate.Release();
            }
        }

        private string LastAnalysisAgeDescription()
        {
            return _lastAnalysisUtc == DateTime.MinValue
                ? "no analysis yet"
                : $"{(DateTime.UtcNow - _lastAnalysisUtc).TotalMinutes:F1} min";
        }

        // Debug captures are only needed for recent history; keep the folder bounded
        // so an always-on monitor cannot fill the disk over long sessions.
        private const int MaxSavedCaptures = 25;

        private static void PruneCaptureFolder(string folder)
        {
            try
            {
                if (!Directory.Exists(folder))
                {
                    return;
                }

                var files = new DirectoryInfo(folder).GetFiles("capture_*.jpg");
                if (files.Length <= MaxSavedCaptures)
                {
                    return;
                }

                Array.Sort(files, (a, b) => b.LastWriteTimeUtc.CompareTo(a.LastWriteTimeUtc));
                for (var i = MaxSavedCaptures; i < files.Length; i++)
                {
                    try
                    {
                        files[i].Delete();
                    }
                    catch (Exception ex)
                    {
                        Logger.Debug($"Failed to delete old capture {files[i].Name}: {ex.Message}");
                    }
                }

                Logger.Debug($"Pruned capture folder to {MaxSavedCaptures} most recent images ({files.Length - MaxSavedCaptures} deleted)");
            }
            catch (Exception ex)
            {
                Logger.Warning($"Failed to prune capture folder: {ex.Message}");
            }
        }

        public void SetupDialog()
        {
            // This would open a settings dialog
            // For now, settings are managed through plugin options
            Logger.Info("Setup dialog requested - use NINA Plugin Options");
        }

        /// <summary>
        /// Get the latest weather analysis result
        /// </summary>
        public WeatherAnalysisResult? GetLatestResult() => _lastResult;

        /// <summary>
        /// Get the latest captured image
        /// </summary>
        public Bitmap? GetLatestImage() => _lastImage != null ? new Bitmap(_lastImage) : null;

        /// <summary>
        /// Force an immediate weather check
        /// </summary>
        public async Task<WeatherAnalysisResult?> ForceCheckAsync(CancellationToken cancellationToken = default)
        {
            await PerformWeatherCheckAsync(cancellationToken);
            return _lastResult;
        }

        /// <summary>
        /// Write safety status to file if enabled
        /// </summary>
        private void WriteSafetyStatusFile()
        {
            try
            {
                if (!Properties.Settings.Default.WriteSafetyStatusFile)
                {
                    return;
                }

                var filePath = Properties.Settings.Default.SafetyStatusFilePath;
                if (string.IsNullOrWhiteSpace(filePath))
                {
                    Logger.Warning("Safety status file writing is enabled but no file path is configured");
                    return;
                }

                // The exported status is the same composite state NINA acts on - hysteresis,
                // data freshness and the external monitor included. It used to be recomputed
                // from the raw result here, which could disagree with IsSafe and hand third
                // party software a different answer than the one driving the sequence.
                var status = IsSafe ? "Safe" : "Unsafe";

                // Write plain SAFE/UNSAFE — compatible with ASCOM Generic File SafetyMonitor
                File.WriteAllText(filePath, status);
                Logger.Debug($"Safety status written to file: {filePath} - Status: {status}");
            }
            catch (Exception ex)
            {
                Logger.Error($"Error writing safety status file: {ex.Message}", ex);
            }
        }
    }
}
