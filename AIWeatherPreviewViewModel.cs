using AIWeather.Equipment;
using AIWeather.Models;
using AIWeather.Services;
using NINA.Core.Utility;
using NINA.Equipment.Interfaces.ViewModel;
using NINA.Profile.Interfaces;
using NINA.WPF.Base.ViewModel;
using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.ComponentModel.Composition;
using System.Drawing;
using System.IO;
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
        private readonly AIWeatherSafetyMonitor _safetyMonitor;
        private BitmapImage? _currentImage;
        private WeatherAnalysisResult? _currentAnalysis;
        private bool _isConnected;
        private string _statusMessage = "Ready";
        private string _activityLog = "AI Weather Monitor initialized...\n";
        private AIWeatherPreviewView? _view;
        private DispatcherTimer _refreshTimer;
        private readonly SemaphoreSlim _refreshGate = new SemaphoreSlim(1, 1);
        private CommunityToolkit.Mvvm.Input.RelayCommand? _saveImageCommand;

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
        public AIWeatherPreviewViewModel(IProfileService profileService) : base(profileService)
        {
            _safetyMonitor = new AIWeatherSafetyMonitor();
            
            this.Title = "AI Weather Monitor";
            
            // Initialize refresh timer for live updates (every 2 seconds when streaming)
            var timerDispatcher = UiDispatcher ?? Dispatcher.CurrentDispatcher;
            _refreshTimer = new DispatcherTimer(DispatcherPriority.Background, timerDispatcher)
            {
                Interval = TimeSpan.FromSeconds(5)
            };
            _refreshTimer.Tick += async (s, e) => await UpdateFromLatestResultAsync(loadImage: true);
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
            var savedUrl = Properties.Settings.Default.RtspUrl ?? "";
            var protocol = "rtsp://";
            var mediaUrl = "";
            
            // Parse saved URL to extract protocol and media URL separately
            if (!string.IsNullOrEmpty(savedUrl))
            {
                var protoIndex = savedUrl.IndexOf("://");
                if (protoIndex > 0)
                {
                    protocol = savedUrl.Substring(0, protoIndex + 3);
                    mediaUrl = savedUrl.Substring(protoIndex + 3);
                }
                else
                {
                    mediaUrl = savedUrl;
                }
            }
            
            Logger.Info($"Initializing camera source - Saved URL: '{savedUrl}' -> Protocol: '{protocol}', MediaUrl: '{mediaUrl}'");
            
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
                    || e.PropertyName == nameof(Properties.Settings.Default.UseGitHubModels))
                {
                    RunOnUiThread(() =>
                    {
                        if (e.PropertyName == nameof(Properties.Settings.Default.CheckIntervalMinutes))
                        {
                            ApplyRefreshIntervalFromSettings();
                        }
                        RaisePropertyChanged(nameof(AnalysisMethod));
                        RaisePropertyChanged(nameof(AiSettingsSummary));
                    });
                }
            };
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
        public bool IsSafe => _currentAnalysis?.IsSafeForImaging ?? false;
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
                return;
            }

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
                    StatusMessage = "Failed to capture frame";
                    return false;
                }

                // Update analysis data
                _currentAnalysis = result;
                CaptureTimestamp = DateTime.Now;
                LastUpdate = DateTime.Now;

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

        private async Task LoadImageAsync(string imagePath)
        {
            BitmapImage? bitmap = null;

            try
            {
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
            }
            catch (Exception ex)
            {
                Logger.Error($"Error loading image: {ex.Message}", ex);
            }

            if (bitmap != null)
            {
                RunOnUiThread(() => { CurrentImage = bitmap; });
            }
        }

        private string? GetLatestCaptureImage()
        {
            try
            {
                var captureDir = Path.Combine(CoreUtil.APPLICATIONTEMPPATH, "AllSkyCameraPlugin");
                if (!Directory.Exists(captureDir))
                    return null;

                var files = Directory.GetFiles(captureDir, "capture_*.jpg");
                if (files.Length == 0)
                    return null;

                Array.Sort(files);
                return files[files.Length - 1]; // Return most recent
            }
            catch
            {
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
                        var connected = await _safetyMonitor.Connect(CancellationToken.None);
                        if (connected)
                        {
                            // Start UI update timer (analysis itself is done by the safety monitor at the configured interval)
                            ApplyRefreshIntervalFromSettings();
                            _refreshTimer.Start();
                            AddLog($"✓ AI analysis enabled ({GetCheckIntervalMinutesClamped()} min intervals)");

                            // Best-effort: wait a moment for the first periodic check to finish, then update UI
                            await Task.Delay(TimeSpan.FromSeconds(2));
                            await UpdateFromLatestResultAsync(loadImage: true);
                            IsConnected = true;
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
            AddLog("✓ Video view initialized");
        }
    }
}
