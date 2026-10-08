using Emgu.CV;
using Emgu.CV.CvEnum;
using Emgu.CV.Structure;
using System;
using System.Drawing;
using System.IO;
using System.Threading;
using System.Threading.Tasks;
using NINA.Core.Utility;
using System.Drawing.Imaging;
using System.Runtime.InteropServices;
using LibVLCSharp.Shared;

namespace AIWeather.Services
{
    /// <summary>
    /// Service for capturing frames from RTSP stream
    /// </summary>
    [System.Runtime.Versioning.SupportedOSPlatform("windows")]
    public class RtspCaptureService : IRtspFrameCaptureService
    {
        private VideoCapture? _capture;
        private readonly object _captureLock = new object();
        private readonly LatestRtspFrameBuffer _latestFrames = new LatestRtspFrameBuffer();
        private CancellationTokenSource? _readerCancellation;
        private Task? _readerTask;
        private bool _readerIsRunning;
        private bool _isDisposed = false;

        private string _lastInitializedRtspUrl = string.Empty;
        private string _captureSessionId = string.Empty;

        private static readonly TimeSpan FirstFrameTimeout = TimeSpan.FromSeconds(15);
        private static readonly TimeSpan FreshFrameTimeout = TimeSpan.FromSeconds(5);
        private static readonly TimeSpan MaximumFrameAge = TimeSpan.FromSeconds(10);
        private static readonly TimeSpan PublishInterval = TimeSpan.FromMilliseconds(500);

        // Only warn once per address, including automatic decoder reconnects.
        private string _warnedUrl = string.Empty;

        public bool IsInitialized
        {
            get
            {
                lock (_captureLock)
                {
                    return !_isDisposed
                        && _capture != null
                        && _readerIsRunning;
                }
            }
        }

        public bool IsInitializedFor(string rtspUrl)
        {
            if (string.IsNullOrWhiteSpace(rtspUrl))
            {
                return false;
            }

            lock (_captureLock)
            {
                return !_isDisposed
                    && _capture != null
                    && _readerIsRunning
                    && string.Equals(_lastInitializedRtspUrl, rtspUrl, StringComparison.Ordinal);
            }
        }

        public event EventHandler<Bitmap>? FrameCaptured;
        public event EventHandler<string>? ErrorOccurred;

