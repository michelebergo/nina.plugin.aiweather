using AIWeather.Equipment;
using AIWeather.Models;
using AIWeather.Services;
using NINA.Core.Utility;
using NINA.Equipment.Interfaces;
using NINA.Equipment.Interfaces.ViewModel;
using NINA.Equipment.Interfaces.Mediator;
using NINA.Profile.Interfaces;
using NINA.WPF.Base.ViewModel;
using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.ComponentModel.Composition;
using System.Drawing;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Threading;

namespace AIWeather
{
    [Export(typeof(IDockableVM))]
    [System.Runtime.Versioning.SupportedOSPlatform("windows")]
    public class AIWeatherPreviewViewModel : DockableVM
    {
        private static AIWeatherSafetyMonitor? _sharedSafetyMonitor;
        private readonly AIWeatherSafetyMonitor _safetyMonitor;
        private BitmapImage? _currentImage;
        private WeatherAnalysisResult? _currentAnalysis;
        private bool _isConnected;
        private bool _isRunning = false;
        private string _statusMessage = "Ready";
        private string _activityLog = "AI Weather Monitor initialized...\n";
        private AIWeatherPreviewView? _view;
        private DispatcherTimer _refreshTimer;
        private readonly SemaphoreSlim _refreshGate = new SemaphoreSlim(1, 1);
        private CommunityToolkit.Mvvm.Input.RelayCommand? _saveImageCommand;

        // Capture mode tracking
        public Models.CaptureMode CurrentCaptureMode
        {
            get
            {
                var mode = Properties.Settings.Default.CaptureMode;
                return (Models.CaptureMode)mode;
            }
        }

        public bool IsRtspMode => CurrentCaptureMode == Models.CaptureMode.RTSPStream;
        public bool IsNonRtspMode => CurrentCaptureMode != Models.CaptureMode.RTSPStream;
        public bool IsFolderMode => CurrentCaptureMode == Models.CaptureMode.FolderWatch;
        public bool IsUrlMode => CurrentCaptureMode != Models.CaptureMode.FolderWatch;

        private static Dispatcher? UiDispatcher => Application.Current?.Dispatcher;

        private static void RunOnUiThread(Action action)
        {
            var dispatcher = UiDispatcher;
            if (dispatcher == null || dispatcher.CheckAccess())
            {
                action();
                return;
            }

            dispatcher.BeginInvoke(action);
        }

