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
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;

namespace AIWeather.Services
{
    /// <summary>
    /// The shapes a request can take, richest first. Every optional parameter is a future
    /// HTTP 400 - Google retired temperature and thinkingBudget between the 2.5 and the 3.x
    /// generations without the model name changing - so the plugin does not assume what a
    /// model accepts: it starts from the shape its generation is documented to take and
    /// steps down on rejection until one is accepted.
    /// </summary>
    public enum GeminiRequestProfile
    {
        /// <summary>Gemini 1.x/2.x: sampling temperature and the thinking budget switched off.</summary>
        Gemini2 = 0,

        /// <summary>Gemini 3.x and the -latest alias: output budget and JSON output only.</summary>
        Lean = 1,

        /// <summary>Last resort for any model: the output budget alone.</summary>
        Bare = 2,
    }

    [System.Runtime.Versioning.SupportedOSPlatform("windows")]
    public class GeminiAnalysisService : IWeatherAnalysisService
    {
        public const string ProviderName = "Gemini";

        /// <summary>
        /// Output budget for one analysis. The answer is a handful of JSON fields, but on a
        /// model that reasons before answering the reasoning is paid out of the same budget,
        /// and on the 3.x generation it cannot be switched off. 512 cut the answer mid-field
        /// (issue #16); the budget is only a ceiling, so a generous one costs nothing on a
        /// model that answers briefly. Capped by the model's own limit when it is lower.
        /// </summary>
        public const int DefaultMaxOutputTokens = 8192;

        private const string BaseUrl = "https://generativelanguage.googleapis.com/v1beta";

        private static readonly HttpClient Http = new HttpClient();

        private readonly string _apiKey;
        private readonly string _modelName;
        private bool _isInitialized;

        // What this model has been seen to accept. Negotiated on the first analysis and
        // remembered, so the steady state is one call per cycle; re-negotiated if the model
        // starts rejecting it, which is what a silent model update looks like from here.
        private GeminiRequestProfile _profile;
        private bool _profileSettled;

        private int _maxOutputTokens = DefaultMaxOutputTokens;
        private bool _metadataChecked;

        public GeminiAnalysisService(string apiKey, string modelName)
        {
            _apiKey = apiKey;
            // The alias tracks Google's latest stable Flash release; concrete version IDs
            // get retired out from under a hardcoded fallback (gemini-2.0-flash was).
            _modelName = string.IsNullOrWhiteSpace(modelName) ? "gemini-flash-latest" : modelName.Trim();
            _profile = StartingProfileFor(_modelName);
        }

        /// <summary>The request shape currently in use for this model. For the panel and the logs.</summary>
        public GeminiRequestProfile CurrentProfile => _profile;

        public Task<bool> InitializeAsync(CancellationToken cancellationToken = default)
        {
            if (string.IsNullOrWhiteSpace(_apiKey))
            {
                Logger.Warning("Gemini API key not configured");
                _isInitialized = false;
                return Task.FromResult(false);
            }

            _isInitialized = true;
            Logger.Info($"Gemini analysis service initialized with model: {_modelName} (starting request profile: {_profile})");
            return Task.FromResult(true);
        }