        /// <summary>
        /// Initialize the RTSP stream connection
        /// </summary>
        public async Task<bool> InitializeAsync(string rtspUrl, CancellationToken cancellationToken = default)
        {
            try
            {
                Logger.Debug($"RtspCaptureService - Opening RTSP session: {RedactRtspUrl(rtspUrl)}");

                Reset();

                lock (_captureLock)
                {
                    if (_isDisposed)
                    {
                        throw new ObjectDisposedException(nameof(RtspCaptureService));
                    }
                }

                try
                {
                    if (!string.Equals(_warnedUrl, rtspUrl, StringComparison.Ordinal)
                        && Uri.TryCreate(rtspUrl, UriKind.Absolute, out var uri))
                    {
                        _warnedUrl = rtspUrl;
                        if (string.IsNullOrWhiteSpace(uri.AbsolutePath) || uri.AbsolutePath == "/")
                        {
                            Logger.Warning($"RtspCaptureService - RTSP URL has no path component. Many cameras require a path like /stream or /live. URL: {RedactRtspUrl(rtspUrl)}");
                        }

                        if (uri.IsDefaultPort)
                        {
                            Logger.Debug($"RtspCaptureService - RTSP URL does not specify a port (default is usually 554). URL: {RedactRtspUrl(rtspUrl)}");
                        }
                    }
                }
                catch
                {
                    // best-effort
                }

                var capture = await Task.Run(() =>
                {
                    Logger.Debug($"RtspCaptureService - Creating VideoCapture with URL: {RedactRtspUrl(rtspUrl)}");
                    try
                    {
                        return new VideoCapture(rtspUrl, VideoCapture.API.Ffmpeg);
                    }
                    catch
                    {
                        // Fallback to default backend if FFmpeg preference isn't available.
                        return new VideoCapture(rtspUrl);
                    }
                }, cancellationToken);

                Logger.Debug($"RtspCaptureService - VideoCapture created, IsOpened: {capture.IsOpened}");
                if (!capture.IsOpened)
                {
                    var error = $"Failed to open RTSP stream: {RedactRtspUrl(rtspUrl)}. IsOpened = {capture.IsOpened}";
                    Logger.Error(error);
                    ErrorOccurred?.Invoke(this, error);
                    capture.Dispose();
                    return false;
                }

                var firstFrame = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
                var readerCancellation = new CancellationTokenSource();

                lock (_captureLock)
                {
                    if (_isDisposed)
                    {
                        readerCancellation.Dispose();
                        capture.Dispose();
                        throw new ObjectDisposedException(nameof(RtspCaptureService));
                    }

                    _capture = capture;
                    _lastInitializedRtspUrl = rtspUrl;
                    _captureSessionId = Guid.NewGuid().ToString("N")[..8];
                    _readerCancellation = readerCancellation;
                    _readerIsRunning = true;
                    _readerTask = Task.Factory.StartNew(
                        () => ReaderLoop(capture, readerCancellation.Token, firstFrame),
                        CancellationToken.None,
                        TaskCreationOptions.LongRunning,
                        TaskScheduler.Default);
                }

                bool receivedFirstFrame;
                using (var startupTimeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken))
                {
                    startupTimeout.CancelAfter(FirstFrameTimeout);
                    try
                    {
                        receivedFirstFrame = await firstFrame.Task.WaitAsync(startupTimeout.Token);
                    }
                    catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
                    {
                        receivedFirstFrame = false;
                    }
                }

                if (!receivedFirstFrame)
                {
                    var error = $"RTSP stream opened but no decodable frame arrived within {FirstFrameTimeout.TotalSeconds:0} seconds: {RedactRtspUrl(rtspUrl)}";
                    Logger.Warning(error);
                    ErrorOccurred?.Invoke(this, error);
                    Reset();
                    return false;
                }

                Logger.Info($"RtspCaptureService - RTSP connection established with continuous latest-frame reader: {RedactRtspUrl(rtspUrl)}");
                return true;
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
                Reset();
                throw;
            }
            catch (Exception ex)
            {
                Reset();
                Logger.Error($"RtspCaptureService - Error initializing RTSP stream: {ex.Message}", ex);
                ErrorOccurred?.Invoke(this, ex.Message);
                return false;
            }
        }

        private void ReaderLoop(
            VideoCapture capture,
            CancellationToken cancellationToken,
            TaskCompletionSource<bool> firstFrame)
        {
            var consecutiveFailures = 0;
            var lastPublishedUtc = DateTime.MinValue;

            try
            {
                using var frame = new Mat();
                while (!cancellationToken.IsCancellationRequested)
                {
                    bool ok;
                    try
                    {
                        ok = capture.Read(frame);
                    }
                    catch (Exception ex) when (cancellationToken.IsCancellationRequested)
                    {
                        Logger.Debug($"RtspCaptureService - Reader stopped during reset: {ex.Message}");
                        break;
                    }
                    catch (Exception ex)
                    {
                        Logger.Warning($"RtspCaptureService - Continuous RTSP read failed: {ex.Message}");
                        ok = false;
                    }

                    if (!ok || frame.IsEmpty)
                    {
                        consecutiveFailures++;
                        if (consecutiveFailures >= 25)
                        {
                            Logger.Warning("RtspCaptureService - Continuous reader stopped after 25 empty/failed frames");
                            break;
                        }

                        Thread.Sleep(40);
                        continue;
                    }

                    consecutiveFailures = 0;
                    var receivedUtc = DateTime.UtcNow;
                    if (receivedUtc - lastPublishedUtc < PublishInterval)
                    {
                        continue;
                    }

                    Bitmap? bitmap = null;
                    try
                    {
                        bitmap = ConvertMatToBgr24Bitmap(frame);
                        var accepted = false;
                        lock (_captureLock)
                        {
                            if (!_isDisposed
                                && ReferenceEquals(_capture, capture)
                                && !cancellationToken.IsCancellationRequested)
                            {
                                _latestFrames.Publish(bitmap, receivedUtc);
                                accepted = true;
                                bitmap = null; // ownership transferred to the latest-frame buffer
                            }
                        }

                        if (!accepted)
                        {
                            break;
                        }

                        lastPublishedUtc = receivedUtc;
                        firstFrame.TrySetResult(true);
                    }
                    catch (Exception ex)
                    {
                        Logger.Warning($"RtspCaptureService - Could not publish decoded RTSP frame: {ex.Message}");
                    }
                    finally
                    {
                        bitmap?.Dispose();
                    }
                }
            }
            finally
            {
                firstFrame.TrySetResult(false);
                lock (_captureLock)
                {
                    if (ReferenceEquals(_capture, capture))
                    {
                        _readerIsRunning = false;
                    }
                }

                // VideoCapture is a native object.  Only the reader thread may dispose it:
                // disposing from Reset while Read() is in progress can crash the whole NINA
                // process with an AccessViolationException rather than a managed exception.
                try
                {
                    capture.Dispose();
                }
                catch (Exception ex)
                {
                    Logger.Debug($"RtspCaptureService - Decoder dispose after reader exit: {ex.Message}");
                }
            }
        }

