using AIWeather.Models;
using NINA.Core.Utility;
using System;
using System.Collections.Generic;
using System.Drawing;
using System.Drawing.Imaging;
using System.Diagnostics;
using System.IO;
using System.Net;
using System.Net.Http;
using System.Text;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using System.Linq;

namespace AIWeather.Services
{
    [System.Runtime.Versioning.SupportedOSPlatform("windows")]
    public class GeminiAnalysisService : IOnlineWeatherAnalysisService
    {
        private static readonly TimeSpan DefaultRequestTimeout = TimeSpan.FromSeconds(60);

        private readonly string _apiKey;
        private readonly string _primaryModelName;
        private readonly IHttpClientProvider _httpProvider;
        private readonly Func<string, GeminiQuotaCircuitBreaker> _quotaCircuitForModel;
        private readonly Func<DateTimeOffset> _utcNow;
        private readonly int _requestEveryChecks;
        private readonly GeminiServiceTier _serviceTier;
        private readonly TimeSpan _requestTimeout;
        private long _requestSequence;
        private bool _isInitialized;
        private GeminiRequestProfile _requestProfile;
        public GeminiRequestProfile CurrentProfile => _requestProfile;

        public GeminiAnalysisService(
            string apiKey,
            string modelName,
            int requestEveryChecks = 1,
            GeminiServiceTier serviceTier = GeminiServiceTier.Paid)
            : this(
                apiKey,
                NormalizeModelName(modelName),
                new SystemProxyAwareHttpClientProvider(),
                candidate => GeminiQuotaCircuitRegistry.Get(apiKey, candidate),
                () => DateTimeOffset.UtcNow,
                requestEveryChecks,
                serviceTier,
                requestTimeout: DefaultRequestTimeout)
        {
        }

        internal GeminiAnalysisService(
            string apiKey,
            string modelName,
            HttpClient http,
            GeminiQuotaCircuitBreaker quotaCircuit,
            Func<DateTimeOffset> utcNow,
            int requestEveryChecks = 1,
            GeminiServiceTier serviceTier = GeminiServiceTier.Paid,
            TimeSpan? requestTimeout = null)
            : this(
                apiKey,
                modelName,
                new FixedHttpClientProvider(http),
                _ => quotaCircuit,
                utcNow,
                requestEveryChecks,
                serviceTier,
                requestTimeout ?? DefaultRequestTimeout)
        {
        }

        internal GeminiAnalysisService(
            string apiKey,
            string modelName,
            HttpClient http,
            Func<string, GeminiQuotaCircuitBreaker> quotaCircuitForModel,
            Func<DateTimeOffset> utcNow,
            int requestEveryChecks = 1,
            GeminiServiceTier serviceTier = GeminiServiceTier.Paid,
            TimeSpan? requestTimeout = null)
            : this(
                apiKey,
                modelName,
                new FixedHttpClientProvider(http),
                quotaCircuitForModel,
                utcNow,
                requestEveryChecks,
                serviceTier,
                requestTimeout ?? DefaultRequestTimeout)
        {
        }

        internal GeminiAnalysisService(
            string apiKey,
            string modelName,
            IHttpClientProvider httpProvider,
            Func<string, GeminiQuotaCircuitBreaker> quotaCircuitForModel,
            Func<DateTimeOffset> utcNow,
            int requestEveryChecks = 1,
            GeminiServiceTier serviceTier = GeminiServiceTier.Paid,
            TimeSpan? requestTimeout = null)
        {
            _apiKey = apiKey;
            // The alias tracks Google's latest stable Flash release; concrete version IDs
            // can be retired while they remain saved as the operator-selected model.
            _primaryModelName = NormalizeModelName(modelName);
            _requestProfile = GeminiRequestPolicy.StartingProfileFor(_primaryModelName);
            _httpProvider = httpProvider ?? throw new ArgumentNullException(nameof(httpProvider));
            _quotaCircuitForModel = quotaCircuitForModel ?? throw new ArgumentNullException(nameof(quotaCircuitForModel));
            _utcNow = utcNow ?? throw new ArgumentNullException(nameof(utcNow));
            _requestEveryChecks = Math.Clamp(requestEveryChecks, 1, 10000);
            _serviceTier = serviceTier;
            _requestTimeout = requestTimeout.GetValueOrDefault(DefaultRequestTimeout);
            if (_requestTimeout <= TimeSpan.Zero)
            {
                _requestTimeout = DefaultRequestTimeout;
            }
        }

