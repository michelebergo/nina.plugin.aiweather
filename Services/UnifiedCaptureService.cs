using System;
using System.Drawing;
using System.Threading;
using System.Threading.Tasks;
using AIWeather.Models;
using NINA.Core.Utility;
using NINA.Equipment.Interfaces.Mediator;

namespace AIWeather.Services
{
    /// <summary>
    /// Unified image capture service that handles RTSP, INDI camera, and folder watch modes
    /// </summary>
    public class UnifiedCaptureService
    {
        private readonly RtspCaptureService _rtspService;
        private readonly INDICameraCapture _indiService; // Not nullable - always initialized
        private readonly FolderWatchCapture _folderService;
        private CaptureMode _currentMode = CaptureMode.RTSPStream;
        private string _rtspUrl = "";
        private string _rtspUsername = "";
        private string _rtspPassword = "";

        public UnifiedCaptureService(ICameraMediator? cameraMediator = null)
        {
            _rtspService = new RtspCaptureService();
            _indiService = new INDICameraCapture(cameraMediator); // Always create - HTTP download doesn't need mediator
            _folderService = new FolderWatchCapture();
        }

        public CaptureMode CurrentMode
        {
            get => _currentMode;
            set => _currentMode = value;
        }

        /// <summary>
        /// Configures the RTSP stream settings
        /// </summary>
        public void ConfigureRTSP(string url, string? username = null, string? password = null)
        {
            _rtspUrl = url;
            _rtspUsername = username ?? "";
            _rtspPassword = password ?? "";
        }

        /// <summary>
        /// Configures the HTTP Image Download settings (URL and optional credentials)
        /// </summary>
        public void ConfigureINDI(string imageUrl, string? username = null, string? password = null)
        {
            _indiService.DeviceName = imageUrl;
            _indiService.Username = username ?? "";
            _indiService.Password = password ?? "";
        }

        /// <summary>
        /// Configures the folder watch path
        /// </summary>
        public void ConfigureFolderWatch(string folderPath)
        {
            _folderService.FolderPath = folderPath;
        }

        /// <summary>
        /// Captures an image using the currently configured mode
        /// </summary>
        public async Task<Bitmap> CaptureImageAsync(CancellationToken ct = default)
        {
            switch (_currentMode)
            {
                case CaptureMode.RTSPStream:
                    return await CaptureFromRTSPAsync(ct);

                case CaptureMode.INDICamera:
                    return await CaptureFromINDIAsync(ct);

                case CaptureMode.FolderWatch:
                    return await CaptureFromFolderAsync();

                default:
                    Logger.Error($"Unknown capture mode: {_currentMode}");
                    return null;
            }
        }

        private async Task<Bitmap> CaptureFromRTSPAsync(CancellationToken ct)
        {
            try
            {
                var authenticatedUrl = BuildAuthenticatedUrl(_rtspUrl, _rtspUsername, _rtspPassword);

                if (!_rtspService.IsInitializedFor(authenticatedUrl))
                {
                    if (string.IsNullOrWhiteSpace(_rtspUrl))
                    {
                        Logger.Warning("RTSP capture requested but RTSP URL is empty");
                        return null;
                    }
                    Logger.Info($"UnifiedCaptureService - Initializing RTSP capture for analysis: {RedactRtspUrl(authenticatedUrl)}");

                    var ok = await _rtspService.InitializeAsync(authenticatedUrl, ct);
                    if (!ok)
                    {
                        Logger.Error($"UnifiedCaptureService - Failed to initialize RTSP capture for analysis: {RedactRtspUrl(authenticatedUrl)}");
                        return null;
                    }
                }

                return await _rtspService.CaptureFrameAsync(ct);
            }
            catch (Exception ex)
            {
                Logger.Error($"Error capturing from RTSP stream: {ex.Message}");
                return null;
            }
        }

        private static string BuildAuthenticatedUrl(string rtspUrl, string username, string password)
        {
            if (string.IsNullOrEmpty(username))
            {
                return rtspUrl;
            }

            if (!rtspUrl.StartsWith("rtsp://", StringComparison.OrdinalIgnoreCase))
            {
                return rtspUrl;
            }

            var urlWithoutProtocol = rtspUrl.Substring(7); // Remove "rtsp://"
            return $"rtsp://{username}:{password}@{urlWithoutProtocol}";
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
                // best-effort
            }

            return url;
        }

        private async Task<Bitmap> CaptureFromINDIAsync(CancellationToken ct)
        {
            try
            {
                if (!_indiService.IsConnected())
                {
                    Logger.Warning("INDI camera not connected - attempting to capture from HTTP URL");
                    // Fall through - will attempt capture anyway
                }

                return await _indiService.CaptureImageAsync(ct);
            }
            catch (Exception ex)
            {
                Logger.Error($"Error capturing from INDI/HTTP camera: {ex.Message}");
                return null;
            }
        }

        private async Task<Bitmap> CaptureFromFolderAsync()
        {
            try
            {
                if (!_folderService.IsValid())
                {
                    Logger.Warning($"Folder watch path is invalid or doesn't exist: {_folderService.FolderPath}");
                    return null;
                }

                return await _folderService.CaptureImageAsync();
            }
            catch (Exception ex)
            {
                Logger.Error($"Error capturing from folder: {ex.Message}");
                return null;
            }
        }

        /// <summary>
        /// Checks if the current capture mode is available and configured
        /// </summary>
        public bool IsConfigured()
        {
            switch (_currentMode)
            {
                case CaptureMode.RTSPStream:
                    return !string.IsNullOrEmpty(_rtspUrl);

                case CaptureMode.INDICamera:
                    return _indiService != null && _indiService.IsConnected();

                case CaptureMode.FolderWatch:
                    return _folderService.IsValid();

                default:
                    return false;
            }
        }

        /// <summary>
        /// Saves a captured image to disk
        /// </summary>
        public async Task<bool> SaveImageAsync(Bitmap image, string filePath, CancellationToken ct = default)
        {
            // Use RTSP service's save method as it works for any Bitmap
            return await _rtspService.SaveFrameAsync(image, filePath, ct);
        }

        /// <summary>
        /// Disposes resources used by the capture services
        /// </summary>
        public void Dispose()
        {
            _rtspService?.Dispose();
        }
    }
}
