using Emgu.CV;
using Emgu.CV.CvEnum;
using Emgu.CV.Structure;
using System;
using System.Drawing;
using System.IO;
using System.Threading;
using System.Threading.Tasks;
using NINA.Core.Utility;

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

                await Task.Run(() =>
                {
                    lock (_captureLock)
                    {
                        _capture?.Dispose();
                        Logger.Debug($"RtspCaptureService - Creating VideoCapture with URL: {RedactRtspUrl(rtspUrl)}");
                        _capture = new VideoCapture(rtspUrl);
                        Logger.Debug($"RtspCaptureService - VideoCapture created, IsOpened: {_capture?.IsOpened}");
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
                        using var frame = new Mat();
                        if (_capture.Read(frame) && !frame.IsEmpty)
                        {
                            // Convert Mat to Bitmap
                            var bitmap = new Bitmap(frame.Width, frame.Height);
                            var data = frame.GetRawData();
                            
                            // Quick conversion - copy data
                            var bmpData = bitmap.LockBits(new Rectangle(0, 0, bitmap.Width, bitmap.Height),
                                System.Drawing.Imaging.ImageLockMode.WriteOnly,
                                System.Drawing.Imaging.PixelFormat.Format24bppRgb);
                            
                            System.Runtime.InteropServices.Marshal.Copy(data, 0, bmpData.Scan0, data.Length);
                            bitmap.UnlockBits(bmpData);
                            
                            FrameCaptured?.Invoke(this, bitmap);
                            return bitmap;
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
                    frame.Save(filePath, System.Drawing.Imaging.ImageFormat.Jpeg);
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

        public void Dispose()
        {
            if (_isDisposed) return;

            lock (_captureLock)
            {
                _capture?.Dispose();
                _capture = null;
            }

            _isDisposed = true;
            GC.SuppressFinalize(this);
        }
    }
}