        private static string NormalizeModelName(string modelName)
        {
            return string.IsNullOrWhiteSpace(modelName) ? "gemini-flash-latest" : modelName.Trim();
        }

        internal string ProviderName => GeminiProviderProfile.DisplayName(_serviceTier);
        private bool UsesQuotaCircuit => _serviceTier == GeminiServiceTier.Free;

        public Task<bool> InitializeAsync(CancellationToken cancellationToken = default)
        {
            if (string.IsNullOrWhiteSpace(_apiKey))
            {
                Logger.Warning("Gemini API key not configured");
                _isInitialized = false;
                return Task.FromResult(false);
            }

            _isInitialized = true;
            Logger.Info(
                $"{ProviderName} analysis service initialized with model {_primaryModelName}; " +
                $"one request, timeout {_requestTimeout.TotalSeconds:F0}s, " +
                "retry/backoff and cross-model failover disabled");
            return Task.FromResult(true);
        }

        public async Task<WeatherAnalysisResult> AnalyzeImageAsync(Bitmap image, AstroContext? astroContext = null, CancellationToken cancellationToken = default)
        {
            var attempt = await TryAnalyzeOnlineOnlyAsync(image, astroContext, cancellationToken);
            if (attempt.Success && attempt.Result != null)
            {
                return attempt.Result;
            }

            // Compatibility for callers that only know IWeatherAnalysisService. This is an
            // explicit failed online result, never a disguised local fallback. The safety
            // monitor uses TryAnalyzeOnlineOnlyAsync and owns the fallback decision.
            return new WeatherAnalysisResult
            {
                Timestamp = DateTime.UtcNow,
                Condition = WeatherCondition.Unknown,
                CloudCoverage = 50,
                Confidence = 0,
                IsSafeForImaging = false,
                Description = $"Gemini analysis failed: {attempt.Provenance.FailureCategory}",
                Provenance = attempt.Provenance.Clone()
            };
        }

