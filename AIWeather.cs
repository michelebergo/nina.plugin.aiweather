using NINA.Core.Utility;
using NINA.Plugin;
using NINA.Plugin.Interfaces;
using NINA.Profile;
using NINA.Profile.Interfaces;
using System;
using System.Collections.ObjectModel;
using System.ComponentModel;
using System.ComponentModel.Composition;
using System.Linq;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Runtime.CompilerServices;
using System.Text;
using System.Text.Json;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Threading;

namespace AIWeather
{
    /// <summary>
    /// Main plugin class for All Sky Camera Weather Monitoring
    /// This plugin monitors an all-sky camera via RTSP stream and uses AI to determine weather conditions
    /// </summary>
    [Export(typeof(IPluginManifest))]
    public class AIWeather : PluginBase, INotifyPropertyChanged
    {
        private readonly IProfileService _profileService;

        // Options instance for UI binding
        public AIWeatherOptions Options { get; private set; }

        private static Dispatcher? UiDispatcher => Application.Current?.Dispatcher;

        private static void RunOnUiThread(Action action)
        {
            var dispatcher = UiDispatcher;
            if (dispatcher == null || dispatcher.CheckAccess())
            {
                action();
                return;
            }

            dispatcher.Invoke(action);
        }

        public sealed class ProviderOption
        {
            public ProviderOption(string id, string name)
            {
                Id = id;
                Name = name;
            }

            public string Id { get; }
            public string Name { get; }
        }

        public ObservableCollection<ProviderOption> ProviderOptions { get; } = new ObservableCollection<ProviderOption>
        {
            new ProviderOption("Local", "Local (offline heuristic)"),
            new ProviderOption("GitHubModels", "GitHub Models"),
            new ProviderOption("OpenAI", "OpenAI"),
            new ProviderOption("Gemini", "Google Gemini"),
            new ProviderOption("Anthropic", "Anthropic Claude")
        };

        // Only models we know how to call for *vision* analysis via ChatCompletions.
        // The GitHub Models catalog includes many non-chat and/or non-vision models.
        private static readonly string[] SupportedVisionModelIds = new[]
        {
            "gpt-4o",
            "gpt-4o-mini",
            "claude-3.5-sonnet",
            "gemini-1.5-flash",
            "gemini-1.5-pro"
        };

        private static readonly System.Collections.Generic.Dictionary<string, string[]> DefaultModelsByProvider =
            new System.Collections.Generic.Dictionary<string, string[]>(StringComparer.OrdinalIgnoreCase)
            {
                ["GitHubModels"] = SupportedVisionModelIds,
                ["OpenAI"] = new[]
                {
                    "gpt-4o",
                    "gpt-4o-mini"
                },
                ["Gemini"] = new[]
                {
                    "gemini-1.5-flash",
                    "gemini-1.5-pro",
                    "gemini-2.0-flash"
                },
                ["Anthropic"] = new[]
                {
                    "claude-3-5-sonnet-20241022",
                    "claude-3-5-haiku-20241022",
                    "claude-3-opus-20240229"
                }
            };

        public ObservableCollection<string> AvailableModels { get; } = new ObservableCollection<string>(SupportedVisionModelIds);

        private string _gitHubTokenStatus = string.Empty;
        public string GitHubTokenStatus
        {
            get => _gitHubTokenStatus;
            private set
            {
                _gitHubTokenStatus = value;
                RaisePropertyChanged();
            }
        }

        private string _openAiKeyStatus = string.Empty;
        public string OpenAIKeyStatus
        {
            get => _openAiKeyStatus;
            private set
            {
                _openAiKeyStatus = value;
                RaisePropertyChanged();
            }
        }

        private string _geminiKeyStatus = string.Empty;
        public string GeminiKeyStatus
        {
            get => _geminiKeyStatus;
            private set
            {
                _geminiKeyStatus = value;
                RaisePropertyChanged();
            }
        }

