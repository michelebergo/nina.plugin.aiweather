using AIWeather.Models;
using AIWeather.Services;
using FluentAssertions;
using NUnit.Framework;

namespace NINA.Plugin.AIWeather.Test
{
    /// <summary>
    /// The night of 14 September: Gemini rejected all 210 requests, the offline analyzer
    /// answered every one of them, and the owner learned it from the log the next morning.
    /// The fallback is right; keeping it quiet is not.
    /// </summary>
    [TestFixture]
    public class ProviderHealthTrackerTest
    {
        private static WeatherAnalysisResult Answered(string provider = "Gemini") =>
            new WeatherAnalysisResult { Provider = provider, FellBackToLocal = false };

        private static WeatherAnalysisResult FellBack(string provider = "Gemini", string error = "HTTP 400: INVALID_ARGUMENT") =>
            new WeatherAnalysisResult { Provider = provider, FellBackToLocal = true, ProviderError = error };

        [Test]
        public void AProviderThatAnswersHasNothingToSay() {
            var tracker = new ProviderHealthTracker();

            tracker.Record(Answered());

            tracker.IsFailing.Should().BeFalse();
            tracker.Describe().Should().BeEmpty();
        }

        [Test]
        public void TheFirstFallbackIsAlreadyVisible() {
            // At 20:02, on the first check, not at 08:00 from the log.
            var tracker = new ProviderHealthTracker();

            tracker.Record(FellBack());

            tracker.Describe().Should().Contain("Gemini").And.Contain("the last check").And.Contain("INVALID_ARGUMENT").And.Contain("offline analyzer");
        }

        [Test]
        public void ConsecutiveFallbacksAreCounted() {
            var tracker = new ProviderHealthTracker();
            for (var i = 0; i < 12; i++) { tracker.Record(FellBack()); }

            tracker.ConsecutiveFallbacks.Should().Be(12);
            tracker.Describe().Should().Contain("the last 12 checks");
        }

        [Test]
        public void OneAnswerClearsTheCount() {
            var tracker = new ProviderHealthTracker();
            tracker.Record(FellBack());
            tracker.Record(FellBack());

            tracker.Record(Answered());

            tracker.IsFailing.Should().BeFalse();
            tracker.Describe().Should().BeEmpty();
        }

        [Test]
        public void OnlyTheTransitionsAreReportedForLogging() {
            // Logged once when the provider stops answering and once when it resumes, not
            // every five minutes all night.
            var tracker = new ProviderHealthTracker();

            tracker.Record(Answered()).Should().BeFalse();
            tracker.Record(FellBack()).Should().BeTrue();
            tracker.Record(FellBack()).Should().BeFalse();
            tracker.Record(FellBack()).Should().BeFalse();
            tracker.Record(Answered()).Should().BeTrue();
            tracker.Record(Answered()).Should().BeFalse();
        }

        [Test]
        public void TheLatestErrorIsTheOneShown() {
            var tracker = new ProviderHealthTracker();
            tracker.Record(FellBack(error: "Gemini timed out"));
            tracker.Record(FellBack(error: "HTTP 429: quota"));

            tracker.Describe().Should().Contain("HTTP 429").And.NotContain("timed out");
        }

        [Test]
        public void TheOfflineAnalyzerChosenOnPurposeIsNotAFailure() {
            var tracker = new ProviderHealthTracker();

            tracker.Record(Answered(provider: "Local"));

            tracker.Describe().Should().BeEmpty();
        }
    }
}
