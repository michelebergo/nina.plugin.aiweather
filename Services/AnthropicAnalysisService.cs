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
            _modelName = string.IsNullOrWhiteSpace(modelName) ? "claude-3-5-sonnet-20241022" : modelName.Trim();
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

        public async Task<WeatherAnalysisResult> AnalyzeImageAsync(Bitmap image, CancellationToken cancellationToken = default)
        {
            if (!_isInitialized)
            {
                Logger.Warning("Anthropic service not initialized, falling back to local analysis");
                var fallback = new LocalWeatherAnalysisService();
                return await fallback.AnalyzeImageAsync(image, cancellationToken);
            }

            try
            {
                Logger.Info($"Starting Anthropic AI weather analysis with {_modelName}");

                var base64Image = ConvertImageToBase64(image);

                var payload = new
                {
                    model = _modelName,
                    max_tokens = 512,
                    system = PromptText.SystemPrompt,
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
                                    text = "Analyze this all-sky camera image and provide weather assessment (JSON only)."
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

                var result = PromptText.ParseAIResponse(text);
                Logger.Info($"Anthropic analysis complete: {result.Condition}, Cloud Coverage: {result.CloudCoverage:F1}%");
                return result;
            }
            catch (OperationCanceledException ex)
            {
                Logger.Warning($"Anthropic API call timed out or was cancelled: {ex.Message}");
                var fallback = new LocalWeatherAnalysisService();
                return await fallback.AnalyzeImageAsync(image, cancellationToken);
            }
            catch (Exception ex)
            {
                Logger.Error($"Error in Anthropic analysis: {ex.Message}", ex);
                var fallback = new LocalWeatherAnalysisService();
                return await fallback.AnalyzeImageAsync(image, cancellationToken);
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

        private static class PromptText
        {
                        public static string SystemPrompt => WeatherAnalysisPrompts.DetailedSystemPrompt;

            public static WeatherAnalysisResult ParseAIResponse(string jsonResponse)
            {
                try
                {
                    jsonResponse = jsonResponse.Trim();
                    if (jsonResponse.StartsWith("```json", StringComparison.OrdinalIgnoreCase))
                    {
                        jsonResponse = jsonResponse.Substring(7);
                    }
                    if (jsonResponse.StartsWith("```", StringComparison.OrdinalIgnoreCase))
                    {
                        jsonResponse = jsonResponse.Substring(3);
                    }
                    if (jsonResponse.EndsWith("```", StringComparison.OrdinalIgnoreCase))
                    {
                        jsonResponse = jsonResponse.Substring(0, jsonResponse.Length - 3);
                    }
                    jsonResponse = jsonResponse.Trim();

                    using var json = JsonDocument.Parse(jsonResponse);
                    var root = json.RootElement;

                    var conditionStr = root.GetProperty("condition").GetString() ?? "Unknown";
                    var condition = Enum.TryParse<WeatherCondition>(conditionStr, true, out var parsedCondition)
                        ? parsedCondition
                        : WeatherCondition.Unknown;

                    var cloudCoverage = root.GetProperty("cloudCoverage").GetDouble();
                    var rainDetected = root.GetProperty("rainDetected").GetBoolean();
                    var fogDetected = root.GetProperty("fogDetected").GetBoolean();
                    var isSafe = root.GetProperty("isSafe").GetBoolean();
                    var description = root.GetProperty("description").GetString() ?? string.Empty;
                    var confidence = root.TryGetProperty("confidence", out var confProp) ? confProp.GetDouble() : 85.0;

                    return new WeatherAnalysisResult
                    {
                        Timestamp = DateTime.UtcNow,
                        Condition = condition,
                        CloudCoverage = cloudCoverage,
                        Confidence = confidence,
                        IsSafeForImaging = isSafe,
                        Description = description,
                        RainDetected = rainDetected,
                        FogDetected = fogDetected,
                        RawAnalysisData = jsonResponse
                    };
                }
                catch (Exception ex)
                {
                    Logger.Error($"Error parsing AI response: {ex.Message}", ex);
                    Logger.Debug($"Raw response: {jsonResponse}");

                    return new WeatherAnalysisResult
                    {
                        Timestamp = DateTime.UtcNow,
                        Condition = WeatherCondition.Unknown,
                        CloudCoverage = 50,
                        Confidence = 0,
                        IsSafeForImaging = false,
                        Description = $"Failed to parse AI response: {ex.Message}",
                        RawAnalysisData = jsonResponse
                    };
                }
            }
        }
    }
}