        private string _anthropicKeyStatus = string.Empty;
        public string AnthropicKeyStatus
        {
            get => _anthropicKeyStatus;
            private set
            {
                _anthropicKeyStatus = value;
                RaisePropertyChanged();
            }
        }

        private string _modelsStatus = "Using built-in model list";
        public string ModelsStatus
        {
            get => _modelsStatus;
            private set
            {
                _modelsStatus = value;
                RaisePropertyChanged();
            }
        }

        [ImportingConstructor]
        public AIWeather(IProfileService profileService)
        {
            if (Properties.Settings.Default.UpdateSettings)
            {
                Properties.Settings.Default.Upgrade();
                Properties.Settings.Default.UpdateSettings = false;
                CoreUtil.SaveSettings(Properties.Settings.Default);
            }

            _profileService = profileService;

            // Create Options instance for binding
            Options = new AIWeatherOptions(_profileService);

            EnsureDefaults();
            
            // Load resource dictionaries for NINA to discover DataTemplates
            try
            {
                RunOnUiThread(() =>
                {
                    var optionsDict = new ResourceDictionary
                    {
                        Source = new Uri("pack://application:,,,/NINA.Plugin.AIWeather;component/Resources/Options.xaml", UriKind.Absolute)
                    };
                    Application.Current?.Resources.MergedDictionaries.Add(optionsDict);

                    var dataTemplatesDict = new ResourceDictionary
                    {
                        Source = new Uri("pack://application:,,,/NINA.Plugin.AIWeather;component/Resources/DataTemplates.xaml", UriKind.Absolute)
                    };
                    Application.Current?.Resources.MergedDictionaries.Add(dataTemplatesDict);
                });

                Logger.Info("Merged resource dictionary: Options");
                Logger.Info("Merged resource dictionary: DataTemplates");
            }
            catch (Exception ex)
            {
                Logger.Error($"Failed to load Options resource dictionary: {ex.Message}");
            }

            // Populate a usable model list immediately and refresh opportunistically if token exists.
            _ = RefreshAvailableModelsAsync();
        }

        private void EnsureDefaults()
        {
            var changed = false;

            // Migration: if AnalysisProvider is missing, derive from legacy UseGitHubModels.
            if (string.IsNullOrWhiteSpace(Properties.Settings.Default.AnalysisProvider))
            {
                Properties.Settings.Default.AnalysisProvider = Properties.Settings.Default.UseGitHubModels ? "GitHubModels" : "Local";
                changed = true;
            }

            // Keep legacy flag aligned for existing logic and older UI bindings.
            var provider = Properties.Settings.Default.AnalysisProvider?.Trim();
            var shouldUseGitHubModels = string.Equals(provider, "GitHubModels", StringComparison.OrdinalIgnoreCase);
            if (Properties.Settings.Default.UseGitHubModels != shouldUseGitHubModels)
            {
                Properties.Settings.Default.UseGitHubModels = shouldUseGitHubModels;
                changed = true;
            }

            if (string.IsNullOrWhiteSpace(Properties.Settings.Default.RtspUrl))
            {
                Properties.Settings.Default.RtspUrl = "rtsp://192.168.1.100:554/stream";
                changed = true;
            }

            if (Properties.Settings.Default.CheckIntervalMinutes < 1)
            {
                Properties.Settings.Default.CheckIntervalMinutes = 5;
                changed = true;
            }

            if (Properties.Settings.Default.CloudCoverageThreshold <= 0)
            {
                Properties.Settings.Default.CloudCoverageThreshold = 70.0;
                changed = true;
            }

            if (string.IsNullOrWhiteSpace(Properties.Settings.Default.SelectedModel))
            {
                Properties.Settings.Default.SelectedModel = "gpt-4o";
                changed = true;
            }

            if (changed)
            {
                CoreUtil.SaveSettings(Properties.Settings.Default);
            }
        }

