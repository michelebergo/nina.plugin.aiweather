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
                    if (message.Contains("unsupported control query", StringComparison.OrdinalIgnoreCase)
                        || message.Contains("surface dimensions", StringComparison.OrdinalIgnoreCase)
                        || message.Contains("SetThumbNailClip failed", StringComparison.OrdinalIgnoreCase))
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

                // Cancel any previous start loop before tearing down the current player/host.
                _startCts?.Cancel();
                _startCts?.Dispose();
                _startCts = null;

                await StopStreamCoreAsync();

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
                    Logger.Error("VideoHost handle never became available; aborting playback setup");
                    await StopStreamCoreAsync();
                    return;
                }

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

                var playbackUrl = BuildAuthenticatedUrl(rtspUrl, username, password);
                Logger.Info($"Creating media for URL: {RedactRtspCredentials(playbackUrl)}");

                _currentMedia?.Dispose();
                _currentMedia = new Media(_libVLC, playbackUrl, FromType.FromLocation);
                _currentMedia.AddOption(":network-caching=1000");
                _currentMedia.AddOption(":rtsp-tcp");
                _currentMedia.AddOption(":no-audio");
                Logger.Info("Media created with options: network-caching=1000, rtsp-tcp, no-audio");

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
                _startCts?.Cancel();
                _startCts?.Dispose();
                _startCts = null;

                if (_videoHost != null)
                {
                    var host = _videoHost;
                    var player = host.Player;

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
