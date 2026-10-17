using AIWeather.Models;
using NINA.Core.Utility;
using System;
using System.Drawing;
using System.Drawing.Imaging;
using System.Diagnostics;
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

        /// <summary>
        /// Used when no model is chosen. The cheapest current vision model: one analysis
        /// every few minutes is a yes/no about clouds, not a job for Sonnet. Dateless IDs
        /// are pinned snapshots with a retirement date at least a year out; the dated
        /// Sonnet 4.5 that sat here before was deprecated on 2026-09-30 (#18).
        /// </summary>
        public const string DefaultModel = "claude-haiku-5-5";

        /// <summary>
        /// Offered when /v1/models cannot be read. Only models Anthropic lists as active;
        /// the retired 3.x entries used to be here and were offered to anyone whose list
        /// call failed (#18).
        /// </summary>
        public static readonly string[] FallbackModels =
        {
            "claude-haiku-5-5",
            "claude-sonnet-5-5",
            "claude-opus-5-5",
            "claude-haiku-4-5-20251001",
        };

        /// <summary>
        /// Whether an ID from /v1/models can take an image. Every model Anthropic serves
        /// today can; the text-only Claude 2 and Instant were retired in 2024-2025 and no
        /// longer appear in the list. A hand-kept prefix list (claude-3, claude-sonnet-4,
        /// ...) dropped every Claude 5.x model at its release (#18).
        /// </summary>
        public static bool IsVisionModel(string? id)
        {
            if (string.IsNullOrWhiteSpace(id)) return false;
            if (!id.StartsWith("claude-", StringComparison.OrdinalIgnoreCase)) return false;
            return !id.StartsWith("claude-2", StringComparison.OrdinalIgnoreCase)
                && !id.StartsWith("claude-instant", StringComparison.OrdinalIgnoreCase)
                && !id.StartsWith("claude-1", StringComparison.OrdinalIgnoreCase);
        }

        public AnthropicAnalysisService(string apiKey, string modelName)
        {
            _apiKey = apiKey;
            _modelName = string.IsNullOrWhiteSpace(modelName) ? DefaultModel : modelName.Trim();
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
            var stopwatch = Stopwatch.StartNew();
            var failureCategory = AnalysisFailureCategory.Unknown;
            int? failureHttpStatus = null;
            if (!_isInitialized)
            {
                Logger.Warning("Anthropic service not initialized, falling back to local analysis");
                var fallback = new LocalWeatherAnalysisService();
                var local = await fallback.AnalyzeImageAsync(image, astroContext, cancellationToken);
                local.Provenance.IsFallback = true;
                local.Provenance.FailureCategory = AnalysisFailureCategory.Authentication;
                return local;
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
                    system = WeatherAnalysisPrompts.BuildSystemPrompt(),
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
                    failureHttpStatus = (int)response.StatusCode;
                    failureCategory = AnalysisMetadata.FromHttpStatus(response.StatusCode);
                    Logger.Error($"Anthropic API error: HTTP {(int)response.StatusCode}: {json}");
                    throw new InvalidOperationException($"HTTP {(int)response.StatusCode}: {json}");
                }

                Logger.Info("Anthropic API responded, parsing response...");

                using var doc = JsonDocument.Parse(json);
                var text = ExtractAnthropicText(doc.RootElement);

                var result = WeatherResponseParser.Parse(text);
                if (!WeatherAnalysisValidator.IsValidProviderResult(result, out var validationReason))
                {
                    failureCategory = AnalysisFailureCategory.SchemaRejected;
                    throw new InvalidDataException($"Anthropic response rejected by weather schema: {validationReason}");
                }
                result.Provenance = AnalysisMetadata.Online(
                    AnalysisOrigin.Anthropic, "Anthropic", _modelName, 1,
                    stopwatch.ElapsedMilliseconds, (int)response.StatusCode);
                Logger.Info($"Anthropic analysis complete: {result.Condition}, Cloud Coverage: {result.CloudCoverage:F1}%");
                return result;
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
                // The caller stopped monitoring: not a failure, not a reading. Propagate.
                throw;
            }
            catch (OperationCanceledException ex)
            {
                Logger.Warning($"Anthropic API call timed out, falling back to local analysis: {ex.Message}");
                var fallback = new LocalWeatherAnalysisService();
                var result = await fallback.AnalyzeImageAsync(image, astroContext, cancellationToken);
                result.Provenance.IsFallback = true;
                result.Provenance.FailureCategory = AnalysisFailureCategory.Timeout;
                result.Description = $"[Fallback: Local] Anthropic timed out. {result.Description}";
                return result;
            }
            catch (Exception ex)
            {
                Logger.Error($"Error in Anthropic analysis, falling back to local analysis: {ex.Message}", ex);
                var fallback = new LocalWeatherAnalysisService();
                var result = await fallback.AnalyzeImageAsync(image, astroContext, cancellationToken);
                result.Provenance.IsFallback = true;
                result.Provenance.FailureCategory = failureCategory != AnalysisFailureCategory.Unknown
                    ? failureCategory
                    : ex is HttpRequestException
                        ? AnalysisFailureCategory.Network
                        : ex is InvalidDataException
                            ? AnalysisFailureCategory.SchemaRejected
                            : ex is WeatherResponseParseException
                                ? AnalysisFailureCategory.MalformedResponse
                                : AnalysisFailureCategory.Unknown;
                result.Provenance.HttpStatus = failureHttpStatus;
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

        private static class PromptText
        {
                        public static string SystemPrompt => WeatherAnalysisPrompts.DetailedSystemPrompt;


        }
    }
}
