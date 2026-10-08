using System;
using System.Drawing;
using System.Threading;
using System.Threading.Tasks;
using AIWeather.Models;
using NINA.Core.Utility;
using NINA.Equipment.Interfaces.Mediator;
using NINA.Image.Interfaces;

namespace AIWeather.Services
{
    /// <summary>
    /// Unified image capture service that handles RTSP, INDI camera, and folder watch modes
    /// </summary>
    [System.Runtime.Versioning.SupportedOSPlatform("windows")]
    public class UnifiedCaptureService : IDisposable
    {
        private readonly IRtspFrameCaptureService _rtspService;
        private readonly SharedRtspPreviewFrameProvider _sharedPreviewFrames;
        private readonly SemaphoreSlim _rtspCaptureGate = new SemaphoreSlim(1, 1);
        private readonly INDICameraCapture _indiService; // Not nullable - always initialized
        private readonly FolderWatchCapture _folderService;
        private CaptureMode _currentMode = CaptureMode.RTSPStream;
        private string _rtspUrl = "";
        private string _rtspUsername = "";
        private string _rtspPassword = "";
        private int _consecutiveRtspCaptureFailures;

        private const int RtspFailuresBeforeReconnect = 2;

        public UnifiedCaptureService(ICameraMediator? cameraMediator = null)
            : this(
                cameraMediator,
                SharedRtspPreviewFrameProvider.Instance,
                new RtspCaptureService())
        {
        }

