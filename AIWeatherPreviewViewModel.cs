using AIWeather.Equipment;
using AIWeather.Localization;
using AIWeather.Models;
using AIWeather.Services;
using AIWeather.Views;
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
        private string _statusMessage = UiLocalization.Text("Runtime.Ready");
        private string _activityLog = UiLocalization.Text("Runtime.Initialized") + "\n";
        private AIWeatherPreviewView? _view;
        private DispatcherTimer _refreshTimer;
        private readonly SemaphoreSlim _refreshGate = new SemaphoreSlim(1, 1);
        private readonly SemaphoreSlim _replicaPreviewGate = new SemaphoreSlim(1, 1);
        private static readonly TimeSpan ReplicaPreviewRetryDelay = TimeSpan.FromMinutes(1);
        private readonly ReplicaPreviewRetryGate _replicaPreviewRetryGate = new ReplicaPreviewRetryGate();
        private CommunityToolkit.Mvvm.Input.RelayCommand? _saveImageCommand;
        private CommunityToolkit.Mvvm.Input.AsyncRelayCommand? _keepDatasetSampleCommand;
        private DatasetLabelReviewWindow? _datasetReviewWindow;
        private bool _solarSuspensionShown;
        private string _previewStreamStatusText = string.Empty;
        private bool _previewStreamRetryAvailable;

        // Capture mode tracking
        public Models.CaptureMode CurrentCaptureMode
        {
            get
            {
                var mode = Properties.Settings.Default.CaptureMode;
                return (Models.CaptureMode)mode;
            }
        }

        public bool IsClusterReplica => ClusterNodeModeParser.Parse(Properties.Settings.Default.ClusterNodeMode) == ClusterNodeMode.Replica;
        public bool IsReplicaStopControlVisible => ShouldShowReplicaStopControl(IsClusterReplica, IsConnected);
        public bool IsReplicaFailoverActive => IsClusterReplica && _safetyMonitor.IsReplicaFailoverActive;
        public bool IsReplicaFollowingPrimary => IsClusterReplica && !IsReplicaFailoverActive;
        private bool HasReplicaPreviewSource =>
            !IsClusterReplica || _safetyMonitor.HasReplicaPreviewConfiguration;
        public bool IsReplicaPreviewUnavailable => IsClusterReplica && !HasReplicaPreviewSource;

        private Models.CaptureMode PreviewCaptureMode
        {
            get
            {
                return IsClusterReplica
                       && _safetyMonitor.TryGetReplicaPreviewSource(
                           out var captureMode,
                           out _,
                           out _,
                           out _)
                    ? captureMode
                    : CurrentCaptureMode;
            }
        }

        public bool IsRtspMode => HasReplicaPreviewSource && PreviewCaptureMode == Models.CaptureMode.RTSPStream;
        public bool IsNonRtspMode => HasReplicaPreviewSource && PreviewCaptureMode != Models.CaptureMode.RTSPStream;
        public bool IsFolderMode => HasReplicaPreviewSource && PreviewCaptureMode == Models.CaptureMode.FolderWatch;
        public bool IsUrlMode => HasReplicaPreviewSource && PreviewCaptureMode != Models.CaptureMode.FolderWatch;
        public string PreviewStreamStatusText => _previewStreamStatusText;
        public bool IsPreviewStreamStatusVisible => !string.IsNullOrWhiteSpace(_previewStreamStatusText);
        public bool IsPreviewStreamRetryAvailable => _previewStreamRetryAvailable;

        internal static bool ShouldShowReplicaStopControl(bool isClusterReplica, bool isConnected)
        {
            return isClusterReplica && isConnected;
        }

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

        private static Task RunOnUiThreadAsync(Func<Task> action)
        {
            var dispatcher = UiDispatcher;
            if (dispatcher == null || dispatcher.CheckAccess())
            {
                return action();
            }

            return dispatcher.InvokeAsync(action).Task.Unwrap();
        }

        [ImportingConstructor]
        public AIWeatherPreviewViewModel(IProfileService profileService, ICameraMediator cameraMediator) : base(profileService)
        {
            // Use shared static instance to persist across navigation
            _sharedSafetyMonitor = AIWeatherSafetyMonitor.Instance;
            _safetyMonitor = _sharedSafetyMonitor;
            
            this.Title = UiLocalization.Text("Preview.Title");
            
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
            
            Logger.Info($"Initializing camera source - Mode: {captureMode}, Saved URL: '{LogRedactor.RedactRtspUrl(savedUrl)}', Protocol: '{protocol}'");
            
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

            // The username typed in the panel grid has to reach the settings the moment it
            // is entered, exactly like the password does through its PasswordChanged handler.
            // Without this it lived only on the in-memory source: the safety monitor read an
            // empty username and opened the stream unauthenticated, and the next settings
            // sync (triggered by saving the password, or by a mode change) overwrote the
            // typed text with the empty stored value - so it also vanished from the grid.
            Sources[0].PropertyChanged += PersistCredentialToSettings;

            RefreshCommand = new CommunityToolkit.Mvvm.Input.AsyncRelayCommand(async () => { await RefreshAsync(); });
            _saveImageCommand = new CommunityToolkit.Mvvm.Input.RelayCommand(SaveImage, () => HasImage);
            SaveImageCommand = _saveImageCommand;
            ConnectCommand = new CommunityToolkit.Mvvm.Input.AsyncRelayCommand(async () => { await ToggleConnectionAsync(); });
            StartStopMonitoringCommand = new CommunityToolkit.Mvvm.Input.AsyncRelayCommand(async () => { await StartStopMonitoringAsync(); });
            _keepDatasetSampleCommand = new CommunityToolkit.Mvvm.Input.AsyncRelayCommand(
                KeepDatasetSampleAsync,
                () => DatasetEnabled && _currentAnalysis != null);
            KeepDatasetSampleCommand = _keepDatasetSampleCommand;
            OpenDatasetReviewCommand = new CommunityToolkit.Mvvm.Input.RelayCommand(OpenDatasetReviewWindow);
            RetryPreviewCommand = new CommunityToolkit.Mvvm.Input.AsyncRelayCommand(RetryPreviewAsync);
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
            RaiseCapturePresentationChanged();
            RaisePropertyChanged(nameof(ReplicaConnectButtonText));
            RaisePropertyChanged(nameof(ReplicaConnectionStatusText));
            RaisePropertyChanged(nameof(ReplicaModeDescription));

            // Restore state if SafetyMonitor is already running
            RestoreMonitoringState();

            // Direct event: fires reliably when safety monitor Connect() succeeds
            _safetyMonitor.MonitoringStarted += (s, e) =>
            {
                Logger.Info("MonitoringStarted event received in ViewModel — scheduling RestoreMonitoringState");
                RunOnUiThread(() =>
                {
                    Logger.Info("MonitoringStarted: now on UI thread, calling RestoreMonitoringState");
                    RestoreMonitoringState();
                });
            };

            // Subscribe to safety monitor updates
            _safetyMonitor.PropertyChanged += async (s, e) =>
            {
                if (e.PropertyName == nameof(AIWeatherSafetyMonitor.Connected))
                {
                    bool isNowConnected = _safetyMonitor.Connected;
                    if (isNowConnected)
                    {
                        RunOnUiThread(() => 
                        { 
                            IsConnected = true;
                            RestoreMonitoringState();
                        });
                    }
                    else
                    {
                        // Stop UI components fully asynchronously if stream is running
                        var view = GetVideoView();
                        if (view != null)
                        {
                            await view.StopStreamAsync();
                        }
                        
                        RunOnUiThread(() => 
                        {
                            _refreshTimer.Stop();
                            IsConnected = false;
                            IsRunning = false;
                            StatusMessage = UiLocalization.Text("Runtime.Disconnected");
                            
                            if (Sources != null)
                            {
                                foreach (var source in Sources)
                                {
                                    source.IsRunning = false;
                                }
                            }
                            AddLog(UiLocalization.Text("Log.MonitoringStopped"));
                        });
                    }
                }
                else if (e.PropertyName == nameof(AIWeatherSafetyMonitor.IsSafe)
                         || e.PropertyName == nameof(AIWeatherSafetyMonitor.IsSolarAltitudeSuspended)
                         || e.PropertyName == nameof(AIWeatherSafetyMonitor.CurrentSunAltitude)
                         || e.PropertyName == nameof(AIWeatherSafetyMonitor.SunAltitudeLimitDegrees)
                         || e.PropertyName == nameof(AIWeatherSafetyMonitor.ReplicaConnectionSummary)
                         || e.PropertyName == nameof(AIWeatherSafetyMonitor.IsReplicaFailoverActive)
                         || e.PropertyName == nameof(AIWeatherSafetyMonitor.HasReplicaPreviewConfiguration))
                {
                    // Weather check completed — update UI with latest results
                    if (_safetyMonitor.Connected)
                    {
                        // Ensure monitoring state is set if we haven't done so yet
                        if (!IsRunning)
                        {
                            Logger.Info("IsSafe changed while not IsRunning — calling RestoreMonitoringState");
                            RunOnUiThread(() => RestoreMonitoringState());
                        }

                        await RunOnUiThreadAsync(async () =>
                        {
                            RaiseCapturePresentationChanged();
                            if (e.PropertyName == nameof(AIWeatherSafetyMonitor.IsReplicaFailoverActive)
                                || e.PropertyName == nameof(AIWeatherSafetyMonitor.HasReplicaPreviewConfiguration))
                            {
                                await SynchronizeReplicaPreviewAsync();
                            }
                            await UpdateFromLatestResultAsync(
                                loadImage: !IsClusterReplica || _safetyMonitor.IsReplicaFailoverActive);
                        });
                    }
                }
                else if (e.PropertyName == nameof(AIWeatherSafetyMonitor.DatasetStatus)
                         || e.PropertyName == nameof(AIWeatherSafetyMonitor.DatasetStatusText))
                {
                    RunOnUiThread(() =>
                    {
                        RaisePropertyChanged(nameof(DatasetStatusText));
                        RaisePropertyChanged(nameof(DatasetEnabled));
                        _keepDatasetSampleCommand?.NotifyCanExecuteChanged();
                    });
                }
            };

            Properties.Settings.Default.PropertyChanged += (s, e) =>
            {
                if (e.PropertyName == nameof(Properties.Settings.Default.AnalysisProvider)
                    || e.PropertyName == nameof(Properties.Settings.Default.SelectedModel)
                    || e.PropertyName == nameof(Properties.Settings.Default.CheckIntervalMinutes)
                    || e.PropertyName == nameof(Properties.Settings.Default.UseSunAltitudeLimit)
                    || e.PropertyName == nameof(Properties.Settings.Default.SunAltitudeLimitDegrees)
                    || e.PropertyName == nameof(Properties.Settings.Default.CloudCoverageThreshold)
                    || e.PropertyName == nameof(Properties.Settings.Default.UseGitHubModels)
                    || e.PropertyName == nameof(Properties.Settings.Default.CaptureMode)
                    || e.PropertyName == nameof(Properties.Settings.Default.RtspUrl)
                    || e.PropertyName == nameof(Properties.Settings.Default.INDIDeviceName)
                    || e.PropertyName == nameof(Properties.Settings.Default.FolderPath)
                    || e.PropertyName == nameof(Properties.Settings.Default.RtspUsername)
                    || e.PropertyName == nameof(Properties.Settings.Default.RtspPassword)
                    || e.PropertyName == nameof(Properties.Settings.Default.ClusterNodeMode)
                    || (!string.IsNullOrWhiteSpace(e.PropertyName)
                        && e.PropertyName.StartsWith("Dataset", StringComparison.Ordinal)))
                {
                    RunOnUiThread(() =>
                    {
                        if (e.PropertyName == nameof(Properties.Settings.Default.CheckIntervalMinutes))
                        {
                            ApplyRefreshIntervalFromSettings();
                        }
                        if (e.PropertyName == nameof(Properties.Settings.Default.CaptureMode))
                        {
                            RaiseCapturePresentationChanged();

                            // Mode changes should immediately reflect in the panel. Also, if something
                            // is currently running (RTSP preview or periodic monitoring), stop it so the
                            // user can switch cleanly.
                            _ = HandleCaptureModeChangedAsync();
                        }
                        else if (e.PropertyName == nameof(Properties.Settings.Default.ClusterNodeMode))
                        {
                            RaiseCapturePresentationChanged();
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
                        RaisePropertyChanged(nameof(DatasetStatusText));
                        RaisePropertyChanged(nameof(DatasetEnabled));
                        _keepDatasetSampleCommand?.NotifyCanExecuteChanged();
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

        /// <summary>
        /// Re-synchronises all capture-mode-dependent UI properties from the current
        /// in-memory settings.  Called from the view's Loaded handler so the panel always
        /// reflects the latest Options page state when the user navigates to it.
        /// </summary>
        public void SyncCaptureMode()
        {
            RaiseCapturePresentationChanged();
            SyncPrimarySourceFromSettings();
        }

        private void RaiseCapturePresentationChanged()
        {
            RaisePropertyChanged(nameof(CurrentCaptureMode));
            RaisePropertyChanged(nameof(IsClusterReplica));
            RaisePropertyChanged(nameof(IsReplicaStopControlVisible));
            RaisePropertyChanged(nameof(IsReplicaFailoverActive));
            RaisePropertyChanged(nameof(IsReplicaFollowingPrimary));
            RaisePropertyChanged(nameof(IsReplicaPreviewUnavailable));
            RaisePropertyChanged(nameof(IsRtspMode));
            RaisePropertyChanged(nameof(IsNonRtspMode));
            RaisePropertyChanged(nameof(IsFolderMode));
            RaisePropertyChanged(nameof(IsUrlMode));
            RaisePropertyChanged(nameof(ReplicaConnectButtonText));
            RaisePropertyChanged(nameof(ReplicaConnectionStatusText));
            RaisePropertyChanged(nameof(ReplicaModeDescription));
        }

        /// <summary>
        /// Every replica terminal keeps one local preview stream in both follower and
        /// takeover states.  Follower mode still consumes only the primary safety verdict;
        /// takeover merely starts reusing this terminal's existing preview frames for local
        /// analysis.  Primary recovery stops the local verdict, not the preview stream.
        /// </summary>
        public async Task SynchronizeReplicaPreviewAsync(bool forceRestart = false)
        {
            if (!IsClusterReplica || _view == null)
            {
                return;
            }

            await _replicaPreviewGate.WaitAsync();
            try
            {
                RaiseCapturePresentationChanged();
                if (!_safetyMonitor.TryGetReplicaPreviewSource(
                        out var captureMode,
                        out var source,
                        out var username,
                        out var password))
                {
                    _replicaPreviewRetryGate.Clear();
                    await _view.StopStreamAsync();
                    CurrentImage = null;
                    return;
                }

                if (captureMode == Models.CaptureMode.RTSPStream)
                {
                    var sourceIdentity = SharedRtspPreviewFrameProvider.CreateSourceIdentity(source);
                    if (!_replicaPreviewRetryGate.ShouldAttempt(
                            sourceIdentity,
                            DateTime.UtcNow,
                            forceRestart))
                    {
                        Logger.Debug(
                            $"Replica RTSP preview retry suppressed until " +
                            $"{_replicaPreviewRetryGate.RetryAfterUtc:O} after the previous connection failure");
                        return;
                    }

                    Logger.Info(
                        $"Replica terminal is starting its synchronized RTSP preview: " +
                        $"{LogRedactor.RedactRtspUrl(source)}");
                    await _view.StartStreamAsync(
                        source,
                        username,
                        password,
                        forceRestart: forceRestart);

                    if (_view.IsPreviewStreamHealthy)
                    {
                        _replicaPreviewRetryGate.Clear();
                    }
                    else
                    {
                        _replicaPreviewRetryGate.RecordFailure(
                            sourceIdentity,
                            DateTime.UtcNow,
                            ReplicaPreviewRetryDelay);
                        Logger.Warning(
                            $"Replica RTSP preview connection failed; automatic retries for this " +
                            $"source are paused until {_replicaPreviewRetryGate.RetryAfterUtc:O}. The manual " +
                            $"retry button bypasses this delay.");
                    }
                    return;
                }

                await _view.StopStreamAsync();
                if (IsReplicaFailoverActive)
                {
                    await UpdateFromLatestResultAsync(loadImage: true);
                }
                else
                {
                    CurrentImage = null;
                }
            }
            finally
            {
                _replicaPreviewGate.Release();
            }
        }

        /// <summary>
        /// Mirrors the primary source's credentials into the plugin settings as they are
        /// edited. The settings are what the safety monitor reads when it connects from the
        /// Equipment tab, which is a different path from the panel's own Connect button.
        /// Writing the same value back is harmless: CameraSource only raises a change when
        /// the value actually differs, so the settings sync cannot loop.
        /// </summary>
        private void PersistCredentialToSettings(object? sender, System.ComponentModel.PropertyChangedEventArgs e)
        {
            if (sender is not CameraSource source)
            {
                return;
            }

            try
            {
                if (e.PropertyName == nameof(CameraSource.Username))
                {
                    var value = source.Username ?? string.Empty;
                    if (!string.Equals(Properties.Settings.Default.RtspUsername ?? string.Empty, value))
                    {
                        Properties.Settings.Default.RtspUsername = value;
                        CoreUtil.SaveSettings(Properties.Settings.Default);
                    }
                }
                else if (e.PropertyName == nameof(CameraSource.Password))
                {
                    var value = source.Password ?? string.Empty;
                    if (!string.Equals(Properties.Settings.Default.RtspPassword ?? string.Empty, value))
                    {
                        Properties.Settings.Default.RtspPassword = value;
                        CoreUtil.SaveSettings(Properties.Settings.Default);
                    }
                }
            }
            catch (Exception ex)
            {
                Logger.Error($"Failed to persist camera credentials from the panel: {ex.Message}");
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
        public ICommand KeepDatasetSampleCommand { get; }
        public ICommand OpenDatasetReviewCommand { get; }
        public ICommand RetryPreviewCommand { get; }

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

        public string ReplicaConnectButtonText => IsConnected
            ? UiLocalization.Text("Preview.DisconnectPrimary")
            : UiLocalization.Text("Preview.ConnectPrimary");

        public string ReplicaConnectionStatusText => IsConnected
            ? _safetyMonitor.ReplicaConnectionSummary
            : UiLocalization.Text("Preview.ReplicaNotStarted");

        public string ReplicaModeDescription => _safetyMonitor.IsReplicaFailoverActive
            ? UiLocalization.Text("Preview.ReplicaFailover")
            : UiLocalization.Text("Preview.ReplicaNoVideo");

        internal void SetPreviewStreamStatus(string? text, bool retryAvailable = false)
        {
            _previewStreamStatusText = text ?? string.Empty;
            _previewStreamRetryAvailable = retryAvailable;
            RaisePropertyChanged(nameof(PreviewStreamStatusText));
            RaisePropertyChanged(nameof(IsPreviewStreamStatusVisible));
            RaisePropertyChanged(nameof(IsPreviewStreamRetryAvailable));
        }

        private async Task RetryPreviewAsync()
        {
            SetPreviewStreamStatus(UiLocalization.Text("Preview.VideoConnecting"));
            if (IsClusterReplica)
            {
                await SynchronizeReplicaPreviewAsync(forceRestart: true);
                return;
            }

            var source = Sources?.FirstOrDefault(candidate => candidate.IsRunning)
                         ?? Sources?.FirstOrDefault();
            if (_view != null
                && source != null
                && CurrentCaptureMode == Models.CaptureMode.RTSPStream)
            {
                await _view.StartStreamAsync(
                    source.FullUrl,
                    source.Username,
                    source.Password,
                    forceRestart: true);
            }
        }

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
                RaisePropertyChanged(nameof(ConnectButtonText));
                RaisePropertyChanged(nameof(ReplicaConnectButtonText));
                RaisePropertyChanged(nameof(ReplicaConnectionStatusText));
                RaisePropertyChanged(nameof(ReplicaModeDescription));
                RaisePropertyChanged(nameof(IsReplicaStopControlVisible));
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

        public string ConnectionStatus => IsConnected
            ? UiLocalization.Text("Runtime.Connected")
            : UiLocalization.Text("Runtime.Disconnected");

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
        public string SafetyStatus => IsSafe
            ? "✅ " + UiLocalization.Text("Preview.Safe")
            : "⛔ " + UiLocalization.Text("Preview.Unsafe");

        /// <summary>
        /// One line saying *why* the monitor reports what it reports - a cloudy sky, a
        /// camera that stopped delivering, an unreachable external device. Without it an
        /// UNSAFE on a visibly clear night is indistinguishable from a broken pipeline.
        /// </summary>
        public string SafetyReason => _safetyMonitor?.SafetyStateReason ?? string.Empty;
        public string WeatherCondition => _currentAnalysis == null
            ? UiLocalization.Text("Common.Unknown")
            : UiLocalization.Condition(_currentAnalysis.Condition);
        public double CloudCoverage => _currentAnalysis?.CloudCoverage ?? 0;
        public double Confidence => _currentAnalysis?.Confidence ?? 0;
        public double HighThreshold => Properties.Settings.Default.CloudCoverageThreshold;
        public double LowThreshold => Properties.Settings.Default.CloudCoverageSafeThreshold;
        public bool RainDetected => _currentAnalysis?.RainDetected ?? false;
        public bool FogDetected => _currentAnalysis?.FogDetected ?? false;
        public string Description => _currentAnalysis == null
            ? (_safetyMonitor.IsSolarAltitudeSuspended
                ? _safetyMonitor.SafetyStateReason
                : UiLocalization.Text("Runtime.NoAnalysis"))
            : UiLocalization.AnalysisDescription(_currentAnalysis, Properties.Settings.Default.AnalysisProvider);
        public string AnalysisSourceSummary
        {
            get
            {
                if (_currentAnalysis == null)
                {
                    return UiLocalization.Text(
                        _safetyMonitor.IsSolarAltitudeSuspended
                            ? "Runtime.SourceSolarSuspended"
                            : "Runtime.SourceWaiting");
                }

                var provenance = _currentAnalysis.Provenance;
                var fallback = provenance.IsFallback
                    ? UiLocalization.FallbackStatus(provenance)
                    : string.Empty;
                return UiLocalization.Text("Runtime.Source", provenance.Provider, provenance.Model, fallback);
            }
        }

        public bool DatasetEnabled => !IsClusterReplica && Properties.Settings.Default.DatasetEnabled;
        public string ProviderHealth => _safetyMonitor.ProviderHealth;

        public string DatasetStatusText => _safetyMonitor.DatasetStatusText;
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

                return UiLocalization.Text("Runtime.LocalProcessing");
            }
        }

        public string AiSettingsSummary
        {
            get
            {
                if (IsClusterReplica)
                {
                    return UiLocalization.Text("Cluster.ReplicaSettings", _safetyMonitor.ReplicaConnectionSummary);
                }

                var provider = Properties.Settings.Default.AnalysisProvider;
                if (string.IsNullOrWhiteSpace(provider))
                {
                    provider = Properties.Settings.Default.UseGitHubModels ? "GitHubModels" : "Local";
                }

                var model = Properties.Settings.Default.SelectedModel;
                var intervalMinutes = GetCheckIntervalMinutesClamped();
                var highThreshold = Properties.Settings.Default.CloudCoverageThreshold;
                var lowThreshold = Properties.Settings.Default.CloudCoverageSafeThreshold;

                var aiLabel = string.IsNullOrWhiteSpace(model)
                    ? provider
                    : $"{provider} - {model}";

                var summary = UiLocalization.Text(
                    "Runtime.AiSettings",
                    aiLabel,
                    intervalMinutes,
                    highThreshold,
                    lowThreshold);
                return Properties.Settings.Default.UseSunAltitudeLimit
                    ? summary + UiLocalization.Text(
                        "Runtime.SunLimitSummary",
                        SolarAltitudeGuard.NormalizeLimit(Properties.Settings.Default.SunAltitudeLimitDegrees))
                    : summary;
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
                Logger.Info("RestoreMonitoringState: SafetyMonitor is connected, restoring UI state");
                AddLog(UiLocalization.Text("Log.StateRestored"));

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
                if (IsClusterReplica)
                {
                    _ = SynchronizeReplicaPreviewAsync();
                }

                // Try to show cached results immediately, then wait for periodic check to update via IsSafe
                var result = _safetyMonitor.GetLatestResult();
                if (result != null)
                {
                    Logger.Info("RestoreMonitoringState: cached result available, updating display");
                    _ = UpdateFromLatestResultAsync(
                        loadImage: !IsClusterReplica || _safetyMonitor.IsReplicaFailoverActive);
                    StatusMessage = UiLocalization.Text("Runtime.MonitoringActive");
                }
                else
                {
                    Logger.Info("RestoreMonitoringState: no cached result yet — will update when first check completes");
                    StatusMessage = UiLocalization.Text("Runtime.WaitingAnalysis");
                    AddLog(UiLocalization.Text("Log.MonitoringFirst"));
                }
            }
            else
            {
                Logger.Info("RestoreMonitoringState: SafetyMonitor not connected, skipping");
            }
        }

        private async Task<bool> ToggleConnectionAsync()
        {
            try
            {
                if (IsConnected)
                {
                    // Disconnect
                    AddLog(UiLocalization.Text(IsClusterReplica
                        ? "Log.ReplicaDisconnecting"
                        : "Log.DisconnectingRtsp"));
                    _safetyMonitor.Disconnect();
                    IsConnected = false;
                    StatusMessage = UiLocalization.Text("Runtime.Disconnected");
                    CurrentImage = null;
                    AddLog(UiLocalization.Text("Log.Disconnected"));
                }
                else
                {
                    // Connect
                    if (!IsClusterReplica && string.IsNullOrWhiteSpace(RtspUrl))
                    {
                        AddLog(UiLocalization.Text("Log.RtspRequired"));
                        StatusMessage = UiLocalization.Text("Runtime.RtspRequired");
                        return false;
                    }

                    if (IsClusterReplica)
                    {
                        CurrentImage = null;
                        AddLog(UiLocalization.Text("Log.ReplicaConnecting"));
                    }
                    else
                    {
                        AddLog(UiLocalization.Text("Log.Connecting", RtspUrl));
                    }
                    StatusMessage = UiLocalization.Text("Runtime.Connecting");
                    var connected = await _safetyMonitor.Connect(CancellationToken.None);
                    
                    if (connected)
                    {
                        IsConnected = true;
                        StatusMessage = UiLocalization.Text("Runtime.Connected");
                        AddLog(UiLocalization.Text(IsClusterReplica
                            ? "Log.ReplicaConnected"
                            : "Log.Connected"));

                        // Do not force an immediate check here; the safety monitor already starts its periodic
                        // monitoring (with an initial check). We'll just sync UI from whatever is available.
                        await UpdateFromLatestResultAsync(
                            loadImage: !IsClusterReplica || _safetyMonitor.IsReplicaFailoverActive);
                    }
                    else
                    {
                        AddLog(UiLocalization.Text(IsClusterReplica
                            ? "Log.ReplicaConnectionFailed"
                            : "Log.ConnectionFailed"));
                        StatusMessage = UiLocalization.Text("Runtime.ConnectionFailed");
                        return false;
                    }
                }

                RaisePropertyChanged(nameof(ConnectButtonText));
                RaisePropertyChanged(nameof(ReplicaConnectButtonText));
                RaisePropertyChanged(nameof(ReplicaConnectionStatusText));
                return true;
            }
            catch (Exception ex)
            {
                AddLog(UiLocalization.Text("Log.ConnectionError", ex.Message));
                Logger.Error($"Connection error: {ex.Message}", ex);
                IsConnected = false;
                StatusMessage = UiLocalization.Text("Runtime.ConnectionError");
                RaisePropertyChanged(nameof(ConnectButtonText));
                RaisePropertyChanged(nameof(ReplicaConnectButtonText));
                RaisePropertyChanged(nameof(ReplicaConnectionStatusText));
                return false;
            }
        }

        private async Task UpdateFromLatestResultAsync(bool loadImage)
        {
            var result = _safetyMonitor.GetLatestResult();
            if (result == null)
            {
                if (_safetyMonitor.IsSolarAltitudeSuspended)
                {
                    _currentAnalysis = null;
                    LastUpdate = DateTime.Now;
                    StatusMessage = UiLocalization.Text("Runtime.SolarSuspendedShort");
                    if (!_solarSuspensionShown)
                    {
                        if (_safetyMonitor.CurrentSunAltitude is double sunAltitude)
                        {
                            AddLog(UiLocalization.Text(
                                "Log.SolarSuspended",
                                sunAltitude,
                                _safetyMonitor.SunAltitudeLimitDegrees));
                        }
                        else
                        {
                            AddLog(UiLocalization.Text("Log.SolarUnavailable"));
                        }
                        _solarSuspensionShown = true;
                    }
                    RaiseAllAnalysisProperties();
                }
                Logger.Debug("UpdateFromLatestResultAsync: No result available from SafetyMonitor");
                return;
            }

            if (_solarSuspensionShown)
            {
                AddLog(UiLocalization.Text("Log.SolarResumed"));
                _solarSuspensionShown = false;
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
                    AddLog(UiLocalization.Text("Log.RefreshingPreview"));
                StatusMessage = UiLocalization.Text("Runtime.Capturing");

                if (!_safetyMonitor.Connected)
                {
                    AddLog(UiLocalization.Text("Log.CameraNotConnected"));
                    StatusMessage = UiLocalization.Text("Runtime.CameraNotConnected");
                    return false;
                }

                // Force a weather check
                var result = await _safetyMonitor.ForceCheckAsync();
                
                if (result == null)
                {
                    if (_safetyMonitor.IsSolarAltitudeSuspended)
                    {
                        await UpdateFromLatestResultAsync(loadImage: false);
                        return true;
                    }
                    AddLog(UiLocalization.Text("Log.CaptureFailed"));
                    if (CurrentCaptureMode == Models.CaptureMode.RTSPStream)
                    {
                        AddLog(UiLocalization.Text("Log.CaptureTip"));
                    }
                    StatusMessage = UiLocalization.Text("Runtime.CaptureFailed");
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
                    AddLog(UiLocalization.Text("Log.CaptureSuccess"));
                    AddLog(UiLocalization.Text(
                        "Log.Analysis",
                        UiLocalization.Condition(result.Condition),
                        result.CloudCoverage));
                    if (result.RainDetected) AddLog(UiLocalization.Text("Log.Rain"));
                    if (result.FogDetected) AddLog(UiLocalization.Text("Log.Fog"));
                    AddLog(UiLocalization.Text(
                        "Log.Status",
                        result.IsSafeForImaging
                            ? UiLocalization.Text("Preview.Safe") + " ✓"
                            : UiLocalization.Text("Preview.Unsafe") + " ⛔"));
                    StatusMessage = UiLocalization.Text("Runtime.AnalysisComplete");
                }
                else
                {
                    Logger.Warning($"Image file not found. Looking in temp path, found: {imagePath ?? "null"}");
                    AddLog(UiLocalization.Text("Log.ImageMissing"));
                    StatusMessage = UiLocalization.Text("Runtime.ImageMissing");
                }

                // Refresh all analysis properties
                RaiseAllAnalysisProperties();

                return true;
            }
            catch (Exception ex)
            {
                AddLog(UiLocalization.Text("Log.Error", ex.Message));
                Logger.Error($"Error refreshing preview: {ex.Message}", ex);
                StatusMessage = UiLocalization.Text("Runtime.Error", ex.Message);
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
                
                AddLog(UiLocalization.Text("Log.MonitoringStopped"));
                StatusMessage = UiLocalization.Text("Runtime.MonitoringStopped");
            }
            else
            {
                // Get the first source for configuration
                var source = Sources.FirstOrDefault();
                if (source == null)
                {
                    AddLog(UiLocalization.Text("Log.NoSource"));
                    return;
                }
                
                Logger.Info($"Starting monitoring - Total sources: {Sources.Count}, Source URL: {LogRedactor.RedactRtspUrl(source.FullUrl)}, Source IsRunning (before): {source.IsRunning}");
                
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
                
                SaveNonEmptySourceCredentials(source);
                CoreUtil.SaveSettings(Properties.Settings.Default);
                
                // Start monitoring
                AddLog(UiLocalization.Text("Log.MonitoringStarting"));
                StatusMessage = UiLocalization.Text("Runtime.Connecting");
                
                var connected = await _safetyMonitor.Connect(CancellationToken.None);
                if (connected)
                {
                    IsConnected = true;
                    IsRunning = true;
                    source.IsRunning = true;
                    
                    Logger.Info($"Monitoring started - IsRunning set to true for source. Mode: {captureMode}");
                    AddLog(UiLocalization.Text("Log.MonitoringStarted", GetCheckIntervalMinutesClamped()));
                    StatusMessage = UiLocalization.Text("Runtime.InitialCapture");
                    
                    // Do initial capture immediately
                    await RefreshAsync();
                    
                    // Start periodic refresh timer for subsequent captures
                    // The timer will call UpdateFromLatestResultAsync which fetches the latest analysis result
                    ApplyRefreshIntervalFromSettings();
                    var intervalMinutes = GetCheckIntervalMinutesClamped();
                    Logger.Info($"Starting refresh timer with {intervalMinutes} minute interval");
                    _refreshTimer.Start();
                    AddLog(UiLocalization.Text("Log.MonitoringActive", intervalMinutes));
                    
                    StatusMessage = UiLocalization.Text("Runtime.MonitoringActive");
                }
                else
                {
                    AddLog(UiLocalization.Text("Log.ConnectFailed"));
                    StatusMessage = UiLocalization.Text("Runtime.ConnectionFailed");
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

                    AddLog(UiLocalization.Text("Log.ImageSaved", Path.GetFileName(dialog.FileName)));
                    StatusMessage = UiLocalization.Text("Runtime.ImageSaved", dialog.FileName);
                    Logger.Info($"Image saved to: {dialog.FileName}");
                }
            }
            catch (Exception ex)
            {
                AddLog(UiLocalization.Text("Log.ImageSaveFailed", ex.Message));
                Logger.Error($"Error saving image: {ex.Message}", ex);
                StatusMessage = UiLocalization.Text("Runtime.ImageSaveError", ex.Message);
            }
        }

        private void RaiseAllAnalysisProperties()
        {
            RaisePropertyChanged(nameof(IsSafe));
            RaisePropertyChanged(nameof(SafetyStatus));
            RaisePropertyChanged(nameof(SafetyReason));
            RaisePropertyChanged(nameof(WeatherCondition));
            RaisePropertyChanged(nameof(CloudCoverage));
            RaisePropertyChanged(nameof(HighThreshold));
            RaisePropertyChanged(nameof(LowThreshold));
            RaisePropertyChanged(nameof(Confidence));
            RaisePropertyChanged(nameof(RainDetected));
            RaisePropertyChanged(nameof(FogDetected));
            RaisePropertyChanged(nameof(Description));
            RaisePropertyChanged(nameof(AnalysisSourceSummary));
            RaisePropertyChanged(nameof(ProviderHealth));
            RaisePropertyChanged(nameof(DatasetStatusText));
            RaisePropertyChanged(nameof(DatasetEnabled));
            RaisePropertyChanged(nameof(CaptureTimestamp));
            RaisePropertyChanged(nameof(LastUpdate));
            RaisePropertyChanged(nameof(AnalysisMethod));
            RaisePropertyChanged(nameof(AiSettingsSummary));
            RaisePropertyChanged(nameof(ReplicaConnectionStatusText));
            RaisePropertyChanged(nameof(ReplicaModeDescription));
            _keepDatasetSampleCommand?.NotifyCanExecuteChanged();
        }

        private async Task KeepDatasetSampleAsync()
        {
            try
            {
                var queued = await _safetyMonitor.KeepLatestFrameForReviewAsync();
                AddLog(UiLocalization.Text(queued ? "Log.FrameQueued" : "Log.FrameNotQueued"));
                RaisePropertyChanged(nameof(DatasetStatusText));
            }
            catch (Exception ex)
            {
                AddLog(UiLocalization.Text("Log.QueueFailed", ex.Message));
                Logger.Error($"Could not queue current dataset review sample: {ex.Message}", ex);
            }
        }

        private void OpenDatasetReviewWindow()
        {
            try
            {
                if (_datasetReviewWindow?.IsLoaded == true)
                {
                    if (_datasetReviewWindow.WindowState == WindowState.Minimized)
                    {
                        _datasetReviewWindow.WindowState = WindowState.Normal;
                    }
                    _datasetReviewWindow.Activate();
                    return;
                }

                var root = DatasetRecorderOptions.FromSettings().RootDirectory;
                _datasetReviewWindow = new DatasetLabelReviewWindow(root)
                {
                    Owner = Application.Current?.MainWindow
                };
                _datasetReviewWindow.Closed += (_, _) => _datasetReviewWindow = null;
                _datasetReviewWindow.Show();
                AddLog(UiLocalization.Text("Log.ReviewerOpened"));
            }
            catch (Exception ex)
            {
                AddLog(UiLocalization.Text("Log.ReviewerOpenFailed", ex.Message));
                Logger.Error($"Could not open dataset label reviewer: {ex.Message}", ex);
            }
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
            // A new row starts with the credentials already saved: the camera account does
            // not change because the address did, and a row born empty used to be saved
            // over the good credentials the moment it was started.
            var newSource = new CameraSource
            {
                Protocol = "rtsp://",
                MediaUrl = "",
                Username = Properties.Settings.Default.RtspUsername ?? "",
                Password = Properties.Settings.Default.RtspPassword ?? ""
            };
            Sources.Add(newSource);
            AddLog(UiLocalization.Text("Log.NewSource"));
        }

        private void DeleteSource(CameraSource source)
        {
            if (source != null && Sources.Contains(source))
            {
                if (source.IsRunning)
                {
                    AddLog(UiLocalization.Text("Log.DeleteRunning", LogRedactor.RedactRtspUrl(source.FullUrl)));
                    return;
                }
                
                Sources.Remove(source);
                AddLog(UiLocalization.Text("Log.SourceRemoved", LogRedactor.RedactRtspUrl(source.FullUrl)));
            }
        }

        private static void SaveNonEmptySourceCredentials(CameraSource source)
        {
            // Starting an empty/new row is not a request to erase a working account.
            // Explicit primary-row edits still use PersistCredentialToSettings to clear it.
            Properties.Settings.Default.RtspUsername = CameraCredentialPolicy.ForStart(
                source.Username, Properties.Settings.Default.RtspUsername);
            Properties.Settings.Default.RtspPassword = CameraCredentialPolicy.ForStart(
                source.Password, Properties.Settings.Default.RtspPassword);
        }

        private async Task<bool> ToggleStreamAsync(CameraSource source)
        {
            if (source == null) return false;

            try
            {
                if (source.IsRunning)
                {
                    // Stop stream
                    AddLog(UiLocalization.Text("Log.StoppingStream", LogRedactor.RedactRtspUrl(source.FullUrl)));
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
                    AddLog(UiLocalization.Text("Log.StreamStopped", LogRedactor.RedactRtspUrl(source.FullUrl)));
                    StatusMessage = UiLocalization.Text("Runtime.StreamStopped");
                    return true;
                }
                else
                {
                    // Start stream
                    if (string.IsNullOrWhiteSpace(source.MediaUrl))
                    {
                        AddLog(UiLocalization.Text("Log.MediaRequired", source.Protocol));
                        return false;
                    }

                    Logger.Info($"Stream start - Protocol: '{source.Protocol}', FullUrl: '{LogRedactor.RedactRtspUrl(source.FullUrl)}'");
                    AddLog(UiLocalization.Text("Log.StartingStream", LogRedactor.RedactRtspUrl(source.FullUrl)));
                    source.IsLoading = true;
                    StatusMessage = UiLocalization.Text("Runtime.StreamConnecting");

                    // Update settings with this source's details
                    Properties.Settings.Default.RtspUrl = source.FullUrl;
                    SaveNonEmptySourceCredentials(source);
                    CoreUtil.SaveSettings(Properties.Settings.Default);

                    try
                    {
                        if (Uri.TryCreate(source.FullUrl, UriKind.Absolute, out var uri)
                            && (string.IsNullOrWhiteSpace(uri.AbsolutePath) || uri.AbsolutePath == "/"))
                        {
                            AddLog(UiLocalization.Text("Log.MissingPath"));
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
                        Logger.Info($"Calling StartStream - URL: {LogRedactor.RedactRtspUrl(source.FullUrl)}, Authentication: {(string.IsNullOrWhiteSpace(source.Username) ? "embedded or not configured" : "configured separately")}");
                        await view.StartStreamAsync(source.FullUrl, source.Username, source.Password);
                        
                        source.IsRunning = true;
                        source.IsLoading = false;
                        StatusMessage = UiLocalization.Text("Runtime.StreamActive");
                        AddLog(UiLocalization.Text("Log.StreamStarted", LogRedactor.RedactRtspUrl(source.FullUrl)));
                        
                        // Also connect safety monitor for analysis
                        AddLog(UiLocalization.Text("Log.ConnectingAnalysis"));
                        Logger.Info($"Attempting to connect safety monitor for AI analysis. Current mode: {CurrentCaptureMode}");
                        var connected = await _safetyMonitor.Connect(CancellationToken.None);
                        Logger.Info($"Safety monitor Connect() returned: {connected}");
                        if (connected)
                        {
                            IsConnected = true;
                            var intervalMinutes = GetCheckIntervalMinutesClamped();
                            AddLog(UiLocalization.Text("Log.AnalysisConnected"));
                            AddLog(UiLocalization.Text("Log.AnalysisSchedule", intervalMinutes));

                            // Connect() starts the periodic timer with an immediate first
                            // check. Do not queue a second ForceCheckAsync here: live testing
                            // showed two back-to-back Gemini calls on every RTSP start, wasting
                            // quota and making rate-limit failures much more likely.
                            StatusMessage = UiLocalization.Text("Runtime.WaitingAnalysis");

                            // Start UI refresh timer to display latest results
                            ApplyRefreshIntervalFromSettings();
                            _refreshTimer.Start();
                            Logger.Info($"UI refresh timer started with {intervalMinutes} minute interval");
                        }
                        else
                        {
                            IsConnected = false;
                            AddLog(UiLocalization.Text("Log.AnalysisNotConnected"));
                        }
                        
                        return true;
                    }
                    else
                    {
                        source.IsLoading = false;
                        AddLog(UiLocalization.Text("Log.VideoNotInitialized"));
                        StatusMessage = UiLocalization.Text("Runtime.VideoError");
                        return false;
                    }
                }
            }
            catch (Exception ex)
            {
                source.IsLoading = false;
                source.IsRunning = false;
                AddLog(UiLocalization.Text("Log.StreamError", ex.Message));
                Logger.Error($"Stream toggle error for {LogRedactor.RedactRtspUrl(source.FullUrl)}: {ex.Message}", ex);
                StatusMessage = UiLocalization.Text("Runtime.Error", ex.Message);
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
            AddLog(UiLocalization.Text("Log.ViewInitialized"));

            Logger.Info($"SetView called - IsRunning: {IsRunning}, Sources.Count: {Sources.Count}, CurrentCaptureMode: {CurrentCaptureMode}, IsNonRtspMode: {IsNonRtspMode}");

            if (IsClusterReplica)
            {
                _ = SynchronizeReplicaPreviewAsync();
                if (!IsRunning && _safetyMonitor.Connected)
                {
                    RestoreMonitoringState();
                }
                return;
            }

            // If we're restoring state, handle mode-specific UI updates
            if (IsRunning && Sources.Count > 0)
            {
                var source = Sources[0];
                Logger.Info($"Source state - IsRunning: {source.IsRunning}, FullUrl: '{LogRedactor.RedactRtspUrl(source.FullUrl)}', CaptureMode: {source.CaptureMode}");

                if (IsRtspMode && source.IsRunning && !string.IsNullOrWhiteSpace(source.FullUrl))
                {
                    // RTSP mode: restart the video stream
                    AddLog(UiLocalization.Text("Log.RestartingStream"));
                    _view.StartStream(source.FullUrl, source.Username, source.Password);
                }
                else if (IsNonRtspMode)
                {
                    // HTTP and Folder modes: restore last image and results
                    AddLog(UiLocalization.Text("Log.RestoringMode", CurrentCaptureMode));
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
                // Safety monitor may have connected before this view was opened.
                // Check and restore monitoring state now.
                if (_safetyMonitor.Connected)
                {
                    RestoreMonitoringState();
                }
            }
        }
    }
}