        private static string RedactRtspUrl(string url)
        {
            return LogRedactor.RedactRtspUrl(url);
        }

        /// <summary>
        /// Capture a single frame from the RTSP stream
        /// </summary>
        public async Task<Bitmap?> CaptureFrameAsync(CancellationToken cancellationToken = default)
        {
            long minimumSequence;
            string fallbackUrl;
            string captureSessionId;
            lock (_captureLock)
            {
                if (_isDisposed || _capture == null || !_readerIsRunning)
                {
                    Logger.Warning("RTSP capture is not initialized");
                    return null;
                }

                minimumSequence = _latestFrames.Sequence;
                fallbackUrl = _lastInitializedRtspUrl;
                captureSessionId = _captureSessionId;
            }

            try
            {
                var deadlineUtc = DateTime.UtcNow + FreshFrameTimeout;
                while (DateTime.UtcNow < deadlineUtc)
                {
                    cancellationToken.ThrowIfCancellationRequested();

                    if (_latestFrames.TryCloneNewerThan(
                        minimumSequence,
                        DateTime.UtcNow,
                        MaximumFrameAge,
                        out var bitmap,
                        out var sequence,
                        out _,
                        out var age))
                    {
                        Logger.Debug(
                            $"RtspCaptureService - Session {captureSessionId} delivered latest RTSP frame sequence {sequence}, " +
                            $"receive age {age.TotalMilliseconds:0} ms");
                        FrameCaptured?.Invoke(this, bitmap!);
                        return bitmap;
                    }

                    bool readerStillRunning;
                    lock (_captureLock)
                    {
                        readerStillRunning = _readerIsRunning;
                    }

                    if (!readerStillRunning)
                    {
                        break;
                    }

                    await Task.Delay(25, cancellationToken);
                }

                Logger.Warning(
                    $"RtspCaptureService - No new frame arrived within {FreshFrameTimeout.TotalSeconds:0} seconds; " +
                    "the previous buffered frame will not be reused");

                // This fallback creates a new playback session and snapshot, so it cannot reuse
                // the stale OpenCV decoder queue. It remains bounded by its own timeouts.
                if (!string.IsNullOrWhiteSpace(fallbackUrl))
                {
                    try
                    {
                        Logger.Info($"RtspCaptureService - Falling back to LibVLC snapshot for analysis: {RedactRtspUrl(fallbackUrl)}");
                        var fallback = await Task.Run(
                            () => CaptureFrameViaVlcSnapshot(fallbackUrl, cancellationToken),
                            cancellationToken);
                        if (fallback != null)
                        {
                            FrameCaptured?.Invoke(this, fallback);
                        }
                        return fallback;
                    }
                    catch (Exception ex)
                    {
                        if (ex is OperationCanceledException && cancellationToken.IsCancellationRequested) throw;
                        Logger.Warning($"RtspCaptureService - LibVLC snapshot fallback failed: {ex.Message}");
                    }
                }
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
                throw;
            }
            catch (Exception ex)
            {
                Logger.Error($"Error capturing frame: {ex.Message}", ex);
                ErrorOccurred?.Invoke(this, ex.Message);
            }

            return null;
        }