        [ImportingConstructor]
        public AIWeatherPreviewViewModel(IProfileService profileService, ICameraMediator cameraMediator) : base(profileService)
        {
            // Use shared static instance to persist across navigation
            if (_sharedSafetyMonitor == null)
            {
                _sharedSafetyMonitor = new AIWeatherSafetyMonitor();
            }
            _safetyMonitor = _sharedSafetyMonitor;
            
            this.Title = "AI Weather Monitor";
            
            // Initialize refresh timer for live updates (every 2 seconds when streaming)
            var timerDispatcher = UiDispatcher ?? Dispatcher.CurrentDispatcher;
            _refreshTimer = new DispatcherTimer(DispatcherPriority.Background, timerDispatcher)
            {
                Interval = TimeSpan.FromSeconds(5)
            };
            _refreshTimer.Tick += async (s, e) =>
            {
                Logger.Debug("🔔 UI Refresh timer tick - updating display from latest result");
                await UpdateFromLatestResultAsync(loadImage: true);
            };
            ApplyRefreshIntervalFromSettings();

            // Cloud + spark icon (AI)
            try
            {
                var geometry = Geometry.Parse("M19.36,10.04C18.67,6.59 15.64,4 12,4C9.11,4 6.6,5.64 5.35,8.04C2.34,8.36 0,10.91 0,14C0,17.31 2.69,20 6,20H19C21.76,20 24,17.76 24,15C24,12.36 21.95,10.22 19.36,10.04Z M12,9.5L13,12H15.5L13.5,13.3L14.3,15.8L12,14.4L9.7,15.8L10.5,13.3L8.5,12H11L12,9.5Z");
                var group = new GeometryGroup { Children = { geometry } };
                if (group.CanFreeze)
                {
                    group.Freeze();
                }

                ImageGeometry = group;
            }
            catch
            {
                // best-effort
            }
            
            // Initialize Sources collection with one default camera
            var captureMode = (Models.CaptureMode)Properties.Settings.Default.CaptureMode;
            var savedUrl = "";
            var protocol = captureMode == Models.CaptureMode.RTSPStream ? "rtsp://" : "http://";
            var mediaUrl = "";
            
            // Get URL based on capture mode
            if (captureMode == Models.CaptureMode.RTSPStream)
            {
                savedUrl = Properties.Settings.Default.RtspUrl ?? "";
            }
            else if (captureMode == Models.CaptureMode.INDICamera)
            {
                savedUrl = Properties.Settings.Default.INDIDeviceName ?? "";
            }
            else if (captureMode == Models.CaptureMode.FolderWatch)
            {
                savedUrl = Properties.Settings.Default.FolderPath ?? "";
            }
            
            // Parse saved URL to extract protocol and media URL separately
            if (!string.IsNullOrEmpty(savedUrl))
            {
                var protoIndex = savedUrl.IndexOf("://");
                if (protoIndex > 0)
                {
                    // User provided full URL with protocol - use it
                    protocol = savedUrl.Substring(0, protoIndex + 3);
                    mediaUrl = savedUrl.Substring(protoIndex + 3);
                }
                else
                {
                    // No protocol in saved URL - treat entire string as media part
                    // For HTTP mode, if it looks like IP/domain, it's probably http not https
                    mediaUrl = savedUrl;
                }
            }
            
            Logger.Info($"Initializing camera source - Mode: {captureMode}, Saved URL: '{savedUrl}' -> Protocol: '{protocol}', MediaUrl: '{mediaUrl}'");
            
            Sources = new ObservableCollection<CameraSource>
            {
                new CameraSource 
                { 
                    Protocol = protocol, 
                    MediaUrl = mediaUrl,
                    Username = Properties.Settings.Default.RtspUsername ?? "",
                    Password = Properties.Settings.Default.RtspPassword ?? ""
                }
            };

            RefreshCommand = new CommunityToolkit.Mvvm.Input.AsyncRelayCommand(async () => { await RefreshAsync(); });
            _saveImageCommand = new CommunityToolkit.Mvvm.Input.RelayCommand(SaveImage, () => HasImage);
            SaveImageCommand = _saveImageCommand;
            ConnectCommand = new CommunityToolkit.Mvvm.Input.AsyncRelayCommand(async () => { await ToggleConnectionAsync(); });
            StartStopMonitoringCommand = new CommunityToolkit.Mvvm.Input.AsyncRelayCommand(async () => { await StartStopMonitoringAsync(); });
            AddSourceCommand = new CommunityToolkit.Mvvm.Input.RelayCommand(AddSource);
            DeleteSourceCommand = new CommunityToolkit.Mvvm.Input.RelayCommand<CameraSource?>(source =>
            {
                if (source != null)
                {
                    DeleteSource(source);
                }
            }, source => source != null);
            StartStreamCommand = new CommunityToolkit.Mvvm.Input.AsyncRelayCommand<CameraSource?>(async source =>
            {
                if (source != null)
                {
                    await ToggleStreamAsync(source);
                }
            }, source => source != null);
            
            // Raise property changed for capture mode visibility on initialization
            RaisePropertyChanged(nameof(CurrentCaptureMode));
            RaisePropertyChanged(nameof(IsRtspMode));
            RaisePropertyChanged(nameof(IsNonRtspMode));
            RaisePropertyChanged(nameof(IsFolderMode));
            RaisePropertyChanged(nameof(IsUrlMode));

            // Restore state if SafetyMonitor is already running
            RestoreMonitoringState();

            // Subscribe to safety monitor updates
            _safetyMonitor.PropertyChanged += (s, e) =>
            {
                if (e.PropertyName == nameof(AIWeatherSafetyMonitor.Connected))
                {
                    RunOnUiThread(() => { IsConnected = _safetyMonitor.Connected; });
                }
            };

            Properties.Settings.Default.PropertyChanged += (s, e) =>
            {
                if (e.PropertyName == nameof(Properties.Settings.Default.AnalysisProvider)
                    || e.PropertyName == nameof(Properties.Settings.Default.SelectedModel)
                    || e.PropertyName == nameof(Properties.Settings.Default.CheckIntervalMinutes)
                    || e.PropertyName == nameof(Properties.Settings.Default.CloudCoverageThreshold)
                    || e.PropertyName == nameof(Properties.Settings.Default.UseGitHubModels)
                    || e.PropertyName == nameof(Properties.Settings.Default.CaptureMode)
                    || e.PropertyName == nameof(Properties.Settings.Default.RtspUrl)
                    || e.PropertyName == nameof(Properties.Settings.Default.INDIDeviceName)
                    || e.PropertyName == nameof(Properties.Settings.Default.FolderPath)
                    || e.PropertyName == nameof(Properties.Settings.Default.RtspUsername)
                    || e.PropertyName == nameof(Properties.Settings.Default.RtspPassword))
                {
                    RunOnUiThread(() =>
                    {
                        if (e.PropertyName == nameof(Properties.Settings.Default.CheckIntervalMinutes))
                        {
                            ApplyRefreshIntervalFromSettings();
                        }
                        if (e.PropertyName == nameof(Properties.Settings.Default.CaptureMode))
                        {
                            RaisePropertyChanged(nameof(CurrentCaptureMode));
                            RaisePropertyChanged(nameof(IsRtspMode));
                            RaisePropertyChanged(nameof(IsNonRtspMode));
                            RaisePropertyChanged(nameof(IsFolderMode));
                            RaisePropertyChanged(nameof(IsUrlMode));

                            // Mode changes should immediately reflect in the panel. Also, if something
                            // is currently running (RTSP preview or periodic monitoring), stop it so the
                            // user can switch cleanly.
                            _ = HandleCaptureModeChangedAsync();
                        }
                        else if (e.PropertyName == nameof(Properties.Settings.Default.RtspUrl)
                            || e.PropertyName == nameof(Properties.Settings.Default.INDIDeviceName)
                            || e.PropertyName == nameof(Properties.Settings.Default.FolderPath)
                            || e.PropertyName == nameof(Properties.Settings.Default.RtspUsername)
                            || e.PropertyName == nameof(Properties.Settings.Default.RtspPassword))
                        {
                            // Options page changed one of the source settings; reflect it in the panel.
                            SyncPrimarySourceFromSettings();
                        }
                        RaisePropertyChanged(nameof(AnalysisMethod));
                        RaisePropertyChanged(nameof(AiSettingsSummary));
                    });
                }
            };
        }

