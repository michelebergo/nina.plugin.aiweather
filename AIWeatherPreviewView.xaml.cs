using System;
using System.ComponentModel.Composition;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Threading;
using LibVLCSharp.Shared;
using NINA.Core.Utility;
using AIWeather.Views;
using AIWeather.Models;
using AIWeather.Services;
using AIWeather.Localization;
using System.Windows.Controls.Primitives;
using MediaColor = System.Windows.Media.Color;
using SolidColorBrush = System.Windows.Media.SolidColorBrush;
using Bitmap = System.Drawing.Bitmap;
using DrawingImage = System.Drawing.Image;

namespace AIWeather
{
    /// <summary>
    /// Interaction logic for AIWeatherPreviewView.xaml
    /// </summary>
    [Export(typeof(AIWeatherPreviewView))]
    [System.Runtime.Versioning.SupportedOSPlatform("windows")]
    public partial class AIWeatherPreviewView : UserControl
    {
        private LibVLC? _libVLC;
        private VideoHwndHost? _videoHost;
        private bool _isStartingStream;
        private readonly SemaphoreSlim _streamGate = new SemaphoreSlim(1, 1);
        private CancellationTokenSource? _startCts;
        private Media? _currentMedia;
        private string? _activePlaybackUrl;
        private VideoHostLayout? _lastLoggedVideoLayout;
        private bool _videoSurfaceReady;
        private double _decodedVideoWidth;
        private double _decodedVideoHeight;
        private int _voutObserved;
        private readonly RtspPreviewHealthMonitor _previewHealthMonitor = new RtspPreviewHealthMonitor();
        private readonly RtspPreviewSurfaceRecovery _surfaceRecovery = new RtspPreviewSurfaceRecovery();
        private int _previewRecoveryScheduled;
        private int _surfaceRecoveryScheduled;
        private int _previewUnhealthy;
        private IDisposable? _sharedPreviewFrameRegistration;

        internal bool IsPreviewStreamHealthy =>
            _videoHost?.Player?.IsPlaying == true
            && _videoSurfaceReady
            && Volatile.Read(ref _previewUnhealthy) == 0;

        public AIWeatherPreviewView()
        {
            InitializeComponent();
            InitializeVLC();
            
            // Subscribe to Unloaded event for cleanup
            this.Unloaded += OnViewUnloaded;
            
            // Set view reference in ViewModel when loaded
            this.Loaded += (s, e) =>
            {
                Logger.Info($"🔄 AI Weather view Loaded event fired");
                ResumePendingPreview();
                
                if (DataContext is AIWeatherPreviewViewModel vm)
                {
                    vm.SetView(this);
                    vm.SyncCaptureMode();
                    Logger.Debug($"View reference set in ViewModel");
                    
                    // If we're navigating back and there's a running RTSP stream, restart it
                    // Use longer delay and background priority to ensure UI is stable
                    Dispatcher.BeginInvoke(new Action(async () =>
                    {
                        try
                        {
                            Logger.Debug("Waiting 1000ms for view stabilization...");
                            // Wait for view to fully stabilize before attempting restart
                            await Task.Delay(1000);
                            
                            // Recheck DataContext in case view was unloaded during delay
                            if (DataContext is not AIWeatherPreviewViewModel viewModel)
                            {
                                Logger.Debug("View unloaded before RTSP restart could complete");
                                return;
                            }
                            
                            Logger.Debug($"Checking for running RTSP stream. Mode: {viewModel.CurrentCaptureMode}");

                            if (viewModel.IsClusterReplica)
                            {
                                await viewModel.SynchronizeReplicaPreviewAsync();
                                return;
                            }

                            // Check if any source is marked as running (RTSP mode only)
                            var runningSource = viewModel.Sources?.FirstOrDefault(src => src.IsRunning);
                            if (runningSource != null && viewModel.CurrentCaptureMode == CaptureMode.RTSPStream)
                            {
                                Logger.Info($"🔄 View reloaded with running RTSP stream - checking playback for {RedactRtspCredentials(runningSource.FullUrl)}");
                                // Restart the stream only if we're still on the UI thread and view is loaded
                                if (this.IsLoaded)
                                {
                                    Logger.Info($"Attempting StartStreamAsync with URL: {RedactRtspCredentials(runningSource.FullUrl)}");
                                    await StartStreamAsync(runningSource.FullUrl, runningSource.Username, runningSource.Password);
                                    Logger.Info("✅ RTSP stream successfully restarted after navigation");
                                }
                                else
                                {
                                    Logger.Warning("View no longer loaded, skipping stream restart");
                                }
                            }
                            else
                            {
                                Logger.Debug($"No running RTSP stream found. RunningSource: {runningSource != null}, Mode: {viewModel.CurrentCaptureMode}");
                            }
                        }
                        catch (Exception ex)
                        {
                            Logger.Error($"Error restarting RTSP stream on view reload: {ex.Message}");
                        }
                    }), DispatcherPriority.Background);
                }
                else
                {
                    Logger.Warning("DataContext is not AIWeatherPreviewViewModel");
                }
                
                // Refresh video layout when view becomes visible
                Dispatcher.BeginInvoke(new Action(() =>
                {
                    try
                    {
                        UpdateVideoHostLayoutToFit();
                    }
                    catch (Exception ex)
                    {
                        Logger.Debug($"Error refreshing video layout on load: {ex.Message}");
                    }
                }), DispatcherPriority.Loaded);
            };
            
            // Also refresh when visibility changes
            this.IsVisibleChanged += (s, e) =>
            {
                if (this.IsVisible)
                {
                    ResumePendingPreview();
                    Dispatcher.BeginInvoke(new Action(() =>
                    {
                        try
                        {
                            UpdateVideoHostLayoutToFit();
                        }
                        catch (Exception ex)
                        {
                            Logger.Debug($"Error refreshing video layout on visibility change: {ex.Message}");
                        }
                    }), DispatcherPriority.Loaded);
                }
            };
        }

