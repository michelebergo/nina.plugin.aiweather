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
                    model);
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

                Connected = false;
                Logger.Info("All Sky Camera Safety Monitor disconnected");
            }
            catch (Exception ex)
            {
                Logger.Error($"Error disconnecting: {ex.Message}", ex);
            }
        }

        public bool IsSafe => _isCurrentlySafe;

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
                    Logger.Warning($"Failed to capture image from {captureMode} source");
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

                // Store a copy of the image for UI restoration
                _lastImage?.Dispose();
                _lastImage = new Bitmap(frame);

                // Update Safety State (Hysteresis)
                UpdateSafetyState(result);

                // Log the results
                Logger.Info($"Weather Analysis - Condition: {result.Condition}, " +
                          $"Cloud Coverage: {result.CloudCoverage:F1}%, " +
                          $"Safe: {result.IsSafeForImaging}, " +
                          $"Confidence: {result.Confidence:F1}%");

                // Raise property changed to notify NINA of safety status change
                RaisePropertyChanged(nameof(IsSafe));

                // Write safety status to file if enabled
                WriteSafetyStatusFile(result);

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
                Logger.Error($"Error performing weather check: {ex.Message}", ex);
            }
            finally
            {
                _checkGate.Release();
            }
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
        private void WriteSafetyStatusFile(WeatherAnalysisResult result)
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

                // Determine safety status
                var threshold = Properties.Settings.Default.CloudCoverageThreshold;
                var isSafe = result.IsSafeForImaging &&
                            result.CloudCoverage < threshold &&
                            !result.RainDetected &&
                            !result.FogDetected;

                var status = isSafe ? "Safe" : "Unsafe";

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