        private async Task HandleCaptureModeChangedAsync()
        {
            try
            {
                // Check if SafetyMonitor is connected - if so, we're restoring state, not changing modes
                // Don't reset everything if background monitoring is still active
                if (_safetyMonitor.Connected)
                {
                    Logger.Info($"Capture mode event fired but SafetyMonitor is connected - keeping monitoring active");
                    SyncPrimarySourceFromSettings();
                    return;
                }

                Logger.Info($"Capture mode changed - stopping UI components");

                // Stop UI components (video stream, refresh timer)
                var view = GetVideoView();
                if (view != null)
                {
                    await view.StopStreamAsync();
                }

                _refreshTimer.Stop();
                IsConnected = false;
                IsRunning = false;
                CurrentImage = null;

                foreach (var s in Sources)
                {
                    s.IsRunning = false;
                    s.IsLoading = false;
                }

                SyncPrimarySourceFromSettings();
            }
            catch (Exception ex)
            {
                Logger.Warning($"Failed to handle capture mode change: {ex.Message}");
            }
        }

        private void SyncPrimarySourceFromSettings()
        {
            if (Sources == null || Sources.Count == 0)
            {
                return;
            }

            var mode = CurrentCaptureMode;
            var source = Sources[0];

            // Always keep credentials in sync (used by RTSP and some cameras).
            source.Username = Properties.Settings.Default.RtspUsername ?? string.Empty;
            source.Password = Properties.Settings.Default.RtspPassword ?? string.Empty;

            if (mode == Models.CaptureMode.RTSPStream)
            {
                var saved = Properties.Settings.Default.RtspUrl ?? string.Empty;
                ApplySavedUrlToSource(source, saved, defaultProtocol: "rtsp://");
                return;
            }

            if (mode == Models.CaptureMode.INDICamera)
            {
                // Historical naming: this stores the full URL (including protocol) for the non-RTSP mode.
                var saved = Properties.Settings.Default.INDIDeviceName ?? string.Empty;
                ApplySavedUrlToSource(source, saved, defaultProtocol: "https://");
                return;
            }

            if (mode == Models.CaptureMode.FolderWatch)
            {
                // Folder mode: store path as-is in MediaUrl.
                source.Protocol = "";
                source.MediaUrl = Properties.Settings.Default.FolderPath ?? string.Empty;
            }
        }

        private static void ApplySavedUrlToSource(CameraSource source, string savedValue, string defaultProtocol)
        {
            if (source == null)
            {
                return;
            }

            var protocol = defaultProtocol;
            var media = string.Empty;

            if (!string.IsNullOrWhiteSpace(savedValue))
            {
                var protoIndex = savedValue.IndexOf("://", StringComparison.Ordinal);
                if (protoIndex > 0)
                {
                    protocol = savedValue.Substring(0, protoIndex + 3);
                    media = savedValue.Substring(protoIndex + 3);
                }
                else
                {
                    media = savedValue;
                }
            }

            source.Protocol = protocol;
            source.MediaUrl = media;
        }

        // Camera Sources Management
        public ObservableCollection<CameraSource> Sources { get; set; }
        public List<string> Protocols => new List<string> { "rtsp://", "http://", "https://" };
        
        public ICommand RefreshCommand { get; }
        public ICommand SaveImageCommand { get; }
        public ICommand ConnectCommand { get; }
        public ICommand AddSourceCommand { get; }
        public ICommand DeleteSourceCommand { get; }
        public ICommand StartStreamCommand { get; }
        public ICommand StartStopMonitoringCommand { get; }

        public string RtspUrl
        {
            get => Properties.Settings.Default.RtspUrl;
            set
            {
                Properties.Settings.Default.RtspUrl = value;
                CoreUtil.SaveSettings(Properties.Settings.Default);
                RaisePropertyChanged();
            }
        }

        public string RtspUsername
        {
            get => Properties.Settings.Default.RtspUsername;
            set
            {
                Properties.Settings.Default.RtspUsername = value;
                CoreUtil.SaveSettings(Properties.Settings.Default);
                RaisePropertyChanged();
            }
        }

        public string ConnectButtonText => IsConnected ? "Disconnect" : "Connect";

        public BitmapImage? CurrentImage
        {
            get => _currentImage;
            set
            {
                _currentImage = value;
                RaisePropertyChanged();
                RaisePropertyChanged(nameof(HasImage));
                _saveImageCommand?.NotifyCanExecuteChanged();
            }
        }

        public bool HasImage => _currentImage != null;