        public async Task<OnlineAnalysisAttempt> TryAnalyzeOnlineOnlyAsync(
            Bitmap image,
            AstroContext? astroContext = null,
            CancellationToken cancellationToken = default)
        {
            var stopwatch = Stopwatch.StartNew();
            var attemptsUsed = 0;
            var diagnostics = new List<AnalysisAttemptDiagnostic>();
            var currentModel = _primaryModelName;

            if (!_isInitialized)
            {
                Logger.Warning("Gemini service is not initialized; returning an online failure for explicit orchestration");
                return OnlineAnalysisAttempt.Failed(
                    AnalysisMetadata.FailedOnline(
                        AnalysisOrigin.Gemini,
                        ProviderName,
                        _primaryModelName,
                        AnalysisFailureCategory.Authentication,
                        0,
                        stopwatch.ElapsedMilliseconds),
                    "Gemini API key is missing or the service was not initialized");
            }

            var quotaProbeDue = false;
            if (UsesQuotaCircuit)
            {
                var startingCircuit = _quotaCircuitForModel(currentModel);
                if (startingCircuit.TryGetActive(_utcNow(), out var activeQuota))
                {
                    return BuildQuotaFailure(
                        currentModel,
                        activeQuota,
                        attempts: 0,
                        stopwatch.ElapsedMilliseconds,
                        httpStatus: null,
                        requestSuppressed: true,
                        diagnostics: diagnostics);
                }

                // Once a quota window expires, probe immediately even if this ordinary
                // check would otherwise fall between paced online calls.
                quotaProbeDue = activeQuota.ConsecutiveFailures > 0;
            }

            var requestSequence = Interlocked.Increment(ref _requestSequence);
            if (!quotaProbeDue
                && _requestEveryChecks > 1
                && (requestSequence - 1) % _requestEveryChecks != 0)
            {
                Logger.Debug(
                    $"Gemini online request intentionally skipped by pacing policy " +
                    $"(check {requestSequence}, every {_requestEveryChecks} checks); " +
                    "local analysis remains active");
                return BuildScheduledLocal(requestSequence, stopwatch.ElapsedMilliseconds);
            }

            if (quotaProbeDue)
            {
                Logger.Info(
                    $"Gemini quota pause expired; forcing one online probe now " +
                    $"(scheduled check {requestSequence}, every {_requestEveryChecks} checks)");
            }

            try
            {
                Logger.Info($"Starting {ProviderName} AI weather analysis with selected model {currentModel}");

                var base64Image = ConvertImageToBase64(image);

                var promptText = PromptText.FullPrompt;
                var promptPrefix = WeatherAnalysisPrompts.BuildPromptPrefix(astroContext);
                if (promptPrefix.Length > 0)
                    promptText = promptPrefix + "\n" + promptText;

                var serializedPayload = GeminiRequestPolicy.BuildRequestBody(
                    promptText, base64Image, _requestProfile, GeminiRequestPolicy.DefaultMaxOutputTokens);

                // Every service instance performs exactly one request. Gemini Free creates
                // one instance per pool entry and owns model ordering/cycles outside this
                // class; ordinary Gemini never retries or switches models here.
                using var timeoutCts = new CancellationTokenSource(_requestTimeout);
                using var linkedCts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken, timeoutCts.Token);

                const int attempt = 1;
                attemptsUsed = attempt;
                var attemptStopwatch = Stopwatch.StartNew();
                try
                {
                        var url = $"https://generativelanguage.googleapis.com/v1beta/models/{Uri.EscapeDataString(currentModel)}:generateContent";
                        using var request = new HttpRequestMessage(HttpMethod.Post, url);
                        request.Headers.TryAddWithoutValidation("x-goog-api-key", _apiKey);
                        request.Headers.UserAgent.ParseAdd("NINA-AIWeather/1.0");
                        request.Content = new StringContent(serializedPayload, Encoding.UTF8, "application/json");

                        Logger.Info($"Calling {ProviderName} API model {currentModel} (single request)...");

                        // Resolve the transport immediately before each real API request.
                        // The provider preserves the normal connection pool, but swaps it
                        // when v2rayN/Windows proxy settings changed while N.I.N.A. stayed
                        // open.  Re-enabling the proxy therefore takes effect on this check
                        // without restarting N.I.N.A.
                        var http = _httpProvider.GetClient();
                        using var response = await http.SendAsync(request, linkedCts.Token);
                        var json = await response.Content.ReadAsStringAsync(linkedCts.Token);

                        if (!response.IsSuccessStatusCode)
                        {
                            if (GeminiRequestPolicy.IsRequestRejected((int)response.StatusCode)
                                && GeminiRequestPolicy.NextProfile(_requestProfile) is { } nextProfile)
                            {
                                Logger.Warning($"Gemini model {currentModel} rejected request profile {_requestProfile}; using {nextProfile} on its next visit");
                                _requestProfile = nextProfile;
                            }
                            var serverRetryDelay = GetServerRetryDelay(response, json, _utcNow());
                            var quota = GeminiQuotaParser.Parse(
                                response.StatusCode,
                                json,
                                serverRetryDelay);
                            if (quota.IsQuotaFailure)
                            {
                                diagnostics.Add(CreateDiagnostic(
                                    attempt,
                                    currentModel,
                                    response.StatusCode,
                                    AnalysisFailureCategory.QuotaExhausted,
                                    attemptStopwatch.ElapsedMilliseconds,
                                    "quota_rejected"));
                                if (!UsesQuotaCircuit)
                                {
                                    Logger.Warning(
                                        $"Gemini selected model {currentModel} rejected the request for quota; " +
                                        "strict billed-project policy will not retry, rotate models or open a backoff circuit");
                                    return BuildImmediateQuotaFailure(
                                        currentModel,
                                        quota,
                                        attempt,
                                        stopwatch.ElapsedMilliseconds,
                                        (int)response.StatusCode,
                                        diagnostics);
                                }

                                var circuit = _quotaCircuitForModel(currentModel).RecordFailure(_utcNow(), quota);
                                Logger.Warning(
                                    $"Gemini API quota unavailable: HTTP {(int)response.StatusCode}; " +
                                    $"immediate retries suppressed, next online attempt no earlier than " +
                                    $"{circuit.RetryAfterUtc:O}; consecutive quota failures " +
                                    $"{circuit.ConsecutiveFailures}; metric " +
                                    $"{circuit.QuotaMetric ?? "unknown"}; quota id " +
                                    $"{circuit.QuotaId ?? "unknown"}; daily quota " +
                                    $"{circuit.IsDailyQuota}.");
                                return BuildQuotaFailure(
                                    currentModel,
                                    circuit,
                                    attempt,
                                    stopwatch.ElapsedMilliseconds,
                                    (int)response.StatusCode,
                                    requestSuppressed: false,
                                    diagnostics: diagnostics);
                            }

                            if (UsesQuotaCircuit && _quotaCircuitForModel(currentModel).Reset())
                            {
                                Logger.Info($"Gemini quota circuit reset for {currentModel} after a non-quota API response");
                            }

                            var isTransient = IsTransientStatus(response.StatusCode);
                            diagnostics.Add(CreateDiagnostic(
                                attempt,
                                currentModel,
                                response.StatusCode,
                                AnalysisMetadata.FromHttpStatus(response.StatusCode),
                                attemptStopwatch.ElapsedMilliseconds,
                                "http_error"));

                            Logger.Error(
                                $"Gemini API error after {attempt} attempt(s): HTTP {(int)response.StatusCode}: " +
                                TruncateForLog(json));
                            throw new GeminiApiException(response.StatusCode, json, attempt, isTransient);
                        }

                        diagnostics.Add(CreateDiagnostic(
                            attempt,
                            currentModel,
                            response.StatusCode,
                            AnalysisFailureCategory.None,
                            attemptStopwatch.ElapsedMilliseconds,
                            "success"));

                        if (UsesQuotaCircuit && _quotaCircuitForModel(currentModel).Reset())
                        {
                            Logger.Info($"Gemini quota circuit closed for {currentModel} after a successful API response");
                        }

                        Logger.Info("Gemini API responded, parsing response...");

                        using var doc = JsonDocument.Parse(json);
                        var text = ExtractGeminiText(doc.RootElement);

                        if (GeminiRequestPolicy.WasCutShort(doc.RootElement))
                        {
                            throw new WeatherResponseParseException("Gemini output was truncated (MAX_TOKENS)", string.Empty);
                        }
                        var result = WeatherResponseParser.Parse(text);
                        if (!WeatherAnalysisValidator.IsValidTeacherResult(result, out var validationReason))
                        {
                            Logger.Warning($"Gemini returned a response rejected by the weather schema: {validationReason}");
                            diagnostics[^1].FailureCategory = AnalysisFailureCategory.SchemaRejected;
                            diagnostics[^1].Outcome = "invalid_weather_response";
                            return OnlineAnalysisAttempt.Failed(
                                AnalysisMetadata.FailedOnline(
                                    AnalysisOrigin.Gemini,
                                    ProviderName,
                                    currentModel,
                                    AnalysisFailureCategory.SchemaRejected,
                                    attempt,
                                    stopwatch.ElapsedMilliseconds,
                                    (int)response.StatusCode,
                                    attemptDiagnostics: diagnostics),
                                validationReason);
                        }

                        result.Provenance = AnalysisMetadata.Online(
                            AnalysisOrigin.Gemini,
                            ProviderName,
                            currentModel,
                            attempt,
                            stopwatch.ElapsedMilliseconds,
                            (int)response.StatusCode,
                            diagnostics);
                        Logger.Info($"Gemini analysis complete: {result.Condition}, Cloud Coverage: {result.CloudCoverage:F1}%");
                        return OnlineAnalysisAttempt.Succeeded(result);
                }
                catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
                {
                        diagnostics.Add(CreateDiagnostic(
                            attempt,
                            currentModel,
                            null,
                            AnalysisFailureCategory.Timeout,
                            attemptStopwatch.ElapsedMilliseconds,
                            "timeout"));
                        Logger.Warning(
                            $"Gemini model {currentModel} exhausted the bounded online-analysis budget; " +
                            "retaining earlier HTTP evidence in provenance");
                        return BuildDiagnosticFailure(
                            diagnostics,
                            currentModel,
                            attemptsUsed,
                            stopwatch.ElapsedMilliseconds,
                            "Gemini request budget exhausted");
                }
                catch (HttpRequestException ex)
                {
                        diagnostics.Add(CreateDiagnostic(
                            attempt,
                            currentModel,
                            null,
                            AnalysisFailureCategory.Network,
                            attemptStopwatch.ElapsedMilliseconds,
                            "network_error"));
                    throw new GeminiApiException(null, ex.Message, attempt, true, ex);
                }
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
                // The caller stopped monitoring. Not a provider failure and not a reading:
                // let it propagate, nothing gets recorded.
                throw;
            }
            catch (OperationCanceledException ex)
            {
                Logger.Warning($"Gemini API call timed out: {ex.Message}");
                if (diagnostics.Count == 0)
                {
                    diagnostics.Add(CreateDiagnostic(
                        Math.Max(1, attemptsUsed),
                        currentModel,
                        null,
                        AnalysisFailureCategory.Timeout,
                        stopwatch.ElapsedMilliseconds,
                        "timeout"));
                }
                return BuildDiagnosticFailure(
                    diagnostics,
                    currentModel,
                    attemptsUsed,
                    stopwatch.ElapsedMilliseconds,
                    "Gemini request timed out");
            }
            catch (GeminiApiException ex)
            {
                var status = ex.StatusCode.HasValue ? $"HTTP {(int)ex.StatusCode.Value}" : "network error";
                var reason = ex.IsTransient
                    ? $"Gemini temporarily unavailable after the single request ({status})"
                    : $"Gemini request rejected ({status})";

                Logger.Error($"{reason}: {ex.Message}");
                return BuildDiagnosticFailure(
                    diagnostics,
                    currentModel,
                    ex.Attempts,
                    stopwatch.ElapsedMilliseconds,
                    reason,
                    AnalysisMetadata.FromHttpStatus(ex.StatusCode),
                    ex.StatusCode.HasValue ? (int)ex.StatusCode.Value : null);
            }
            catch (WeatherResponseParseException ex)
            {
                if (diagnostics.Count > 0)
                {
                    diagnostics[^1].FailureCategory = AnalysisFailureCategory.MalformedResponse;
                    diagnostics[^1].Outcome = "invalid_weather_response";
                }
                return BuildDiagnosticFailure(diagnostics, currentModel, attemptsUsed,
                    stopwatch.ElapsedMilliseconds, ex.Message,
                    AnalysisFailureCategory.MalformedResponse, 200);
            }
            catch (JsonException ex)
            {
                Logger.Error($"Gemini returned malformed envelope JSON: {ex.Message}");
                if (diagnostics.Count > 0)
                {
                    diagnostics[^1].FailureCategory = AnalysisFailureCategory.MalformedResponse;
                    diagnostics[^1].Outcome = "invalid_envelope";
                }
                return OnlineAnalysisAttempt.Failed(
                    AnalysisMetadata.FailedOnline(
                        AnalysisOrigin.Gemini,
                        ProviderName,
                        currentModel,
                        AnalysisFailureCategory.MalformedResponse,
                        attemptsUsed,
                        stopwatch.ElapsedMilliseconds,
                        200,
                        attemptDiagnostics: diagnostics),
                    "Gemini response envelope was malformed");
            }
            catch (Exception ex)
            {
                Logger.Error($"Error in Gemini online analysis: {ex.Message}", ex);
                return OnlineAnalysisAttempt.Failed(
                    AnalysisMetadata.FailedOnline(
                        AnalysisOrigin.Gemini,
                        ProviderName,
                        currentModel,
                        ex is HttpRequestException
                            ? AnalysisFailureCategory.Network
                            : AnalysisFailureCategory.Unknown,
                        attemptsUsed,
                        stopwatch.ElapsedMilliseconds,
                        attemptDiagnostics: diagnostics),
                    ex.GetType().Name);
            }
        }