        private void OnViewUnloaded(object sender, RoutedEventArgs e)
        {
            try
            {
                // NOTE: WPF can raise Unloaded during normal docking/layout changes (tab switching).
                // DO NOT stop the stream here - it should continue running in the background.
                // Only clean up when explicitly stopped by user or plugin shutdown.
                
                // Disposing LibVLC here can crash NINA on the next start (native AV).
                // DO NOT dispose resources here - let them persist across navigation.
                
                Logger.Debug("AI Weather view unloaded (navigated away) - keeping stream running");
            }
            catch (Exception ex)
            {
                Logger.Error($"Error in view unloaded handler: {ex.Message}");
            }
        }

        private void InitializeVLC()
        {
            try
            {
                Core.Initialize();
                _libVLC = new LibVLC();
                
                // Subscribe to VLC log events to see errors
                _libVLC.Log += (sender, e) =>
                {
                    var message = RedactRtspCredentials(e.Message);

                    // Some IP-camera streams slowly drift away from LibVLC's input clock. VLC
                    // then reports every decoded picture as late and drops every new frame,
                    // leaving the preview frozen on its last good frame even though the RTSP
                    // source and the independent OpenCV analysis path are still healthy. Do not
                    // write thousands of identical lines per minute; aggregate the burst and
                    // rebuild only the preview player if it remains unhealthy.
                    if (RtspPreviewHealthMonitor.IsLateFrameMessage(message))
                    {
                        var observation = _previewHealthMonitor.Observe(message, DateTime.UtcNow);
                        if (observation.ShouldLogSummary)
                        {
                            Logger.Warning(
                                $"VLC preview timing is late: {observation.LateMessageCount} messages over " +
                                $"{observation.LateDuration.TotalSeconds:0.0}s; latest: {message}");
                        }

                        if (observation.ShouldRecover)
                        {
                            SchedulePreviewRecovery(observation);
                        }

                        return;
                    }

                    // LibVLC can be noisy with benign messages; don't surface these as warnings/errors.
                    // The decoder's timing chatter goes with them: "picture is too late", late
                    // frames dropped, PCR/clock complaints, buffer deadlocks. One night of a
                    // Tapo stream produced 1,700 of those lines and nothing anyone could act on,
                    // burying the eight session drops that mattered. Network events stay visible.
                    if (message.Contains("unsupported control query", StringComparison.OrdinalIgnoreCase)
                        || message.Contains("surface dimensions", StringComparison.OrdinalIgnoreCase)
                        || message.Contains("SetThumbNailClip failed", StringComparison.OrdinalIgnoreCase)
                        || message.Contains("too late", StringComparison.OrdinalIgnoreCase)
                        || message.Contains("late frames", StringComparison.OrdinalIgnoreCase)
                        || message.Contains("late video", StringComparison.OrdinalIgnoreCase)
                        || message.Contains("PCR", StringComparison.Ordinal)
                        || message.Contains("pts_delay", StringComparison.OrdinalIgnoreCase)
                        || message.Contains("reference clock", StringComparison.OrdinalIgnoreCase)
                        || message.Contains("convert timestamp", StringComparison.OrdinalIgnoreCase)
                        || message.Contains("buffer deadlock", StringComparison.OrdinalIgnoreCase)
                        || message.Contains("MatchingDeviceId", StringComparison.OrdinalIgnoreCase)
                        || message.Contains("Password in a URI", StringComparison.OrdinalIgnoreCase))
                    {
                        Logger.Debug($"VLC: {message}");
                        return;
                    }

                    // VLC is extremely chatty; only surface real warnings/errors.
                    if (e.Level == LogLevel.Error)
                    {
                        Logger.Error($"VLC: {message}");
                        return;
                    }

                    if (e.Level == LogLevel.Warning)
                    {
                        Logger.Warning($"VLC: {message}");
                        return;
                    }

                    Logger.Debug($"VLC: {message}");
                };
                
                Logger.Info("LibVLC initialized successfully");
            }
            catch (Exception ex)
            {
                Logger.Error($"Failed to initialize LibVLC: {ex.Message}");
            }
        }

        private void SchedulePreviewRecovery(RtspPreviewHealthObservation observation)
        {
            Volatile.Write(ref _previewUnhealthy, 1);
            if (Interlocked.CompareExchange(ref _previewRecoveryScheduled, 1, 0) != 0)
            {
                return;
            }

            Logger.Warning(
                $"RTSP preview watchdog detected sustained late-frame dropping " +
                $"({observation.LateMessageCount} messages over {observation.LateDuration.TotalSeconds:0.0}s); " +
                "scheduling a preview-only restart. Weather analysis and safety state are not restarted.");

            if (Dispatcher.HasShutdownStarted || Dispatcher.HasShutdownFinished)
            {
                Interlocked.Exchange(ref _previewRecoveryScheduled, 0);
                return;
            }

            Dispatcher.BeginInvoke(new Action(async () =>
            {
                try
                {
                    var playbackUrl = _activePlaybackUrl;
                    if (string.IsNullOrWhiteSpace(playbackUrl) || _videoHost == null)
                    {
                        Logger.Debug("RTSP preview watchdog recovery skipped because no preview player is active");
                        return;
                    }

                    if (!IsLoaded || !IsVisible)
                    {
                        // Keep _previewUnhealthy set. The normal Loaded handler will see it and
                        // rebuild the player instead of accepting IsPlaying from a frozen vout.
                        Logger.Info("RTSP preview watchdog deferred recovery until the preview view is visible");
                        return;
                    }

                    Logger.Warning($"RTSP preview watchdog is rebuilding {RedactRtspCredentials(playbackUrl)}");
                    await StartStreamAsync(
                        playbackUrl,
                        cancellationToken: CancellationToken.None,
                        forceRestart: true);
                }
                catch (Exception ex)
                {
                    Logger.Error($"RTSP preview watchdog restart failed: {ex.Message}", ex);
                }
                finally
                {
                    Interlocked.Exchange(ref _previewRecoveryScheduled, 0);
                }
            }), DispatcherPriority.Background);
        }

        public void StartStream(string rtspUrl, string? username = null, string? password = null)
        {
            // Backward-compatible fire-and-forget entrypoint used by the ViewModel.
            _ = StartStreamAsync(rtspUrl, username, password);
        }