        public bool IsConnected
        {
            get => _isConnected;
            set
            {
                _isConnected = value;
                RaisePropertyChanged();
                RaisePropertyChanged(nameof(ConnectionStatus));
            }
        }

        public bool IsRunning
        {
            get => _isRunning;
            set
            {
                _isRunning = value;
                RaisePropertyChanged();
            }
        }

        public string ConnectionStatus => IsConnected ? "Connected" : "Disconnected";

        public string StatusMessage
        {
            get => _statusMessage;
            set
            {
                _statusMessage = value;
                RaisePropertyChanged();
            }
        }

        public string ActivityLog
        {
            get => _activityLog;
            set
            {
                _activityLog = value;
                RaisePropertyChanged();
            }
        }

        // Analysis properties
        // Safety state comes from the safety monitor (optionally ASCOM-backed).
        public bool IsSafe => _safetyMonitor?.IsSafe ?? (_currentAnalysis?.IsSafeForImaging ?? false);
        public string SafetyStatus => IsSafe ? "✅ SAFE" : "⛔ UNSAFE";
        public string WeatherCondition => _currentAnalysis?.Condition.ToString() ?? "Unknown";
        public double CloudCoverage => _currentAnalysis?.CloudCoverage ?? 0;
        public double Confidence => _currentAnalysis?.Confidence ?? 0;
        public bool RainDetected => _currentAnalysis?.RainDetected ?? false;
        public bool FogDetected => _currentAnalysis?.FogDetected ?? false;
        public string Description => _currentAnalysis?.Description ?? "No analysis available";
        public DateTime? CaptureTimestamp { get; private set; }
        public DateTime? LastUpdate { get; private set; }

        public string AnalysisMethod
        {
            get
            {
                var provider = Properties.Settings.Default.AnalysisProvider;
                if (!string.IsNullOrWhiteSpace(provider) && !string.Equals(provider, "Local", StringComparison.OrdinalIgnoreCase))
                {
                    var model = Properties.Settings.Default.SelectedModel;
                    return string.IsNullOrWhiteSpace(model) ? provider : $"{provider} - {model}";
                }

                return "Local Image Processing";
            }
        }

        public string AiSettingsSummary
        {
            get
            {
                var provider = Properties.Settings.Default.AnalysisProvider;
                if (string.IsNullOrWhiteSpace(provider))
                {
                    provider = Properties.Settings.Default.UseGitHubModels ? "GitHubModels" : "Local";
                }

                var model = Properties.Settings.Default.SelectedModel;
                var intervalMinutes = GetCheckIntervalMinutesClamped();
                var threshold = Properties.Settings.Default.CloudCoverageThreshold;

                var aiLabel = string.IsNullOrWhiteSpace(model)
                    ? provider
                    : $"{provider} - {model}";

                return $"AI: {aiLabel} | Check: {intervalMinutes}m | Cloud thresh: {threshold:F0}%";
            }
        }

        private static int GetCheckIntervalMinutesClamped()
        {
            var minutes = Properties.Settings.Default.CheckIntervalMinutes;
            return minutes <= 0 ? 1 : minutes;
        }

        private void ApplyRefreshIntervalFromSettings()
        {
            var minutes = GetCheckIntervalMinutesClamped();
            _refreshTimer.Interval = TimeSpan.FromMinutes(minutes);
            RaisePropertyChanged(nameof(AiSettingsSummary));
        }

        private void RestoreMonitoringState()
        {
            // Check if the shared SafetyMonitor is already connected and monitoring
            if (_safetyMonitor.Connected)
            {
                AddLog("✓ Monitoring state restored - monitoring is active");

                // Restore connection state
                IsConnected = true;
                IsRunning = true;

                // Restore source running state
                if (Sources.Count > 0)
                {
                    Sources[0].IsRunning = true;
                }

                // Apply refresh interval and start timer
                ApplyRefreshIntervalFromSettings();
                _refreshTimer.Start();

                // Load latest analysis results and update UI
                _ = UpdateFromLatestResultAsync(loadImage: true);

                StatusMessage = "Monitoring active";
            }
        }

        private async Task<bool> ToggleConnectionAsync()
        {
            try
            {
                if (IsConnected)
                {
                    // Disconnect
                    AddLog("Disconnecting from RTSP stream...");
                    _safetyMonitor.Disconnect();
                    IsConnected = false;
                    StatusMessage = "Disconnected";
                    CurrentImage = null;
                    AddLog("✓ Disconnected successfully");
                }
                else
                {
                    // Connect
                    if (string.IsNullOrWhiteSpace(RtspUrl))
                    {
                        AddLog("ERROR: RTSP URL is required");
                        StatusMessage = "RTSP URL required";
                        return false;
                    }

                    AddLog($"Connecting to {RtspUrl}...");
                    StatusMessage = "Connecting...";
                    var connected = await _safetyMonitor.Connect(CancellationToken.None);
                    
                    if (connected)
                    {
                        IsConnected = true;
                        StatusMessage = "Connected";
                        AddLog("✓ Connected successfully - stream ready");

                        // Do not force an immediate check here; the safety monitor already starts its periodic
                        // monitoring (with an initial check). We'll just sync UI from whatever is available.
                        await UpdateFromLatestResultAsync(loadImage: true);
                    }
                    else
                    {
                        AddLog("ERROR: Connection failed - check URL and credentials");
                        StatusMessage = "Connection failed";
                        return false;
                    }
                }

                RaisePropertyChanged(nameof(ConnectButtonText));
                return true;
            }
            catch (Exception ex)
            {
                AddLog($"ERROR: Connection error - {ex.Message}");
                Logger.Error($"Connection error: {ex.Message}", ex);
                IsConnected = false;
                StatusMessage = "Connection error";
                RaisePropertyChanged(nameof(ConnectButtonText));
                return false;
            }
        }