        private static AnalysisAttemptDiagnostic CreateDiagnostic(
            int attempt,
            string model,
            HttpStatusCode? statusCode,
            AnalysisFailureCategory category,
            long durationMilliseconds,
            string outcome)
        {
            return new AnalysisAttemptDiagnostic
            {
                Attempt = Math.Max(1, attempt),
                Model = model,
                HttpStatus = statusCode.HasValue ? (int)statusCode.Value : null,
                FailureCategory = category,
                DurationMilliseconds = Math.Max(0, durationMilliseconds),
                Outcome = outcome
            };
        }

        private OnlineAnalysisAttempt BuildDiagnosticFailure(
            IReadOnlyList<AnalysisAttemptDiagnostic> diagnostics,
            string currentModel,
            int attempts,
            long latencyMilliseconds,
            string reason,
            AnalysisFailureCategory fallbackCategory = AnalysisFailureCategory.Unknown,
            int? fallbackHttpStatus = null)
        {
            // A concrete provider response is more informative than a generic transport
            // failure when constructing the final provenance.
            var strongest = diagnostics.LastOrDefault(item => item.HttpStatus == 503)
                ?? diagnostics.LastOrDefault();
            var category = strongest?.FailureCategory ?? fallbackCategory;
            if (category == AnalysisFailureCategory.None)
            {
                category = fallbackCategory;
            }
            var httpStatus = strongest?.HttpStatus ?? fallbackHttpStatus;
            var model = strongest?.Model ?? currentModel;
            var providerFailureCode = category switch
            {
                AnalysisFailureCategory.ServiceUnavailable => "service_unavailable",
                AnalysisFailureCategory.Timeout => "timeout",
                AnalysisFailureCategory.Network => "network_error",
                AnalysisFailureCategory.ModelUnavailable => "model_unavailable",
                _ => null
            };

            return OnlineAnalysisAttempt.Failed(
                AnalysisMetadata.FailedOnline(
                    AnalysisOrigin.Gemini,
                    ProviderName,
                    model,
                    category,
                    Math.Max(attempts, diagnostics.Count),
                    latencyMilliseconds,
                    httpStatus,
                    providerFailureCode: providerFailureCode,
                    attemptDiagnostics: diagnostics),
                reason);
        }

