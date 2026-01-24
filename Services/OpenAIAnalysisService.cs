using AIWeather.Models;
using NINA.Core.Utility;
using System;
using System.Drawing;
using System.Drawing.Imaging;
using System.IO;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;

namespace AIWeather.Services
{
    [System.Runtime.Versioning.SupportedOSPlatform("windows")]
    public class OpenAIAnalysisService : IWeatherAnalysisService
    {
        private static readonly HttpClient Http = new HttpClient();

        private readonly string _apiKey;
        private readonly string _modelName;
        private bool _isInitialized;

        private const string Endpoint = "https://api.openai.com/v1/chat/completions";

        public OpenAIAnalysisService(string apiKey, string modelName)
        {
            _apiKey = apiKey;
            _modelName = string.IsNullOrWhiteSpace(modelName) ? "gpt-4o-mini" : modelName.Trim();
        }

        public Task<bool> InitializeAsync(CancellationToken cancellationToken = default)
        {
            if (string.IsNullOrWhiteSpace(_apiKey))
            {
                Logger.Warning("OpenAI API key not configured");
                _isInitialized = false;
                return Task.FromResult(false);
            }

            _isInitialized = true;
            Logger.Info($"OpenAI analysis service initialized with model: {_modelName}");
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
                var imageUrl = $"data:image/jpeg;base64,{base64Image}";

                var payload = new
                {
                    model = _modelName,
                    temperature = 0.1,
                    max_tokens = 512,
                    messages = new object[]
                    {
                        new { role = "system", content = PromptText.SystemPrompt },
                        new {
                            role = "user",
                            content = new object[]
                            {
                                new { type = "text", text = "Analyze this all-sky camera image and provide weather assessment:" },
                                new { type = "image_url", image_url = new { url = imageUrl } }
                            }
                        }
                    }
                };

                using var request = new HttpRequestMessage(HttpMethod.Post, Endpoint);
                request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", _apiKey);
                request.Headers.UserAgent.ParseAdd("NINA-AIWeather/1.0");
                request.Content = new StringContent(JsonSerializer.Serialize(payload), Encoding.UTF8, "application/json");

                using var response = await Http.SendAsync(request, cancellationToken);
                var json = await response.Content.ReadAsStringAsync(cancellationToken);

                if (!response.IsSuccessStatusCode)
                {
                    throw new InvalidOperationException($"HTTP {(int)response.StatusCode}: {json}");
                }

                using var doc = JsonDocument.Parse(json);
                var root = doc.RootElement;
                var content = ExtractOpenAIMessageContent(root);

                return PromptText.ParseAIResponse(content);
            }
            catch (Exception ex)
            {
                Logger.Error($"Error in OpenAI analysis: {ex.Message}", ex);
                var fallback = new LocalWeatherAnalysisService();
                return await fallback.AnalyzeImageAsync(image, cancellationToken);
            }
        }

        private static string ExtractOpenAIMessageContent(JsonElement root)
        {
            try
            {
                if (root.TryGetProperty("choices", out var choices) && choices.ValueKind == JsonValueKind.Array && choices.GetArrayLength() > 0)
                {
                    var message = choices[0].GetProperty("message");
                    if (message.TryGetProperty("content", out var content))
                    {
                        if (content.ValueKind == JsonValueKind.String)
                        {
                            return content.GetString() ?? string.Empty;
                        }

                        if (content.ValueKind == JsonValueKind.Array)
                        {
                            var sb = new StringBuilder();
                            foreach (var part in content.EnumerateArray())
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
                        public const string SystemPrompt = @"You are an expert meteorologist analyzing all-sky camera images for astronomical observation safety.
Analyze the provided all-sky camera image and determine:
1. Weather condition (Clear, PartlyCloudy, MostlyCloudy, Overcast, Rainy, Foggy)
2. Cloud coverage percentage (0-100)
3. Whether rain is detected
4. Whether fog is detected
5. Whether conditions are safe for astronomical imaging
6. A brief description of the conditions

Respond in JSON format:
{
    ""condition"": ""Clear|PartlyCloudy|MostlyCloudy|Overcast|Rainy|Foggy"",
    ""cloudCoverage"": 0-100,
    ""rainDetected"": true|false,
    ""fogDetected"": true|false,
    ""isSafe"": true|false,
    ""description"": ""brief description"",
    ""confidence"": 0-100
}";

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