        public async Task StartStreamAsync(
            string rtspUrl,
            string? username = null,
            string? password = null,
            CancellationToken cancellationToken = default,
            bool forceRestart = false)
        {
            if (!Dispatcher.CheckAccess())
            {
                await Dispatcher.InvokeAsync(() => StartStreamAsync(rtspUrl, username, password, cancellationToken, forceRestart)).Task.Unwrap();
                return;
            }

            if (!IsOnScreen())
            {
                _pendingPreview = (rtspUrl, username, password);
                ReportPreviewStatus(UiLocalization.Text("Preview.VideoConnecting"));
                Logger.Debug("RTSP preview start deferred until its view is attached to a visible window");
                return;
            }
            _pendingPreview = null;
            _requestedPreview = (rtspUrl, username, password);

            await _streamGate.WaitAsync(cancellationToken);
            try
            {
                if (_isStartingStream)
                {
                    Logger.Warning("StartStream ignored because a start is already in progress");
                    return;
                }

                _isStartingStream = true;
                ReportPreviewStatus(UiLocalization.Text("Preview.VideoConnecting"));

                Logger.Info($"StartStream called with URL: {RedactRtspCredentials(rtspUrl)}");
                Logger.Info($"Authentication: {(string.IsNullOrWhiteSpace(username) ? "not configured separately" : "configured")}");

                if (_libVLC == null)
                {
                    Logger.Error("LibVLC not initialized - cannot start stream");
                    ReportPreviewFailure(UiLocalization.Text("Preview.VideoLibVlcUnavailable"));
                    return;
                }

                if (VideoPanel == null)
                {
                    Logger.Error("VideoPanel is null - XAML element not found!");
                    ReportPreviewFailure(UiLocalization.Text("Preview.VideoViewUnavailable"));
                    return;
                }

                Logger.Info($"VideoPanel found. Size: {VideoPanel.ActualWidth}x{VideoPanel.ActualHeight}");

                var playbackUrl = BuildAuthenticatedUrl(rtspUrl, username, password);
                if (!forceRestart
                    && Volatile.Read(ref _previewUnhealthy) == 0
                    && _videoHost?.Player?.IsPlaying == true
                    && _videoSurfaceReady
                    && string.Equals(_activePlaybackUrl, playbackUrl, StringComparison.Ordinal))
                {
                    Logger.Debug("RTSP preview is already playing this source; skipping duplicate restart");
                    UpdateVideoHostLayoutToFit();
                    ReportPreviewStatus(null);
                    return;
                }

                // Cancel any previous start loop before tearing down the current player/host.
                _startCts?.Cancel();
                _startCts?.Dispose();
                _startCts = null;

                await StopStreamCoreAsync();

                // Create a fresh CTS for this start attempt. StopStreamCoreAsync clears _startCts.
                _startCts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
                var startToken = _startCts.Token;

                // Reserve this endpoint before LibVLC opens it. The safety monitor uses
                // the reservation to release an idle background OpenCV decoder and prefer
                // frames from the decoder that is already proven healthy.
                _sharedPreviewFrameRegistration =
                    await SharedRtspPreviewFrameProvider.Instance.RegisterAsync(
                        playbackUrl,
                        CaptureCurrentPreviewFrameAsync,
                        startToken);
                var sharedSourceIdentity =
                    SharedRtspPreviewFrameProvider.CreateSourceIdentity(playbackUrl);
                Logger.Info(
                    $"Reserved RTSP source " +
                    $"{SharedRtspPreviewFrameProvider.Fingerprint(sharedSourceIdentity)} " +
                    "for shared preview and AI capture");

                Logger.Info("Creating MediaPlayer and VideoHost...");
                Logger.Debug("Creating LibVLC MediaPlayer...");

                var player = new MediaPlayer(_libVLC)
                {
                    Volume = 0,
                    EnableHardwareDecoding = true
                };

                player.EncounteredError += OnPreviewPlaybackLost;
                player.EndReached += OnPreviewPlaybackLost;
                player.Stopped += OnPreviewPlaybackLost;
                Logger.Debug("MediaPlayer created");

                VideoPanel.Visibility = Visibility.Visible;
                CameraImage.Visibility = Visibility.Collapsed;

                Logger.Debug("Creating VideoHwndHost...");
                // Give the native host a conventional video-shaped viewport from the outset.
                // HwndHost pixels cannot be transparently composed with WPF, so allowing the
                // native host to fill a tall panel would expose its white unused area before
                // dimensions arrive. The surrounding VideoPanel remains genuine N.I.N.A. WPF.
                var initialViewport = VideoFitCalculator.FitInside(
                    VideoPanel.ActualWidth,
                    VideoPanel.ActualHeight,
                    16,
                    9);
                _videoHost = new VideoHwndHost(player, ResolveVideoBackgroundColor())
                {
                    HorizontalAlignment = HorizontalAlignment.Center,
                    VerticalAlignment = VerticalAlignment.Center,
                    Width = initialViewport.Width,
                    Height = initialViewport.Height
                };
                _videoSurfaceReady = false;
                _decodedVideoWidth = 0;
                _decodedVideoHeight = 0;
                Volatile.Write(ref _voutObserved, 0);
                _lastLoggedVideoLayout = null;
                _previewHealthMonitor.ResetBurst();
                Volatile.Write(ref _previewUnhealthy, 0);
                player.Vout += Player_Vout;
                Logger.Info($"VideoHost created (Panel: {VideoPanel.ActualWidth}x{VideoPanel.ActualHeight})");

                Logger.Info("Adding VideoHost to VideoPanel...");
                VideoPanel.Children.Add(_videoHost);
                _videoHost.Margin = new Thickness(0);

                await Dispatcher.Yield(DispatcherPriority.Loaded);

                IntPtr hwnd = IntPtr.Zero;
                for (int attempt = 0; attempt < 100; attempt++)
                {
                    startToken.ThrowIfCancellationRequested();
                    hwnd = _videoHost.VideoHwnd;
                    if (hwnd != IntPtr.Zero)
                    {
                        break;
                    }
                    await Task.Delay(20, startToken);
                }

                Logger.Info($"VideoHost handle resolved: {hwnd}");
                if (hwnd == IntPtr.Zero)
                {
                    Logger.Error("VideoHost handle never became available; aborting playback setup");
                    await StopStreamCoreAsync();
                    ReportPreviewFailure(UiLocalization.Text("Preview.VideoSurfaceUnavailable"));
                    return;
                }

                player.Hwnd = hwnd;
                Logger.Info($"Player dedicated video-target Hwnd set to: {player.Hwnd}, Volume: {player.Volume}, HW decode: {player.EnableHardwareDecoding}");

                try
                {
                    UpdateVideoHostLayoutToFit();
                }
                catch (Exception ex)
                {
                    Logger.Debug($"VideoHost initial resize failed: {ex.Message}");
                }

                Logger.Info($"Creating media for URL: {RedactRtspCredentials(playbackUrl)}");

                _currentMedia?.Dispose();
                _currentMedia = new Media(_libVLC, playbackUrl, FromType.FromLocation);
                _currentMedia.AddOption(":network-caching=1000");
                _currentMedia.AddOption(":rtsp-tcp");
                _currentMedia.AddOption(":no-audio");
                // This preview is a silent real-time safety-camera view, not synchronized A/V.
                // Disabling the input-clock correction prevents camera timestamp drift from
                // growing into a permanent late-frame drop loop after several hours.
                _currentMedia.AddOption(":clock-synchro=0");
                _currentMedia.AddOption(":clock-jitter=0");
                _currentMedia.AddOption(":drop-late-frames");
                _currentMedia.AddOption(":skip-frames");
                Logger.Info("Media created with options: network-caching=1000, rtsp-tcp, no-audio, clock-synchro=0, clock-jitter=0, drop-late-frames, skip-frames");

                Logger.Info("Starting playback...");
                var playResult = player.Play(_currentMedia);
                Logger.Info($"Play() returned: {playResult}, Player state: {player.State}");

                if (!playResult)
                {
                    Logger.Error("Play() returned false - VLC refused to play media");
                    await StopStreamCoreAsync();
                    ReportPreviewFailure(UiLocalization.Text("Preview.VideoOpenFailed"));
                    return;
                }

                for (int i = 0; i < 50; i++)
                {
                    startToken.ThrowIfCancellationRequested();
                    var state = player.State;
                    Logger.Debug($"Waiting for playback... State: {state}, IsPlaying: {player.IsPlaying}");

                    if (state == VLCState.Error || state == VLCState.Ended)
                    {
                        Logger.Error($"Player entered error/ended state: {state}");
                        break;
                    }

                    if (player.IsPlaying)
                    {
                        Logger.Info("RTSP stream playing successfully!");
                        try
                        {
                            await RefreshVideoHostLayoutAfterVoutAsync(player, startToken);
                        }
                        catch (OperationCanceledException) when (startToken.IsCancellationRequested)
                        {
                            throw;
                        }
                        catch (Exception ex)
                        {
                            Logger.Debug($"Video fill layout update failed: {ex.Message}");
                        }
                        break;
                    }

                    await Task.Delay(100, startToken);
                }

                var finalState = player.State;
                var finalIsPlaying = player.IsPlaying;
                Logger.Info($"Stream startup complete. Final state: {finalState}, IsPlaying: {finalIsPlaying}");
                
                if (!finalIsPlaying)
                {
                    var errorMsg = "RTSP stream failed to start. ";
                    if (finalState == VLCState.Error)
                    {
                        errorMsg += "Possible causes: incorrect URL, wrong credentials, network unreachable, or unsupported codec.";
                    }
                    else
                    {
                        errorMsg += $"Player state: {finalState}. Check URL format and network connectivity.";
                    }
                    Logger.Warning(errorMsg);
                    Logger.Warning($"Troubleshooting tips:\n" +
                        $"  1. Verify RTSP URL is correct (e.g., rtsp://camera-ip:554/stream)\n" +
                        $"  2. Check username/password if authentication is required\n" +
                        $"  3. Ensure camera is reachable (ping the IP address)\n" +
                        $"  4. Try the URL in VLC media player to verify it works\n" +
                        $"  5. Some cameras require specific paths like /h264, /live, or /stream");

                    // The shared source reservation is deliberately installed before
                    // LibVLC opens the stream. If playback never becomes healthy, release
                    // that reservation as well as the failed player so headless monitoring
                    // can use its independent OpenCV path on the next analysis cycle.
                    await StopStreamCoreAsync();
                    ReportPreviewFailure(UiLocalization.Text("Preview.VideoOpenFailed"));
                    return;
                }
                else
                {
                    _activePlaybackUrl = playbackUrl;
                    _previewReconnectAttempt = 0;
                    _previewHealthMonitor.ResetBurst();
                    Volatile.Write(ref _previewUnhealthy, 0);
                    if (_videoSurfaceReady)
                    {
                        ReportPreviewStatus(null);
                    }
                    else
                    {
                        // Some cameras resume decoding only after LibVLC has reported Playing
                        // for several seconds. Keep the same player/session alive and continue
                        // probing; tearing it down here was the source of a permanent black
                        // preview even though later AI snapshots from this player succeeded.
                        ReportPreviewStatus(UiLocalization.Text("Preview.VideoSurfaceWaiting"));
                        ScheduleSurfaceRecovery(player, startToken);
                    }
                }

                Logger.Info($"Started RTSP stream: {RedactRtspCredentials(playbackUrl)}");
            }
            catch (OperationCanceledException)
            {
                Logger.Info("StartStream canceled");
                await StopStreamCoreAsync();
            }
            catch (UriFormatException ex)
            {
                Logger.Error($"Invalid RTSP URL format: {ex.Message}");
                Logger.Error("URL must be in format: rtsp://[username:password@]camera-ip[:port]/path");
                await StopStreamCoreAsync();
                ReportPreviewFailure(UiLocalization.Text("Preview.VideoUrlInvalid"));
            }
            catch (Exception ex)
            {
                Logger.Error($"Failed to start RTSP stream: {ex.Message}", ex);
                Logger.Error("Common issues: Wrong URL, authentication failure, network error, or camera offline.");
                await StopStreamCoreAsync();
                ReportPreviewFailure(UiLocalization.Text("Preview.VideoOpenFailed"));
            }
            finally
            {
                _isStartingStream = false;
                if (_requestedPreview.HasValue && _videoHost?.Player?.IsPlaying != true)
                    SchedulePreviewReconnect();
                _streamGate.Release();
            }
        }

