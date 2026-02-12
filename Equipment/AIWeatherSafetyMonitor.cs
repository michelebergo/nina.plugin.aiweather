using NINA.Equipment.Interfaces;
using NINA.Core.Utility;
using NINA.Profile.Interfaces;
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
        private readonly UnifiedCaptureService _captureService;
        private IWeatherAnalysisService _analysisService;
        private Timer? _monitoringTimer;
        private WeatherAnalysisResult? _lastResult;
        private Bitmap? _lastImage;
        private bool _isMonitoring = false;
        private CancellationTokenSource? _cts;
        private readonly SemaphoreSlim _checkGate = new SemaphoreSlim(1, 1);

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

                // Start periodic monitoring
                StartPeriodicMonitoring();

                Connected = true;
                Logger.Info($"All Sky Camera Safety Monitor connected using {captureMode} mode");
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

        public bool IsSafe
        {
            get
            {
                if (_lastResult == null)
                {
                    // No analysis yet - assume unsafe until we have data
                    return false;
                }

                // Check cloud coverage threshold
                var threshold = Properties.Settings.Default.CloudCoverageThreshold;
                var isSafe = _lastResult.IsSafeForImaging && 
                            _lastResult.CloudCoverage < threshold &&
                            !_lastResult.RainDetected;

                Logger.Debug($"Safety check: {(isSafe ? "SAFE" : "UNSAFE")} - " +
                           $"Cloud coverage: {_lastResult.CloudCoverage:F1}%, " +
                           $"Threshold: {threshold}%, " +
                           $"Condition: {_lastResult.Condition}");

                return isSafe;
            }
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
            Logger.Info($"⏰ Starting periodic monitoring every {intervalMinutes} minutes (Mode: {captureMode})");

            _monitoringTimer = new Timer(_ =>
            {
                var currentMode = (CaptureMode)Properties.Settings.Default.CaptureMode;
                Logger.Info($"🔔 Timer fired! Interval: {intervalMinutes} min, Mode: {currentMode}");
                
                if (_cts?.Token.IsCancellationRequested ?? true)
                {
                    Logger.Warning("⚠ Timer fired but cancellation was requested - skipping");
                    return;
                }

                try
                {
                    Logger.Info($"🚀 Launching weather check task from timer (Mode: {currentMode})...");
                    Task.Run(async () =>
                    {
                        try
                        {
                            Logger.Info($"📸 Executing periodic weather check (Mode: {currentMode})...");
                            await PerformWeatherCheckAsync(_cts.Token);
                            Logger.Info($"✅ Weather check complete - next check in {intervalMinutes} min");
                        }
                        catch (Exception ex)
                        {
                            Logger.Error($"❌ Error in periodic weather check: {ex.Message}", ex);
                        }
                    });
                }
                catch (Exception ex)
                {
                    Logger.Error($"❌ Failed to start weather check task: {ex.Message}", ex);
                }
            }, null, TimeSpan.Zero, interval);
            
            Logger.Info($"✅ Timer created and started - first check will run immediately");
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
                Logger.Info($"🎯 PerformWeatherCheckAsync - Mode: {captureMode}");

                Bitmap? frame = null;

                // Capture image from all modes
                Logger.Info($"📥 Capturing image from {captureMode} source...");
                frame = await _captureService.CaptureImageAsync(cancellationToken);

                if (frame == null)
                {
                    Logger.Warning($"❌ Failed to capture image from {captureMode} source");
                    return;
                }

                Logger.Info($"✓ Image captured successfully from {captureMode}, size: {frame.Width}x{frame.Height}");

                // Analyze the frame
                Logger.Info($"🤖 Starting AI analysis using {_analysisService.GetType().Name}...");
                var result = await _analysisService.AnalyzeImageAsync(frame, cancellationToken);
                Logger.Info($"✅ AI analysis completed successfully");
                _lastResult = result;

                // Store a copy of the image for UI restoration
                _lastImage?.Dispose();
                _lastImage = new Bitmap(frame);

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
                var imagePath = Path.Combine(
                    CoreUtil.APPLICATIONTEMPPATH,
                    "AllSkyCameraPlugin",
                    $"capture_{DateTime.Now:yyyyMMdd_HHmmss}.jpg");

                // Save image (HTTP/Folder modes only, RTSP handled above)
                await _captureService.SaveImageAsync(frame, imagePath, cancellationToken);

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
