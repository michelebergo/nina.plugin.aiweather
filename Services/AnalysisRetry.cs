using System;
using System.Drawing;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;
using AIWeather.Models;
using NINA.Core.Utility;

namespace AIWeather.Services
{
    /// <summary>
    /// A busy provider is not a broken one. Google answers a demand spike with 503, every
    /// provider has a 429, and a request can time out; a check that goes to the offline
    /// analyzer on the first such answer throws away the night's accuracy for a ten-second
    /// hiccup (#21). So: one retry after a pause, then the reserve model if the owner named
    /// one, and only then the offline analyzer. A request the provider rejected (400, 401)
    /// will be rejected again; it is not retried.
    /// </summary>
    public static class AnalysisRetry
    {
        public static readonly TimeSpan Pause = TimeSpan.FromSeconds(10);

        private static readonly Regex Transient = new Regex(@"HTTP (429|5\d\d)\b|timed out|high demand|overloaded", RegexOptions.IgnoreCase);

        /// <summary>Whether a provider error is the kind that waiting can fix.</summary>
        public static bool IsTransient(string? providerError) =>
            !string.IsNullOrEmpty(providerError) && Transient.IsMatch(providerError);

        private static bool WentOfflineForATransientReason(WeatherAnalysisResult r) => r.FellBackToLocal && IsTransient(r.ProviderError);

        /// <param name="reserve">Makes the reserve-model service, or null when none is configured.</param>
        /// <param name="delay">The pause between attempts; the tests pass one that does not wait.</param>
        public static async Task<WeatherAnalysisResult> AnalyzeAsync(
            IWeatherAnalysisService primary, string primaryModel,
            Func<IWeatherAnalysisService?>? reserve, string? reserveModel,
            Bitmap frame, AstroContext? astroContext,
            Func<TimeSpan, CancellationToken, Task> delay, CancellationToken ct)
        {
            var result = await primary.AnalyzeImageAsync(frame, astroContext, ct);
            result.Model = primaryModel;
            if (!WentOfflineForATransientReason(result)) return result;

            Logger.Warning($"Provider busy or slow ({result.ProviderError}); retrying once in {Pause.TotalSeconds:F0}s");
            await delay(Pause, ct);
            result = await primary.AnalyzeImageAsync(frame, astroContext, ct);
            result.Model = primaryModel;
            if (!WentOfflineForATransientReason(result)) return result;

            var reserveService = reserve?.Invoke();
            if (reserveService == null || string.IsNullOrWhiteSpace(reserveModel))
            {
                Logger.Warning($"Provider still busy ({result.ProviderError}); no reserve model configured, the offline analyzer's answer stands");
                return result;
            }

            Logger.Warning($"Provider still busy ({result.ProviderError}); asking the reserve model {reserveModel}");
            if (!await reserveService.InitializeAsync(ct)) return result;
            var fromReserve = await reserveService.AnalyzeImageAsync(frame, astroContext, ct);
            fromReserve.Model = reserveModel;
            return fromReserve;
        }
    }
}