        private async Task<Bitmap?> CaptureCurrentPreviewFrameAsync(
            CancellationToken cancellationToken)
        {
            if (Dispatcher.HasShutdownStarted || Dispatcher.HasShutdownFinished)
            {
                return null;
            }

            var tempPath = Path.Combine(
                Path.GetTempPath(),
                $"aiweather_shared_preview_{Guid.NewGuid():N}.png");
            try
            {
                // TakeSnapshot queues a write from LibVLC. A few bounded attempts cover the
                // short interval between Play() succeeding and the first video output frame.
                for (var attempt = 0; attempt < 4; attempt++)
                {
                    cancellationToken.ThrowIfCancellationRequested();
                    TryDeleteSnapshot(tempPath);
                    MediaPlayer? requestedPlayer = null;

                    var requested = await Dispatcher.InvokeAsync(
                        () =>
                        {
                            var player = _videoHost?.Player;
                            if (player?.IsPlaying != true
                                || string.IsNullOrWhiteSpace(_activePlaybackUrl)
                                || Volatile.Read(ref _previewUnhealthy) != 0)
                            {
                                return false;
                            }

                            requestedPlayer = player;
                            return player.TakeSnapshot(0, tempPath, 0, 0);
                        },
                        DispatcherPriority.Background,
                        cancellationToken);

                    if (!requested)
                    {
                        await Task.Delay(150, cancellationToken);
                        continue;
                    }

                    for (var wait = 0; wait < 30; wait++)
                    {
                        cancellationToken.ThrowIfCancellationRequested();
                        var frame = await Task.Run(
                            () => TryLoadSnapshot(tempPath),
                            cancellationToken);
                        if (frame != null)
                        {
                            await PromoteDecodedFrameToPreviewAsync(
                                requestedPlayer,
                                frame.Width,
                                frame.Height,
                                "shared AI snapshot");
                            Logger.Debug(
                                $"Captured current AI frame from existing LibVLC preview: " +
                                $"{frame.Width}x{frame.Height}");
                            return frame;
                        }

                        await Task.Delay(50, cancellationToken);
                    }
                }

                Logger.Warning(
                    "Existing LibVLC preview did not produce a current snapshot within the bounded wait");
                return null;
            }
            finally
            {
                TryDeleteSnapshot(tempPath);
            }
        }

