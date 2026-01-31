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
    public class RtspCaptureService : IDisposable
    {
        private VideoCapture? _capture;
        private readonly object _captureLock = new object();
        private bool _isDisposed = false;

        private string _lastInitializedRtspUrl = string.Empty;

        public bool IsInitialized
        {
            get
            {
                lock (_captureLock)
                {
                    return _capture != null && _capture.IsOpened;
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
                return _capture != null
                    && _capture.IsOpened
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
                Logger.Info($"RtspCaptureService - Initializing RTSP connection to: {RedactRtspUrl(rtspUrl)}");

                _lastInitializedRtspUrl = rtspUrl;

                try
                {
                    if (Uri.TryCreate(rtspUrl, UriKind.Absolute, out var uri))
                    {
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

                await Task.Run(() =>
                {
                    lock (_captureLock)
                    {
                        _capture?.Dispose();
                        Logger.Debug($"RtspCaptureService - Creating VideoCapture with URL: {RedactRtspUrl(rtspUrl)}");
                        // Prefer FFmpeg backend for RTSP stability.
                        try
                        {
                            _capture = new VideoCapture(rtspUrl, VideoCapture.API.Ffmpeg);
                        }
                        catch
                        {
                            // Fallback to default backend if FFmpeg preference isn't available.
                            _capture = new VideoCapture(rtspUrl);
                        }
                        Logger.Debug($"RtspCaptureService - VideoCapture created, IsOpened: {_capture?.IsOpened}");

                        // Buffer tuning is backend/version dependent; keep best-effort minimal here.
                    }
                }, cancellationToken);

                if (_capture == null || !_capture.IsOpened)
                {
                    var error = $"Failed to open RTSP stream: {RedactRtspUrl(rtspUrl)}. IsOpened = {_capture?.IsOpened}, _capture is null = {_capture == null}";
                    Logger.Error(error);
                    ErrorOccurred?.Invoke(this, error);
                    return false;
                }

                Logger.Info($"RtspCaptureService - RTSP connection established successfully to {RedactRtspUrl(rtspUrl)}");
                return true;
            }
            catch (Exception ex)
            {
                Logger.Error($"RtspCaptureService - Error initializing RTSP stream: {ex.Message}", ex);
                ErrorOccurred?.Invoke(this, ex.Message);
                return false;
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

        /// <summary>
        /// Capture a single frame from the RTSP stream
        /// </summary>
        public async Task<Bitmap?> CaptureFrameAsync(CancellationToken cancellationToken = default)
        {
            try
            {
                if (_capture == null || !_capture.IsOpened)
                {
                    Logger.Warning("RTSP capture is not initialized");
                    return null;
                }

                return await Task.Run(() =>
                {
                    lock (_captureLock)
                    {
                        // RTSP sources can return a few empty frames initially.
                        // Retry briefly to avoid reporting capture failure while video is actually playing.
                        for (var attempt = 0; attempt < 25; attempt++)
                        {
                            using var frame = new Mat();

                            bool ok;
                            try
                            {
                                ok = _capture.Read(frame);
                            }
                            catch (Exception ex)
                            {
                                Logger.Warning($"RTSP read failed (attempt {attempt + 1}/25): {ex.Message}");
                                ok = false;
                            }

                            if (!ok || frame.IsEmpty)
                            {
                                Thread.Sleep(40);
                                continue;
                            }

                            try
                            {
                                var bitmap = ConvertMatToBgr24Bitmap(frame);
                                FrameCaptured?.Invoke(this, bitmap);
                                return bitmap;
                            }
                            catch (Exception ex)
                            {
                                Logger.Error($"Error converting RTSP frame to bitmap: {ex.Message}");
                                return null;
                            }
                        }

                        Logger.Warning("RTSP capture returned empty frames after retries");

                        // Fallback: if OpenCV/FFmpeg can't decode frames but VLC can play the stream,
                        // take a LibVLC snapshot (headless) and use that for analysis.
                        if (!string.IsNullOrWhiteSpace(_lastInitializedRtspUrl))
                        {
                            try
                            {
                                Logger.Info($"RtspCaptureService - Falling back to LibVLC snapshot for analysis: {RedactRtspUrl(_lastInitializedRtspUrl)}");
                                return CaptureFrameViaVlcSnapshot(_lastInitializedRtspUrl, cancellationToken);
                            }
                            catch (Exception ex)
                            {
                                Logger.Warning($"RtspCaptureService - LibVLC snapshot fallback failed: {ex.Message}");
                            }
                        }

                        return null;
                    }
                }, cancellationToken);
            }
            catch (Exception ex)
            {
                Logger.Error($"Error capturing frame: {ex.Message}", ex);
                ErrorOccurred?.Invoke(this, ex.Message);
                return null;
            }
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

        public void Dispose()
        {
            if (_isDisposed) return;

            lock (_captureLock)
            {
                _capture?.Dispose();
                _capture = null;
                _lastInitializedRtspUrl = string.Empty;
            }

            _isDisposed = true;
            GC.SuppressFinalize(this);
        }
    }
}