        public bool IsGitHubModelsProvider => string.Equals(AnalysisProvider, "GitHubModels", StringComparison.OrdinalIgnoreCase);
        public bool IsOpenAIProvider => string.Equals(AnalysisProvider, "OpenAI", StringComparison.OrdinalIgnoreCase);
        public bool IsGeminiProvider => string.Equals(AnalysisProvider, "Gemini", StringComparison.OrdinalIgnoreCase);
        public bool IsAnthropicProvider => string.Equals(AnalysisProvider, "Anthropic", StringComparison.OrdinalIgnoreCase);
        public bool IsLocalProvider => string.Equals(AnalysisProvider, "Local", StringComparison.OrdinalIgnoreCase);
        public bool IsNonLocalProvider => !IsLocalProvider;

        public async Task RefreshAvailableModelsAsync()
        {
            try
            {
                string statusMessage;
                System.Collections.Generic.List<string>? newModels = null;

                // Non-GitHub providers: show a reasonable built-in suggestion list.
                if (!IsGitHubModelsProvider)
                {
                    if (DefaultModelsByProvider.TryGetValue(AnalysisProvider ?? string.Empty, out var defaults) && defaults.Length > 0)
                    {
                        newModels = defaults.ToList();
                        statusMessage = $"Using built-in {AnalysisProvider} model list";
                    }
                    else
                    {
                        statusMessage = $"Model list not available for {AnalysisProvider}; enter a model name";
                    }

                    RunOnUiThread(() =>
                    {
                        if (newModels != null)
                        {
                            AvailableModels.Clear();
                            foreach (var model in newModels)
                            {
                                AvailableModels.Add(model);
                            }
                        }

                        ModelsStatus = statusMessage;
                        EnsureSelectedModelIsValid();
                    });

                    return;
                }

                var token = Properties.Settings.Default.GitHubToken;
                if (string.IsNullOrWhiteSpace(token))
                {
                    RunOnUiThread(() =>
                    {
                        ModelsStatus = "Using built-in model list (no GitHub token set)";
                        EnsureSelectedModelIsValid();
                    });
                    return;
                }

                RunOnUiThread(() => { ModelsStatus = "Fetching models from GitHub Models..."; });

                var models = await FetchGitHubModelsAsync(token);
                if (models == null || models.Count == 0)
                {
                    RunOnUiThread(() =>
                    {
                        ModelsStatus = "Model fetch returned no results; using built-in list";
                        EnsureSelectedModelIsValid();
                    });
                    return;
                }

                // Filter to supported vision-capable chat models.
                newModels = models
                    .Where(m => SupportedVisionModelIds.Any(s => string.Equals(s, m, StringComparison.OrdinalIgnoreCase)))
                    .Distinct(StringComparer.OrdinalIgnoreCase)
                    .OrderBy(m => m)
                    .ToList();

                if (newModels.Count == 0)
                {
                    RunOnUiThread(() =>
                    {
                        ModelsStatus = $"Fetched {models.Count} models, but none are supported for vision analysis; using built-in list";
                        EnsureSelectedModelIsValid();
                    });
                    return;
                }

                RunOnUiThread(() =>
                {
                    AvailableModels.Clear();
                    foreach (var model in newModels)
                    {
                        AvailableModels.Add(model);
                    }

                    ModelsStatus = $"Loaded {AvailableModels.Count} supported models";
                    EnsureSelectedModelIsValid();
                });
            }
            catch (Exception ex)
            {
                RunOnUiThread(() =>
                {
                    ModelsStatus = $"Model fetch failed; using built-in list ({ex.Message})";
                    EnsureSelectedModelIsValid();
                });
                Logger.Warning($"Failed to refresh model list: {ex.Message}");
            }
        }

        public async Task TryGitHubTokenAsync()
        {
            try
            {
                var token = Properties.Settings.Default.GitHubToken;
                if (string.IsNullOrWhiteSpace(token))
                {
                    GitHubTokenStatus = "Token is empty";
                    return;
                }

                GitHubTokenStatus = "Testing token...";
                var models = await FetchGitHubModelsAsync(token);
                if (models == null)
                {
                    GitHubTokenStatus = "Token test failed (no response)";
                    return;
                }

                GitHubTokenStatus = $"Token OK (fetched {models.Count} models)";

                // Opportunistically refresh the filtered dropdown list.
                await RefreshAvailableModelsAsync();
            }
            catch (Exception ex)
            {
                GitHubTokenStatus = $"Token test failed: {ex.Message}";
            }
        }

