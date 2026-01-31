using System;
using System.Drawing;
using System.IO;
using System.Net;
using System.Net.Http;
using System.Threading;
using System.Threading.Tasks;
using NINA.Core.Utility;
using NINA.Equipment.Interfaces.Mediator;

namespace AIWeather.Services
{
    /// <summary>
    /// Downloads images from HTTP URL (e.g., indi-allsky, AllSky web interfaces)
    /// Supports basic authentication for password-protected URLs
    /// </summary>
    public class INDICameraCapture
    {
        private readonly ICameraMediator? _cameraMediator;
        private string _imageUrl = string.Empty;
        private string _username = string.Empty;
        private string _password = string.Empty;
        
        // HttpClient with handler that accepts self-signed certificates
        private static readonly HttpClient _httpClient = CreateHttpClient();

        private static HttpClient CreateHttpClient()
        {
            var handler = new HttpClientHandler
            {
                ServerCertificateCustomValidationCallback = (message, cert, chain, errors) =>
                {
                    // Accept all certificates (including self-signed)
                    // This is necessary for indi-allsky and other local servers with self-signed certs
                    return true;
                }
            };
            
            return new HttpClient(handler) { Timeout = TimeSpan.FromSeconds(30) };
        }

        public INDICameraCapture(ICameraMediator? cameraMediator)
        {
            _cameraMediator = cameraMediator;
        }

        public string DeviceName
        {
            get => _imageUrl;
            set => _imageUrl = value ?? string.Empty;
        }

        public string Username
        {
            get => _username;
            set => _username = value ?? string.Empty;
        }

        public string Password
        {
            get => _password;
            set => _password = value ?? string.Empty;
        }

        /// <summary>
        /// Downloads an image from the configured HTTP URL
        /// </summary>
        public async Task<Bitmap?> CaptureImageAsync(CancellationToken ct = default)
        {
            if (string.IsNullOrWhiteSpace(_imageUrl))
            {
                Logger.Warning("HTTP Image Download: No URL configured");
                return null;
            }

            try
            {
                Logger.Info($"HTTP Image Download: Fetching from {_imageUrl}");

                // Create request with optional authentication
                var request = new HttpRequestMessage(HttpMethod.Get, _imageUrl);
                
                // Add basic authentication if credentials provided
                if (!string.IsNullOrWhiteSpace(_username))
                {
                    var credentials = Convert.ToBase64String(
                        System.Text.Encoding.ASCII.GetBytes($"{_username}:{_password}"));
                    request.Headers.Authorization = new System.Net.Http.Headers.AuthenticationHeaderValue("Basic", credentials);
                    Logger.Debug($"HTTP Image Download: Using authentication for user '{_username}'");
                }

                // Download image
                var response = await _httpClient.SendAsync(request, ct);
                response.EnsureSuccessStatusCode();

                // Read image data
                var imageBytes = await response.Content.ReadAsByteArrayAsync(ct);
                Logger.Info($"HTTP Image Download: Downloaded {imageBytes.Length} bytes");

                // Convert to Bitmap
                using (var ms = new MemoryStream(imageBytes))
                {
                    // Bitmap(Stream) keeps the stream alive; clone to detach from the MemoryStream.
                    using var tempBitmap = new Bitmap(ms);
                    var bitmap = new Bitmap(tempBitmap);
                    Logger.Info($"HTTP Image Download: Image size {bitmap.Width}x{bitmap.Height}");
                    return bitmap;
                }
            }
            catch (HttpRequestException ex)
            {
                var statusCode = ex.StatusCode;
                if (statusCode.HasValue)
                {
                    if (statusCode.Value == System.Net.HttpStatusCode.Unauthorized)
                    {
                        Logger.Error($"HTTP Image Download failed: 401 Unauthorized - Check username/password");
                        Logger.Error("Verify credentials are correct for this camera/server.");
                    }
                    else if (statusCode.Value == System.Net.HttpStatusCode.Forbidden)
                    {
                        Logger.Error($"HTTP Image Download failed: 403 Forbidden - Access denied even with credentials");
                    }
                    else if (statusCode.Value == System.Net.HttpStatusCode.NotFound)
                    {
                        Logger.Error($"HTTP Image Download failed: 404 Not Found - URL path is incorrect");
                        Logger.Error($"Check the URL: {_imageUrl}");
                        Logger.Error("Example: http://camera-ip/indi-allsky/latest.jpg or http://camera-ip/current.jpg");
                    }
                    else
                    {
                        Logger.Error($"HTTP Image Download failed: HTTP {(int)statusCode.Value} {statusCode.Value} - {ex.Message}");
                    }
                }
                else
                {
                    Logger.Error($"HTTP Image Download failed: {ex.Message}");
                    Logger.Error("Possible causes: incorrect hostname/IP, network unreachable, or DNS resolution failed.");
                    Logger.Error($"Check URL: {_imageUrl}");
                }
                return null;
            }
            catch (TaskCanceledException)
            {
                Logger.Warning($"HTTP Image Download timed out after 30 seconds");
                Logger.Warning($"URL: {_imageUrl}");
                Logger.Warning("Camera may be offline or responding very slowly.");
                return null;
            }
            catch (UriFormatException ex)
            {
                Logger.Error($"HTTP Image Download - Invalid URL format: {ex.Message}");
                Logger.Error($"URL: {_imageUrl}");
                Logger.Error("URL must be in format: http://camera-ip/path/to/image.jpg or https://...");
                return null;
            }
            catch (Exception ex)
            {
                Logger.Error($"HTTP Image Download unexpected error: {ex.Message}", ex);
                Logger.Error($"URL: {_imageUrl}");
                return null;
            }
        }

        /// <summary>
        /// Checks if the HTTP URL is configured
        /// </summary>
        public bool IsConnected()
        {
            return !string.IsNullOrWhiteSpace(_imageUrl);
        }
    }
}
