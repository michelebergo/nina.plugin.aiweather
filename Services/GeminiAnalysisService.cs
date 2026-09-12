using AIWeather.Models;
using NINA.Core.Utility;
using System;
using System.Collections.Generic;
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
            // The alias tracks Google's latest stable Flash release; concrete version IDs
            // get retired out from under a hardcoded fallback (gemini-2.0-flash was).
            _modelName = string.IsNullOrWhiteSpace(modelName) ? "gemini-flash-latest" : modelName.Trim();
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

        public async Task<WeatherAnalysisResult> AnalyzeImageAsync(Bitmap image, AstroContext? astroContext = null, CancellationToken cancellationToken = default)
        {
            if (!_isInitialized)
            {
                Logger.Warning("Gemini service not initialized, falling back to local analysis");
                var fallback = new LocalWeatherAnalysisService();
                return await fallback.AnalyzeImageAsync(image, astroContext, cancellationToken);
            }

            try
            {
                Logger.Info($"Starting Gemini AI weather analysis with {_modelName}");

                var base64Image = ConvertImageToBase64(image);
                var url = $"https://generativelanguage.googleapis.com/v1beta/models/{Uri.EscapeDataString(_modelName)}:generateContent";

                var promptText = WeatherAnalysisPrompts.DetailedSystemPrompt;
                var promptPrefix = WeatherAnalysisPrompts.BuildPromptPrefix(astroContext);
                if (promptPrefix.Length > 0)
                    promptText = promptPrefix + "\n" + promptText;

                Logger.Info("Calling Gemini API...");

                // Create a timeout cancellation token source (60 seconds timeout)
                using var timeoutCts = new CancellationTokenSource(TimeSpan.FromSeconds(60));
                using var linkedCts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken, timeoutCts.Token);

                var (json, status) = await PostAsync(url, BuildRequestBody(promptText, base64Image, disableThinking: true), linkedCts.Token);

                // Not every model lets its reasoning phase be switched off. When one refuses
                // the parameter, ask again without it rather than losing the analysis: the
                // output budget alone is now large enough for both.
                if (!status && IsThinkingConfigRejected(json))
                {
                    Logger.Info($"{_modelName} does not accept thinkingConfig; retrying without it");
                    (json, status) = await PostAsync(url, BuildRequestBody(promptText, base64Image, disableThinking: false), linkedCts.Token);
                }

                if (!status)
                {
                    Logger.Error($"Gemini API error: {json}");
                    throw new InvalidOperationException(json);
                }

                Logger.Info("Gemini API responded, parsing response...");

                using var doc = JsonDocument.Parse(json);

                // A truncated answer has a cause the owner can act on, and the raw JSON reader
                // error does not name it. Check before parsing so the message says so.
                if (WasCutShort(doc.RootElement))
                {
                    throw new WeatherResponseParseException(
                        $"{_modelName} ran out of output budget before finishing its answer (finishReason MAX_TOKENS)",
                        ExtractGeminiText(doc.RootElement));
                }

                var text = ExtractGeminiText(doc.RootElement);

                var result = WeatherResponseParser.Parse(text);
                Logger.Info($"Gemini analysis complete: {result.Condition}, Cloud Coverage: {result.CloudCoverage:F1}%");
                return result;
            }
            catch (OperationCanceledException ex)
            {
                Logger.Warning($"Gemini API call timed out or was cancelled, falling back to local analysis: {ex.Message}");
                var fallback = new LocalWeatherAnalysisService();
                var result = await fallback.AnalyzeImageAsync(image, astroContext, cancellationToken);
                result.Description = $"[Fallback: Local] Gemini timed out. {result.Description}";
                return result;
            }
            catch (WeatherResponseParseException ex)
            {
                // The answer arrived but could not be read - most often cut short by the
                // model. That is a failed reading, not a reading of 50%: fall back to the
                // offline analyzer, which measures the image that was actually captured.
                Logger.Warning($"Gemini answer could not be parsed, falling back to local analysis: {ex.Message}");
                Logger.Debug($"Unparsed response: {ex.RawResponse}");
                var fallback = new LocalWeatherAnalysisService();
                var result = await fallback.AnalyzeImageAsync(image, astroContext, cancellationToken);
                result.Description = $"[Fallback: Local] Gemini: {ex.Message} {result.Description}";
                return result;
            }
            catch (Exception ex)
            {
                Logger.Error($"Error in Gemini analysis, falling back to local analysis: {ex.Message}", ex);
                var fallback = new LocalWeatherAnalysisService();
                var result = await fallback.AnalyzeImageAsync(image, astroContext, cancellationToken);
                result.Description = $"[Fallback: Local] Gemini error. {result.Description}";
                return result;
            }
        }

        /// <summary>
        /// Output budget for one analysis. The answer itself is a handful of JSON fields -
        /// a couple of hundred tokens - but on a model that reasons before answering, the
        /// reasoning is paid out of this same budget. The old value of 512 left nothing for
        /// the answer on current Flash models: the JSON arrived cut off mid-field and the
        /// analysis was lost every cycle (issue #16).
        /// </summary>
        private const int MaxOutputTokens = 2048;

        private async Task<(string body, bool ok)> PostAsync(string url, string requestBody, CancellationToken token)
        {
            using var request = new HttpRequestMessage(HttpMethod.Post, url);
            request.Headers.TryAddWithoutValidation("x-goog-api-key", _apiKey);
            request.Headers.UserAgent.ParseAdd("NINA-AIWeather/1.0");
            request.Content = new StringContent(requestBody, Encoding.UTF8, "application/json");

            using var response = await Http.SendAsync(request, token);
            var body = await response.Content.ReadAsStringAsync(token);

            return response.IsSuccessStatusCode
                ? (body, true)
                : ($"HTTP {(int)response.StatusCode}: {body}", false);
        }

        /// <summary>
        /// Build the request for one image. Three things keep the answer whole: a budget that
        /// fits reasoning and answer together, the response requested as JSON so the model does
        /// not wrap it in prose, and the reasoning phase switched off where the model allows it.
        /// </summary>
        public static string BuildRequestBody(string promptText, string base64Image, bool disableThinking)
        {
            var generationConfig = new Dictionary<string, object>
            {
                ["temperature"] = 0.1,
                ["maxOutputTokens"] = MaxOutputTokens,
                ["responseMimeType"] = "application/json"
            };

            if (disableThinking)
            {
                generationConfig["thinkingConfig"] = new Dictionary<string, object> { ["thinkingBudget"] = 0 };
            }

            var payload = new
            {
                contents = new object[]
                {
                    new
                    {
                        role = "user",
                        parts = new object[]
                        {
                            new { text = promptText },
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
                generationConfig
            };

            return JsonSerializer.Serialize(payload);
        }

        /// <summary>
        /// True when the API refused the request because of the thinking configuration, as
        /// opposed to any other error. Only that case is worth a second attempt.
        /// </summary>
        public static bool IsThinkingConfigRejected(string? errorBody)
        {
            if (string.IsNullOrEmpty(errorBody)) { return false; }

            return errorBody.Contains("thinking", StringComparison.OrdinalIgnoreCase)
                || errorBody.Contains("thought", StringComparison.OrdinalIgnoreCase);
        }

        /// <summary>
        /// True when the model stopped because it hit the output limit, which is the one
        /// failure that produces a half-written answer.
        /// </summary>
        public static bool WasCutShort(JsonElement root)
        {
            if (!root.TryGetProperty("candidates", out var candidates)
                || candidates.ValueKind != JsonValueKind.Array
                || candidates.GetArrayLength() == 0)
            {
                return false;
            }

            return candidates[0].TryGetProperty("finishReason", out var reason)
                && reason.ValueKind == JsonValueKind.String
                && string.Equals(reason.GetString(), "MAX_TOKENS", StringComparison.OrdinalIgnoreCase);
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

    }
}