        private async Task UpdateFromLatestResultAsync(bool loadImage)
        {
            var result = _safetyMonitor.GetLatestResult();
            if (result == null)
            {
                Logger.Debug("UpdateFromLatestResultAsync: No result available from SafetyMonitor");
                return;
            }

            Logger.Debug($"UpdateFromLatestResultAsync: Displaying result - {result.Condition}, {result.CloudCoverage:F1}% clouds");
            _currentAnalysis = result;
            LastUpdate = DateTime.Now;

            if (loadImage)
            {
                var imagePath = GetLatestCaptureImage();
                if (!string.IsNullOrEmpty(imagePath) && File.Exists(imagePath))
                {
                    await LoadImageAsync(imagePath);
                }
            }

            RaiseAllAnalysisProperties();
        }

        private async Task<bool> RefreshAsync()
        {
            if (!await _refreshGate.WaitAsync(0))
            {
                return false;
            }
            try
            {
                AddLog("Refreshing camera preview...");
                StatusMessage = "Capturing frame...";

                if (!_safetyMonitor.Connected)
                {
                    AddLog("ERROR: Not connected to camera");
                    StatusMessage = "Not connected to camera";
                    return false;
                }

                // Force a weather check
                var result = await _safetyMonitor.ForceCheckAsync();
                
                if (result == null)
                {
                    AddLog("ERROR: Failed to capture frame");
                    if (CurrentCaptureMode == Models.CaptureMode.RTSPStream)
                    {
                        AddLog("Tip: RTSP AI capture uses OpenCV/FFmpeg (not VLC). If your URL is just rtsp://IP it may show video but still return empty frames. Use the camera's full RTSP stream URL including the path (e.g. /stream, /live, /h264) and port if needed.");
                    }
                    StatusMessage = "Failed to capture frame";
                    return false;
                }

                // Update analysis data
                _currentAnalysis = result;
                CaptureTimestamp = DateTime.Now;
                LastUpdate = DateTime.Now;

                // Give a moment for the image to be saved to disk
                await Task.Delay(500);
                
                // Load the most recent image
                var imagePath = GetLatestCaptureImage();
                if (!string.IsNullOrEmpty(imagePath) && File.Exists(imagePath))
                {
                    await LoadImageAsync(imagePath);
                    AddLog($"✓ Frame captured successfully");
                    AddLog($"Analysis: {result.Condition} (Cloud: {result.CloudCoverage:F1}%)");
                    if (result.RainDetected) AddLog("⚠ Rain detected");
                    if (result.FogDetected) AddLog("⚠ Fog detected");
                    AddLog($"Status: {(result.IsSafeForImaging ? "SAFE ✓" : "UNSAFE ⛔")}");
                    StatusMessage = "Analysis complete";
                }
                else
                {
                    Logger.Warning($"Image file not found. Looking in temp path, found: {imagePath ?? "null"}");
                    AddLog("WARNING: Image captured but not found");
                    StatusMessage = "Image captured but not found";
                }

                // Refresh all analysis properties
                RaiseAllAnalysisProperties();

                return true;
            }
            catch (Exception ex)
            {
                AddLog($"ERROR: {ex.Message}");
                Logger.Error($"Error refreshing preview: {ex.Message}", ex);
                StatusMessage = $"Error: {ex.Message}";
                return false;
            }
            finally
            {
                _refreshGate.Release();
            }
        }