        private void ScheduleSurfaceRecovery(MediaPlayer player, CancellationToken cancellationToken)
        {
            if (Interlocked.CompareExchange(ref _surfaceRecoveryScheduled, 1, 0) != 0)
            {
                return;
            }

            _ = ContinueSurfaceRecoveryAsync(player, cancellationToken);
        }

        private async Task ContinueSurfaceRecoveryAsync(
            MediaPlayer player,
            CancellationToken cancellationToken)
        {
            try
            {
                Logger.Info(
                    "RTSP player is connected without a displayable surface; continuing " +
                    "same-session decoded-frame probes for up to one minute");

                var recovered = await _surfaceRecovery.WaitForSurfaceAsync(
                    async token =>
                    {
                        if (_videoHost == null
                            || !ReferenceEquals(_videoHost.Player, player)
                            || player.IsPlaying != true)
                        {
                            return false;
                        }

                        if (_videoSurfaceReady)
                        {
                            return true;
                        }

                        if (TryGetCurrentVideoSize(player, out var width, out var height)
                            && await PromoteDecodedFrameToPreviewAsync(
                                player,
                                (int)Math.Round(width),
                                (int)Math.Round(height),
                                "delayed video surface"))
                        {
                            return true;
                        }

                        if (await TryProbeDecodedFrameSizeAsync(player, token))
                        {
                            return await PromoteDecodedFrameToPreviewAsync(
                                player,
                                (int)Math.Round(_decodedVideoWidth),
                                (int)Math.Round(_decodedVideoHeight),
                                "delayed same-session snapshot");
                        }

                        return false;
                    },
                    cancellationToken);

                if (!recovered
                    && _videoHost != null
                    && ReferenceEquals(_videoHost.Player, player)
                    && !_videoSurfaceReady)
                {
                    Logger.Warning(
                        "RTSP player remained connected but no displayable frame became " +
                        "available during the one-minute same-session recovery window");
                    ReportPreviewFailure(UiLocalization.Text("Preview.VideoSurfaceUnavailable"));
                }
            }
            catch (OperationCanceledException)
            {
                // Stream teardown or replacement cancels the old player's recovery loop.
            }
            catch (Exception ex)
            {
                Logger.Error($"RTSP delayed surface recovery failed: {ex.Message}", ex);
                ReportPreviewFailure(UiLocalization.Text("Preview.VideoSurfaceUnavailable"));
            }
            finally
            {
                Interlocked.Exchange(ref _surfaceRecoveryScheduled, 0);
            }
        }

        private async Task<bool> PromoteDecodedFrameToPreviewAsync(
            MediaPlayer? player,
            int width,
            int height,
            string evidence)
        {
            if (player == null || width <= 1 || height <= 1)
            {
                return false;
            }

            if (!Dispatcher.CheckAccess())
            {
                if (Dispatcher.HasShutdownStarted || Dispatcher.HasShutdownFinished)
                {
                    return false;
                }

                return await Dispatcher.InvokeAsync(
                    () => PromoteDecodedFrameToPreviewAsync(player, width, height, evidence))
                    .Task.Unwrap();
            }

            if (_videoHost == null
                || !ReferenceEquals(_videoHost.Player, player)
                || player.IsPlaying != true)
            {
                return false;
            }

            _decodedVideoWidth = width;
            _decodedVideoHeight = height;
            _videoSurfaceReady = true;
            if (!TryUpdateVideoHostLayoutToFit(allowBeforeReady: true))
            {
                _videoSurfaceReady = false;
                return false;
            }

            _videoHost.ShowVideoSurface();
            Volatile.Write(ref _previewUnhealthy, 0);
            ReportPreviewStatus(null);
            Logger.Info(
                $"RTSP preview surface recovered from {evidence}: {width}x{height}; " +
                "the existing player was retained and no second RTSP session was opened");
            return true;
        }

        private void ReportPreviewStatus(string? text, bool retryAvailable = false)
        {
            if (DataContext is AIWeatherPreviewViewModel viewModel)
            {
                viewModel.SetPreviewStreamStatus(text, retryAvailable);
            }
        }