        public async Task<WeatherAnalysisResult> AnalyzeImageAsync(Bitmap image, AstroContext? astroContext = null, CancellationToken cancellationToken = default)
        {
            if (!_isInitialized)
            {
                Logger.Warning("Gemini service not initialized, falling back to local analysis");
                return await FallBackAsync(image, astroContext, "Gemini is not configured (missing API key)", cancellationToken);
            }

            try
            {
                Logger.Info($"Starting Gemini AI weather analysis with {_modelName}");

                var base64Image = ConvertImageToBase64(image);
                var url = $"{BaseUrl}/models/{Uri.EscapeDataString(_modelName)}:generateContent";

                var promptText = WeatherAnalysisPrompts.DetailedSystemPrompt;
                var promptPrefix = WeatherAnalysisPrompts.BuildPromptPrefix(astroContext);
                if (promptPrefix.Length > 0)
                    promptText = promptPrefix + "\n" + promptText;

                // Create a timeout cancellation token source (60 seconds timeout)
                using var timeoutCts = new CancellationTokenSource(TimeSpan.FromSeconds(60));
                using var linkedCts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken, timeoutCts.Token);

                await EnsureModelMetadataAsync(linkedCts.Token);

                Logger.Info("Calling Gemini API...");
                var (json, _) = await NegotiateAsync(body => PostAsync(url, body, linkedCts.Token), promptText, base64Image);

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
                result.Provider = ProviderName;
                Logger.Info($"Gemini analysis complete: {result.Condition}, Cloud Coverage: {result.CloudCoverage:F1}%");
                return result;
            }
            catch (OperationCanceledException ex)
            {
                Logger.Warning($"Gemini API call timed out or was cancelled, falling back to local analysis: {ex.Message}");
                return await FallBackAsync(image, astroContext, "Gemini timed out", cancellationToken);
            }
            catch (WeatherResponseParseException ex)
            {
                // The answer arrived but could not be read - most often cut short by the
                // model. That is a failed reading, not a reading of 50%: fall back to the
                // offline analyzer exactly like a timeout or an API error.
                Logger.Error($"Gemini answer could not be read, falling back to local analysis: {ex.Message}");
                Logger.Debug($"Raw Gemini answer: {ex.RawResponse}");
                return await FallBackAsync(image, astroContext, $"Gemini: {ex.Message}", cancellationToken);
            }
            catch (Exception ex)
            {
                Logger.Error($"Error in Gemini analysis, falling back to local analysis: {ex.Message}", ex);
                return await FallBackAsync(image, astroContext, $"Gemini error: {FirstLine(ex.Message)}", cancellationToken);
            }
        }

        /// <summary>
        /// Send the request in the shape this model is known to accept, and on an HTTP 400 step
        /// down to the next shape until one goes through. The accepted shape is remembered.
        /// Any other failure - quota, key, server - is not a shape problem and is reported as
        /// it is, without wasting further calls.
        /// </summary>
        /// <param name="post">Sends one request body and returns the response body and HTTP status.</param>
        public async Task<(string body, GeminiRequestProfile profile)> NegotiateAsync(
            Func<string, Task<(string body, int status)>> post, string promptText, string base64Image)
        {
            var profile = _profile;
            while (true)
            {
                var (body, status) = await post(BuildRequestBody(promptText, base64Image, profile, _maxOutputTokens));

                if (status >= 200 && status < 300)
                {
                    if (!_profileSettled || profile != _profile)
                    {
                        Logger.Info($"{_modelName}: using the {profile} request profile");
                    }
                    _profile = profile;
                    _profileSettled = true;
                    return (body, profile);
                }

                var next = IsRequestRejected(status) ? NextProfile(profile) : null;
                if (next == null)
                {
                    throw new InvalidOperationException($"HTTP {status}: {body}");
                }

                Logger.Warning($"{_modelName} rejected the {profile} request (HTTP {status}); retrying with the {next} profile");
                profile = next.Value;
            }
        }

        /// <summary>
        /// The shape a model's generation is documented to accept, from its name. Gemini 1.x
        /// and 2.x take a sampling temperature and a thinking budget; 3.x dropped both and
        /// cannot switch reasoning off at all. The -latest alias points at the newest Flash,
        /// which is 3.x. A wrong guess costs one extra call, then it is remembered.
        /// </summary>
        public static GeminiRequestProfile StartingProfileFor(string? modelName)
        {
            return !string.IsNullOrEmpty(modelName) && Regex.IsMatch(modelName, @"gemini-[12]\.", RegexOptions.IgnoreCase)
                ? GeminiRequestProfile.Gemini2
                : GeminiRequestProfile.Lean;
        }

        /// <summary>The next, poorer shape to try; null once the bare request has been refused too.</summary>
        public static GeminiRequestProfile? NextProfile(GeminiRequestProfile profile)
        {
            return profile switch
            {
                GeminiRequestProfile.Gemini2 => GeminiRequestProfile.Lean,
                GeminiRequestProfile.Lean => GeminiRequestProfile.Bare,
                _ => null,
            };
        }

        /// <summary>
        /// Whether a failure is about the request's shape. Google answers every unsupported
        /// parameter with a bare 400 INVALID_ARGUMENT that does not name the parameter, so
        /// the status is all there is to go on - and it is enough: a key, quota or server
        /// problem never comes back as 400.
        /// </summary>
        public static bool IsRequestRejected(int status) => status == 400;

        /// <summary>
        /// The budget to ask for, given what the model itself reports as its output limit.
        /// Asking for more than the model allows is one more way to get a 400.
        /// </summary>
        public static int ClampBudget(int? modelOutputTokenLimit)
        {
            return modelOutputTokenLimit is > 0
                ? Math.Min(DefaultMaxOutputTokens, modelOutputTokenLimit.Value)
                : DefaultMaxOutputTokens;
        }

