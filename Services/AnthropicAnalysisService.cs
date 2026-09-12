using AIWeather.Models;
using NINA.Core.Utility;
using System;
using System.Drawing;
using System.Drawing.Imaging;
using System.IO;
using System.Net.Http;
using System.Text;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;

namespace AIWeather.Services
{
    [System.Runtime.Versioning.SupportedOSPlatform("windows")]
    public class AnthropicAnalysisService : IWeatherAnalysisService
    {
        private static readonly HttpClient Http = new HttpClient();

        private readonly string _apiKey;
        private readonly string _modelName;
        private bool _isInitialized;

        private const string Endpoint = "https://api.anthropic.com/v1/messages";
        private const string AnthropicVersion = "2023-06-01";

        public AnthropicAnalysisService(string apiKey, string modelName)
        {
            _apiKey = apiKey;
            _modelName = string.IsNullOrWhiteSpace(modelName) ? "claude-sonnet-4-5-20250929" : modelName.Trim();
        }

        public Task<bool> InitializeAsync(CancellationToken cancellationToken = default)
        {
            if (string.IsNullOrWhiteSpace(_apiKey))
            {
                Logger.Warning("Anthropic API key not configured");
                _isInitialized = false;
                return Task.FromResult(false);
            }

            _isInitialized = true;
            Logger.Info($"Anthropic analysis service initialized with model: {_modelName}");
            return Task.FromResult(true);
        }

        public async Task<WeatherAnalysisResult> AnalyzeImageAsync(Bitmap image, AstroContext? astroContext = null, CancellationToken cancellationToken = default)
        {
            if (!_isInitialized)
            {
                Logger.Warning("Anthropic service not initialized, falling back to local analysis");
                var fallback = new LocalWeatherAnalysisService();
                return await fallback.AnalyzeImageAsync(image, astroContext, cancellationToken);
            }

            try
            {
                Logger.Info($"Starting Anthropic AI weather analysis with {_modelName}");

                var base64Image = ConvertImageToBase64(image);

                var userText = "Analyze this all-sky camera image and provide weather assessment (JSON only).";
                var promptPrefix = WeatherAnalysisPrompts.BuildPromptPrefix(astroContext);
                if (promptPrefix.Length > 0)
                    userText = promptPrefix + "\n" + userText;

                var payload = new
                {
                    model = _modelName,
                    // Room for a reasoning phase and the answer together (issue #16).
                    max_tokens = 2048,
                    system = WeatherAnalysisPrompts.DetailedSystemPrompt,
                    messages = new object[]
                    {
                        new
                        {
                            role = "user",
                            content = new object[]
                            {
                                new
                                {
                                    type = "image",
                                    source = new
                                    {
                                        type = "base64",
                                        media_type = "image/jpeg",
                                        data = base64Image
                                    }
                                },
                                new
                                {
                                    type = "text",
                                    text = userText
                                }
                            }
                        }
                    }
                };

                using var request = new HttpRequestMessage(HttpMethod.Post, Endpoint);
                request.Headers.TryAddWithoutValidation("x-api-key", _apiKey);
                request.Headers.TryAddWithoutValidation("anthropic-version", AnthropicVersion);
                request.Headers.UserAgent.ParseAdd("NINA-AIWeather/1.0");
                request.Content = new StringContent(JsonSerializer.Serialize(payload), Encoding.UTF8, "application/json");

                Logger.Info("Calling Anthropic API...");

                // Create a timeout cancellation token source (60 seconds timeout)
                using var timeoutCts = new CancellationTokenSource(TimeSpan.FromSeconds(60));
                using var linkedCts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken, timeoutCts.Token);

                using var response = await Http.SendAsync(request, linkedCts.Token);
                var json = await response.Content.ReadAsStringAsync(linkedCts.Token);

                if (!response.IsSuccessStatusCode)
                {
                    Logger.Error($"Anthropic API error: HTTP {(int)response.StatusCode}: {json}");
                    throw new InvalidOperationException($"HTTP {(int)response.StatusCode}: {json}");
                }

                Logger.Info("Anthropic API responded, parsing response...");

                using var doc = JsonDocument.Parse(json);
                var text = ExtractAnthropicText(doc.RootElement);

                var result = WeatherResponseParser.Parse(text);
                Logger.Info($"Anthropic analysis complete: {result.Condition}, Cloud Coverage: {result.CloudCoverage:F1}%");
                return result;
            }
            catch (OperationCanceledException ex)
            {
                Logger.Warning($"Anthropic API call timed out or was cancelled, falling back to local analysis: {ex.Message}");
                var fallback = new LocalWeatherAnalysisService();
                var result = await fallback.AnalyzeImageAsync(image, astroContext, cancellationToken);
                result.Description = $"[Fallback: Local] Anthropic timed out. {result.Description}";
                return result;
            }
            catch (WeatherResponseParseException ex)
            {
                // The answer arrived but could not be read - most often cut short by the
                // model. That is a failed reading, not a reading of 50%: fall back to the
                // offline analyzer, which measures the image that was actually captured.
                Logger.Warning($"Anthropic answer could not be parsed, falling back to local analysis: {ex.Message}");
                Logger.Debug($"Unparsed response: {ex.RawResponse}");
                var fallback = new LocalWeatherAnalysisService();
                var result = await fallback.AnalyzeImageAsync(image, astroContext, cancellationToken);
                result.Description = $"[Fallback: Local] Anthropic: {ex.Message} {result.Description}";
                return result;
            }
            catch (Exception ex)
            {
                Logger.Error($"Error in Anthropic analysis, falling back to local analysis: {ex.Message}", ex);
                var fallback = new LocalWeatherAnalysisService();
                var result = await fallback.AnalyzeImageAsync(image, astroContext, cancellationToken);
                result.Description = $"[Fallback: Local] Anthropic error. {result.Description}";
                return result;
            }
        }

        private static string ExtractAnthropicText(JsonElement root)
        {
            try
            {
                if (root.TryGetProperty("content", out var content) && content.ValueKind == JsonValueKind.Array)
                {
                    var sb = new StringBuilder();
                    foreach (var part in content.EnumerateArray())
                    {
                        if (part.ValueKind == JsonValueKind.Object && part.TryGetProperty("type", out var type) && type.ValueKind == JsonValueKind.String)
                        {
                            if (string.Equals(type.GetString(), "text", StringComparison.OrdinalIgnoreCase)
                                && part.TryGetProperty("text", out var textProp)
                                && textProp.ValueKind == JsonValueKind.String)
                            {
                                sb.AppendLine(textProp.GetString());
                            }
                        }
                    }
                    return sb.ToString().Trim();
                }
            }
            catch
            {
                // best-effort
            }

            return string.Empty;
        }

        private static string ConvertImageToBase64(Bitmap image)
        {
            using var memoryStream = new MemoryStream();
            image.Save(memoryStream, ImageFormat.Jpeg);
            return Convert.ToBase64String(memoryStream.ToArray());
        }

    }
}
