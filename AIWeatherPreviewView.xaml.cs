using System;
using System.ComponentModel.Composition;
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
using System.Windows.Controls.Primitives;

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

        // A start asked for while the view is not on screen. A native child window cannot be
        // created inside an invisible visual tree, so instead of polling for a handle that
        // cannot come, the request is parked here and honoured when the view becomes visible.
        private (string url, string? user, string? password)? _pendingStart;

        // What was last asked to play, so a lost stream can be brought back without the
        // caller's help.
        private (string url, string? user, string? password)? _lastStart;

        // True while a stop was asked for by the plugin itself; a Stopped/EndReached event
        // arriving then is our own doing and must not trigger a reconnect.
        private bool _stopRequested;

        // Reconnection with growing pauses: 5, 10, 20, 40, then 60 seconds, reset by a
        // successful playback. A camera that drops the session every few minutes (the Tapo
        // does) would otherwise leave the preview dark until the next tab switch.
        private int _reconnectAttempt;
        private CancellationTokenSource? _reconnectCts;
        private static readonly TimeSpan[] ReconnectPauses =
        {
            TimeSpan.FromSeconds(5), TimeSpan.FromSeconds(10), TimeSpan.FromSeconds(20),
            TimeSpan.FromSeconds(40), TimeSpan.FromSeconds(60),
        };

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
                            
                            // Check if any source is marked as running (RTSP mode only)
                            var runningSource = viewModel.Sources?.FirstOrDefault(src => src.IsRunning);
                            if (runningSource != null && viewModel.CurrentCaptureMode == CaptureMode.RTSPStream)
                            {
                                // SetView, called from this same Loaded event, has usually started the
                                // stream already. Starting again here created a second player one
                                // second after the first on every tab switch - two RTSP sessions on a
                                // camera that grants few, and the first torn down for nothing.
                                if (IsStreamAlive())
                                {
                                    Logger.Debug("View reloaded with the RTSP stream already playing - no restart needed");
                                    return;
                                }

                                Logger.Info($"🔄 View reloaded with running RTSP stream - restarting playback for {runningSource.FullUrl}");
                                // Restart the stream only if we're still on the UI thread and view is loaded
                                if (this.IsLoaded)
                                {
                                    Logger.Info($"Attempting StartStreamAsync with URL: {runningSource.FullUrl}");
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
                        UpdateVideoHostLayoutToFill();
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
                    if (_pendingStart is { } pending)
                    {
                        _pendingStart = null;
                        Logger.Info("AI Weather view is visible again - starting the deferred RTSP playback");
                        _ = StartStreamAsync(pending.url, pending.user, pending.password);
                    }

                    Dispatcher.BeginInvoke(new Action(() =>
                    {
                        try
                        {
                            UpdateVideoHostLayoutToFill();
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

        public void StartStream(string rtspUrl, string? username = null, string? password = null)
        {
            // Backward-compatible fire-and-forget entrypoint used by the ViewModel.
            _ = StartStreamAsync(rtspUrl, username, password);
        }

        public async Task StartStreamAsync(string rtspUrl, string? username = null, string? password = null, CancellationToken cancellationToken = default)
        {
            if (!Dispatcher.CheckAccess())
            {
                await Dispatcher.InvokeAsync(() => StartStreamAsync(rtspUrl, username, password, cancellationToken)).Task.Unwrap();
                return;
            }

            await _streamGate.WaitAsync(cancellationToken);
            try
            {
                if (_isStartingStream)
                {
                    Logger.Warning("StartStream ignored because a start is already in progress");
                    return;
                }

                _isStartingStream = true;

                Logger.Info($"StartStream called with URL: {RedactRtspCredentials(rtspUrl)}");
                Logger.Info($"Username: '{username ?? "(null)"}', Password length: {password?.Length ?? 0}");

                if (_libVLC == null)
                {
                    Logger.Error("LibVLC not initialized - cannot start stream");
                    return;
                }

                if (VideoPanel == null)
                {
                    Logger.Error("VideoPanel is null - XAML element not found!");
                    return;
                }

                Logger.Info($"VideoPanel found. Size: {VideoPanel.ActualWidth}x{VideoPanel.ActualHeight}");

                _lastStart = (rtspUrl, username, password);
                CancelReconnect();

                // NINA raises Loaded for views it is rebuilding off-screen (every equipment
                // refresh on 3.3 does). Off-screen there is no window to render into, and
                // waiting for one only produced "handle never became available" four times a
                // night. Park the request; IsVisibleChanged picks it up.
                if (!IsOnScreen())
                {
                    _pendingStart = (rtspUrl, username, password);
                    Logger.Info("AI Weather view is not on screen - RTSP playback deferred until it is");
                    return;
                }

                // Cancel any previous start loop before tearing down the current player/host.
                _startCts?.Cancel();
                _startCts?.Dispose();
                _startCts = null;

                await StopStreamCoreAsync();

                // Only now: StopStreamCoreAsync marks the stop as ours, and from here on a
                // Stopped/EndReached event is the stream being lost, which must reconnect.
                _stopRequested = false;

                // Create a fresh CTS for this start attempt. StopStreamCoreAsync clears _startCts.
                _startCts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
                var startToken = _startCts.Token;

                Logger.Info("Creating MediaPlayer and VideoHost...");
                Logger.Debug("Creating LibVLC MediaPlayer...");

                var player = new MediaPlayer(_libVLC)
                {
                    Volume = 0,
                    EnableHardwareDecoding = true
                };

                Logger.Debug("MediaPlayer created");

                VideoPanel.Visibility = Visibility.Visible;
                CameraImage.Visibility = Visibility.Collapsed;

                Logger.Debug("Creating VideoHwndHost...");
                _videoHost = new VideoHwndHost(player)
                {
                    HorizontalAlignment = HorizontalAlignment.Left,
                    VerticalAlignment = VerticalAlignment.Top
                };
                Logger.Info($"VideoHost created (Panel: {VideoPanel.ActualWidth}x{VideoPanel.ActualHeight})");

                Logger.Info("Adding VideoHost to VideoPanel...");
                VideoPanel.Children.Add(_videoHost);
                _videoHost.Margin = new Thickness(0);

                await Dispatcher.Yield(DispatcherPriority.Loaded);

                IntPtr hwnd = IntPtr.Zero;
                for (int attempt = 0; attempt < 100; attempt++)
                {
                    startToken.ThrowIfCancellationRequested();
                    hwnd = _videoHost.Handle;
                    if (hwnd != IntPtr.Zero)
                    {
                        break;
                    }
                    await Task.Delay(20, startToken);
                }

                Logger.Info($"VideoHost handle resolved: {hwnd}");
                if (hwnd == IntPtr.Zero)
                {
                    // The view left the screen while the host was being built. Not an error:
                    // the start is parked and resumes when the view is visible again.
                    Logger.Warning("VideoHost handle not available (view left the screen); RTSP playback deferred");
                    await StopStreamCoreAsync();
                    _pendingStart = (rtspUrl, username, password);
                    return;
                }

                player.EncounteredError += OnPlayerEncounteredError;
                player.EndReached += OnPlayerEndReached;
                player.Stopped += OnPlayerStopped;

                player.Hwnd = hwnd;
                Logger.Info($"Player Hwnd set to: {player.Hwnd}, Volume: {player.Volume}, HW decode: {player.EnableHardwareDecoding}");

                try
                {
                    UpdateVideoHostLayoutToFill();
                }
                catch (Exception ex)
                {
                    Logger.Debug($"VideoHost initial resize failed: {ex.Message}");
                }

                var playbackUrl = StripCredentials(rtspUrl);
                Logger.Info($"Creating media for URL: {RedactRtspCredentials(playbackUrl)}");

                _currentMedia?.Dispose();
                _currentMedia = new Media(_libVLC, playbackUrl, FromType.FromLocation);
                _currentMedia.AddOption(":network-caching=1000");
                _currentMedia.AddOption(":rtsp-tcp");
                _currentMedia.AddOption(":no-audio");
                // Credentials go in as VLC options rather than in the URI: VLC deprecates the
                // latter (it logged so on every start), and an option never needs
                // percent-encoding of the '@' and ':' that camera passwords are full of.
                if (!string.IsNullOrWhiteSpace(username))
                {
                    _currentMedia.AddOption($":rtsp-user={username}");
                    _currentMedia.AddOption($":rtsp-pwd={password ?? string.Empty}");
                }
                Logger.Info($"Media created with options: network-caching=1000, rtsp-tcp, no-audio{(string.IsNullOrWhiteSpace(username) ? string.Empty : ", rtsp-user/rtsp-pwd")}");

                Logger.Info("Starting playback...");
                var playResult = player.Play(_currentMedia);
                Logger.Info($"Play() returned: {playResult}, Player state: {player.State}");

                if (!playResult)
                {
                    Logger.Error("Play() returned false - VLC refused to play media");
                    await StopStreamCoreAsync();
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
                        _reconnectAttempt = 0;
                        try
                        {
                            UpdateVideoHostLayoutToFill();
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
                }

                Logger.Info($"Started RTSP stream: {RedactRtspCredentials(playbackUrl)}");
            }
            catch (OperationCanceledException)
            {
                Logger.Info("StartStream canceled");
            }
            catch (UriFormatException ex)
            {
                Logger.Error($"Invalid RTSP URL format: {ex.Message}");
                Logger.Error("URL must be in format: rtsp://[username:password@]camera-ip[:port]/path");
                await StopStreamCoreAsync();
            }
            catch (Exception ex)
            {
                Logger.Error($"Failed to start RTSP stream: {ex.Message}", ex);
                Logger.Error("Common issues: Wrong URL, authentication failure, network error, or camera offline.");
                await StopStreamCoreAsync();
            }
            finally
            {
                _isStartingStream = false;
                _streamGate.Release();
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

            await _streamGate.WaitAsync();
            try
            {
                await StopStreamCoreAsync();
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
                _stopRequested = true;
                _pendingStart = null;
                CancelReconnect();

                _startCts?.Cancel();
                _startCts?.Dispose();
                _startCts = null;

                if (_videoHost != null)
                {
                    var host = _videoHost;
                    var player = host.Player;

                    try
                    {
                        if (player != null)
                        {
                            player.EncounteredError -= OnPlayerEncounteredError;
                            player.EndReached -= OnPlayerEndReached;
                            player.Stopped -= OnPlayerStopped;
                        }
                    }
                    catch
                    {
                        // best-effort
                    }

                    try
                    {
                        // Detach the native render target first to reduce odds of a blocking stop.
                        if (player != null)
                        {
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

        /// <summary>A player exists and is starting or playing: nothing to restart.</summary>
        private bool IsStreamAlive()
        {
            if (_isStartingStream) { return true; }
            var player = _videoHost?.Player;
            if (player == null) { return false; }
            try
            {
                var state = player.State;
                return state == VLCState.Opening || state == VLCState.Buffering || state == VLCState.Playing;
            }
            catch
            {
                return false;
            }
        }

        /// <summary>
        /// Whether this view is part of a window that is actually shown. IsVisible alone is
        /// not enough: a view NINA is rebuilding off-screen reports IsVisible while it has no
        /// presentation source, and an HwndHost cannot be built without one.
        /// </summary>
        private bool IsOnScreen()
        {
            return IsVisible && PresentationSource.FromVisual(this) != null;
        }

        private void OnPlayerEncounteredError(object? sender, EventArgs e) => OnPlaybackLost("the player reported an error");
        private void OnPlayerEndReached(object? sender, EventArgs e) => OnPlaybackLost("the stream ended");
        private void OnPlayerStopped(object? sender, EventArgs e) => OnPlaybackLost("the player stopped");

        /// <summary>
        /// Called on VLC's thread when playback ends for a reason that is not ours. The
        /// Tapo drops its RTSP session every few minutes; without this the preview stayed
        /// dark until the next tab switch while the safety monitor kept working underneath.
        /// </summary>
        private void OnPlaybackLost(string reason)
        {
            if (_stopRequested) { return; }
            Dispatcher.BeginInvoke(new Action(() => ScheduleReconnect(reason)), DispatcherPriority.Background);
        }

        private void ScheduleReconnect(string reason)
        {
            if (_stopRequested || _lastStart is not { } last || _reconnectCts != null) { return; }

            var pause = ReconnectPauses[Math.Min(_reconnectAttempt, ReconnectPauses.Length - 1)];
            _reconnectAttempt++;
            var attempt = _reconnectAttempt;
            Logger.Info($"RTSP preview lost ({reason}); reconnecting in {pause.TotalSeconds:F0}s (attempt {attempt})");

            var cts = new CancellationTokenSource();
            _reconnectCts = cts;
            _ = Task.Run(async () =>
            {
                try
                {
                    await Task.Delay(pause, cts.Token);
                    await Dispatcher.InvokeAsync(async () =>
                    {
                        if (ReferenceEquals(_reconnectCts, cts)) { _reconnectCts = null; }
                        if (cts.IsCancellationRequested || _stopRequested) { return; }
                        Logger.Info($"RTSP preview reconnect attempt {attempt}");
                        await StartStreamAsync(last.url, last.user, last.password);
                    }).Task.Unwrap();
                }
                catch (OperationCanceledException)
                {
                    // a stop or a fresh start superseded this reconnect
                }
                catch (Exception ex)
                {
                    Logger.Warning($"RTSP preview reconnect failed: {ex.Message}");
                }
                finally
                {
                    cts.Dispose();
                }
            });
        }

        private void CancelReconnect()
        {
            var cts = _reconnectCts;
            _reconnectCts = null;
            try { cts?.Cancel(); } catch { /* best-effort */ }
        }

        /// <summary>The URL without user:password@, which now travel as VLC options.</summary>
        private static string StripCredentials(string rtspUrl)
        {
            try
            {
                var uri = new Uri(rtspUrl);
                if (string.IsNullOrEmpty(uri.UserInfo)) { return rtspUrl; }
                return new UriBuilder(uri) { UserName = string.Empty, Password = string.Empty }.Uri.ToString();
            }
            catch
            {
                return rtspUrl;
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

        private void PasswordBox_PasswordChanged(object sender, RoutedEventArgs e)
        {
            if (sender is PasswordBox passwordBox && passwordBox.DataContext is CameraSource source)
            {
                source.Password = passwordBox.Password;
                Logger.Debug($"Password updated for camera source: {RedactRtspCredentials(source.MediaUrl)}");
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
            if (string.IsNullOrEmpty(input))
            {
                return string.Empty;
            }

            // Redact credentials in URLs like: rtsp://user:pass@host/...
            // Keep username, replace password with ***.
            try
            {
                return System.Text.RegularExpressions.Regex.Replace(
                    input,
                    @"(?i)\b(rtsps?:\/\/)(?<user>[^:@\/\s]+):[^@\s]+@",
                    "$1${user}:***@");
            }
            catch
            {
                return input;
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
                UpdateVideoHostLayoutToFill();
            }
            catch (Exception ex)
            {
                Logger.Debug($"VideoPanel resize handler failed: {ex.Message}");
            }
        }

        // Resize and position the native video output so it fills the panel (cropping as needed).
        // This matches the "no bands" look of Berg's RTSP Client plugin.
        private void UpdateVideoHostLayoutToFill()
        {
            if (!Dispatcher.CheckAccess())
            {
                Dispatcher.Invoke(UpdateVideoHostLayoutToFill);
                return;
            }

            if (_videoHost == null || VideoPanel == null)
            {
                return;
            }

            var panelWidth = VideoPanel.ActualWidth;
            var panelHeight = VideoPanel.ActualHeight;
            if (panelWidth <= 1 || panelHeight <= 1)
            {
                return;
            }

            // Try to detect video dimensions from VLC.
            double videoWidth = 0;
            double videoHeight = 0;
            try
            {
                uint vw = 0;
                uint vh = 0;
                _videoHost.Player.Size(0, ref vw, ref vh);
                videoWidth = vw;
                videoHeight = vh;
            }
            catch
            {
                // best-effort
            }

            if (videoWidth <= 0 || videoHeight <= 0)
            {
                // Fallback: fill the panel without cropping until we know video size.
                _videoHost.Width = panelWidth;
                _videoHost.Height = panelHeight;
                _videoHost.Margin = new Thickness(0);
                _videoHost.ResizeTo(panelWidth, panelHeight);

                try
                {
                    // Let VLC pick default scaling when we don't know the video size yet.
                    _videoHost.Player.Scale = 0;
                }
                catch
                {
                    // best-effort
                }
                return;
            }

            // Keep the HWND viewport exactly the size of the panel.
            // Then ask VLC to scale the video up so it fully covers the viewport (cropping as needed).
            // This avoids relying on WPF clipping (HwndHost isn't reliably clipped) and prevents
            // letterboxing/"white bands" after navigation/relayout.
            _videoHost.HorizontalAlignment = HorizontalAlignment.Left;
            _videoHost.VerticalAlignment = VerticalAlignment.Top;
            _videoHost.Margin = new Thickness(0);
            _videoHost.Width = panelWidth;
            _videoHost.Height = panelHeight;
            _videoHost.ResizeTo(panelWidth, panelHeight);

            var scaleToFill = Math.Max(panelWidth / videoWidth, panelHeight / videoHeight);
            if (double.IsNaN(scaleToFill) || double.IsInfinity(scaleToFill) || scaleToFill <= 0.001)
            {
                scaleToFill = 1.0;
            }

            try
            {
                _videoHost.Player.Scale = (float)scaleToFill;
            }
            catch
            {
                // best-effort
            }

            // Force layout update
            _videoHost.UpdateLayout();
            VideoPanel.UpdateLayout();
        }
    }
}
