using System;
using AIWeather.Models;

namespace AIWeather.Services
{
    /// <summary>
    /// Whether a check asked for "now" is worth running, given when the last one ran.
    /// Every press of the start button used to trigger an immediate analysis - a paid
    /// provider call per click, and a user testing a camera pays for every attempt. A
    /// reading a few seconds old is the same sky.
    /// </summary>
    public static class AnalysisThrottle
    {
        public static readonly TimeSpan MinimumGap = TimeSpan.FromSeconds(30);

        // Failure envelopes are useful diagnostics, but never a new sky measurement.
        public static bool HasUsableMeasurement(WeatherAnalysisResult? result) =>
            result != null && result.Condition != WeatherCondition.Unknown
            && double.IsFinite(result.CloudCoverage) && result.CloudCoverage >= 0 && result.CloudCoverage <= 100
            && double.IsFinite(result.Confidence) && result.Confidence > 0 && result.Confidence <= 100;

        public static bool ShouldReuseResult(WeatherAnalysisResult? result, DateTime lastAnalysisUtc,
            DateTime nowUtc, long resultRevision, long currentRevision) =>
            resultRevision == currentRevision && HasUsableMeasurement(result)
            && ShouldReuse(lastAnalysisUtc, nowUtc);

        /// <summary>True when the last analysis is recent enough to be reused instead of repeated.</summary>
        public static bool ShouldReuse(DateTime lastAnalysisUtc, DateTime nowUtc, TimeSpan? minimumGap = null)
        {
            if (lastAnalysisUtc == DateTime.MinValue) { return false; }
            var age = nowUtc - lastAnalysisUtc;
            return age >= TimeSpan.Zero && age < (minimumGap ?? MinimumGap);
        }
    }
}
