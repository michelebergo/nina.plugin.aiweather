using NINA.Core.Utility;
using AIWeather.Localization;
using NINA.Plugin;
using NINA.Plugin.Interfaces;
using NINA.Profile;
using NINA.Profile.Interfaces;
using System;
using System.Collections.ObjectModel;
using System.ComponentModel;
using System.ComponentModel.Composition;
using System.Globalization;
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

        public ObservableCollection<ProviderOption> ProviderOptions { get; } = CreateProviderOptions();

        private static ObservableCollection<ProviderOption> CreateProviderOptions()
        {
            return new ObservableCollection<ProviderOption>
            {
                new("Local", UiLocalization.Text("Options.ProviderNameLocal")),
                new("GitHubModels", UiLocalization.Text("Options.ProviderNameGitHub")),
                new("OpenAI", "OpenAI"),
                new(Services.GeminiProviderProfile.PaidProviderId,
                    UiLocalization.Text("Options.ProviderNameGeminiPaid")),
                new(Services.GeminiProviderProfile.FreeProviderId,
                    UiLocalization.Text("Options.ProviderNameGeminiFree")),
                new("Anthropic", "Anthropic Claude"),
                new("Ollama", UiLocalization.Text("Options.ProviderNameOllama"))
            };
        }

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
                ["GeminiFree"] = Services.GeminiProviderProfile.DefaultFreeModelOrder.ToArray(),
                ["Anthropic"] = Services.AnthropicAnalysisService.FallbackModels,
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

        public ObservableCollection<string> GeminiFreeModels { get; } = new ObservableCollection<string>(
            Services.GeminiProviderProfile.ParseFreeModelOrder(
                Properties.Settings.Default.GeminiFreeModelOrder));

        private string? _selectedGeminiFreeModel;
        public string? SelectedGeminiFreeModel
        {
            get => _selectedGeminiFreeModel;
            set
            {
                if (string.Equals(_selectedGeminiFreeModel, value, StringComparison.OrdinalIgnoreCase))
                {
                    return;
                }
                _selectedGeminiFreeModel = value;
                RaisePropertyChanged();
            }
        }

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

        private string _geminiPaidKeyStatus = string.Empty;
        public string GeminiPaidKeyStatus
        {
            get => _geminiPaidKeyStatus;
            private set
            {
                _geminiPaidKeyStatus = value;
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

        private string _modelsStatus = UiLocalization.Text("Runtime.ModelsBuiltIn");
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
            Logger.Info(
                $"AI Weather UI culture: {CultureInfo.CurrentUICulture.Name} " +
                $"({(UiLocalization.IsChineseCulture() ? "Chinese" : "English fallback")})");

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

            // The pre-split Gemini option was explicitly documented as the free API tier.
            // Preserve that meaning exactly once; the marker prevents a user-selected paid
            // provider from being changed again on future starts.
            var provider = Services.GeminiProviderProfile.MigrateLegacyProvider(
                Properties.Settings.Default.AnalysisProvider,
                Properties.Settings.Default.GeminiTierSplitMigrated);
            if (!Properties.Settings.Default.GeminiTierSplitMigrated)
            {
                Properties.Settings.Default.AnalysisProvider = provider;
                Properties.Settings.Default.GeminiTierSplitMigrated = true;
                changed = true;
            }

            // Keep legacy flag aligned for existing logic and older UI bindings.
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

            var normalizedGeminiRequestEveryChecks = Math.Clamp(
                Properties.Settings.Default.GeminiRequestEveryChecks, 1, 10000);
            if (Properties.Settings.Default.GeminiRequestEveryChecks != normalizedGeminiRequestEveryChecks)
            {
                Properties.Settings.Default.GeminiRequestEveryChecks = normalizedGeminiRequestEveryChecks;
                changed = true;
            }

            var normalizedGeminiFreeCycleCount = Math.Clamp(
                Properties.Settings.Default.GeminiFreeCycleCount, 1, 10);
            if (Properties.Settings.Default.GeminiFreeCycleCount != normalizedGeminiFreeCycleCount)
            {
                Properties.Settings.Default.GeminiFreeCycleCount = normalizedGeminiFreeCycleCount;
                changed = true;
            }

            var normalizedGeminiFreeModelOrder = Services.GeminiProviderProfile.SerializeFreeModelOrder(
                Services.GeminiProviderProfile.ParseFreeModelOrder(
                    Properties.Settings.Default.GeminiFreeModelOrder));
            if (!string.Equals(
                    Properties.Settings.Default.GeminiFreeModelOrder,
                    normalizedGeminiFreeModelOrder,
                    StringComparison.Ordinal))
            {
                Properties.Settings.Default.GeminiFreeModelOrder = normalizedGeminiFreeModelOrder;
                changed = true;
            }

            var normalizedGeminiPaidRequestEveryChecks = Math.Clamp(
                Properties.Settings.Default.GeminiPaidRequestEveryChecks, 1, 10000);
            if (Properties.Settings.Default.GeminiPaidRequestEveryChecks != normalizedGeminiPaidRequestEveryChecks)
            {
                Properties.Settings.Default.GeminiPaidRequestEveryChecks = normalizedGeminiPaidRequestEveryChecks;
                changed = true;
            }

            var normalizedSampleEveryChecks = Math.Clamp(
                Properties.Settings.Default.DatasetSampleEveryChecks, 1, 10000);
            if (Properties.Settings.Default.DatasetSampleEveryChecks != normalizedSampleEveryChecks)
            {
                Properties.Settings.Default.DatasetSampleEveryChecks = normalizedSampleEveryChecks;
                changed = true;
            }

            var configuredImageScale = Properties.Settings.Default.DatasetImageScalePercent;
            var normalizedImageScale = double.IsFinite(configuredImageScale)
                ? Math.Clamp(configuredImageScale, 5, 100)
                : 50;
            if (Math.Abs(configuredImageScale - normalizedImageScale) > 0.0001
                || !double.IsFinite(configuredImageScale))
            {
                Properties.Settings.Default.DatasetImageScalePercent = normalizedImageScale;
                changed = true;
            }

            var normalizedHammingLimit = Math.Clamp(
                Properties.Settings.Default.DatasetNearDuplicateHammingDistance, 0, 64);
            if (Properties.Settings.Default.DatasetNearDuplicateHammingDistance != normalizedHammingLimit)
            {
                Properties.Settings.Default.DatasetNearDuplicateHammingDistance = normalizedHammingLimit;
                changed = true;
            }

            var normalizedSunLimit = Services.SolarAltitudeGuard.NormalizeLimit(
                Properties.Settings.Default.SunAltitudeLimitDegrees);
            if (Math.Abs(Properties.Settings.Default.SunAltitudeLimitDegrees - normalizedSunLimit) > 0.0001)
            {
                Properties.Settings.Default.SunAltitudeLimitDegrees = normalizedSunLimit;
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
        public bool IsGeminiProvider => Services.GeminiProviderProfile.IsPaid(AnalysisProvider);
        public bool IsGeminiFreeProvider => Services.GeminiProviderProfile.IsFree(AnalysisProvider);
        public bool IsAnyGeminiProvider => Services.GeminiProviderProfile.IsGemini(AnalysisProvider);
        public bool IsAnthropicProvider => string.Equals(AnalysisProvider, "Anthropic", StringComparison.OrdinalIgnoreCase);
        public bool IsOllamaProvider => string.Equals(AnalysisProvider, "Ollama", StringComparison.OrdinalIgnoreCase);
        public bool IsLocalProvider => string.Equals(AnalysisProvider, "Local", StringComparison.OrdinalIgnoreCase);
        public bool IsNonLocalProvider => !IsLocalProvider;
        public bool IsModelSelectionVisible => IsNonLocalProvider && !IsGeminiFreeProvider;

        public async Task RefreshAvailableModelsAsync(bool forceRefresh = false)
        {
            var currentProvider = AnalysisProvider ?? "Local";

            // Local provider has no model dropdown.
            if (string.Equals(currentProvider, "Local", StringComparison.OrdinalIgnoreCase))
            {
                RunOnUiThread(() =>
                {
                    AvailableModels.Clear();
                    ModelsStatus = UiLocalization.Text("Runtime.ModelsLocal");
                });
                return;
            }

            if (Services.GeminiProviderProfile.IsFree(currentProvider))
            {
                RunOnUiThread(() =>
                {
                    ModelsStatus = UiLocalization.Text(
                        "Runtime.GeminiFreePoolReady",
                        GeminiFreeModels.Count,
                        GeminiFreeCycleCount);
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
                        ModelsStatus = UiLocalization.Text("Runtime.ModelsCached", cached.models.Length);
                        EnsureSelectedModelIsValid();
                    });
                    return;
                }

                // Clear cache for this provider when forcing.
                if (forceRefresh)
                {
                    _modelCache.Remove(currentProvider);
                }

                RunOnUiThread(() => { ModelsStatus = UiLocalization.Text("Runtime.ModelsFetching", currentProvider); });

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
                    ? UiLocalization.Text("Runtime.ModelsLoaded", finalModels.Count, currentProvider)
                    : UiLocalization.Text("Runtime.ModelsFallback", currentProvider, finalModels.Count);

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
                    ModelsStatus = UiLocalization.Text("Runtime.ModelsFailed", ex.Message);
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
            if (Services.GeminiProviderProfile.IsGemini(provider))
            {
                var key = Services.GeminiProviderProfile.IsPaid(provider)
                    ? Properties.Settings.Default.GeminiPaidKey
                    : Properties.Settings.Default.GeminiKey;
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
                        if (!Services.AnthropicAnalysisService.IsVisionModel(id)) continue;

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
            GitHubTokenStatus = UiLocalization.Text("Options.GitHubRetired");
            return Task.CompletedTask;
        }

        public async Task TryOpenAIKeyAsync()
        {
            try
            {
                var key = Properties.Settings.Default.OpenAIKey;
                if (string.IsNullOrWhiteSpace(key))
                {
                    OpenAIKeyStatus = UiLocalization.Text("Runtime.KeyEmpty");
                    return;
                }

                OpenAIKeyStatus = UiLocalization.Text("Runtime.KeyTesting");

                using var http = new HttpClient();
                http.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", key.Trim());
                http.DefaultRequestHeaders.UserAgent.ParseAdd("NINA-AIWeather/1.0");

                using var response = await http.GetAsync("https://api.openai.com/v1/models");
                var json = await response.Content.ReadAsStringAsync();

                if (!response.IsSuccessStatusCode)
                {
                    OpenAIKeyStatus = UiLocalization.Text("Runtime.KeyHttpFailed", (int)response.StatusCode, response.StatusCode);
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

                OpenAIKeyStatus = count > 0
                    ? UiLocalization.Text("Runtime.KeyOkModels", count)
                    : UiLocalization.Text("Runtime.KeyOk");

                // Invalidate cache so next refresh fetches fresh models.
                _modelCache.Remove("OpenAI");
                await RefreshAvailableModelsAsync();
            }
            catch (Exception ex)
            {
                OpenAIKeyStatus = UiLocalization.Text("Runtime.KeyFailed", ex.Message);
            }
        }

        private string _providerTestStatus = string.Empty;
        public string ProviderTestStatus
        {
            get => _providerTestStatus;
            set { _providerTestStatus = value; RaisePropertyChanged(); }
        }

        public async Task TestAnalysisAsync()
        {
            try
            {
                ProviderTestStatus = UiLocalization.Text("Options.TestAnalysisRunning");
                var service = Services.AnalysisServiceFactory.CreateFromSettings();
                if (!await service.InitializeAsync())
                {
                    ProviderTestStatus = UiLocalization.Text("Options.TestAnalysisInitFailed");
                    return;
                }
                using var sky = SyntheticTestSky();
                if (service is Services.IOnlineWeatherAnalysisService online)
                {
                    var attempt = await online.TryAnalyzeOnlineOnlyAsync(sky);
                    ProviderTestStatus = attempt.Success
                        ? UiLocalization.Text("Options.TestAnalysisSucceeded", attempt.Provenance.Provider, attempt.Provenance.Model)
                        : UiLocalization.Text("Options.TestAnalysisUnavailable",
                            attempt.Provenance.ProviderFailureCode is "free_pool_exhausted" or "free_pool_daily_quota"
                                ? UiLocalization.FreePoolFailureSummary(attempt.Provenance)
                                : UiLocalization.FailureCategory(attempt.Provenance));
                }
                else
                {
                    var result = await service.AnalyzeImageAsync(sky);
                    ProviderTestStatus = result.Provenance.IsFallback
                        ? UiLocalization.FallbackStatus(result.Provenance)
                        : UiLocalization.Text("Options.TestAnalysisSucceeded", result.Provenance.Provider, result.Provenance.Model);
                }
            }
            catch (Exception ex)
            {
                // Never expose raw transport errors containing credentials in the options.
                ProviderTestStatus = UiLocalization.Text("Options.TestAnalysisFailed", ex.GetType().Name);
            }
        }

        private static System.Drawing.Bitmap SyntheticTestSky()
        {
            const int size = 256;
            var bitmap = new System.Drawing.Bitmap(size, size);
            using (var g = System.Drawing.Graphics.FromImage(bitmap))
            {
                g.Clear(System.Drawing.Color.FromArgb(6, 8, 18));
                using var glow = new System.Drawing.Drawing2D.LinearGradientBrush(
                    new System.Drawing.Rectangle(0, 0, size, size),
                    System.Drawing.Color.FromArgb(6, 8, 18), System.Drawing.Color.FromArgb(18, 22, 40), 90f);
                g.FillRectangle(glow, 0, size / 2, size, size / 2);
                using var star = new System.Drawing.SolidBrush(System.Drawing.Color.FromArgb(230, 230, 240));
                foreach (var (x, y) in new[] { (40, 30), (120, 70), (200, 45), (80, 150), (170, 190), (220, 130), (30, 210) })
                {
                    g.FillEllipse(star, x, y, 3, 3);
                }
            }
            return bitmap;
        }

        public async Task TryGeminiKeyAsync()
        {
            var provider = AnalysisProvider;
            var isPaid = Services.GeminiProviderProfile.IsPaid(provider);

            void SetStatus(string value)
            {
                if (isPaid)
                {
                    GeminiPaidKeyStatus = value;
                }
                else
                {
                    GeminiKeyStatus = value;
                }
            }

            try
            {
                var key = isPaid
                    ? Properties.Settings.Default.GeminiPaidKey
                    : Properties.Settings.Default.GeminiKey;
                if (string.IsNullOrWhiteSpace(key))
                {
                    SetStatus(UiLocalization.Text("Runtime.KeyEmpty"));
                    return;
                }

                SetStatus(UiLocalization.Text("Runtime.KeyTesting"));

                using var http = new HttpClient();
                http.DefaultRequestHeaders.UserAgent.ParseAdd("NINA-AIWeather/1.0");

                var url = $"https://generativelanguage.googleapis.com/v1beta/models?key={Uri.EscapeDataString(key.Trim())}";
                using var response = await http.GetAsync(url);
                var json = await response.Content.ReadAsStringAsync();

                if (!response.IsSuccessStatusCode)
                {
                    SetStatus(UiLocalization.Text(
                        "Runtime.KeyHttpFailed",
                        (int)response.StatusCode,
                        response.StatusCode));
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

                SetStatus(count > 0
                    ? UiLocalization.Text("Runtime.KeyOkModels", count)
                    : UiLocalization.Text("Runtime.KeyOk"));

                // Invalidate cache so next refresh fetches fresh models.
                _modelCache.Remove(provider);
                await RefreshAvailableModelsAsync();
            }
            catch (Exception ex)
            {
                SetStatus(UiLocalization.Text("Runtime.KeyFailed", ex.Message));
            }
        }

        public async Task TryAnthropicKeyAsync()
        {
            try
            {
                var key = Properties.Settings.Default.AnthropicKey;
                if (string.IsNullOrWhiteSpace(key))
                {
                    AnthropicKeyStatus = UiLocalization.Text("Runtime.KeyEmpty");
                    return;
                }

                AnthropicKeyStatus = UiLocalization.Text("Runtime.KeyTesting");

                using var http = new HttpClient();
                http.DefaultRequestHeaders.UserAgent.ParseAdd("NINA-AIWeather/1.0");
                http.DefaultRequestHeaders.TryAddWithoutValidation("x-api-key", key.Trim());
                http.DefaultRequestHeaders.TryAddWithoutValidation("anthropic-version", "2023-06-01");

                // Prefer listing models if available.
                using var response = await http.GetAsync("https://api.anthropic.com/v1/models");
                var json = await response.Content.ReadAsStringAsync();

                if (response.IsSuccessStatusCode)
                {
                    AnthropicKeyStatus = UiLocalization.Text("Runtime.KeyOk");

                    // Invalidate cache so next refresh fetches fresh models.
                    _modelCache.Remove("Anthropic");
                    await RefreshAvailableModelsAsync();
                    return;
                }

                // Some accounts / versions may not expose /v1/models; fall back to a tiny /v1/messages call.
                if (response.StatusCode == System.Net.HttpStatusCode.NotFound)
                {
                    var model = string.IsNullOrWhiteSpace(Properties.Settings.Default.SelectedModel)
                        ? Services.AnthropicAnalysisService.DefaultModel
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
                        ? UiLocalization.Text("Runtime.KeyOk")
                        : UiLocalization.Text("Runtime.KeyHttpFailed", (int)postResp.StatusCode, postResp.StatusCode);

                    if (postResp.IsSuccessStatusCode)
                    {
                        _modelCache.Remove("Anthropic");
                        await RefreshAvailableModelsAsync();
                    }

                    return;
                }

                AnthropicKeyStatus = UiLocalization.Text("Runtime.KeyHttpFailed", (int)response.StatusCode, response.StatusCode);
            }
            catch (Exception ex)
            {
                AnthropicKeyStatus = UiLocalization.Text("Runtime.KeyFailed", ex.Message);
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
        /// Runs the Gemini online teacher once every N ordinary weather checks. Local
        /// analysis still runs on every check, so camera and safety freshness are unchanged.
        /// </summary>
        public int GeminiRequestEveryChecks
        {
            get => Properties.Settings.Default.GeminiRequestEveryChecks;
            set
            {
                Properties.Settings.Default.GeminiRequestEveryChecks = Math.Clamp(value, 1, 10000);
                CoreUtil.SaveSettings(Properties.Settings.Default);
                RaisePropertyChanged();
            }
        }

        public int GeminiFreeCycleCount
        {
            get => Properties.Settings.Default.GeminiFreeCycleCount;
            set
            {
                Properties.Settings.Default.GeminiFreeCycleCount = Math.Clamp(value, 1, 10);
                CoreUtil.SaveSettings(Properties.Settings.Default);
                RaisePropertyChanged();
            }
        }

        public void MoveSelectedGeminiFreeModel(int offset)
        {
            if (offset == 0 || string.IsNullOrWhiteSpace(SelectedGeminiFreeModel))
            {
                return;
            }

            var currentIndex = GeminiFreeModels.IndexOf(SelectedGeminiFreeModel);
            var targetIndex = currentIndex + offset;
            if (currentIndex < 0 || targetIndex < 0 || targetIndex >= GeminiFreeModels.Count)
            {
                return;
            }

            GeminiFreeModels.Move(currentIndex, targetIndex);
            PersistGeminiFreeModelOrder();
        }

        public void ResetGeminiFreeModelOrder()
        {
            GeminiFreeModels.Clear();
            foreach (var model in Services.GeminiProviderProfile.DefaultFreeModelOrder)
            {
                GeminiFreeModels.Add(model);
            }
            SelectedGeminiFreeModel = GeminiFreeModels.FirstOrDefault();
            PersistGeminiFreeModelOrder();
        }

        private void PersistGeminiFreeModelOrder()
        {
            Properties.Settings.Default.GeminiFreeModelOrder =
                Services.GeminiProviderProfile.SerializeFreeModelOrder(GeminiFreeModels);
            CoreUtil.SaveSettings(Properties.Settings.Default);
            RaisePropertyChanged(nameof(GeminiFreeModels));
        }

        /// <summary>
        /// Independent pacing for a billed Gemini project. The API endpoint is shared with
        /// the free tier, but credentials and operator policy are intentionally separate.
        /// </summary>
        public int GeminiPaidRequestEveryChecks
        {
            get => Properties.Settings.Default.GeminiPaidRequestEveryChecks;
            set
            {
                Properties.Settings.Default.GeminiPaidRequestEveryChecks = Math.Clamp(value, 1, 10000);
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
                RaisePropertyChanged(nameof(IsGeminiFreeProvider));
                RaisePropertyChanged(nameof(IsAnyGeminiProvider));
                RaisePropertyChanged(nameof(IsModelSelectionVisible));
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
                RaisePropertyChanged(nameof(IsGeminiFreeProvider));
                RaisePropertyChanged(nameof(IsAnyGeminiProvider));
                RaisePropertyChanged(nameof(IsModelSelectionVisible));
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
        /// 0 = all-sky fisheye, 1 = fixed camera looking at part of the sky. Decides which
        /// part of the image the prompt calls sky (see WeatherAnalysisPrompts).
        /// </summary>
        public int CameraView
        {
            get => Properties.Settings.Default.CameraView;
            set
            {
                Properties.Settings.Default.CameraView = value == 1 ? 1 : 0;
                CoreUtil.SaveSettings(Properties.Settings.Default);
                RaisePropertyChanged();
            }
        }

        /// <summary>
        /// 0 = light-polluted site (clouds lit from below look bright), 1 = dark site
        /// (clouds are darker than the sky; stars are the only evidence).
        /// </summary>
        public int SiteDarkness
        {
            get => Properties.Settings.Default.SiteDarkness;
            set
            {
                Properties.Settings.Default.SiteDarkness = value == 1 ? 1 : 0;
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

        public override async Task Teardown()
        {
            // Give the bounded dataset writer a short chance to atomically finish queued
            // labels before N.I.N.A. unloads the plugin.
            await Equipment.AIWeatherSafetyMonitor.Instance.ShutdownAsync();
            await base.Teardown();
        }

        public string GeminiPaidKey
        {
            get => Properties.Settings.Default.GeminiPaidKey;
            set
            {
                Properties.Settings.Default.GeminiPaidKey = value;
                CoreUtil.SaveSettings(Properties.Settings.Default);
                RaisePropertyChanged();
            }
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