        public async Task TryOpenAIKeyAsync()
        {
            try
            {
                var key = Properties.Settings.Default.OpenAIKey;
                if (string.IsNullOrWhiteSpace(key))
                {
                    OpenAIKeyStatus = "Key is empty";
                    return;
                }

                OpenAIKeyStatus = "Testing key...";

                using var http = new HttpClient();
                http.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", key.Trim());
                http.DefaultRequestHeaders.UserAgent.ParseAdd("NINA-AIWeather/1.0");

                using var response = await http.GetAsync("https://api.openai.com/v1/models");
                var json = await response.Content.ReadAsStringAsync();

                if (!response.IsSuccessStatusCode)
                {
                    OpenAIKeyStatus = $"Key test failed: HTTP {(int)response.StatusCode} {response.StatusCode}";
                    return;
                }

                var count = 0;
                try
                {
                    using var doc = JsonDocument.Parse(json);
                    if (doc.RootElement.ValueKind == JsonValueKind.Object && doc.RootElement.TryGetProperty("data", out var data) && data.ValueKind == JsonValueKind.Array)
                    {
                        count = data.GetArrayLength();
                    }
                }
                catch
                {
                    // ignore parse issues; success is enough
                }

                OpenAIKeyStatus = count > 0 ? $"Key OK (models: {count})" : "Key OK";
            }
            catch (Exception ex)
            {
                OpenAIKeyStatus = $"Key test failed: {ex.Message}";
            }
        }

        public async Task TryGeminiKeyAsync()
        {
            try
            {
                var key = Properties.Settings.Default.GeminiKey;
                if (string.IsNullOrWhiteSpace(key))
                {
                    GeminiKeyStatus = "Key is empty";
                    return;
                }

                GeminiKeyStatus = "Testing key...";

                using var http = new HttpClient();
                http.DefaultRequestHeaders.UserAgent.ParseAdd("NINA-AIWeather/1.0");

                var url = $"https://generativelanguage.googleapis.com/v1beta/models?key={Uri.EscapeDataString(key.Trim())}";
                using var response = await http.GetAsync(url);
                var json = await response.Content.ReadAsStringAsync();

                if (!response.IsSuccessStatusCode)
                {
                    GeminiKeyStatus = $"Key test failed: HTTP {(int)response.StatusCode} {response.StatusCode}";
                    return;
                }

                var count = 0;
                try
                {
                    using var doc = JsonDocument.Parse(json);
                    if (doc.RootElement.ValueKind == JsonValueKind.Object && doc.RootElement.TryGetProperty("models", out var models) && models.ValueKind == JsonValueKind.Array)
                    {
                        count = models.GetArrayLength();
                    }
                }
                catch
                {
                    // ignore parse issues; success is enough
                }

                GeminiKeyStatus = count > 0 ? $"Key OK (models: {count})" : "Key OK";
            }
            catch (Exception ex)
            {
                GeminiKeyStatus = $"Key test failed: {ex.Message}";
            }
        }