        /// <summary>Read outputTokenLimit from a /models/{name} metadata answer, if present.</summary>
        public static int? ParseOutputTokenLimit(string? metadataJson)
        {
            if (string.IsNullOrWhiteSpace(metadataJson)) { return null; }

            try
            {
                using var doc = JsonDocument.Parse(metadataJson);
                if (doc.RootElement.ValueKind == JsonValueKind.Object
                    && doc.RootElement.TryGetProperty("outputTokenLimit", out var limit)
                    && limit.ValueKind == JsonValueKind.Number
                    && limit.TryGetInt32(out var value))
                {
                    return value;
                }
            }
            catch (JsonException)
            {
                // Not metadata; the default budget applies.
            }

            return null;
        }

        /// <summary>
        /// Build the request for one image in the given shape. The budget always travels;
        /// JSON output is requested where the shape allows it, so the model does not wrap
        /// the object in prose; temperature and the thinking budget only in the 1.x/2.x shape.
        /// </summary>
        public static string BuildRequestBody(string promptText, string base64Image, GeminiRequestProfile profile, int maxOutputTokens)
        {
            var generationConfig = new Dictionary<string, object>
            {
                ["maxOutputTokens"] = maxOutputTokens,
            };

            if (profile != GeminiRequestProfile.Bare)
            {
                generationConfig["responseMimeType"] = "application/json";
            }

            if (profile == GeminiRequestProfile.Gemini2)
            {
                generationConfig["temperature"] = 0.1;
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

        /// <summary>
        /// Ask the API what the model's output limit is, once. Best-effort: a model that
        /// cannot be described still gets the default budget, and the negotiation handles
        /// the rest.
        /// </summary>
        private async Task EnsureModelMetadataAsync(CancellationToken token)
        {
            if (_metadataChecked) { return; }
            _metadataChecked = true;

            try
            {
                using var request = new HttpRequestMessage(HttpMethod.Get, $"{BaseUrl}/models/{Uri.EscapeDataString(_modelName)}");
                request.Headers.TryAddWithoutValidation("x-goog-api-key", _apiKey);
                request.Headers.UserAgent.ParseAdd("NINA-AIWeather/1.0");

                using var response = await Http.SendAsync(request, token);
                var body = await response.Content.ReadAsStringAsync(token);
                if (!response.IsSuccessStatusCode)
                {
                    Logger.Debug($"Gemini model metadata unavailable for {_modelName}: HTTP {(int)response.StatusCode}");
                    return;
                }

                var limit = ParseOutputTokenLimit(body);
                _maxOutputTokens = ClampBudget(limit);
                Logger.Info($"{_modelName}: output token limit {(limit.HasValue ? limit.Value.ToString() : "unknown")}, using a budget of {_maxOutputTokens}");
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                Logger.Debug($"Gemini model metadata lookup failed for {_modelName}: {ex.Message}");
            }
        }

        private async Task<(string body, int status)> PostAsync(string url, string requestBody, CancellationToken token)
        {
            using var request = new HttpRequestMessage(HttpMethod.Post, url);
            request.Headers.TryAddWithoutValidation("x-goog-api-key", _apiKey);
            request.Headers.UserAgent.ParseAdd("NINA-AIWeather/1.0");
            request.Content = new StringContent(requestBody, Encoding.UTF8, "application/json");

            using var response = await Http.SendAsync(request, token);
            var body = await response.Content.ReadAsStringAsync(token);
            return (body, (int)response.StatusCode);
        }

        /// <summary>
        /// The offline analyzer's reading, marked as such: the panel shows that the provider
        /// was not the one who answered, and why.
        /// </summary>
        private static async Task<WeatherAnalysisResult> FallBackAsync(Bitmap image, AstroContext? astroContext, string reason, CancellationToken token)
        {
            var fallback = new LocalWeatherAnalysisService();
            var result = await fallback.AnalyzeImageAsync(image, astroContext, token);
            result.Provider = ProviderName;
            result.FellBackToLocal = true;
            result.ProviderError = reason;
            result.Description = $"[Fallback: Local] {reason}. {result.Description}";
            return result;
        }

        private static string FirstLine(string message)
        {
            if (string.IsNullOrEmpty(message)) { return message; }
            var newline = message.IndexOf('\n');
            return (newline > 0 ? message.Substring(0, newline) : message).Trim();
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