        // Start/Stop periodic monitoring for HTTP/Folder Watch modes
        private async Task StartStopMonitoringAsync()
        {
            if (IsRunning)
            {
                // Stop monitoring
                _refreshTimer.Stop();
                _safetyMonitor.Disconnect();
                IsConnected = false;
                IsRunning = false;
                
                // Update all sources to show stopped
                foreach (var source in Sources)
                {
                    source.IsRunning = false;
                }
                
                AddLog("⏹ Monitoring stopped");
                StatusMessage = "Monitoring stopped";
            }
            else
            {
                // Get the first source for configuration
                var source = Sources.FirstOrDefault();
                if (source == null)
                {
                    AddLog("ERROR: No camera source configured");
                    return;
                }
                
                Logger.Info($"Starting monitoring - Total sources: {Sources.Count}, Source URL: {source.FullUrl}, Source IsRunning (before): {source.IsRunning}");
                
                // Save settings based on capture mode
                var captureMode = CurrentCaptureMode;
                if (captureMode == Models.CaptureMode.INDICamera)
                {
                    // For HTTP downloads, use full URL with protocol
                    Properties.Settings.Default.INDIDeviceName = source.FullUrl;
                }
                else if (captureMode == Models.CaptureMode.FolderWatch)
                {
                    // For folder watch, use raw path without protocol
                    Properties.Settings.Default.FolderPath = source.MediaUrl;
                }
                
                Properties.Settings.Default.RtspUsername = source.Username;
                Properties.Settings.Default.RtspPassword = source.Password;
                CoreUtil.SaveSettings(Properties.Settings.Default);
                
                // Start monitoring
                AddLog("▶ Starting periodic monitoring...");
                StatusMessage = "Connecting...";
                
                var connected = await _safetyMonitor.Connect(CancellationToken.None);
                if (connected)
                {
                    IsConnected = true;
                    IsRunning = true;
                    source.IsRunning = true;
                    
                    Logger.Info($"Monitoring started - IsRunning set to true for source. Mode: {captureMode}");
                    AddLog($"✓ Monitoring started ({GetCheckIntervalMinutesClamped()} min intervals)");
                    StatusMessage = "Capturing initial image...";
                    
                    // Do initial capture immediately
                    await RefreshAsync();
                    
                    // Start periodic refresh timer for subsequent captures
                    // The timer will call UpdateFromLatestResultAsync which fetches the latest analysis result
                    ApplyRefreshIntervalFromSettings();
                    var intervalMinutes = GetCheckIntervalMinutesClamped();
                    Logger.Info($"Starting refresh timer with {intervalMinutes} minute interval");
                    _refreshTimer.Start();
                    AddLog($"📊 Periodic monitoring active - next update in {intervalMinutes} minute(s)");
                    
                    StatusMessage = "Monitoring active";
                }
                else
                {
                    AddLog("ERROR: Failed to connect");
                    StatusMessage = "Connection failed";
                    IsRunning = false;
                    source.IsRunning = false;
                }
            }
        }

        private async Task LoadImageAsync(string imagePath)
        {
            BitmapImage? bitmap = null;

            try
            {
                Logger.Info($"LoadImageAsync: Attempting to load image from {imagePath}");
                Logger.Info($"File exists: {File.Exists(imagePath)}, File size: {(File.Exists(imagePath) ? new FileInfo(imagePath).Length : 0)} bytes");
                
                bitmap = await Task.Run(() =>
                {
                    var img = new BitmapImage();
                    img.BeginInit();
                    img.CacheOption = BitmapCacheOption.OnLoad;
                    img.UriSource = new Uri(imagePath);
                    img.EndInit();
                    img.Freeze();
                    return img;
                });
                
                Logger.Info($"Image loaded successfully: {bitmap.PixelWidth}x{bitmap.PixelHeight}");
            }
            catch (Exception ex)
            {
                Logger.Error($"Error loading image from {imagePath}: {ex.Message}", ex);
            }

            if (bitmap != null)
            {
                RunOnUiThread(() => 
                { 
                    CurrentImage = bitmap;
                    Logger.Info("CurrentImage property set on UI thread");
                });
            }
            else
            {
                Logger.Warning("Bitmap is null, CurrentImage not set");
            }
        }

        private string? GetLatestCaptureImage()
        {
            try
            {
                var captureDir = Path.Combine(CoreUtil.APPLICATIONTEMPPATH, "AllSkyCameraPlugin");
                Logger.Info($"Looking for images in: {captureDir}");
                
                if (!Directory.Exists(captureDir))
                {
                    Logger.Warning($"Capture directory does not exist: {captureDir}");
                    return null;
                }

                var files = Directory.GetFiles(captureDir, "capture_*.jpg");
                Logger.Info($"Found {files.Length} capture files in directory");
                
                if (files.Length == 0)
                    return null;

                Array.Sort(files);
                var latestFile = files[files.Length - 1];
                Logger.Info($"Latest capture file: {latestFile}");
                return latestFile;
            }
            catch (Exception ex)
            {
                Logger.Error($"Error getting latest capture image: {ex.Message}", ex);
                return null;
            }
        }

        private void SaveImage()
        {
            try
            {
                if (CurrentImage == null) return;

                var dialog = new Microsoft.Win32.SaveFileDialog
                {
                    Filter = "JPEG Image|*.jpg|PNG Image|*.png|All Files|*.*",
                    DefaultExt = ".jpg",
                    FileName = $"AllSkyCamera_{DateTime.Now:yyyyMMdd_HHmmss}"
                };

                if (dialog.ShowDialog() == true)
                {
                    var encoder = new JpegBitmapEncoder();
                    encoder.Frames.Add(BitmapFrame.Create(CurrentImage));

                    using var fileStream = new FileStream(dialog.FileName, FileMode.Create);
                    encoder.Save(fileStream);

                    AddLog($"✓ Image saved to {Path.GetFileName(dialog.FileName)}");
                    StatusMessage = $"Image saved to {dialog.FileName}";
                    Logger.Info($"Image saved to: {dialog.FileName}");
                }
            }
            catch (Exception ex)
            {
                AddLog($"ERROR: Failed to save image - {ex.Message}");
                Logger.Error($"Error saving image: {ex.Message}", ex);
                StatusMessage = $"Error saving image: {ex.Message}";
            }
        }