        public async Task TryAnthropicKeyAsync()
        {
            try
            {
                var key = Properties.Settings.Default.AnthropicKey;
                if (string.IsNullOrWhiteSpace(key))
                {
                    AnthropicKeyStatus = "Key is empty";
                    return;
                }

                AnthropicKeyStatus = "Testing key...";

                using var http = new HttpClient();
                http.DefaultRequestHeaders.UserAgent.ParseAdd("NINA-AIWeather/1.0");
                http.DefaultRequestHeaders.TryAddWithoutValidation("x-api-key", key.Trim());
                http.DefaultRequestHeaders.TryAddWithoutValidation("anthropic-version", "2023-06-01");

                // Prefer listing models if available.
                using var response = await http.GetAsync("https://api.anthropic.com/v1/models");
                var json = await response.Content.ReadAsStringAsync();

                if (response.IsSuccessStatusCode)
                {
                    AnthropicKeyStatus = "Key OK";
                    return;
                }

                // Some accounts / versions may not expose /v1/models; fall back to a tiny /v1/messages call.
                if (response.StatusCode == System.Net.HttpStatusCode.NotFound)
                {
                    var model = string.IsNullOrWhiteSpace(Properties.Settings.Default.SelectedModel)
                        ? "claude-3-5-sonnet-20241022"
                        : Properties.Settings.Default.SelectedModel.Trim();

                    var payload = new
                    {
                        model,
                        max_tokens = 1,
                        messages = new object[]
                        {
                            new { role = "user", content = "ping" }
                        }
                    };

                    using var post = new HttpRequestMessage(HttpMethod.Post, "https://api.anthropic.com/v1/messages");
                    post.Headers.TryAddWithoutValidation("x-api-key", key.Trim());
                    post.Headers.TryAddWithoutValidation("anthropic-version", "2023-06-01");
                    post.Headers.UserAgent.ParseAdd("NINA-AIWeather/1.0");
                    post.Content = new StringContent(JsonSerializer.Serialize(payload), Encoding.UTF8, "application/json");

                    using var postResp = await http.SendAsync(post);
                    _ = await postResp.Content.ReadAsStringAsync();

                    AnthropicKeyStatus = postResp.IsSuccessStatusCode
                        ? "Key OK"
                        : $"Key test failed: HTTP {(int)postResp.StatusCode} {postResp.StatusCode}";

                    return;
                }

                AnthropicKeyStatus = $"Key test failed: HTTP {(int)response.StatusCode} {response.StatusCode}";
            }
            catch (Exception ex)
            {
                AnthropicKeyStatus = $"Key test failed: {ex.Message}";
            }
        }

        private void EnsureSelectedModelIsValid()
        {
            var selected = Properties.Settings.Default.SelectedModel;
            if (string.IsNullOrWhiteSpace(selected))
            {
                Properties.Settings.Default.SelectedModel = AvailableModels.FirstOrDefault() ?? "gpt-4o";
                CoreUtil.SaveSettings(Properties.Settings.Default);
                RaisePropertyChanged(nameof(SelectedModel));
                return;
            }

            // For GitHub Models we enforce membership to avoid accidental unsupported/unknown model calls.
            // For other providers the model box is editable; keep user-entered values even if not in the suggestion list.
            if (IsGitHubModelsProvider && AvailableModels.Count > 0 && !AvailableModels.Any(m => string.Equals(m, selected, StringComparison.OrdinalIgnoreCase)))
            {
                Properties.Settings.Default.SelectedModel = AvailableModels[0];
                CoreUtil.SaveSettings(Properties.Settings.Default);
                RaisePropertyChanged(nameof(SelectedModel));
            }
        }