        private static bool IsTransientStatus(HttpStatusCode statusCode)
        {
            var code = (int)statusCode;
            return code == 408 || code == 429 || code == 500 || code == 502 || code == 503 || code == 504;
        }

        private OnlineAnalysisAttempt BuildQuotaFailure(
            string modelName,
            GeminiQuotaCircuitState circuit,
            int attempts,
            long latencyMilliseconds,
            int? httpStatus,
            bool requestSuppressed,
            IReadOnlyList<AnalysisAttemptDiagnostic>? diagnostics = null)
        {
            var retryAfterUtc = circuit.RetryAfterUtc.UtcDateTime;
            var action = requestSuppressed ? "API request skipped" : "API request rejected";
            var reason =
                $"Gemini quota unavailable; {action}; next online attempt after " +
                $"{retryAfterUtc:O}";

            if (requestSuppressed)
            {
                Logger.Info(
                    $"Gemini quota circuit active; API request skipped until " +
                    $"{circuit.RetryAfterUtc:O} (consecutive quota failures " +
                    $"{circuit.ConsecutiveFailures})");
            }

            return OnlineAnalysisAttempt.Failed(
                AnalysisMetadata.FailedOnline(
                    AnalysisOrigin.Gemini,
                    ProviderName,
                    modelName,
                    AnalysisFailureCategory.QuotaExhausted,
                    attempts,
                    latencyMilliseconds,
                    httpStatus,
                    providerFailureCode: circuit.ProviderFailureCode,
                    retryAfterUtc: retryAfterUtc,
                    quotaMetric: circuit.QuotaMetric,
                    quotaId: circuit.QuotaId,
                    consecutiveQuotaFailures: circuit.ConsecutiveFailures,
                    requestSuppressed: requestSuppressed,
                    attemptDiagnostics: diagnostics),
                reason);
        }

