using System;
using System.Collections.Generic;
using System.Linq;

namespace AIWeather.Models
{
    /// <summary>
    /// Stable, machine-readable identity for the component that produced an analysis.
    /// Never infer this information from the user-facing Description string.
    /// </summary>
    public enum AnalysisOrigin
    {
        Unknown,
        Gemini,
        OpenAI,
        Anthropic,
        GitHubModels,
        Ollama,
        LocalHeuristic,
        LocalOnnx
    }

    /// <summary>
    /// Controlled failure categories used by logs, dataset labels and fallback decisions.
    /// The values are deliberately provider-neutral so datasets remain queryable after a
    /// provider or model is changed.
    /// </summary>
    public enum AnalysisFailureCategory
    {
        None,
        RateLimited,
        Timeout,
        Network,
        Authentication,
        ModelUnavailable,
        MalformedResponse,
        SchemaRejected,
        Cancelled,
        ServiceRetired,
        Unknown,
        QuotaExhausted,
        ScheduledLocal,
        ServiceUnavailable
    }

    /// <summary>
    /// One provider request inside a bounded online analysis. It intentionally contains
    /// no response body, credentials or image data, so it is safe to persist with dataset
    /// metadata and useful when a later timeout would otherwise hide an earlier HTTP 503.
    /// </summary>
    public sealed class AnalysisAttemptDiagnostic
    {
        public int Attempt { get; set; }
        public string Model { get; set; } = "Unknown";
        public int? HttpStatus { get; set; }
        public AnalysisFailureCategory FailureCategory { get; set; } = AnalysisFailureCategory.None;
        public long DurationMilliseconds { get; set; }
        public string Outcome { get; set; } = "unknown";

        public AnalysisAttemptDiagnostic Clone()
        {
            return new AnalysisAttemptDiagnostic
            {
                Attempt = Attempt,
                Model = Model,
                HttpStatus = HttpStatus,
                FailureCategory = FailureCategory,
                DurationMilliseconds = DurationMilliseconds,
                Outcome = Outcome
            };
        }
    }

    public sealed class AnalysisProvenance
    {
        public AnalysisOrigin Origin { get; set; } = AnalysisOrigin.Unknown;
        public string Provider { get; set; } = "Unknown";
        public string Model { get; set; } = "Unknown";
        public string PromptVersion { get; set; } = "none";
        public bool OnlineSucceeded { get; set; }
        public bool IsFallback { get; set; }
        public AnalysisFailureCategory FailureCategory { get; set; } = AnalysisFailureCategory.None;
        public int Attempts { get; set; }
        public int? HttpStatus { get; set; }
        public long LatencyMilliseconds { get; set; }
        public string? ProviderFailureCode { get; set; }
        public DateTime? RetryAfterUtc { get; set; }
        public string? QuotaMetric { get; set; }
        public string? QuotaId { get; set; }
        public int ConsecutiveQuotaFailures { get; set; }
        public bool RequestSuppressed { get; set; }
        public int RequestEveryChecks { get; set; }
        public long RequestSequence { get; set; }
        public IReadOnlyList<AnalysisAttemptDiagnostic> AttemptDiagnostics { get; set; } =
            Array.Empty<AnalysisAttemptDiagnostic>();

        public AnalysisProvenance Clone()
        {
            return new AnalysisProvenance
            {
                Origin = Origin,
                Provider = Provider,
                Model = Model,
                PromptVersion = PromptVersion,
                OnlineSucceeded = OnlineSucceeded,
                IsFallback = IsFallback,
                FailureCategory = FailureCategory,
                Attempts = Attempts,
                HttpStatus = HttpStatus,
                LatencyMilliseconds = LatencyMilliseconds,
                ProviderFailureCode = ProviderFailureCode,
                RetryAfterUtc = RetryAfterUtc,
                QuotaMetric = QuotaMetric,
                QuotaId = QuotaId,
                ConsecutiveQuotaFailures = ConsecutiveQuotaFailures,
                RequestSuppressed = RequestSuppressed,
                RequestEveryChecks = RequestEveryChecks,
                RequestSequence = RequestSequence,
                AttemptDiagnostics = AttemptDiagnostics?.Select(item => item.Clone()).ToArray()
                    ?? Array.Empty<AnalysisAttemptDiagnostic>()
            };
        }
    }

    /// <summary>
    /// Result of one online-only teacher call. A failed attempt intentionally has no
    /// WeatherAnalysisResult; the orchestration layer, not the provider, chooses fallback.
    /// </summary>
    public sealed class OnlineAnalysisAttempt
    {
        public bool Success { get; init; }
        public WeatherAnalysisResult? Result { get; init; }
        public AnalysisProvenance Provenance { get; init; } = new AnalysisProvenance();
        public string? FailureMessage { get; init; }

        public static OnlineAnalysisAttempt Succeeded(WeatherAnalysisResult result)
        {
            return new OnlineAnalysisAttempt
            {
                Success = true,
                Result = result,
                Provenance = result.Provenance.Clone()
            };
        }

        public static OnlineAnalysisAttempt Failed(
            AnalysisProvenance provenance,
            string? failureMessage = null)
        {
            return new OnlineAnalysisAttempt
            {
                Success = false,
                Provenance = provenance,
                FailureMessage = failureMessage
            };
        }
    }

    /// <summary>
    /// All analyses of one captured frame. EffectiveResult is the only member allowed to
    /// feed the existing safety decision; Teacher and Student remain available for shadow
    /// comparison and dataset recording.
    /// </summary>
    public sealed class WeatherAnalysisBundle
    {
        public WeatherAnalysisResult EffectiveResult { get; init; } = new WeatherAnalysisResult();
        public OnlineAnalysisAttempt? Teacher { get; init; }
        public WeatherAnalysisResult Student { get; init; } = new WeatherAnalysisResult();
        public bool UsedFallback { get; init; }
        public double? TeacherStudentCloudDifference =>
            Teacher?.Success == true && Teacher.Result != null && !Teacher.Result.IsSkyUnreadable
                ? Math.Abs(Teacher.Result.CloudCoverage - Student.CloudCoverage)
                : null;
    }
}
