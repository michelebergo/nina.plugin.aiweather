using AIWeather.Models;
using System;
using System.Net;
using System.Collections.Generic;
using System.Linq;

namespace AIWeather.Services
{
    internal static class AnalysisMetadata
    {
        public const string LocalHeuristicModelVersion = "local-heuristic-v1";
        public const string LocalOnnxModelVersion = "aiweather-mobilenetv3-test-v1";

        public static AnalysisProvenance Local(
            long latencyMilliseconds,
            bool isFallback = false,
            AnalysisFailureCategory upstreamFailure = AnalysisFailureCategory.None)
        {
            return new AnalysisProvenance
            {
                Origin = AnalysisOrigin.LocalHeuristic,
                Provider = "Local",
                Model = LocalHeuristicModelVersion,
                PromptVersion = "none",
                OnlineSucceeded = false,
                IsFallback = isFallback,
                FailureCategory = upstreamFailure,
                Attempts = 1,
                LatencyMilliseconds = Math.Max(0, latencyMilliseconds)
            };
        }

        public static AnalysisProvenance LocalOnnx(
            long latencyMilliseconds,
            bool isFallback = false,
            AnalysisFailureCategory upstreamFailure = AnalysisFailureCategory.None)
        {
            return new AnalysisProvenance
            {
                Origin = AnalysisOrigin.LocalOnnx,
                Provider = "Local",
                Model = LocalOnnxModelVersion,
                PromptVersion = "none",
                OnlineSucceeded = false,
                IsFallback = isFallback,
                FailureCategory = upstreamFailure,
                Attempts = 1,
                LatencyMilliseconds = Math.Max(0, latencyMilliseconds)
            };
        }

        public static AnalysisProvenance Online(
            AnalysisOrigin origin,
            string provider,
            string model,
            int attempts,
            long latencyMilliseconds,
            int? httpStatus = null,
            IReadOnlyList<AnalysisAttemptDiagnostic>? attemptDiagnostics = null)
        {
            return new AnalysisProvenance
            {
                Origin = origin,
                Provider = provider,
                Model = model,
                PromptVersion = WeatherAnalysisPrompts.CurrentPromptVersion,
                OnlineSucceeded = true,
                IsFallback = false,
                FailureCategory = AnalysisFailureCategory.None,
                Attempts = Math.Max(1, attempts),
                HttpStatus = httpStatus,
                LatencyMilliseconds = Math.Max(0, latencyMilliseconds),
                AttemptDiagnostics = attemptDiagnostics?.Select(item => item.Clone()).ToArray()
                    ?? Array.Empty<AnalysisAttemptDiagnostic>()
            };
        }

        public static AnalysisProvenance FailedOnline(
            AnalysisOrigin origin,
            string provider,
            string model,
            AnalysisFailureCategory category,
            int attempts,
            long latencyMilliseconds,
            int? httpStatus = null,
            string? providerFailureCode = null,
            DateTime? retryAfterUtc = null,
            string? quotaMetric = null,
            string? quotaId = null,
            int consecutiveQuotaFailures = 0,
            bool requestSuppressed = false,
            int requestEveryChecks = 0,
            long requestSequence = 0,
            IReadOnlyList<AnalysisAttemptDiagnostic>? attemptDiagnostics = null)
        {
            return new AnalysisProvenance
            {
                Origin = origin,
                Provider = provider,
                Model = model,
                PromptVersion = WeatherAnalysisPrompts.CurrentPromptVersion,
                OnlineSucceeded = false,
                IsFallback = false,
                FailureCategory = category,
                Attempts = Math.Max(0, attempts),
                HttpStatus = httpStatus,
                LatencyMilliseconds = Math.Max(0, latencyMilliseconds),
                ProviderFailureCode = providerFailureCode,
                RetryAfterUtc = retryAfterUtc,
                QuotaMetric = quotaMetric,
                QuotaId = quotaId,
                ConsecutiveQuotaFailures = Math.Max(0, consecutiveQuotaFailures),
                RequestSuppressed = requestSuppressed,
                RequestEveryChecks = Math.Max(0, requestEveryChecks),
                RequestSequence = Math.Max(0, requestSequence),
                AttemptDiagnostics = attemptDiagnostics?.Select(item => item.Clone()).ToArray()
                    ?? Array.Empty<AnalysisAttemptDiagnostic>()
            };
        }