        internal UnifiedCaptureService(
            ICameraMediator? cameraMediator,
            SharedRtspPreviewFrameProvider sharedPreviewFrames,
            IRtspFrameCaptureService rtspService)
        {
            _sharedPreviewFrames = sharedPreviewFrames
                ?? throw new ArgumentNullException(nameof(sharedPreviewFrames));
            _rtspService = rtspService
                ?? throw new ArgumentNullException(nameof(rtspService));
            _indiService = new INDICameraCapture(cameraMediator); // Always create - HTTP download doesn't need mediator
            _folderService = new FolderWatchCapture();
            _sharedPreviewFrames.SourceReserved += OnSharedPreviewSourceReservedAsync;
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
            var nextUsername = username ?? "";
            var nextPassword = password ?? "";
            var settingsChanged = !string.Equals(_rtspUrl, url, StringComparison.Ordinal)
                || !string.Equals(_rtspUsername, nextUsername, StringComparison.Ordinal)
                || !string.Equals(_rtspPassword, nextPassword, StringComparison.Ordinal);

            if (settingsChanged)
            {
                _rtspService.Reset();
                _consecutiveRtspCaptureFailures = 0;
            }

            _rtspUrl = url;
            _rtspUsername = nextUsername;
            _rtspPassword = nextPassword;
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
        /// Injects NINA's image data factory for proper FITS/TIFF loading.
        /// </summary>
        public void SetImageDataFactory(IImageDataFactory imageDataFactory)
        {
            _folderService.ImageDataFactory = imageDataFactory;
        }

        /// <summary>
        /// Captures an image using the currently configured mode
        /// </summary>
        public async Task<Bitmap?> CaptureImageAsync(CancellationToken ct = default)
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

        private async Task<Bitmap?> CaptureFromRTSPAsync(CancellationToken ct)
        {
            await _rtspCaptureGate.WaitAsync(ct);
            try
            {
                return await CaptureFromRTSPCoreAsync(ct);
            }
            finally
            {
                _rtspCaptureGate.Release();
            }
        }

        private async Task<Bitmap?> CaptureFromRTSPCoreAsync(CancellationToken ct)
        {
            try
            {
                var authenticatedUrl = BuildAuthenticatedUrl(_rtspUrl, _rtspUsername, _rtspPassword);

                var sharedPreview = await _sharedPreviewFrames.TryCaptureAsync(
                    authenticatedUrl,
                    TimeSpan.FromSeconds(5),
                    ct);
                if (sharedPreview.Status == SharedRtspPreviewCaptureStatus.Captured)
                {
                    // A preview reservation normally closes this decoder before LibVLC
                    // starts. Reset again here as a final ownership guarantee in case a
                    // previous initialization completed concurrently with the reservation.
                    _rtspService.Reset();
                    _consecutiveRtspCaptureFailures = 0;
                    Logger.Info(
                        "UnifiedCaptureService - Captured AI frame from the existing " +
                        "LibVLC preview; no second RTSP decoder was opened");
                    return sharedPreview.Frame;
                }

                if (sharedPreview.Status == SharedRtspPreviewCaptureStatus.Failed)
                {
                    // Prefer the already decoded LibVLC frame, but do not confuse that
                    // optimization with a camera connection limit. Both observatories have
                    // now verified that the camera can serve N.I.N.A. and OBS concurrently.
                    // If the snapshot bridge itself is unhealthy, make one real decoder
                    // attempt below; failure still propagates as a missing frame and ages the
                    // safety state toward UNSAFE.
                    Logger.Warning(
                        $"UnifiedCaptureService - Active LibVLC preview could not provide " +
                        $"a current analysis frame ({sharedPreview.Reason}); trying the " +
                        "independent RTSP decoder as a health fallback");
                }

                if (!_rtspService.IsInitializedFor(authenticatedUrl))
                {
                    if (string.IsNullOrWhiteSpace(_rtspUrl))
                    {
                        Logger.Warning("RTSP capture requested but RTSP URL is empty");
                        return null;
                    }
                    Logger.Debug($"UnifiedCaptureService - Opening continuous latest-frame RTSP capture: {RedactRtspUrl(authenticatedUrl)}");

                    var ok = await _rtspService.InitializeAsync(authenticatedUrl, ct);
                    if (!ok)
                    {
                        Logger.Error($"UnifiedCaptureService - Failed to initialize RTSP capture for analysis: {RedactRtspUrl(authenticatedUrl)}");
                        return null;
                    }
                }

                var frame = await _rtspService.CaptureFrameAsync(ct);
                if (frame != null)
                {
                    _consecutiveRtspCaptureFailures = 0;
                    return frame;
                }

                _consecutiveRtspCaptureFailures++;
                if (_consecutiveRtspCaptureFailures < RtspFailuresBeforeReconnect)
                {
                    return null;
                }

                // VideoCapture can stay "open" after the decoder or TCP session has died.
                // IsOpened alone therefore cannot be used as a health check. Rebuild the
                // pipeline after consecutive empty captures and make one bounded retry.
                Logger.Warning(
                    $"UnifiedCaptureService - RTSP returned no frame " +
                    $"{_consecutiveRtspCaptureFailures} times; rebuilding the analysis connection");
                _rtspService.Reset();

                var recovered = await _rtspService.InitializeAsync(authenticatedUrl, ct);
                if (!recovered)
                {
                    Logger.Error("UnifiedCaptureService - RTSP recovery initialization failed");
                    return null;
                }

                frame = await _rtspService.CaptureFrameAsync(ct);
                if (frame != null)
                {
                    _consecutiveRtspCaptureFailures = 0;
                    Logger.Info("UnifiedCaptureService - RTSP analysis capture recovered after reconnect");
                }

                return frame;
            }
            catch (Exception ex)
            {
                if (ex is OperationCanceledException && ct.IsCancellationRequested) throw;
                Logger.Error($"Error capturing from RTSP stream: {ex.Message}");
                return null;
            }
        }

        private Task OnSharedPreviewSourceReservedAsync(string sourceIdentity)
        {
            // Reset can wait for a native OpenCV Read() to return. Keep that wait away from
            // WPF's dispatcher; RegisterAsync awaits this task before opening LibVLC.
            return Task.Run(() =>
            {
                _rtspCaptureGate.Wait();
                try
                {
                    var configuredUrl = BuildAuthenticatedUrl(
                        _rtspUrl,
                        _rtspUsername,
                        _rtspPassword);
                    var configuredIdentity =
                        SharedRtspPreviewFrameProvider.CreateSourceIdentity(configuredUrl);
                    if (!string.Equals(
                            configuredIdentity,
                            sourceIdentity,
                            StringComparison.Ordinal))
                    {
                        return;
                    }

                    _rtspService.Reset();
                    _consecutiveRtspCaptureFailures = 0;
                    Logger.Info(
                        $"UnifiedCaptureService - Reserved RTSP source " +
                        $"{SharedRtspPreviewFrameProvider.Fingerprint(sourceIdentity)} for " +
                        "the preferred shared LibVLC preview; idle independent decoder released");
                }
                finally
                {
                    _rtspCaptureGate.Release();
                }
            });
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

            // Credentials must be percent-encoded before they go into the URL. Camera
            // accounts routinely contain characters that are structural in a URL - '@'
            // ends the credentials, ':' separates them, '/' ends the host - so an
            // unencoded password silently produced a malformed URL and the stream failed
            // to open with no hint that the password was the cause.
            var user = Uri.EscapeDataString(username);
            var secret = Uri.EscapeDataString(password ?? string.Empty);
            return $"rtsp://{user}:{secret}@{urlWithoutProtocol}";
        }

        private static string RedactRtspUrl(string url)
        {
            return LogRedactor.RedactRtspUrl(url);
        }

        private async Task<Bitmap?> CaptureFromINDIAsync(CancellationToken ct)
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

        private async Task<Bitmap?> CaptureFromFolderAsync()
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
        /// Closes active capture resources while keeping the service reusable after a NINA
        /// disconnect/reconnect cycle.
        /// </summary>
        public void Reset()
        {
            _consecutiveRtspCaptureFailures = 0;
            _rtspService.Reset();
        }

        /// <summary>
        /// Permanently disposes resources used by the capture services.
        /// </summary>
        public void Dispose()
        {
            _sharedPreviewFrames.SourceReserved -= OnSharedPreviewSourceReservedAsync;
            _rtspService.Dispose();
        }
    }
}