        private static async Task<System.Collections.Generic.List<string>> FetchGitHubModelsAsync(string githubToken)
        {
            using var http = new HttpClient();
            http.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", githubToken);
            http.DefaultRequestHeaders.UserAgent.ParseAdd("NINA-AIWeather/1.0");

            // GitHub Models endpoint. Response shape can be array or object; we parse both.
            var url = "https://models.inference.ai.azure.com/models";
            using var response = await http.GetAsync(url);
            var json = await response.Content.ReadAsStringAsync();

            if (!response.IsSuccessStatusCode)
            {
                throw new InvalidOperationException($"HTTP {(int)response.StatusCode}: {json}");
            }

            using var doc = JsonDocument.Parse(json);
            var root = doc.RootElement;
            var results = new System.Collections.Generic.List<string>();

            void AddFromArray(JsonElement arr)
            {
                foreach (var el in arr.EnumerateArray())
                {
                    if (el.ValueKind == JsonValueKind.String)
                    {
                        var s = el.GetString();
                        if (!string.IsNullOrWhiteSpace(s)) results.Add(s);
                        continue;
                    }

                    if (el.ValueKind == JsonValueKind.Object)
                    {
                        // Prefer short names like "gpt-4o-mini" over AzureML-style IDs.
                        if (el.TryGetProperty("name", out var nameProp) && nameProp.ValueKind == JsonValueKind.String)
                        {
                            var name = nameProp.GetString();
                            if (!string.IsNullOrWhiteSpace(name)) results.Add(name);
                            continue;
                        }

                        if (el.TryGetProperty("model", out var modelProp) && modelProp.ValueKind == JsonValueKind.String)
                        {
                            var model = modelProp.GetString();
                            if (!string.IsNullOrWhiteSpace(model)) results.Add(model);
                            continue;
                        }

                        if (el.TryGetProperty("id", out var idProp) && idProp.ValueKind == JsonValueKind.String)
                        {
                            var id = idProp.GetString();
                            if (!string.IsNullOrWhiteSpace(id) && !id.StartsWith("azureml://", StringComparison.OrdinalIgnoreCase))
                            {
                                results.Add(id);
                            }
                        }
                    }
                }
            }

            if (root.ValueKind == JsonValueKind.Array)
            {
                AddFromArray(root);
            }
            else if (root.ValueKind == JsonValueKind.Object)
            {
                if (root.TryGetProperty("data", out var dataProp) && dataProp.ValueKind == JsonValueKind.Array)
                {
                    AddFromArray(dataProp);
                }
                else if (root.TryGetProperty("models", out var modelsProp) && modelsProp.ValueKind == JsonValueKind.Array)
                {
                    AddFromArray(modelsProp);
                }
                else if (root.TryGetProperty("items", out var itemsProp) && itemsProp.ValueKind == JsonValueKind.Array)
                {
                    AddFromArray(itemsProp);
                }
            }

            return results;
        }

        #region Plugin Settings Properties

        /// <summary>
        /// RTSP URL of the all-sky camera stream
        /// </summary>
        public string RtspUrl
        {
            get => Properties.Settings.Default.RtspUrl;
            set
            {
                Properties.Settings.Default.RtspUrl = value;
                CoreUtil.SaveSettings(Properties.Settings.Default);
                RaisePropertyChanged();
            }
        }

        /// <summary>
        /// RTSP camera username (optional)
        /// </summary>
        public string RtspUsername
        {
            get => Properties.Settings.Default.RtspUsername;
            set
            {
                Properties.Settings.Default.RtspUsername = value;
                CoreUtil.SaveSettings(Properties.Settings.Default);
                RaisePropertyChanged();
            }
        }

        /// <summary>
        /// RTSP camera password (optional)
        /// </summary>
        public string RtspPassword
        {
            get => Properties.Settings.Default.RtspPassword;
            set
            {
                Properties.Settings.Default.RtspPassword = value;
                CoreUtil.SaveSettings(Properties.Settings.Default);
                RaisePropertyChanged();
            }
        }

        /// <summary>
        /// Interval in minutes between weather checks
        /// </summary>
        public int CheckIntervalMinutes
        {
            get => Properties.Settings.Default.CheckIntervalMinutes;
            set
            {
                if (value < 1) value = 1;
                if (value > 60) value = 60;
                Properties.Settings.Default.CheckIntervalMinutes = value;
                CoreUtil.SaveSettings(Properties.Settings.Default);
                RaisePropertyChanged();
            }
        }

        /// <summary>
        /// Cloud coverage threshold percentage (0-100)
        /// </summary>
        public double CloudCoverageThreshold
        {
            get => Properties.Settings.Default.CloudCoverageThreshold;
            set
            {
                if (value < 0) value = 0;
                if (value > 100) value = 100;
                Properties.Settings.Default.CloudCoverageThreshold = value;
                CoreUtil.SaveSettings(Properties.Settings.Default);
                RaisePropertyChanged();
            }
        }