        private void ReportPreviewFailure(string text)
        {
            ReportPreviewStatus(text, retryAvailable: true);
        }

        private static Bitmap? TryLoadSnapshot(string path)
        {
            try
            {
                var info = new FileInfo(path);
                if (!info.Exists || info.Length <= 0)
                {
                    return null;
                }

                using var stream = new FileStream(
                    path,
                    FileMode.Open,
                    FileAccess.Read,
                    FileShare.ReadWrite | FileShare.Delete);
                using var image = DrawingImage.FromStream(stream);
                return new Bitmap(image);
            }
            catch (IOException)
            {
                return null;
            }
            catch (ArgumentException)
            {
                // The file exists but LibVLC has not finished writing a complete image.
                return null;
            }
        }

        private static void TryDeleteSnapshot(string path)
        {
            try
            {
                File.Delete(path);
            }
            catch
            {
                // Best effort only. Every request uses a unique path.
            }
        }

        public void StopStream()
        {
            _ = StopStreamAsync();
        }

        public async Task StopStreamAsync()
        {
            // Log the call stack to understand who's calling this
            Logger.Info($"🛑 StopStreamAsync called - Stack trace: {Environment.StackTrace.Split('\n').Take(5).Aggregate((a, b) => a + "\n" + b)}");
            
            if (!Dispatcher.CheckAccess())
            {
                await Dispatcher.InvokeAsync(() => StopStreamAsync()).Task.Unwrap();
                return;
            }

            _requestedPreview = null;
            _pendingPreview = null;
            var reconnect = _previewReconnectCts;
            _previewReconnectCts = null;
            reconnect?.Cancel();
            _startCts?.Cancel();

            await _streamGate.WaitAsync();
            try
            {
                await StopStreamCoreAsync();
                _previewHealthMonitor.ResetAll();
                Volatile.Write(ref _previewUnhealthy, 0);
                ReportPreviewStatus(null);
            }
            catch (Exception ex)
            {
                Logger.Error($"Failed to stop stream: {ex.Message}");
            }
            finally
            {
                _streamGate.Release();
            }
        }

        // Core stop logic. Caller must already be on UI thread.
        // If called from StartStreamAsync, it runs while holding _streamGate to avoid races.
        private async Task StopStreamCoreAsync()
        {
            try
            {
                _sharedPreviewFrameRegistration?.Dispose();
                _sharedPreviewFrameRegistration = null;
                _activePlaybackUrl = null;
                _previewHealthMonitor.ResetBurst();
                _startCts?.Cancel();
                _startCts?.Dispose();
                _startCts = null;

                if (_videoHost != null)
                {
                    var host = _videoHost;
                    var player = host.Player;
                    if (player != null)
                    {
                        player.EncounteredError -= OnPreviewPlaybackLost;
                        player.EndReached -= OnPreviewPlaybackLost;
                        player.Stopped -= OnPreviewPlaybackLost;
                    }

                    try
                    {
                        // Detach the native render target first to reduce odds of a blocking stop.
                        if (player != null)
                        {
                            player.Vout -= Player_Vout;
                            player.Hwnd = IntPtr.Zero;
                        }
                    }
                    catch (Exception ex)
                    {
                        Logger.Error($"Error detaching MediaPlayer HWND: {ex.Message}");
                    }

                    try
                    {
                        VideoPanel?.Children.Remove(host);
                    }
                    catch
                    {
                        // best-effort
                    }

                    try
                    {
                        host.Dispose();
                    }
                    catch (Exception ex)
                    {
                        Logger.Error($"Error disposing VideoHost: {ex.Message}");
                    }

                    _videoHost = null;
                    _lastLoggedVideoLayout = null;
                    _videoSurfaceReady = false;
                    _decodedVideoWidth = 0;
                    _decodedVideoHeight = 0;
                    Volatile.Write(ref _voutObserved, 0);

                    // Stop/Dispose can sometimes block. Do it off-UI with a timeout.
                    await StopAndDisposePlayerBestEffortAsync(player, TimeSpan.FromSeconds(2));
                }

                _currentMedia?.Dispose();
                _currentMedia = null;

                if (VideoPanel != null)
                {
                    VideoPanel.Visibility = Visibility.Collapsed;
                }

                if (CameraImage != null)
                {
                    CameraImage.Visibility = Visibility.Visible;
                }

                Logger.Info("Stopped RTSP stream");
            }
            catch (Exception ex)
            {
                Logger.Error($"Failed to stop stream: {ex.Message}");
            }
        }

        private static async Task StopAndDisposePlayerBestEffortAsync(MediaPlayer? player, TimeSpan timeout)
        {
            if (player == null)
            {
                return;
            }

            try
            {
                var stopTask = Task.Run(() =>
                {
                    try
                    {
                        player.Stop();
                    }
                    catch (Exception ex)
                    {
                        Logger.Error($"Error stopping MediaPlayer: {ex.Message}");
                    }

                    try
                    {
                        player.Dispose();
                    }
                    catch (Exception ex)
                    {
                        Logger.Error($"Error disposing MediaPlayer: {ex.Message}");
                    }
                });

                var completed = await Task.WhenAny(stopTask, Task.Delay(timeout)) == stopTask;
                if (!completed)
                {
                    Logger.Warning("MediaPlayer stop/dispose timed out; continuing cleanup to avoid UI hang");
                }
            }
            catch (Exception ex)
            {
                Logger.Error($"Unexpected error stopping/disposing MediaPlayer: {ex.Message}");
            }
        }

        private static string BuildAuthenticatedUrl(string rtspUrl, string? username, string? password)
        {
            try
            {
                var uri = new Uri(rtspUrl);
                if (!string.IsNullOrWhiteSpace(username) && !string.IsNullOrWhiteSpace(password))
                {
                    var builder = new UriBuilder(uri)
                    {
                        UserName = username,
                        Password = password
                    };
                    return builder.Uri.ToString();
                }

                if (string.IsNullOrEmpty(uri.AbsolutePath) || uri.AbsolutePath == "/")
                {
                    Logger.Warning($"RTSP URL has no path component. Most cameras need a path like /stream or /live. Current URL: {RedactRtspCredentials(rtspUrl)}");
                }

                return rtspUrl;
            }
            catch
            {
                return rtspUrl;
            }
        }

