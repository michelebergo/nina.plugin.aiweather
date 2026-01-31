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
    public class GeminiAnalysisService : IWeatherAnalysisService
    {
        private static readonly HttpClient Http = new HttpClient();

        private readonly string _apiKey;
        private readonly string _modelName;
        private bool _isInitialized;

        public GeminiAnalysisService(string apiKey, string modelName)
        {
            _apiKey = apiKey;
            _modelName = string.IsNullOrWhiteSpace(modelName) ? "gemini-1.5-flash" : modelName.Trim();
        }

        public Task<bool> InitializeAsync(CancellationToken cancellationToken = default)
        {
            if (string.IsNullOrWhiteSpace(_apiKey))
            {
                Logger.Warning("Gemini API key not configured");
                _isInitialized = false;
                return Task.FromResult(false);
            }

            _isInitialized = true;
            Logger.Info($"Gemini analysis service initialized with model: {_modelName}");
            return Task.FromResult(true);
        }

        public async Task<WeatherAnalysisResult> AnalyzeImageAsync(Bitmap image, CancellationToken cancellationToken = default)
        {
            if (!_isInitialized)
            {
                var fallback = new LocalWeatherAnalysisService();
                return await fallback.AnalyzeImageAsync(image, cancellationToken);
            }

            try
            {
                var base64Image = ConvertImageToBase64(image);
                var url = $"https://generativelanguage.googleapis.com/v1beta/models/{Uri.EscapeDataString(_modelName)}:generateContent";

                var payload = new
                {
                    contents = new object[]
                    {
                        new
                        {
                            role = "user",
                            parts = new object[]
                            {
                                new { text = PromptText.FullPrompt },
                                new
                                {
                                    inlineData = new
                                    {
                                        mimeType = "image/jpeg",
                                        data = base64Image
                                    }
                                }
                            }
                        }
                    },
                    generationConfig = new
                    {
                        temperature = 0.1,
                        maxOutputTokens = 512
                    }
                };

                using var request = new HttpRequestMessage(HttpMethod.Post, url);
                request.Headers.TryAddWithoutValidation("x-goog-api-key", _apiKey);
                request.Headers.UserAgent.ParseAdd("NINA-AIWeather/1.0");
                request.Content = new StringContent(JsonSerializer.Serialize(payload), Encoding.UTF8, "application/json");

                using var response = await Http.SendAsync(request, cancellationToken);
                var json = await response.Content.ReadAsStringAsync(cancellationToken);

                if (!response.IsSuccessStatusCode)
                {
                    throw new InvalidOperationException($"HTTP {(int)response.StatusCode}: {json}");
                }

                using var doc = JsonDocument.Parse(json);
                var text = ExtractGeminiText(doc.RootElement);

                return PromptText.ParseAIResponse(text);
            }
            catch (Exception ex)
            {
                Logger.Error($"Error in Gemini analysis: {ex.Message}", ex);
                var fallback = new LocalWeatherAnalysisService();
                return await fallback.AnalyzeImageAsync(image, cancellationToken);
            }
        }

        private static string ExtractGeminiText(JsonElement root)
        {
            try
            {
                if (root.TryGetProperty("candidates", out var candidates) && candidates.ValueKind == JsonValueKind.Array && candidates.GetArrayLength() > 0)
                {
                    var content = candidates[0].GetProperty("content");
                    if (content.TryGetProperty("parts", out var parts) && parts.ValueKind == JsonValueKind.Array)
                    {
                        var sb = new StringBuilder();
                        foreach (var part in parts.EnumerateArray())
                        {
                            if (part.ValueKind == JsonValueKind.Object && part.TryGetProperty("text", out var textProp) && textProp.ValueKind == JsonValueKind.String)
                            {
                                sb.AppendLine(textProp.GetString());
                            }
                        }
                        return sb.ToString().Trim();
                    }
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
                        public static string FullPrompt => WeatherAnalysisPrompts.DetailedSystemPrompt;

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