        private static Bitmap? CaptureFrameViaVlcSnapshot(string rtspUrl, CancellationToken cancellationToken)
        {
            // LibVLC snapshot is synchronous, but we call it from a Task.Run already.
            // Keep it bounded with timeouts and cancellation checks.
            cancellationToken.ThrowIfCancellationRequested();

            Core.Initialize();

            // A few helpful options for RTSP stability.
            using var libVlc = new LibVLC(
                "--no-audio",
                "--intf=dummy",
                "--no-video-title-show",
                "--no-sub-autodetect-file",
                "--quiet");

            using var player = new MediaPlayer(libVlc)
            {
                Volume = 0,
                EnableHardwareDecoding = true
            };

            // IMPORTANT: Without an explicit video output handle, LibVLC may create its own top-level
            // window on some systems. That looks like a large streaming window that pops up each time
            // analysis runs. We avoid this by creating an invisible Win32 host window and assigning
            // it as the output.
            var hwnd = HiddenVlcHostWindow.Create();
            try
            {
                player.Hwnd = hwnd;

                using var media = new Media(libVlc, rtspUrl, FromType.FromLocation);
                media.AddOption(":rtsp-tcp");
                media.AddOption(":network-caching=300");
                media.AddOption(":clock-jitter=0");
                media.AddOption(":clock-synchro=0");
                media.AddOption(":no-audio");

                player.Media = media;

                if (!player.Play())
                {
                    throw new InvalidOperationException("LibVLC Play() returned false");
                }

                // Wait briefly for playback to start.
                var start = Environment.TickCount;
                while (!cancellationToken.IsCancellationRequested)
                {
                    if (player.IsPlaying)
                    {
                        break;
                    }

                    if (Environment.TickCount - start > 2500)
                    {
                        break;
                    }

                    Thread.Sleep(50);
                }

                cancellationToken.ThrowIfCancellationRequested();

                var tempPath = Path.Combine(Path.GetTempPath(), $"aiweather_rtsp_snapshot_{Guid.NewGuid():N}.png");
                try
                {
                    // Try a few snapshots; some streams need a moment before the first decodable frame.
                    for (var attempt = 0; attempt < 6; attempt++)
                    {
                        cancellationToken.ThrowIfCancellationRequested();

                        try
                        {
                            File.Delete(tempPath);
                        }
                        catch
                        {
                            // best-effort
                        }

                        var ok = player.TakeSnapshot(0, tempPath, 0, 0);
                        if (!ok)
                        {
                            Thread.Sleep(150);
                            continue;
                        }

                        // Wait for the file to be written.
                        for (var wait = 0; wait < 20; wait++)
                        {
                            cancellationToken.ThrowIfCancellationRequested();

                            try
                            {
                                var info = new FileInfo(tempPath);
                                if (info.Exists && info.Length > 0)
                                {
                                    using var img = Image.FromFile(tempPath);
                                    return new Bitmap(img);
                                }
                            }
                            catch
                            {
                                // keep waiting
                            }

                            Thread.Sleep(50);
                        }

                        Thread.Sleep(150);
                    }

                    return null;
                }
                finally
                {
                    try
                    {
                        player.Stop();
                    }
                    catch
                    {
                        // best-effort
                    }

                    try
                    {
                        if (File.Exists(tempPath))
                        {
                            File.Delete(tempPath);
                        }
                    }
                    catch
                    {
                        // best-effort
                    }
                }
            }
            finally
            {
                HiddenVlcHostWindow.Destroy(hwnd);
            }
        }

        private static class HiddenVlcHostWindow
        {
            // Keep implementation tiny and local to this file.
            // We reuse the built-in "Static" class (same approach as VideoHwndHost) and create a
            // hidden top-level window. LibVLC will render into it without creating its own UI.

            private const int WS_EX_TOOLWINDOW = 0x00000080;
            private const int WS_EX_NOACTIVATE = 0x08000000;
            private const int WS_POPUP = unchecked((int)0x80000000);