        /// <summary>
        /// Whether to use GitHub Models AI for analysis
        /// </summary>
        public bool UseGitHubModels
        {
            get => Properties.Settings.Default.UseGitHubModels;
            set
            {
                Properties.Settings.Default.UseGitHubModels = value;
                // Keep provider in sync.
                Properties.Settings.Default.AnalysisProvider = value ? "GitHubModels" : "Local";
                CoreUtil.SaveSettings(Properties.Settings.Default);
                RaisePropertyChanged();
                RaisePropertyChanged(nameof(AnalysisProvider));
                RaisePropertyChanged(nameof(IsGitHubModelsProvider));
                RaisePropertyChanged(nameof(IsOpenAIProvider));
                RaisePropertyChanged(nameof(IsGeminiProvider));
                RaisePropertyChanged(nameof(IsAnthropicProvider));
                RaisePropertyChanged(nameof(IsLocalProvider));
                RaisePropertyChanged(nameof(IsNonLocalProvider));
            }
        }

        public string AnalysisProvider
        {
            get => Properties.Settings.Default.AnalysisProvider;
            set
            {
                var normalized = (value ?? string.Empty).Trim();
                if (string.IsNullOrWhiteSpace(normalized))
                {
                    normalized = "Local";
                }

                Properties.Settings.Default.AnalysisProvider = normalized;
                Properties.Settings.Default.UseGitHubModels = string.Equals(normalized, "GitHubModels", StringComparison.OrdinalIgnoreCase);
                CoreUtil.SaveSettings(Properties.Settings.Default);
                RaisePropertyChanged();
                RaisePropertyChanged(nameof(UseGitHubModels));
                RaisePropertyChanged(nameof(IsGitHubModelsProvider));
                RaisePropertyChanged(nameof(IsOpenAIProvider));
                RaisePropertyChanged(nameof(IsGeminiProvider));
                RaisePropertyChanged(nameof(IsAnthropicProvider));
                RaisePropertyChanged(nameof(IsLocalProvider));
                RaisePropertyChanged(nameof(IsNonLocalProvider));

                // Update model suggestions for the selected provider.
                _ = RefreshAvailableModelsAsync();
            }
        }

        public string OpenAIKey
        {
            get => Properties.Settings.Default.OpenAIKey;
            set
            {
                Properties.Settings.Default.OpenAIKey = value;
                CoreUtil.SaveSettings(Properties.Settings.Default);
                RaisePropertyChanged();
            }
        }

        public string GeminiKey
        {
            get => Properties.Settings.Default.GeminiKey;
            set
            {
                Properties.Settings.Default.GeminiKey = value;
                CoreUtil.SaveSettings(Properties.Settings.Default);
                RaisePropertyChanged();
            }
        }

        public string AnthropicKey
        {
            get => Properties.Settings.Default.AnthropicKey;
            set
            {
                Properties.Settings.Default.AnthropicKey = value;
                CoreUtil.SaveSettings(Properties.Settings.Default);
                RaisePropertyChanged();
            }
        }

        /// <summary>
        /// GitHub Models API token
        /// </summary>
        public string GitHubToken
        {
            get => Properties.Settings.Default.GitHubToken;
            set
            {
                Properties.Settings.Default.GitHubToken = value;
                CoreUtil.SaveSettings(Properties.Settings.Default);
                RaisePropertyChanged();
            }
        }

        /// <summary>
        /// Selected AI model for analysis
        /// </summary>
        public string SelectedModel
        {
            get => Properties.Settings.Default.SelectedModel;
            set
            {
                Properties.Settings.Default.SelectedModel = value;
                CoreUtil.SaveSettings(Properties.Settings.Default);
                RaisePropertyChanged();
            }
        }

        #endregion

        public override Task Teardown()
        {
            // Clean up resources when plugin is unloaded
            return base.Teardown();
        }

        public event PropertyChangedEventHandler? PropertyChanged;
        protected void RaisePropertyChanged([CallerMemberName] string? propertyName = null)
        {
            var dispatcher = UiDispatcher;
            if (dispatcher != null && !dispatcher.CheckAccess())
            {
                dispatcher.BeginInvoke(new Action(() => RaisePropertyChanged(propertyName)));
                return;
            }

            PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(propertyName));
        }
    }
}