        private void RaiseAllAnalysisProperties()
        {
            RaisePropertyChanged(nameof(IsSafe));
            RaisePropertyChanged(nameof(SafetyStatus));
            RaisePropertyChanged(nameof(WeatherCondition));
            RaisePropertyChanged(nameof(CloudCoverage));
            RaisePropertyChanged(nameof(Confidence));
            RaisePropertyChanged(nameof(RainDetected));
            RaisePropertyChanged(nameof(FogDetected));
            RaisePropertyChanged(nameof(Description));
            RaisePropertyChanged(nameof(CaptureTimestamp));
            RaisePropertyChanged(nameof(LastUpdate));
            RaisePropertyChanged(nameof(AnalysisMethod));
            RaisePropertyChanged(nameof(AiSettingsSummary));
        }

        private void AddLog(string message)
        {
            RunOnUiThread(() =>
            {
                var timestamp = DateTime.Now.ToString("HH:mm:ss");
                ActivityLog = $"[{timestamp}] {message}\n" + ActivityLog;

                // Keep only last 100 lines
                var lines = ActivityLog.Split('\n');
                if (lines.Length > 100)
                {
                    ActivityLog = string.Join("\n", lines, 0, 100);
                }
            });
        }

        // Camera Source Management Commands
        private void AddSource()
        {
            var newSource = new CameraSource
            {
                Protocol = "rtsp://",
                MediaUrl = "",
                Username = "",
                Password = ""
            };
            Sources.Add(newSource);
            AddLog("➕ New camera source added");
        }

        private void DeleteSource(CameraSource source)
        {
            if (source != null && Sources.Contains(source))
            {
                if (source.IsRunning)
                {
                    AddLog($"⚠ Stop stream before deleting source {source.MediaUrl}");
                    return;
                }
                
                Sources.Remove(source);
                AddLog($"➖ Camera source removed: {source.MediaUrl}");
            }
        }

        private async Task<bool> ToggleStreamAsync(CameraSource source)
        {
            if (source == null) return false;

            try
            {
                if (source.IsRunning)
                {
                    // Stop stream
                    AddLog($"⏹ Stopping stream from {source.FullUrl}...");
                    source.IsLoading = true;
                    
                    // Stop live video stream
                    var view = GetVideoView();
                    if (view != null)
                    {
                        await view.StopStreamAsync();
                    }
                    
                    // Stop auto-refresh timer
                    _refreshTimer.Stop();
                    
                    _safetyMonitor.Disconnect();
                    IsConnected = false;
                    CurrentImage = null;
                    
                    source.IsRunning = false;
                    source.IsLoading = false;
                    AddLog($"✓ Stream stopped: {source.MediaUrl}");
                    StatusMessage = "Stream stopped";
                    return true;
                }
                else
                {
                    // Start stream
                    if (string.IsNullOrWhiteSpace(source.MediaUrl))
                    {
                        AddLog($"⚠ ERROR: Media URL is required for {source.Protocol}");
                        return false;
                    }

                    Logger.Info($"Stream start - Protocol: '{source.Protocol}', MediaUrl: '{source.MediaUrl}', FullUrl: '{source.FullUrl}'");
                    AddLog($"▶ Starting RTSP stream from {source.FullUrl}...");
                    source.IsLoading = true;
                    StatusMessage = "Connecting to stream...";

                    // Update settings with this source's details
                    Properties.Settings.Default.RtspUrl = source.FullUrl;
                    Properties.Settings.Default.RtspUsername = source.Username;
                    Properties.Settings.Default.RtspPassword = source.Password;
                    CoreUtil.SaveSettings(Properties.Settings.Default);

                    try
                    {
                        if (Uri.TryCreate(source.FullUrl, UriKind.Absolute, out var uri)
                            && (string.IsNullOrWhiteSpace(uri.AbsolutePath) || uri.AbsolutePath == "/"))
                        {
                            AddLog("⚠ RTSP URL has no stream path. Live preview may still work, but AI frame capture often fails. Enter the full RTSP stream URL (include /stream or similar).");
                        }
                    }
                    catch
                    {
                        // best-effort
                    }

                    // Start live video stream via LibVLC
                    var view = GetVideoView();
                    if (view != null)
                    {
                        Logger.Info($"Calling StartStream - URL: {source.FullUrl}, Username: '{source.Username}', Password length: {source.Password?.Length ?? 0}");
                        await view.StartStreamAsync(source.FullUrl, source.Username, source.Password);
                        
                        source.IsRunning = true;
                        source.IsLoading = false;
                        StatusMessage = "Live stream active";
                        AddLog($"✓ Live RTSP stream started: {source.MediaUrl}");
                        
                        // Also connect safety monitor for analysis
                        AddLog($"📊 Connecting AI analysis for RTSP mode...");
                        Logger.Info($"Attempting to connect safety monitor for AI analysis. Current mode: {CurrentCaptureMode}");
                        var connected = await _safetyMonitor.Connect(CancellationToken.None);
                        Logger.Info($"Safety monitor Connect() returned: {connected}");
                        if (connected)
                        {
                            IsConnected = true;
                            var intervalMinutes = GetCheckIntervalMinutesClamped();
                            AddLog($"✓ AI analysis connected - automatic monitoring enabled");
                            AddLog($"📊 Weather analysis will run automatically every {intervalMinutes} minute(s)");

                            // Trigger immediate first analysis
                            StatusMessage = "Running initial analysis...";
                            _ = Task.Run(async () =>
                            {
                                try
                                {
                                    await Task.Delay(2000); // Give RTSP stream a moment to stabilize
                                    Logger.Info("Triggering immediate first analysis for RTSP mode");
                                    var result = await _safetyMonitor.ForceCheckAsync();
                                    if (result != null)
                                    {
                                        RunOnUiThread(() =>
                                        {
                                            AddLog($"✓ First analysis complete: {result.Condition} (Cloud: {result.CloudCoverage:F0}%)");
                                            StatusMessage = "Monitoring active";
                                        });
                                        await UpdateFromLatestResultAsync(loadImage: true);
                                    }
                                }
                                catch (Exception ex)
                                {
                                    Logger.Error($"Error in initial analysis: {ex.Message}");
                                }
                            });

                            // Start UI refresh timer to display latest results
                            ApplyRefreshIntervalFromSettings();
                            _refreshTimer.Start();
                            Logger.Info($"UI refresh timer started with {intervalMinutes} minute interval");
                        }
                        else
                        {
                            IsConnected = false;
                            AddLog("⚠ AI analysis not connected (preview still running)");
                        }
                        
                        return true;
                    }
                    else
                    {
                        source.IsLoading = false;
                        AddLog($"❌ ERROR: Video view not initialized");
                        StatusMessage = "Video view error";
                        return false;
                    }
                }
            }
            catch (Exception ex)
            {
                source.IsLoading = false;
                source.IsRunning = false;
                AddLog($"❌ ERROR: Stream error - {ex.Message}");
                Logger.Error($"Stream toggle error for {source.FullUrl}: {ex.Message}", ex);
                StatusMessage = $"Error: {ex.Message}";
                return false;
            }
        }

