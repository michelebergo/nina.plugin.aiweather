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
            new ProviderOption("GitHubModels", "GitHub Models (RETIRED)"),
            new ProviderOption("OpenAI", "OpenAI"),
            new ProviderOption("Gemini", "Google Gemini"),
            new ProviderOption("Anthropic", "Anthropic Claude"),
            new ProviderOption("Ollama", "Ollama / Custom (local server)")
        };

        // Vision-capable model prefixes known to work with image analysis via ChatCompletions.
        // Used as a prefix filter when querying the GitHub Models catalog (which contains many
        // non-vision, non-chat models). Prefix matching allows versioned variants
        // (e.g. "claude-sonnet-4-5-20250929") to pass the filter automatically.
        private static readonly string[] SupportedVisionModelPrefixes = new[]
        {
            // OpenAI — all GPT-4o/4.1 and o-series support vision
            "gpt-4o",
            "gpt-4.1",
            "o1",
            "o3",
            "o4-mini",
            // Anthropic (via GitHub) — only Claude 3+ supports vision
            "claude-sonnet-4",
            "claude-haiku-4",
            "claude-3.5-sonnet",
            "claude-3-5-sonnet",
            "claude-3-opus",
            "claude-3-sonnet",
            "claude-3-haiku",
            // Google (via GitHub) — all Gemini 1.5+ support vision
            "gemini-1.5",
            "gemini-2.0",
            "gemini-2.5",
        };

        // Models/patterns to exclude even if they match a vision prefix above.
        // These are text-only, audio-only, or otherwise non-vision variants.
        private static readonly string[] NonVisionExcludePatterns = new[]
        {
            "embed",      // embedding models
            "tts",        // text-to-speech
            "whisper",    // speech recognition
            "dall-e",     // image generation (not analysis)
            "realtime",   // realtime API variants
            "audio",      // audio-only models
            "search",     // search-optimized (no vision)
            "preview",    // preview models (often lack vision)
            "-mini-high", // reasoning variants without vision
        };

        // Per-provider default/fallback model lists. These are shown when the provider's
        // live API cannot be reached, and serve as the initial list before the first fetch.
        // Ordered with recommended default first.
        private static readonly System.Collections.Generic.Dictionary<string, string[]> DefaultModelsByProvider =
            new System.Collections.Generic.Dictionary<string, string[]>(StringComparer.OrdinalIgnoreCase)
            {
                ["GitHubModels"] = new[]
                {
                    "gpt-4o",
                    "gpt-4o-mini",
                    "gpt-4.1",
                    "gpt-4.1-mini",
                    "gpt-4.1-nano",
                    "o1",
                    "o3",
                    "o4-mini",
                    "claude-sonnet-4-5",
                    "claude-sonnet-4",
                    "claude-haiku-4-5",
                    "claude-3.5-sonnet",
                    "claude-3-5-sonnet",
                    "gemini-1.5-flash",
                    "gemini-1.5-pro",
                    "gemini-2.0-flash",
                    "gemini-2.5-flash",
                    "gemini-2.5-pro",
                },
                ["OpenAI"] = new[]
                {
                    "gpt-4o",
                    "gpt-4o-mini",
                    "gpt-4.1",
                    "gpt-4.1-mini",
                    "gpt-4.1-nano",
                    "o1",
                    "o3",
                    "o4-mini",
                },
                ["Gemini"] = new[]
                {
                    // The -latest alias always points at Google's newest stable Flash and
                    // cannot expire; Gemini 1.x/2.0 were shut down by Google in 2026.
                    "gemini-flash-latest",
                    "gemini-3.6-flash",
                    "gemini-3.5-flash",
                    "gemini-2.5-flash",
                    "gemini-2.5-pro",
                },
                ["Anthropic"] = new[]
                {
                    "claude-sonnet-4-5-20250929",
                    "claude-sonnet-4-20250514",
                    "claude-haiku-4-5-20251001",
                    "claude-3-5-sonnet-20241022",
                    "claude-3-5-haiku-20241022",
                    "claude-3-opus-20240229",
                },
                // Local servers don't advertise vision capability; these are common
                // vision-capable Ollama models used only when /v1/models is unreachable.
                ["Ollama"] = new[]
                {
                    "llava",
                    "qwen2.5vl",
                    "minicpm-v",
                }
            };

        // ── Model cache (1-hour TTL, per provider – mirrors AI Assistant pattern) ──
        private static readonly System.Collections.Generic.Dictionary<string, (string[] models, DateTime fetchedAt)> _modelCache =
            new System.Collections.Generic.Dictionary<string, (string[], DateTime)>(StringComparer.OrdinalIgnoreCase);
        private static readonly TimeSpan ModelCacheDuration = TimeSpan.FromHours(1);

        public ObservableCollection<string> AvailableModels { get; } = new ObservableCollection<string>(
            DefaultModelsByProvider.TryGetValue("GitHubModels", out var init) ? init : Array.Empty<string>());

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

            // Make the AIWeather category visible in the sequencer Symbols sidebar right
            // away (N.I.N.A. 3.3+), with blank values until the first analysis fills them.
            Services.SequencerSymbolPublisher.TryRegisterAtStartup();
            
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
        public bool IsOllamaProvider => string.Equals(AnalysisProvider, "Ollama", StringComparison.OrdinalIgnoreCase);
        public bool IsLocalProvider => string.Equals(AnalysisProvider, "Local", StringComparison.OrdinalIgnoreCase);
        public bool IsNonLocalProvider => !IsLocalProvider;

        public async Task RefreshAvailableModelsAsync(bool forceRefresh = false)
        {
            var currentProvider = AnalysisProvider ?? "Local";

            // Local provider has no model dropdown.
            if (string.Equals(currentProvider, "Local", StringComparison.OrdinalIgnoreCase))
            {
                RunOnUiThread(() =>
                {
                    AvailableModels.Clear();
                    ModelsStatus = "Local analysis — no model selection needed";
                });
                return;
            }

            try
            {
                // ── Check cache first (1-hour TTL) ──────────────────────────────
                if (!forceRefresh &&
                    _modelCache.TryGetValue(currentProvider, out var cached) &&
                    DateTime.UtcNow - cached.fetchedAt < ModelCacheDuration &&
                    cached.models.Length > 0)
                {
                    Logger.Debug($"Returning {cached.models.Length} cached models for {currentProvider}");
                    RunOnUiThread(() =>
                    {
                        SyncModelsCollection(cached.models);
                        ModelsStatus = $"Loaded {cached.models.Length} models (cached)";
                        EnsureSelectedModelIsValid();
                    });
                    return;
                }

                // Clear cache for this provider when forcing.
                if (forceRefresh)
                {
                    _modelCache.Remove(currentProvider);
                }

                RunOnUiThread(() => { ModelsStatus = $"Fetching models from {currentProvider}..."; });

                // ── Fetch live model list per provider ──────────────────────────
                string[]? liveModels = null;
                try
                {
                    liveModels = await FetchModelsForProviderAsync(currentProvider);
                }
                catch (Exception ex)
                {
                    Logger.Warning($"Live model fetch failed for {currentProvider}: {ex.Message}");
                }

                // Filter / fallback
                System.Collections.Generic.List<string> finalModels;

                if (liveModels != null && liveModels.Length > 0)
                {
                    // For GitHub Models we must filter to vision-capable models only.
                    if (string.Equals(currentProvider, "GitHubModels", StringComparison.OrdinalIgnoreCase))
                    {
                        finalModels = liveModels
                            .Where(m => SupportedVisionModelPrefixes.Any(prefix =>
                                m.StartsWith(prefix, StringComparison.OrdinalIgnoreCase)))
                            .Where(m => !NonVisionExcludePatterns.Any(excl =>
                                m.Contains(excl, StringComparison.OrdinalIgnoreCase)))
                            .Distinct(StringComparer.OrdinalIgnoreCase)
                            .OrderBy(m => m)
                            .ToList();

                        if (finalModels.Count == 0)
                        {
                            // None of the live models passed the vision filter — use defaults.
                            Logger.Info($"GitHub Models returned {liveModels.Length} models but none matched vision filter; using defaults");
                            finalModels = DefaultModelsByProvider.TryGetValue(currentProvider, out var defs)
                                ? defs.ToList()
                                : new System.Collections.Generic.List<string> { "gpt-4o" };
                        }
                    }
                    else
                    {
                        // Other providers: use the live list as-is (already vision-filtered by their fetch methods).
                        finalModels = liveModels.ToList();
                    }

                    // Cache the result.
                    _modelCache[currentProvider] = (finalModels.ToArray(), DateTime.UtcNow);
                    Logger.Info($"Cached {finalModels.Count} vision-capable models for {currentProvider}");
                }
                else
                {
                    // Fallback to built-in defaults.
                    finalModels = DefaultModelsByProvider.TryGetValue(currentProvider, out var defaults) && defaults.Length > 0
                        ? defaults.ToList()
                        : new System.Collections.Generic.List<string>();
                }

                var statusMsg = liveModels != null && liveModels.Length > 0
                    ? $"Loaded {finalModels.Count} vision-capable models from {currentProvider}"
                    : $"Using built-in {currentProvider} model list ({finalModels.Count} models)";

                RunOnUiThread(() =>
                {
                    SyncModelsCollection(finalModels);

                    ModelsStatus = statusMsg;
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

        /// <summary>
        /// Synchronize AvailableModels in-place to avoid blanking the ComboBox.
        /// Adds new models, removes stale ones, preserves scroll position and selection.
        /// </summary>
        private void SyncModelsCollection(System.Collections.Generic.IList<string> newModels)
        {
            // Remove models no longer in the new list.
            for (int i = AvailableModels.Count - 1; i >= 0; i--)
            {
                if (!newModels.Contains(AvailableModels[i], StringComparer.OrdinalIgnoreCase))
                {
                    AvailableModels.RemoveAt(i);
                }
            }

            // Add new models that aren't in the current list.
            foreach (var model in newModels)
            {
                if (!AvailableModels.Any(m => string.Equals(m, model, StringComparison.OrdinalIgnoreCase)))
                {
                    AvailableModels.Add(model);
                }
            }

            // Reorder to match new list order.
            for (int i = 0; i < newModels.Count && i < AvailableModels.Count; i++)
            {
                if (!string.Equals(AvailableModels[i], newModels[i], StringComparison.OrdinalIgnoreCase))
                {
                    var idx = -1;
                    for (int j = i + 1; j < AvailableModels.Count; j++)
                    {
                        if (string.Equals(AvailableModels[j], newModels[i], StringComparison.OrdinalIgnoreCase))
                        {
                            idx = j;
                            break;
                        }
                    }
                    if (idx >= 0)
                    {
                        AvailableModels.Move(idx, i);
                    }
                }
            }
        }

        /// <summary>
        /// Query the provider's API to discover currently available models.
        /// Mirrors the pattern used in AI Assistant's per-provider GetAvailableModelsAsync().
        /// Returns null on failure (caller falls back to defaults).
        /// </summary>
        private async Task<string[]?> FetchModelsForProviderAsync(string provider)
        {
            // ── GitHub Models ───────────────────────────────────────────────────
            if (string.Equals(provider, "GitHubModels", StringComparison.OrdinalIgnoreCase))
            {
                // Retired by GitHub on 2026-07-30: the catalog endpoint returns 404 for
                // everyone. Skip the network and let the caller use the static defaults.
                return null;
            }

            // ── OpenAI ──────────────────────────────────────────────────────────
            if (string.Equals(provider, "OpenAI", StringComparison.OrdinalIgnoreCase))
            {
                var key = Properties.Settings.Default.OpenAIKey;
                if (string.IsNullOrWhiteSpace(key)) return null;

                return await FetchOpenAIModelsAsync(key.Trim());
            }

            // ── Gemini ──────────────────────────────────────────────────────────
            if (string.Equals(provider, "Gemini", StringComparison.OrdinalIgnoreCase))
            {
                var key = Properties.Settings.Default.GeminiKey;
                if (string.IsNullOrWhiteSpace(key)) return null;

                return await FetchGeminiModelsAsync(key.Trim());
            }

            // ── Anthropic ───────────────────────────────────────────────────────
            if (string.Equals(provider, "Anthropic", StringComparison.OrdinalIgnoreCase))
            {
                var key = Properties.Settings.Default.AnthropicKey;
                if (string.IsNullOrWhiteSpace(key)) return null;

                return await FetchAnthropicModelsAsync(key.Trim());
            }

            // ── Ollama ──────────────────────────────────────────────────────────
            if (string.Equals(provider, "Ollama", StringComparison.OrdinalIgnoreCase))
            {
                var baseUrl = Properties.Settings.Default.OllamaBaseUrl;
                if (string.IsNullOrWhiteSpace(baseUrl)) return null;

                return await FetchOllamaModelsAsync(baseUrl.Trim());
            }

            return null;
        }

        /// <summary>
        /// Fetch available OpenAI models via /v1/models, filtering to vision-capable models.
        /// </summary>
        private static async Task<string[]> FetchOpenAIModelsAsync(string apiKey)
        {
            using var http = new HttpClient();
            http.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", apiKey);
            http.DefaultRequestHeaders.UserAgent.ParseAdd("NINA-AIWeather/1.0");

            using var response = await http.GetAsync("https://api.openai.com/v1/models");
            var json = await response.Content.ReadAsStringAsync();

            if (!response.IsSuccessStatusCode)
                throw new InvalidOperationException($"HTTP {(int)response.StatusCode}");

            using var doc = JsonDocument.Parse(json);
            var models = doc.RootElement.GetProperty("data");

            // Keep only GPT-4o+ and o-series vision models (exclude embeddings, whisper, dall-e, tts, etc.)
            var visionPrefixes = new[] { "gpt-4o", "gpt-4.1", "o1", "o3", "o4-mini" };
            var excludePatterns = new[] { "embed", "whisper", "dall-e", "tts", "realtime", "audio", "search", "preview" };

            var result = new System.Collections.Generic.List<string>();
            foreach (var m in models.EnumerateArray())
            {
                var id = m.GetProperty("id").GetString();
                if (string.IsNullOrEmpty(id)) continue;
                if (excludePatterns.Any(p => id.Contains(p, StringComparison.OrdinalIgnoreCase))) continue;
                if (!visionPrefixes.Any(p => id.StartsWith(p, StringComparison.OrdinalIgnoreCase))) continue;

                result.Add(id);
            }

            // Sort and deduplicate: prefer shorter canonical names first.
            return result
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .OrderBy(m => m, StringComparer.OrdinalIgnoreCase)
                .ToArray();
        }

        /// <summary>
        /// Fetch available Gemini models via the generativelanguage API, filtering to generateContent-capable models.
        /// </summary>
        private static async Task<string[]> FetchGeminiModelsAsync(string apiKey)
        {
            using var http = new HttpClient();
            http.DefaultRequestHeaders.UserAgent.ParseAdd("NINA-AIWeather/1.0");

            var url = $"https://generativelanguage.googleapis.com/v1beta/models?key={Uri.EscapeDataString(apiKey)}";
            using var response = await http.GetAsync(url);
            var json = await response.Content.ReadAsStringAsync();

            if (!response.IsSuccessStatusCode)
                throw new InvalidOperationException($"HTTP {(int)response.StatusCode}");

            using var doc = JsonDocument.Parse(json);
            if (!doc.RootElement.TryGetProperty("models", out var models))
                return Array.Empty<string>();

            var result = new System.Collections.Generic.List<string>();
            foreach (var m in models.EnumerateArray())
            {
                // Only include models that support generateContent (i.e., actual chat/vision models).
                if (m.TryGetProperty("supportedGenerationMethods", out var methods) && methods.ValueKind == JsonValueKind.Array)
                {
                    bool supportsGenerate = false;
                    foreach (var method in methods.EnumerateArray())
                    {
                        if (string.Equals(method.GetString(), "generateContent", StringComparison.OrdinalIgnoreCase))
                        {
                            supportsGenerate = true;
                            break;
                        }
                    }
                    if (!supportsGenerate) continue;
                }

                // Name comes as "models/gemini-2.0-flash" — strip the prefix.
                var name = m.TryGetProperty("name", out var nameProp) ? nameProp.GetString() : null;
                if (string.IsNullOrEmpty(name)) continue;
                if (name.StartsWith("models/", StringComparison.OrdinalIgnoreCase))
                    name = name.Substring("models/".Length);

                // Only include Gemini models (skip legacy PaLM, etc.)
                if (!name.StartsWith("gemini", StringComparison.OrdinalIgnoreCase)) continue;

                result.Add(name);
            }

            return result
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .OrderBy(m => m, StringComparer.OrdinalIgnoreCase)
                .ToArray();
        }

        /// <summary>
        /// Fetch available Anthropic models via /v1/models (if supported) or return curated list.
        /// </summary>
        private static async Task<string[]> FetchAnthropicModelsAsync(string apiKey)
        {
            using var http = new HttpClient();
            http.DefaultRequestHeaders.UserAgent.ParseAdd("NINA-AIWeather/1.0");
            http.DefaultRequestHeaders.TryAddWithoutValidation("x-api-key", apiKey);
            http.DefaultRequestHeaders.TryAddWithoutValidation("anthropic-version", "2023-06-01");

            using var response = await http.GetAsync("https://api.anthropic.com/v1/models");

            if (response.IsSuccessStatusCode)
            {
                var json = await response.Content.ReadAsStringAsync();
                using var doc = JsonDocument.Parse(json);

                if (doc.RootElement.TryGetProperty("data", out var data) && data.ValueKind == JsonValueKind.Array)
                {
                    var result = new System.Collections.Generic.List<string>();
                    foreach (var m in data.EnumerateArray())
                    {
                        var id = m.TryGetProperty("id", out var idProp) ? idProp.GetString() : null;
                        if (string.IsNullOrEmpty(id)) continue;
                        // Only include Claude 3+ models that support vision.
                        // Claude 2.x, claude-instant, etc. are text-only.
                        if (!id.StartsWith("claude-3", StringComparison.OrdinalIgnoreCase)
                            && !id.StartsWith("claude-sonnet-4", StringComparison.OrdinalIgnoreCase)
                            && !id.StartsWith("claude-haiku-4", StringComparison.OrdinalIgnoreCase)
                            && !id.StartsWith("claude-opus-4", StringComparison.OrdinalIgnoreCase))
                            continue;

                        result.Add(id);
                    }

                    if (result.Count > 0)
                    {
                        return result
                            .Distinct(StringComparer.OrdinalIgnoreCase)
                            .OrderByDescending(m => m) // Newest first
                            .ToArray();
                    }
                }
            }

            // /v1/models may not be available for all accounts — return null to use defaults.
            return null!;
        }

        /// <summary>
        /// Fetch available models from an Ollama (or OpenAI-compatible local) server via {baseUrl}/models.
        /// Local servers don't advertise vision capability, so all discovered model ids are returned
        /// unfiltered. Returns null on any failure so the caller falls back to the default list.
        /// </summary>
        private static async Task<string[]> FetchOllamaModelsAsync(string baseUrl)
        {
            using var http = new HttpClient();
            http.Timeout = TimeSpan.FromSeconds(10);
            // Dummy bearer token: local servers ignore it, but some OpenAI-compatible
            // frontends reject requests without an Authorization header.
            http.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", "ollama");
            http.DefaultRequestHeaders.UserAgent.ParseAdd("NINA-AIWeather/1.0");

            var url = baseUrl.TrimEnd('/') + "/models";
            using var response = await http.GetAsync(url);

            if (!response.IsSuccessStatusCode)
                return null!;

            var json = await response.Content.ReadAsStringAsync();
            using var doc = JsonDocument.Parse(json);

            if (!doc.RootElement.TryGetProperty("data", out var data) || data.ValueKind != JsonValueKind.Array)
                return null!;

            var result = new System.Collections.Generic.List<string>();
            foreach (var m in data.EnumerateArray())
            {
                var id = m.TryGetProperty("id", out var idProp) ? idProp.GetString() : null;
                if (string.IsNullOrEmpty(id)) continue;

                result.Add(id);
            }

            if (result.Count == 0)
                return null!;

            return result
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .OrderBy(m => m, StringComparer.OrdinalIgnoreCase)
                .ToArray();
        }

        public Task TryGitHubTokenAsync()
        {
            // Retired by GitHub on 2026-07-30 for every customer, valid token or not:
            // testing against the dead endpoint would only produce a confusing 404.
            GitHubTokenStatus = Services.GitHubModelsAnalysisService.RetirementMessage;
            return Task.CompletedTask;
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

                // Invalidate cache so next refresh fetches fresh models.
                _modelCache.Remove("OpenAI");
                await RefreshAvailableModelsAsync();
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

                // Invalidate cache so next refresh fetches fresh models.
                _modelCache.Remove("Gemini");
                await RefreshAvailableModelsAsync();
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

                    // Invalidate cache so next refresh fetches fresh models.
                    _modelCache.Remove("Anthropic");
                    await RefreshAvailableModelsAsync();
                    return;
                }

                // Some accounts / versions may not expose /v1/models; fall back to a tiny /v1/messages call.
                if (response.StatusCode == System.Net.HttpStatusCode.NotFound)
                {
                    var model = string.IsNullOrWhiteSpace(Properties.Settings.Default.SelectedModel)
                        ? "claude-sonnet-4-5-20250929"
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

                    if (postResp.IsSuccessStatusCode)
                    {
                        _modelCache.Remove("Anthropic");
                        await RefreshAvailableModelsAsync();
                    }

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
            if (IsGitHubModelsProvider && AvailableModels.Count > 0
                && !AvailableModels.Any(m => string.Equals(m, selected, StringComparison.OrdinalIgnoreCase))
                && !SupportedVisionModelPrefixes.Any(prefix => selected.StartsWith(prefix, StringComparison.OrdinalIgnoreCase)))
            {
                Properties.Settings.Default.SelectedModel = AvailableModels[0];
                CoreUtil.SaveSettings(Properties.Settings.Default);
                RaisePropertyChanged(nameof(SelectedModel));
            }
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
        /// Cloud coverage safe lower limit percentage (0-100)
        /// </summary>
        public double CloudCoverageSafeThreshold
        {
            get => Properties.Settings.Default.CloudCoverageSafeThreshold;
            set
            {
                if (value < 0) value = 0;
                if (value > 100) value = 100;
                Properties.Settings.Default.CloudCoverageSafeThreshold = value;
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
                RaisePropertyChanged(nameof(IsOllamaProvider));
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
                RaisePropertyChanged(nameof(IsOllamaProvider));
                RaisePropertyChanged(nameof(IsLocalProvider));
                RaisePropertyChanged(nameof(IsNonLocalProvider));

                // Update model suggestions for the selected provider.
                _ = RefreshAvailableModelsAsync(forceRefresh: true);
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
        /// Base URL of the Ollama (or OpenAI-compatible local) server, e.g. http://localhost:11434/v1
        /// </summary>
        public string OllamaBaseUrl
        {
            get => Properties.Settings.Default.OllamaBaseUrl;
            set
            {
                Properties.Settings.Default.OllamaBaseUrl = value;
                CoreUtil.SaveSettings(Properties.Settings.Default);
                RaisePropertyChanged();
            }
        }

        /// <summary>
        /// When true (default), asks the local server to skip the model's "thinking"
        /// phase. Newer Ollama models (Gemma 4, Qwen 3.x, DeepSeek) reason at length
        /// by default, which can multiply response times and push past the timeout.
        /// </summary>
        public bool OllamaDisableThinking
        {
            get => Properties.Settings.Default.OllamaDisableThinking;
            set
            {
                Properties.Settings.Default.OllamaDisableThinking = value;
                CoreUtil.SaveSettings(Properties.Settings.Default);
                RaisePropertyChanged();
            }
        }

        /// <summary>
        /// Free-text notes about this observing site, appended to every analysis prompt.
        /// Every site has visual quirks a general prompt cannot anticipate — clouds lit by
        /// a nearby city that read as overcast, a dome reflection that reads as haze — and
        /// only the owner knows them. Empty by default: the prompt is unchanged for anyone
        /// who does not use the field.
        /// </summary>
        public string SiteNotes
        {
            get => Properties.Settings.Default.SiteNotes;
            set
            {
                Properties.Settings.Default.SiteNotes = value;
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