        private OnlineAnalysisAttempt BuildImmediateQuotaFailure(
            string modelName,
            GeminiQuotaInfo quota,
            int attempts,
            long latencyMilliseconds,
            int? httpStatus,
            IReadOnlyList<AnalysisAttemptDiagnostic>? diagnostics = null)
        {
            return OnlineAnalysisAttempt.Failed(
                AnalysisMetadata.FailedOnline(
                    AnalysisOrigin.Gemini,
                    ProviderName,
                    modelName,
                    AnalysisFailureCategory.QuotaExhausted,
                    attempts,
                    latencyMilliseconds,
                    httpStatus,
                    providerFailureCode: quota.ProviderFailureCode,
                    quotaMetric: quota.QuotaMetric,
                    quotaId: quota.QuotaId,
                    requestSuppressed: false,
                    attemptDiagnostics: diagnostics),
                "Gemini selected model rejected the request for quota; strict policy did not retry or switch models");
        }

        private OnlineAnalysisAttempt BuildScheduledLocal(
            long requestSequence,
            long latencyMilliseconds)
        {
            return OnlineAnalysisAttempt.Failed(
                AnalysisMetadata.FailedOnline(
                    AnalysisOrigin.Gemini,
                    ProviderName,
                    _primaryModelName,
                    AnalysisFailureCategory.ScheduledLocal,
                    attempts: 0,
                    latencyMilliseconds,
                    providerFailureCode: "scheduled_local",
                    requestSuppressed: true,
                    requestEveryChecks: _requestEveryChecks,
                    requestSequence: requestSequence),
                $"Gemini pacing policy uses local analysis on this check; " +
                $"online request runs every {_requestEveryChecks} checks");
        }

