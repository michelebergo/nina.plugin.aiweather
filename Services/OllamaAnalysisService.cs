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
    /// <summary>
    /// Analysis service for Ollama and any OpenAI-compatible local server
    /// (LM Studio, llama.cpp, LocalAI). No API key required; a dummy bearer
    /// token is sent for servers that expect an Authorization header.
    /// </summary>
    [System.Runtime.Versioning.SupportedOSPlatform("windows")]
    public class OllamaAnalysisService : IWeatherAnalysisService
    {
        private static readonly HttpClient Http = new HttpClient();

        private readonly string _endpoint;
        private readonly string _modelName;
        private readonly bool _disableThinking;
        private bool _isInitialized;

        private const string DefaultBaseUrl = "http://localhost:11434/v1";

        public OllamaAnalysisService(string baseUrl, string modelName, bool disableThinking = true)
        {
            var normalized = string.IsNullOrWhiteSpace(baseUrl) ? DefaultBaseUrl : baseUrl.Trim();
            _endpoint = normalized.TrimEnd('/') + "/chat/completions";
            _modelName = string.IsNullOrWhiteSpace(modelName) ? "llava" : modelName.Trim();
            _disableThinking = disableThinking;
        }

        public Task<bool> InitializeAsync(CancellationToken cancellationToken = default)
        {
            _isInitialized = true;
            Logger.Info($"Ollama analysis service initialized with model: {_modelName} (endpoint: {_endpoint})");
            return Task.FromResult(true);
        }

        public async Task<WeatherAnalysisResult> AnalyzeImageAsync(Bitmap image, AstroContext? astroContext = null, CancellationToken cancellationToken = default)
        {
            if (!_isInitialized)
            {
                Logger.Warning("Ollama service not initialized, falling back to local analysis");
                var fallback = new LocalWeatherAnalysisService();
                return await fallback.AnalyzeImageAsync(image, astroContext, cancellationToken);
            }

            try
            {
                Logger.Info($"Starting Ollama AI weather analysis with {_modelName}");

                var base64Image = ConvertImageToBase64(image);
                var imageUrl = $"data:image/jpeg;base64,{base64Image}";

                var userText = "Analyze this all-sky camera image and provide weather assessment:";
                var promptPrefix = WeatherAnalysisPrompts.BuildPromptPrefix(astroContext);
                if (promptPrefix.Length > 0)
                    userText = promptPrefix + "\n" + userText;

                var payload = new System.Collections.Generic.Dictionary<string, object>
                {
                    ["model"] = _modelName,
                    ["temperature"] = 0.1,
                    // Room for a reasoning phase and the answer together: a local model
                    // that thinks before answering spends this budget twice (issue #16).
                    ["max_tokens"] = 2048,
                    ["messages"] = new object[]
                    {
                        new { role = "system", content = WeatherAnalysisPrompts.DetailedSystemPrompt },
                        new {
                            role = "user",
                            content = new object[]
                            {
                                new { type = "text", text = userText },
                                new { type = "image_url", image_url = new { url = imageUrl } }
                            }
                        }
                    }
                };

                if (_disableThinking)
                {
                    // Newer Ollama models (Gemma 4, Qwen 3.x, DeepSeek) enable a "thinking"
                    // phase by default, multiplying response times (77s vs 14s measured in
                    // the field on the same image) and sometimes leaving the actual answer
                    // in a separate reasoning field. Servers that do not know this
                    // parameter ignore it.
                    payload["reasoning_effort"] = "none";
                }

                using var request = new HttpRequestMessage(HttpMethod.Post, _endpoint);
                // Dummy bearer token: local servers ignore it, but some OpenAI-compatible
                // frontends reject requests without an Authorization header.
                request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", "ollama");
                request.Headers.UserAgent.ParseAdd("NINA-AIWeather/1.0");
                request.Content = new StringContent(JsonSerializer.Serialize(payload), Encoding.UTF8, "application/json");

                Logger.Info("Calling Ollama API...");

                // Create a timeout cancellation token source (120 seconds timeout —
                // local models can be slow, especially on first load)
                using var timeoutCts = new CancellationTokenSource(TimeSpan.FromSeconds(120));
                using var linkedCts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken, timeoutCts.Token);

                using var response = await Http.SendAsync(request, linkedCts.Token);
                var json = await response.Content.ReadAsStringAsync(linkedCts.Token);

                if (!response.IsSuccessStatusCode)
                {
                    Logger.Error($"Ollama API error: HTTP {(int)response.StatusCode}: {json}");
                    throw new InvalidOperationException($"HTTP {(int)response.StatusCode}: {json}");
                }

                Logger.Info("Ollama API responded, parsing response...");

                using var doc = JsonDocument.Parse(json);
                var root = doc.RootElement;
                var content = ExtractMessageContent(root);

                var result = WeatherResponseParser.Parse(content);
                Logger.Info($"Ollama analysis complete: {result.Condition}, Cloud Coverage: {result.CloudCoverage:F1}%");
                return result;
            }
            catch (OperationCanceledException ex)
            {
                Logger.Warning($"Ollama API call timed out or was cancelled, falling back to local analysis: {ex.Message}");
                var fallback = new LocalWeatherAnalysisService();
                var result = await fallback.AnalyzeImageAsync(image, astroContext, cancellationToken);
                result.Description = $"[Fallback: Local] Ollama timed out. {result.Description}";
                return result;
            }
            catch (WeatherResponseParseException ex)
            {
                // The answer arrived but could not be read - most often cut short by the
                // model. That is a failed reading, not a reading of 50%: fall back to the
                // offline analyzer, which measures the image that was actually captured.
                Logger.Warning($"Ollama answer could not be parsed, falling back to local analysis: {ex.Message}");
                Logger.Debug($"Unparsed response: {ex.RawResponse}");
                var fallback = new LocalWeatherAnalysisService();
                var result = await fallback.AnalyzeImageAsync(image, astroContext, cancellationToken);
                result.Description = $"[Fallback: Local] Ollama: {ex.Message} {result.Description}";
                return result;
            }
            catch (Exception ex)
            {
                Logger.Error($"Error in Ollama analysis, falling back to local analysis: {ex.Message}", ex);
                var fallback = new LocalWeatherAnalysisService();
                var result = await fallback.AnalyzeImageAsync(image, astroContext, cancellationToken);
                result.Description = $"[Fallback: Local] Ollama error. {result.Description}";
                return result;
            }
        }

        private static string ExtractMessageContent(JsonElement root)
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
                            var text = StripThinkingTags(content.GetString() ?? string.Empty);
                            if (!string.IsNullOrWhiteSpace(text))
                            {
                                return text;
                            }
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
                            var joined = StripThinkingTags(sb.ToString().Trim());
                            if (!string.IsNullOrWhiteSpace(joined))
                            {
                                return joined;
                            }
                        }
                    }

                    // Thinking-capable models can leave content empty and put the actual
                    // answer in a reasoning field instead - recover it from there.
                    foreach (var field in new[] { "reasoning", "reasoning_content", "thinking" })
                    {
                        if (message.TryGetProperty(field, out var reasoning) && reasoning.ValueKind == JsonValueKind.String)
                        {
                            var text = reasoning.GetString() ?? string.Empty;
                            if (!string.IsNullOrWhiteSpace(text))
                            {
                                Logger.Info($"Ollama response content was empty; recovered answer from '{field}' field");
                                return text;
                            }
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

        /// <summary>
        /// Some models (e.g. Qwen 3.x) emit their reasoning inline as &lt;think&gt;...&lt;/think&gt;
        /// blocks inside the content; the answer follows the closing tag.
        /// </summary>
        private static string StripThinkingTags(string text)
        {
            if (string.IsNullOrEmpty(text))
            {
                return text;
            }

            var result = System.Text.RegularExpressions.Regex.Replace(
                text, "<think>.*?</think>", string.Empty,
                System.Text.RegularExpressions.RegexOptions.Singleline | System.Text.RegularExpressions.RegexOptions.IgnoreCase);
            return result.Trim();
        }

        private static string ConvertImageToBase64(Bitmap image)
        {
            using var memoryStream = new MemoryStream();
            image.Save(memoryStream, ImageFormat.Jpeg);
            return Convert.ToBase64String(memoryStream.ToArray());
        }

    }
}