            private const int SW_HIDE = 0;

            public static IntPtr Create()
            {
                // Create 1x1 window and ensure it stays hidden.
                var hInstance = GetModuleHandle(null);
                var hwnd = CreateWindowEx(
                    WS_EX_TOOLWINDOW | WS_EX_NOACTIVATE,
                    "Static",
                    "",
                    WS_POPUP,
                    0,
                    0,
                    1,
                    1,
                    IntPtr.Zero,
                    IntPtr.Zero,
                    hInstance,
                    IntPtr.Zero);

                if (hwnd != IntPtr.Zero)
                {
                    ShowWindow(hwnd, SW_HIDE);
                }

                return hwnd;
            }

            public static void Destroy(IntPtr hwnd)
            {
                if (hwnd == IntPtr.Zero)
                {
                    return;
                }

                try
                {
                    DestroyWindow(hwnd);
                }
                catch
                {
                    // best-effort
                }
            }

            [DllImport("user32.dll", EntryPoint = "CreateWindowEx", SetLastError = true, CharSet = CharSet.Auto)]
            private static extern IntPtr CreateWindowEx(
                int dwExStyle,
                string lpClassName,
                string lpWindowName,
                int dwStyle,
                int x,
                int y,
                int nWidth,
                int nHeight,
                IntPtr hwndParent,
                IntPtr hMenu,
                IntPtr hInst,
                IntPtr lpParam);

            [DllImport("kernel32.dll", CharSet = CharSet.Auto, SetLastError = true)]
            private static extern IntPtr GetModuleHandle(string? lpModuleName);

            [DllImport("user32.dll", SetLastError = true)]
            [return: MarshalAs(UnmanagedType.Bool)]
            private static extern bool DestroyWindow(IntPtr hWnd);

            [DllImport("user32.dll", SetLastError = true)]
            [return: MarshalAs(UnmanagedType.Bool)]
            private static extern bool ShowWindow(IntPtr hWnd, int nCmdShow);
        }

        private static Bitmap ConvertMatToBgr24Bitmap(Mat src)
        {
            // Ensure we have 8-bit 3-channel BGR for Bitmap.Format24bppRgb.
            Mat working = src;
            Mat? converted = null;

            if (src.Depth != DepthType.Cv8U)
            {
                converted = new Mat();
                src.ConvertTo(converted, DepthType.Cv8U);
                working = converted;
            }

            if (working.NumberOfChannels == 4)
            {
                var bgr = new Mat();
                CvInvoke.CvtColor(working, bgr, ColorConversion.Bgra2Bgr);
                converted?.Dispose();
                converted = bgr;
                working = converted;
            }
            else if (working.NumberOfChannels == 1)
            {
                var bgr = new Mat();
                CvInvoke.CvtColor(working, bgr, ColorConversion.Gray2Bgr);
                converted?.Dispose();
                converted = bgr;
                working = converted;
            }

            if (working.NumberOfChannels != 3)
            {
                converted?.Dispose();
                throw new InvalidOperationException($"Unsupported channel count: {working.NumberOfChannels}");
            }

            var width = working.Width;
            var height = working.Height;
            var bitmap = new Bitmap(width, height, PixelFormat.Format24bppRgb);

            var rect = new Rectangle(0, 0, width, height);
            var bmpData = bitmap.LockBits(rect, ImageLockMode.WriteOnly, PixelFormat.Format24bppRgb);
            try
            {
                var srcStride = working.Step;
                var dstStride = bmpData.Stride;
                var rowBytes = width * 3;

                // Copy row-by-row (handles stride/padding correctly)
                var rowBuffer = new byte[rowBytes];
                var srcBase = working.DataPointer;
                var dstBase = bmpData.Scan0;

                for (int y = 0; y < height; y++)
                {
                    var srcRow = IntPtr.Add(srcBase, y * srcStride);
                    var dstRow = IntPtr.Add(dstBase, y * dstStride);
                    Marshal.Copy(srcRow, rowBuffer, 0, rowBytes);
                    Marshal.Copy(rowBuffer, 0, dstRow, rowBytes);
                }
            }
            finally
            {
                bitmap.UnlockBits(bmpData);
                converted?.Dispose();
            }

            return bitmap;
        }