        private void PasswordBox_PasswordChanged(object sender, RoutedEventArgs e)
        {
            if (sender is PasswordBox passwordBox && passwordBox.DataContext is CameraSource source)
            {
                source.Password = passwordBox.Password;
                Logger.Debug($"Password updated for camera source: {RedactRtspCredentials(source.FullUrl)}");
                try
                {
                    Properties.Settings.Default.RtspPassword = source.Password ?? string.Empty;
                    CoreUtil.SaveSettings(Properties.Settings.Default);
                }
                catch (Exception ex)
                {
                    Logger.Error($"Failed to persist RTSP password from preview: {ex.Message}");
                }
            }
        }

        private static string RedactRtspCredentials(string? input)
        {
            return LogRedactor.RedactRtspUrl(input);
        }

        private MediaColor ResolveVideoBackgroundColor()
        {
            // HwndHost is a native airspace and cannot inherit a transparent WPF
            // background. Mirror N.I.N.A.'s active secondary background into the native
            // child window so letterbox/pillarbox regions follow light and dark themes.
            if (VideoPanel?.Background is SolidColorBrush renderedPanelBrush)
            {
                Logger.Info($"RTSP native host background resolved from rendered VideoPanel: {renderedPanelBrush.Color}");
                return renderedPanelBrush.Color;
            }

            if (TryFindResource("SecondaryBackgroundBrush") is SolidColorBrush localBrush)
            {
                Logger.Info($"RTSP native host background resolved from local theme resource: {localBrush.Color}");
                return localBrush.Color;
            }

            if (Application.Current?.TryFindResource("SecondaryBackgroundBrush") is SolidColorBrush appBrush)
            {
                Logger.Info($"RTSP native host background resolved from application theme resource: {appBrush.Color}");
                return appBrush.Color;
            }

            Logger.Warning("RTSP native host background resource was unavailable; using dark fallback #202B30");
            return MediaColor.FromRgb(32, 43, 48);
        }

        private void Player_Vout(object? sender, MediaPlayerVoutEventArgs e)
        {
            if (sender is not MediaPlayer player)
            {
                return;
            }

            Volatile.Write(ref _voutObserved, 1);

            Dispatcher.BeginInvoke(new Action(async () =>
            {
                try
                {
                    var token = _startCts?.Token ?? CancellationToken.None;
                    await RefreshVideoHostLayoutAfterVoutAsync(player, token);
                }
                catch (OperationCanceledException)
                {
                    // Stream teardown cancels any pending first-frame layout work.
                }
                catch (Exception ex)
                {
                    Logger.Debug($"Deferred RTSP video layout failed: {ex.Message}");
                }
            }), DispatcherPriority.Background);
        }

        private async Task<bool> RefreshVideoHostLayoutAfterVoutAsync(
            MediaPlayer player,
            CancellationToken cancellationToken)
        {
            // Vout/Playing can be raised before libvlc_video_get_size starts returning
            // dimensions. Keep the real render target full-sized under the native cover and
            // accept three independent signs of readiness: LibVLC size, a rendered output
            // child, or a successfully decoded snapshot. The last one is important on Windows
            // machines whose Direct3D backend renders correctly but reports video_get_size=0.
            for (var attempt = 0; attempt < 60; attempt++)
            {
                cancellationToken.ThrowIfCancellationRequested();
                if (_videoHost == null || !ReferenceEquals(_videoHost.Player, player))
                {
                    return false;
                }

                // Multiple Vout/Playing notifications may schedule overlapping probes. Once
                // one probe has revealed a ready surface, later probes must never reapply the
                // startup clip and blank an already playing preview.
                if (_videoSurfaceReady)
                {
                    if (TryUpdateVideoHostLayoutToFit(allowBeforeReady: true))
                    {
                        _videoHost.ShowVideoSurface();
                        return true;
                    }
                }

                // LibVLC can reorder its native child hierarchy while vout is being created.
                // Reassert the solid startup cover until a real output surface is measurable,
                // otherwise the future 16:9 viewport briefly becomes a white rectangle.
                _videoHost.ShowStartupCover();

                if (TryGetCurrentVideoSize(player, out _, out _))
                {
                    // Keep the native target under the theme cover while Direct3D prepares the
                    // first frame. Raising it only after this warm-up avoids a white flash.
                    await Task.Delay(250, cancellationToken);
                    if (_videoHost != null && ReferenceEquals(_videoHost.Player, player))
                    {
                        _videoSurfaceReady = true;
                        if (TryUpdateVideoHostLayoutToFit(allowBeforeReady: true))
                        {
                            _videoHost.ShowVideoSurface();
                            return true;
                        }
                    }
                }

                // Probe only twice per second. TakeSnapshot uses this MediaPlayer, so it does
                // not open another RTSP connection or alter the single-session architecture.
                if (attempt >= 5
                    && attempt % 5 == 0
                    && await TryProbeDecodedFrameSizeAsync(player, cancellationToken))
                {
                    _videoSurfaceReady = true;
                    if (TryUpdateVideoHostLayoutToFit(allowBeforeReady: true))
                    {
                        _videoHost?.ShowVideoSurface();
                        Logger.Info(
                            $"RTSP preview surface revealed from decoded-frame probe: " +
                            $"{_decodedVideoWidth:0}x{_decodedVideoHeight:0}");
                        return true;
                    }
                }

                await Task.Delay(100, cancellationToken);
            }

            // A Vout callback itself proves that VLC created a video output. If this backend
            // exposes neither dimensions nor snapshots, reveal a conventional 16:9 target
            // after a bounded warm-up instead of leaving a permanent dark cover. Do not apply
            // this fallback when Playing was raised without any Vout: that would hide a real
            // decoder/open failure behind an apparently successful black preview.
            if (Volatile.Read(ref _voutObserved) == 0)
            {
                Logger.Warning(
                    "RTSP playback reported Playing, but no video output or decoded frame " +
                    "became available within 6 seconds");
                return false;
            }

            // The video target is SS_BLACKRECT, so it stays dark rather than flashing white
            // while the first frame is still being painted.
            _decodedVideoWidth = 16;
            _decodedVideoHeight = 9;
            _videoSurfaceReady = true;
            if (TryUpdateVideoHostLayoutToFit(allowBeforeReady: true))
            {
                _videoHost?.ShowVideoSurface();
                Logger.Warning(
                    "RTSP backend did not expose video dimensions within 6 seconds; " +
                    "revealed the active vout using a 16:9 compatibility viewport");
                return true;
            }

            Logger.Warning(
                "RTSP vout started but the compatibility viewport could not be laid out");
            return false;
        }

