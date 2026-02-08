using AIWeather.Models;
using Azure.AI.OpenAI;
using NINA.Core.Utility;
using OpenAI.Chat;
using System;
using System.ClientModel;
using System.Collections.Generic;
using System.Drawing;
using System.Drawing.Imaging;
using System.IO;
using System.Threading;
using System.Threading.Tasks;

namespace AIWeather.Services
{
    /// <summary>
    /// GitHub Models-based weather analysis service
    /// Supports Claude, GPT-4, and Gemini models via GitHub's AI endpoint
    /// </summary>
    [System.Runtime.Versioning.SupportedOSPlatform("windows")]
    public class GitHubModelsAnalysisService : IWeatherAnalysisService
    {
        private AzureOpenAIClient? _client;
        private readonly string _githubToken;
        private readonly string _modelName;
        private bool _isInitialized = false;

        private const string GITHUB_MODELS_ENDPOINT = "https://models.inference.ai.azure.com";

        // Model mapping
        private static readonly Dictionary<string, string> ModelMap = new()
        {
            { "gpt-4o", "gpt-4o" },
            { "gpt-4o-mini", "gpt-4o-mini" },
            { "claude-3.5-sonnet", "claude-3.5-sonnet" },
            { "gemini-1.5-flash", "gemini-1.5-flash" },
            { "gemini-1.5-pro", "gemini-1.5-pro" }
        };

        public GitHubModelsAnalysisService(string githubToken, string modelName)
        {
            _githubToken = githubToken;
            _modelName = NormalizeModelName(modelName);
        }

        public Task<bool> InitializeAsync(CancellationToken cancellationToken = default)
        {
            try
            {
                if (string.IsNullOrEmpty(_githubToken))
                {
                    Logger.Warning("GitHub token not configured");
                    return Task.FromResult(false);
                }

                // Create client with GitHub Models endpoint
                _client = new AzureOpenAIClient(
                    new Uri(GITHUB_MODELS_ENDPOINT),
                    new ApiKeyCredential(_githubToken));

                _isInitialized = true;
                Logger.Info($"GitHub Models service initialized with model: {_modelName}");
                return Task.FromResult(true);
            }
            catch (Exception ex)
            {
                Logger.Error($"Failed to initialize GitHub Models service: {ex.Message}", ex);
                return Task.FromResult(false);
            }
        }

        public async Task<WeatherAnalysisResult> AnalyzeImageAsync(Bitmap image, CancellationToken cancellationToken = default)
        {
            if (!_isInitialized || _client == null)
            {
                Logger.Warning("GitHub Models service not initialized, falling back to local analysis");
                var fallback = new LocalWeatherAnalysisService();
                return await fallback.AnalyzeImageAsync(image, cancellationToken);
            }

            try
            {
                Logger.Info($"Starting GitHub Models AI weather analysis with {_modelName}");

                // Convert bitmap to base64
                Logger.Debug("Converting image to base64...");
                string base64Image = ConvertImageToBase64(image);
                Logger.Debug($"Image converted to base64, size: {base64Image.Length} chars");

                // Get the chat client for the selected model
                var normalizedModelName = NormalizeModelName(_modelName);
                if (!ModelMap.ContainsKey(normalizedModelName))
                {
                    Logger.Warning($"Selected GitHub model '{normalizedModelName}' is not supported for vision analysis; falling back to local analysis");
                    var fallback = new LocalWeatherAnalysisService();
                    return await fallback.AnalyzeImageAsync(image, cancellationToken);
                }

                var modelId = ModelMap[normalizedModelName];
                Logger.Debug($"Getting chat client for model: {modelId}");
                var chatClient = _client.GetChatClient(modelId);

                // Create the prompt for weather analysis
                Logger.Debug("Creating chat messages with image...");
                var messages = new List<ChatMessage>
                {
                    new SystemChatMessage(WeatherAnalysisPrompts.DetailedSystemPrompt),
                    new UserChatMessage(
                        ChatMessageContentPart.CreateTextPart("Analyze this all-sky camera image and provide weather assessment:"),
                        ChatMessageContentPart.CreateImagePart(BinaryData.FromBytes(Convert.FromBase64String(base64Image)), "image/jpeg")
                    )
                };

                // Call the AI model with timeout
                Logger.Info("Calling GitHub Models API...");

                // Create a timeout cancellation token source (60 seconds timeout)
                using var timeoutCts = new CancellationTokenSource(TimeSpan.FromSeconds(60));
                using var linkedCts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken, timeoutCts.Token);

                var chatCompletion = await chatClient.CompleteChatAsync(messages, cancellationToken: linkedCts.Token);
                var response = chatCompletion.Value.Content[0].Text;

                Logger.Info($"GitHub Models API responded, response length: {response.Length} chars");
                Logger.Debug($"AI Response: {response}");

                // Parse the response
                var weatherResult = ParseAIResponse(response);

                Logger.Info($"GitHub Models analysis complete: {weatherResult.Condition}, Cloud Coverage: {weatherResult.CloudCoverage:F1}%, Safe: {weatherResult.IsSafeForImaging}");
                return weatherResult;
            }
            catch (OperationCanceledException ex)
            {
                Logger.Warning($"GitHub Models API call timed out or was cancelled: {ex.Message}");

                // Fallback to local analysis
                var fallback = new LocalWeatherAnalysisService();
                return await fallback.AnalyzeImageAsync(image, cancellationToken);
            }
            catch (Exception ex)
            {
                Logger.Error($"Error in GitHub Models analysis: {ex.Message}", ex);
                Logger.Debug($"Exception type: {ex.GetType().Name}, StackTrace: {ex.StackTrace}");

                // Fallback to local analysis
                var fallback = new LocalWeatherAnalysisService();
                return await fallback.AnalyzeImageAsync(image, cancellationToken);
            }
        }