        /// <summary>
        /// Save captured frame to file
        /// </summary>
        public async Task<bool> SaveFrameAsync(Bitmap frame, string filePath, CancellationToken cancellationToken = default)
        {
            try
            {
                await Task.Run(() =>
                {
                    var directory = Path.GetDirectoryName(filePath);
                    if (!string.IsNullOrEmpty(directory) && !Directory.Exists(directory))
                    {
                        Directory.CreateDirectory(directory);
                    }
                    
                    // Clone the bitmap to ensure it's in a format that can be saved
                    using (var clonedBitmap = new Bitmap(frame.Width, frame.Height, System.Drawing.Imaging.PixelFormat.Format24bppRgb))
                    {
                        using (var g = Graphics.FromImage(clonedBitmap))
                        {
                            g.DrawImage(frame, 0, 0, frame.Width, frame.Height);
                        }
                        
                        // Save with JPEG encoder and quality settings
                        var jpegEncoder = GetEncoder(System.Drawing.Imaging.ImageFormat.Jpeg);
                        var encoderParams = new System.Drawing.Imaging.EncoderParameters(1);
                        encoderParams.Param[0] = new System.Drawing.Imaging.EncoderParameter(
                            System.Drawing.Imaging.Encoder.Quality, 90L);
                        
                        clonedBitmap.Save(filePath, jpegEncoder, encoderParams);
                    }
                }, cancellationToken);

                Logger.Info($"Frame saved to: {filePath}");
                return true;
            }
            catch (OperationCanceledException)
            {
                Logger.Debug("Frame save cancelled (monitoring stopped)");
                return false;
            }
            catch (Exception ex)
            {
                Logger.Error($"Error saving frame: {ex.Message}", ex);
                return false;
            }
        }
        
        private System.Drawing.Imaging.ImageCodecInfo GetEncoder(System.Drawing.Imaging.ImageFormat format)
        {
            var codecs = System.Drawing.Imaging.ImageCodecInfo.GetImageDecoders();
            foreach (var codec in codecs)
            {
                if (codec.FormatID == format.Guid)
                {
                    return codec;
                }
            }
            return null;
        }

        /// <summary>
        /// Closes the active stream while keeping this service reusable. NINA can disconnect
        /// and reconnect the same singleton safety monitor many times in one process.
        /// </summary>
        public void Reset()
        {
            VideoCapture? capture;
            CancellationTokenSource? readerCancellation;
            Task? readerTask;

            lock (_captureLock)
            {
                capture = _capture;
                readerCancellation = _readerCancellation;
                readerTask = _readerTask;

                _capture = null;
                _readerCancellation = null;
                _readerTask = null;
                _readerIsRunning = false;
                _lastInitializedRtspUrl = string.Empty;
                _captureSessionId = string.Empty;
                _latestFrames.Clear();
            }

            try
            {
                readerCancellation?.Cancel();
            }
            catch
            {
                // best-effort
            }

            if (readerTask != null && Task.CurrentId != readerTask.Id)
            {
                try
                {
                    if (!readerTask.Wait(TimeSpan.FromSeconds(5)))
                    {
                        // Do not dispose a native VideoCapture concurrently with Read().  The
                        // detached reader owns it and will release it as soon as Read returns.
                        Logger.Warning(
                            "RtspCaptureService - Reader did not stop within 5 seconds; " +
                            "native decoder disposal is deferred to the reader thread");
                    }
                }
                catch
                {
                    // The session has already been detached; a late reader cannot publish to it.
                }
            }
            else if (readerTask == null)
            {
                // Initialization can create a decoder before its reader task is installed.
                capture?.Dispose();
            }

            readerCancellation?.Dispose();
        }

        public void Dispose()
        {
            lock (_captureLock)
            {
                if (_isDisposed)
                {
                    return;
                }

                _isDisposed = true;
            }

            Reset();
            _latestFrames.Dispose();

            GC.SuppressFinalize(this);
        }
    }
}