        public static AnalysisFailureCategory FromHttpStatus(HttpStatusCode? statusCode)
        {
            return statusCode switch
            {
                HttpStatusCode.Unauthorized or HttpStatusCode.Forbidden => AnalysisFailureCategory.Authentication,
                HttpStatusCode.NotFound => AnalysisFailureCategory.ModelUnavailable,
                HttpStatusCode.ServiceUnavailable => AnalysisFailureCategory.ServiceUnavailable,
                HttpStatusCode.RequestTimeout or HttpStatusCode.GatewayTimeout => AnalysisFailureCategory.Timeout,
                (HttpStatusCode)429 => AnalysisFailureCategory.RateLimited,
                null => AnalysisFailureCategory.Network,
                _ => AnalysisFailureCategory.Unknown
            };
        }
    }

    internal static class WeatherAnalysisValidator
    {
        // A provider may successfully report that there is no readable sky. Accept the
        // unsafe observation, but do not turn it into a valid cloud training label.
        public static bool IsValidProviderResult(WeatherAnalysisResult? result, out string reason)
        {
            if (result is { IsSkyUnreadable: true, Condition: WeatherCondition.Unknown,
                IsSafeForImaging: false, CloudCoverage: 100 }
                && IsPercentage(result.Confidence) && !string.IsNullOrWhiteSpace(result.Description))
            {
                reason = string.Empty;
                return true;
            }
            return IsValidTeacherResult(result, out reason);
        }

        public static bool IsValidTeacherResult(WeatherAnalysisResult? result, out string reason)
        {
            if (result == null)
            {
                reason = "result is null";
                return false;
            }

            if (!Enum.IsDefined(typeof(WeatherCondition), result.Condition)
                || result.Condition == WeatherCondition.Unknown)
            {
                reason = "condition is missing or unknown";
                return false;
            }

            if (!IsPercentage(result.CloudCoverage))
            {
                reason = "cloudCoverage is outside 0-100 or is not finite";
                return false;
            }

            if (!IsPercentage(result.Confidence))
            {
                reason = "confidence is outside 0-100 or is not finite";
                return false;
            }

            if (string.IsNullOrWhiteSpace(result.Description))
            {
                reason = "description is empty";
                return false;
            }

            reason = string.Empty;
            return true;
        }

        /// <summary>
        /// A structurally valid result can still contradict its own label. It may be shown
        /// and used exactly as before, but it belongs in dataset quarantine rather than the
        /// automatically trainable label pool.
        /// </summary>
        public static bool IsInternallyConsistent(WeatherAnalysisResult result, out string reason)
        {
            if (result.IsSkyUnreadable || result.Condition == WeatherCondition.Unknown)
            {
                reason = "sky is unreadable; cloudCoverage is a safety sentinel, not a training label";
                return false;
            }
            if (result.RainDetected && result.Condition != WeatherCondition.Rainy)
            {
                reason = "rainDetected=true but condition is not Rainy";
                return false;
            }

            if (result.FogDetected && result.Condition != WeatherCondition.Foggy)
            {
                reason = "fogDetected=true but condition is not Foggy";
                return false;
            }

            var plausible = result.Condition switch
            {
                WeatherCondition.Clear => result.CloudCoverage <= 25,
                WeatherCondition.PartlyCloudy => result.CloudCoverage >= 5 && result.CloudCoverage <= 60,
                WeatherCondition.MostlyCloudy => result.CloudCoverage >= 40 && result.CloudCoverage <= 95,
                WeatherCondition.Overcast => result.CloudCoverage >= 70,
                _ => true
            };

            if (!plausible)
            {
                reason = $"condition {result.Condition} contradicts cloudCoverage {result.CloudCoverage:F1}";
                return false;
            }

            reason = string.Empty;
            return true;
        }

        private static bool IsPercentage(double value)
        {
            return !double.IsNaN(value) && !double.IsInfinity(value) && value >= 0 && value <= 100;
        }
    }
}