        private async Task<bool> TryProbeDecodedFrameSizeAsync(
            MediaPlayer player,
            CancellationToken cancellationToken)
        {
            if (_videoHost == null
                || !ReferenceEquals(_videoHost.Player, player)
                || player.IsPlaying != true)
            {
                return false;
            }

            var tempPath = Path.Combine(
                Path.GetTempPath(),
                $"aiweather_preview_probe_{Guid.NewGuid():N}.png");
            try
            {
                if (!player.TakeSnapshot(0, tempPath, 0, 0))
                {
                    return false;
                }

                for (var wait = 0; wait < 10; wait++)
                {
                    cancellationToken.ThrowIfCancellationRequested();
                    var frame = await Task.Run(
                        () => TryLoadSnapshot(tempPath),
                        cancellationToken);
                    if (frame != null)
                    {
                        using (frame)
                        {
                            _decodedVideoWidth = frame.Width;
                            _decodedVideoHeight = frame.Height;
                        }

                        return _decodedVideoWidth > 1 && _decodedVideoHeight > 1;
                    }

                    await Task.Delay(50, cancellationToken);
                }

                return false;
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                Logger.Debug($"Decoded-frame preview probe was unavailable: {ex.Message}");
                return false;
            }
            finally
            {
                TryDeleteSnapshot(tempPath);
            }
        }

        private void PasswordBox_Loaded(object sender, RoutedEventArgs e)
        {
            if (sender is PasswordBox passwordBox && passwordBox.DataContext is CameraSource source)
            {
                passwordBox.Password = source.Password;
            }
        }

        private void VideoPanel_SizeChanged(object sender, SizeChangedEventArgs e)
        {
            try
            {
                TryUpdateVideoHostLayoutToFit();
            }
            catch (Exception ex)
            {
                Logger.Debug($"VideoPanel resize handler failed: {ex.Message}");
            }
        }

        // Resize the native video output so the complete frame remains visible.
        // Any aspect-ratio mismatch is shown as letterboxing/pillarboxing instead of cropping.
        private void UpdateVideoHostLayoutToFit()
        {
            TryUpdateVideoHostLayoutToFit();
        }

        private bool TryUpdateVideoHostLayoutToFit(bool allowBeforeReady = false)
        {
            if (!Dispatcher.CheckAccess())
            {
                return Dispatcher.Invoke(() => TryUpdateVideoHostLayoutToFit(allowBeforeReady));
            }

            if (_videoHost == null || VideoPanel == null)
            {
                return false;
            }

            if (!_videoSurfaceReady && !allowBeforeReady)
            {
                return false;
            }

            var panelWidth = VideoPanel.ActualWidth;
            var panelHeight = VideoPanel.ActualHeight;
            if (panelWidth <= 1 || panelHeight <= 1)
            {
                return false;
            }

            if (!TryGetCurrentVideoSize(_videoHost.Player, out var videoWidth, out var videoHeight))
            {
                // The dedicated VLC target stays behind the native theme cover until dimensions
                // exist. Exposing the target here caused the startup white flash.
                return false;
            }

            // Resize the entire native airspace to the video aspect. The surrounding pixels are
            // then genuine WPF VideoPanel pixels, so neither the system host nor LibVLC can ever
            // repaint the letterbox area white.
            var fittedViewport = VideoFitCalculator.FitInside(
                panelWidth,
                panelHeight,
                videoWidth,
                videoHeight);
            _videoHost.HorizontalAlignment = HorizontalAlignment.Center;
            _videoHost.VerticalAlignment = VerticalAlignment.Center;
            _videoHost.Width = fittedViewport.Width;
            _videoHost.Height = fittedViewport.Height;
            VideoPanel.UpdateLayout();
            _videoHost.UpdateLayout();

            // VLC still receives a dedicated inner child rather than the HwndHost itself. That
            // keeps its generated native hierarchy isolated from the WPF layout lifecycle.
            if (!_videoHost.TrySetVideoContentSize(videoWidth, videoHeight, out var layout))
            {
                return false;
            }

            if (_lastLoggedVideoLayout != layout)
            {
                Logger.Info(
                    $"RTSP preview nested fit: themed host {layout.ContainerWidth}x{layout.ContainerHeight}, " +
                    $"video {videoWidth:0}x{videoHeight:0}, dedicated VLC target " +
                    $"{layout.VideoWidth}x{layout.VideoHeight} at ({layout.X},{layout.Y})");
                _lastLoggedVideoLayout = layout;
            }

            try
            {
                // Zero means VLC automatically fits the picture to the HWND. Because the HWND
                // now has the same aspect ratio, the picture fills it without native bars.
                _videoHost.Player.Scale = 0;
            }
            catch
            {
                // best-effort
            }

            // Force layout update
            _videoHost.UpdateLayout();
            VideoPanel.UpdateLayout();
            return true;
        }

        private bool TryGetCurrentVideoSize(
            MediaPlayer player,
            out double videoWidth,
            out double videoHeight)
        {
            videoWidth = 0;
            videoHeight = 0;
            try
            {
                uint width = 0;
                uint height = 0;
                player.Size(0, ref width, ref height);
                videoWidth = width;
                videoHeight = height;
                if (width > 0 && height > 0)
                {
                    return true;
                }
            }
            catch
            {
                // Fall through to the native VLC output-window probe below. Some LibVLC builds
                // render correctly but keep video_get_size at 0 for an HWND target.
            }

            return _videoHost != null
                && ReferenceEquals(_videoHost.Player, player)
                && (_videoHost.TryGetRenderedVideoSize(out videoWidth, out videoHeight)
                    || TryGetDecodedVideoSize(out videoWidth, out videoHeight));
        }

        private bool TryGetDecodedVideoSize(out double videoWidth, out double videoHeight)
        {
            videoWidth = _decodedVideoWidth;
            videoHeight = _decodedVideoHeight;
            return videoWidth > 1 && videoHeight > 1;
        }
    }
}