        private static string NormalizeModelName(string modelName)
        {
            if (string.IsNullOrWhiteSpace(modelName))
            {
                return "gpt-4o";
            }

            // Common bad persisted values when binding ComboBox SelectedItem to a string.
            // Example: "System.Windows.Controls.ComboBoxItem: gpt-4o"
            const string comboBoxItemPrefix = "System.Windows.Controls.ComboBoxItem:";
            var normalized = modelName.Trim();
            if (normalized.StartsWith(comboBoxItemPrefix, StringComparison.OrdinalIgnoreCase))
            {
                normalized = normalized.Substring(comboBoxItemPrefix.Length).Trim();
            }

            // Some UIs may persist display text instead of Tag/content.
            // Keep only the first token if it looks like "gpt-4o (OpenAI)".
            var parenIndex = normalized.IndexOf('(');
            if (parenIndex > 0)
            {
                normalized = normalized.Substring(0, parenIndex).Trim();
            }

            // Some model lists return AzureML-style IDs like:
            // azureml://registries/azure-openai/models/gpt-4o-mini/versions/1
            // GitHub Models inference expects the short name (e.g., gpt-4o-mini).
            if (normalized.StartsWith("azureml://", StringComparison.OrdinalIgnoreCase))
            {
                try
                {
                    var match = System.Text.RegularExpressions.Regex.Match(
                        normalized,
                        @"/models/(?<name>[^/]+)/versions/",
                        System.Text.RegularExpressions.RegexOptions.IgnoreCase);

                    if (match.Success)
                    {
                        var name = match.Groups["name"].Value;
                        if (!string.IsNullOrWhiteSpace(name))
                        {
                            normalized = name.Trim();
                        }
                    }
                }
                catch
                {
                    // best-effort; fall back to original
                }
            }

            return string.IsNullOrWhiteSpace(normalized) ? "gpt-4o" : normalized;
        }

        private string ConvertImageToBase64(Bitmap image)
        {
            using var memoryStream = new MemoryStream();
            image.Save(memoryStream, ImageFormat.Jpeg);
            byte[] imageBytes = memoryStream.ToArray();
            return Convert.ToBase64String(imageBytes);
        }

        private WeatherAnalysisResult ParseAIResponse(string jsonResponse)
        {
            try
            {
                // Remove markdown code blocks if present
                jsonResponse = jsonResponse.Trim();
                if (jsonResponse.StartsWith("```json"))
                {
                    jsonResponse = jsonResponse.Substring(7);
                }
                if (jsonResponse.StartsWith("```"))
                {
                    jsonResponse = jsonResponse.Substring(3);
                }
                if (jsonResponse.EndsWith("```"))
                {
                    jsonResponse = jsonResponse.Substring(0, jsonResponse.Length - 3);
                }
                jsonResponse = jsonResponse.Trim();

                // Parse JSON response
                var json = System.Text.Json.JsonDocument.Parse(jsonResponse);
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

                // Return a default result indicating unknown conditions
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
