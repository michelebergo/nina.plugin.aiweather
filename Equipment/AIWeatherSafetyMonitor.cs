using NINA.Equipment.Interfaces;
using NINA.Core.Utility;
using NINA.Profile.Interfaces;
using System;
using System.Collections.Generic;
using System.ComponentModel.Composition;
using System.IO;
using System.Threading;
using System.Threading.Tasks;
using AIWeather.Models;
using AIWeather.Services;

namespace AIWeather.Equipment
{
    /// <summary>
    /// All Sky Camera Safety Monitor
    /// Integrates with NINA's safety monitoring system to automatically pause/stop imaging
    /// when weather conditions become unsafe
    /// </summary>
    [Export(typeof(ISafetyMonitor))]
    [PartCreationPolicy(CreationPolicy.Shared)]
    [System.Runtime.Versioning.SupportedOSPlatform("windows")]
    public class AIWeatherSafetyMonitor : BaseINPC, ISafetyMonitor
    {
        private readonly RtspCaptureService _captureService;
        private IWeatherAnalysisService _analysisService;
        private Timer? _monitoringTimer;
        private WeatherAnalysisResult? _lastResult;
        private bool _isMonitoring = false;
        private CancellationTokenSource? _cts;
        private readonly SemaphoreSlim _checkGate = new SemaphoreSlim(1, 1);

        [ImportingConstructor]
        public AIWeatherSafetyMonitor()
        {
            _captureService = new RtspCaptureService();
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

                // Reload settings from disk to ensure we have latest values
                Properties.Settings.Default.Reload();
                
                // Initialize RTSP capture with credentials
                var rtspUrl = Properties.Settings.Default.RtspUrl;
                var username = Properties.Settings.Default.RtspUsername;
                var password = Properties.Settings.Default.RtspPassword;
                
                Logger.Info($"Safety Monitor - RTSP URL from settings: '{rtspUrl}'");
                Logger.Info($"Safety Monitor - Username: '{username}', Password length: {password?.Length ?? 0}");
                Logger.Info($"Safety Monitor - Settings file location: {Properties.Settings.Default.SettingsKey}");
                
                // Build authenticated URL if credentials provided
                var authenticatedUrl = BuildAuthenticatedUrl(rtspUrl, username, password);
                Logger.Info($"Safety Monitor - Authenticated URL: '{RedactRtspUrl(authenticatedUrl)}'");
                
                var success = await _captureService.InitializeAsync(authenticatedUrl, token);

                if (!success)
                {
                    Logger.Error($"Safety Monitor - Failed to connect to RTSP stream: {RedactRtspUrl(authenticatedUrl)}");
                    return false;
                }

                // Initialize analysis service (ensure correct mode/token/model at connect time)
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
                Logger.Info("All Sky Camera Safety Monitor connected");
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

            Logger.Info($"Starting periodic monitoring every {intervalMinutes} minutes");

            _monitoringTimer = new Timer(async _ =>
            {
                if (_cts?.Token.IsCancellationRequested ?? true) return;

                try
                {
                    await PerformWeatherCheckAsync(_cts.Token);
                }
                catch (Exception ex)
                {
                    Logger.Error($"Error during periodic weather check: {ex.Message}", ex);
                }
            }, null, TimeSpan.Zero, interval);
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
                Logger.Debug("Performing weather check");

                // Capture frame from RTSP stream
                var frame = await _captureService.CaptureFrameAsync(cancellationToken);
                if (frame == null)
                {
                    Logger.Warning("Failed to capture frame from RTSP stream");
                    return;
                }

                // Analyze the frame
                var result = await _analysisService.AnalyzeImageAsync(frame, cancellationToken);
                _lastResult = result;

                // Log the results
                Logger.Info($"Weather Analysis - Condition: {result.Condition}, " +
                          $"Cloud Coverage: {result.CloudCoverage:F1}%, " +
                          $"Safe: {result.IsSafeForImaging}, " +
                          $"Confidence: {result.Confidence:F1}%");

                // Raise property changed to notify NINA of safety status change
                RaisePropertyChanged(nameof(IsSafe));

                // Save frame for debugging/logging (optional)
                var imagePath = Path.Combine(
                    CoreUtil.APPLICATIONTEMPPATH,
                    "AllSkyCameraPlugin",
                    $"capture_{DateTime.Now:yyyyMMdd_HHmmss}.jpg");

                await _captureService.SaveFrameAsync(frame, imagePath, cancellationToken);

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
        /// Force an immediate weather check
        /// </summary>
        public async Task<WeatherAnalysisResult?> ForceCheckAsync(CancellationToken cancellationToken = default)
        {
            await PerformWeatherCheckAsync(cancellationToken);
            return _lastResult;
        }

        /// <summary>
        /// Build authenticated RTSP URL with embedded credentials
        /// </summary>
        private string BuildAuthenticatedUrl(string rtspUrl, string? username, string? password)
        {
            if (string.IsNullOrWhiteSpace(username) || string.IsNullOrWhiteSpace(password))
            {
                Logger.Info($"BuildAuthenticatedUrl - No credentials provided, returning original URL: {RedactRtspUrl(rtspUrl)}");
                return rtspUrl; // No authentication needed
            }

            try
            {
                var uri = new Uri(rtspUrl);
                var authenticatedUri = new UriBuilder(uri)
                {
                    UserName = username,
                    Password = password
                };
                var result = authenticatedUri.ToString();
                Logger.Info($"BuildAuthenticatedUrl - Built authenticated URL with username '{username}' (password hidden)");
                return result;
            }
            catch (Exception ex)
            {
                Logger.Warning($"Failed to build authenticated URL: {ex.Message}. Using original URL.");
                return rtspUrl;
            }
        }

        private static string RedactRtspUrl(string url)
        {
            if (string.IsNullOrWhiteSpace(url))
            {
                return url;
            }

            try
            {
                if (Uri.TryCreate(url, UriKind.Absolute, out var uri) && !string.IsNullOrEmpty(uri.UserInfo))
                {
                    var parts = uri.UserInfo.Split(new[] { ':' }, 2);
                    var user = parts.Length > 0 ? parts[0] : string.Empty;
                    var builder = new UriBuilder(uri)
                    {
                        UserName = user,
                        Password = "***"
                    };

                    return builder.Uri.ToString();
                }
            }
            catch
            {
                // best-effort redaction
            }

            return url;
        }
    }
}