        private static TimeSpan? GetServerRetryDelay(
            HttpResponseMessage? response,
            string? responseBody,
            DateTimeOffset nowUtc)
        {
            var retryAfter = response?.Headers.RetryAfter;
            if (retryAfter?.Delta is TimeSpan delta && delta > TimeSpan.Zero)
            {
                return delta;
            }

            if (retryAfter?.Date is DateTimeOffset retryDate)
            {
                var requestedDelay = retryDate - nowUtc;
                if (requestedDelay > TimeSpan.Zero)
                {
                    return requestedDelay;
                }
            }

            var parsed = GeminiQuotaParser.Parse(
                response?.StatusCode ?? (HttpStatusCode)0,
                responseBody);
            return parsed.RetryDelay;
        }

        private static string TruncateForLog(string value, int maxLength = 1000)
        {
            if (string.IsNullOrEmpty(value) || value.Length <= maxLength)
            {
                return value;
            }

            return value.Substring(0, maxLength) + "...";
        }

        private sealed class GeminiApiException : Exception
        {
            public GeminiApiException(
                HttpStatusCode? statusCode,
                string responseBody,
                int attempts,
                bool isTransient,
                Exception? innerException = null)
                : base(TruncateForLog(responseBody), innerException)
            {
                StatusCode = statusCode;
                Attempts = attempts;
                IsTransient = isTransient;
            }

            public HttpStatusCode? StatusCode { get; }
            public int Attempts { get; }
            public bool IsTransient { get; }
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


        }
    }
}