        private AIWeatherPreviewView? GetVideoView()
        {
            return _view;
        }

        public void SetView(AIWeatherPreviewView view)
        {
            _view = view;
            AddLog("✓ AI Weather Monitor view initialized");

            Logger.Info($"SetView called - IsRunning: {IsRunning}, Sources.Count: {Sources.Count}, CurrentCaptureMode: {CurrentCaptureMode}, IsNonRtspMode: {IsNonRtspMode}");

            // If we're restoring state, handle mode-specific UI updates
            if (IsRunning && Sources.Count > 0)
            {
                var source = Sources[0];
                Logger.Info($"Source state - IsRunning: {source.IsRunning}, FullUrl: '{source.FullUrl}', CaptureMode: {source.CaptureMode}");

                if (IsRtspMode && source.IsRunning && !string.IsNullOrWhiteSpace(source.FullUrl))
                {
                    // RTSP mode: restart the video stream
                    AddLog("✓ Restarting RTSP video stream...");
                    _view.StartStream(source.FullUrl, source.Username, source.Password);
                }
                else if (IsNonRtspMode)
                {
                    // HTTP and Folder modes: restore last image and results
                    AddLog($"✓ Restoring monitoring display for {CurrentCaptureMode} mode...");
                    Logger.Info($"Attempting to restore image for {CurrentCaptureMode} mode");

                    // Get and display the latest captured image
                    var latestImage = _safetyMonitor.GetLatestImage();
                    Logger.Info($"GetLatestImage returned: {(latestImage != null ? $"image {latestImage.Width}x{latestImage.Height}" : "null")}");

                    if (latestImage != null)
                    {
                        RunOnUiThread(() =>
                        {
                            try
                            {
                                var bitmapImage = new BitmapImage();
                                using (var memory = new System.IO.MemoryStream())
                                {
                                    latestImage.Save(memory, System.Drawing.Imaging.ImageFormat.Png);
                                    memory.Position = 0;
                                    bitmapImage.BeginInit();
                                    bitmapImage.CacheOption = BitmapCacheOption.OnLoad;
                                    bitmapImage.StreamSource = memory;
                                    bitmapImage.EndInit();
                                    bitmapImage.Freeze();
                                }
                                CurrentImage = bitmapImage;
                                Logger.Info($"Successfully restored image for {CurrentCaptureMode} mode");
                                latestImage.Dispose();
                            }
                            catch (Exception ex)
                            {
                                Logger.Error($"Error restoring image for {CurrentCaptureMode}: {ex.Message}", ex);
                            }
                        });
                    }
                    else
                    {
                        Logger.Warning($"No image available to restore for {CurrentCaptureMode} mode");
                    }

                    // Update analysis results display
                    _ = UpdateFromLatestResultAsync(loadImage: false);
                }
            }
            else
            {
                Logger.Info($"Not restoring state - IsRunning: {IsRunning}, Sources.Count: {Sources.Count}");
            }
        }
    }
}
