using System;
using System.Collections.Generic;
using System.Drawing;
using System.Threading;
using System.Threading.Tasks;
using AIWeather.Models;
using AIWeather.Services;
using FluentAssertions;
using NUnit.Framework;

namespace NINA.Plugin.AIWeather.Test
{
    /// <summary>
    /// Google answered a first call to gemini-flash-latest with 503 "high demand" and the
    /// check went to the offline analyzer at once (#21). A busy provider is not a broken
    /// one: one retry after a pause, then a reserve model, then the offline analyzer.
    /// </summary>
    [TestFixture]
    public class AnalysisRetryTest
    {
        private sealed class Scripted : IWeatherAnalysisService
        {
            private readonly Queue<WeatherAnalysisResult> _answers;
            public int Calls;
            public Scripted(params WeatherAnalysisResult[] answers) { _answers = new Queue<WeatherAnalysisResult>(answers); }
            public Task<bool> InitializeAsync(CancellationToken ct = default) => Task.FromResult(true);
            public Task<WeatherAnalysisResult> AnalyzeImageAsync(Bitmap image, AstroContext? a = null, CancellationToken ct = default) {
                Calls++;
                return Task.FromResult(_answers.Dequeue());
            }
        }

        private static WeatherAnalysisResult Answer(double coverage) => new WeatherAnalysisResult { Provider = "Gemini", CloudCoverage = coverage, Condition = WeatherCondition.Clear, IsSafeForImaging = true };
        private static WeatherAnalysisResult Busy() => new WeatherAnalysisResult { Provider = "Gemini", FellBackToLocal = true, ProviderError = "Gemini error: HTTP 503: high demand", CloudCoverage = 47 };
        private static WeatherAnalysisResult Rejected() => new WeatherAnalysisResult { Provider = "Gemini", FellBackToLocal = true, ProviderError = "Gemini error: HTTP 400: bad request", CloudCoverage = 47 };

        private readonly List<TimeSpan> _pauses = new List<TimeSpan>();

        [SetUp]
        public void SetUp() { _pauses.Clear(); }
        private Task NoPause(TimeSpan t, CancellationToken ct) { _pauses.Add(t); return Task.CompletedTask; }

        [TestCase("Gemini error: HTTP 503: high demand", true)]
        [TestCase("OpenAI error: HTTP 429: rate limit", true)]
        [TestCase("Anthropic error: HTTP 529: overloaded", true)]
        [TestCase("Gemini timed out", true)]
        [TestCase("Gemini error: HTTP 400: bad request", false)]
        [TestCase("Anthropic error: HTTP 401: invalid key", false)]
        [TestCase(null, false)]
        public void OnlyABusyOrSlowProviderIsWorthRetrying(string? error, bool expected) {
            AnalysisRetry.IsTransient(error).Should().Be(expected);
        }

        [Test]
        public async Task AnAnswerOnTheFirstCallIsUsedAsIs() {
            var primary = new Scripted(Answer(10));
            using var frame = new Bitmap(4, 4);
            var r = await AnalysisRetry.AnalyzeAsync(primary, "gemini-flash-latest", null, null, frame, null, NoPause, CancellationToken.None);
            r.CloudCoverage.Should().Be(10);
            r.Model.Should().Be("gemini-flash-latest");
            _pauses.Should().BeEmpty();
        }

        [Test]
        public async Task ABusyProviderIsAskedAgainAfterAPause() {
            var primary = new Scripted(Busy(), Answer(12));
            using var frame = new Bitmap(4, 4);
            var r = await AnalysisRetry.AnalyzeAsync(primary, "gemini-flash-latest", null, null, frame, null, NoPause, CancellationToken.None);
            r.FellBackToLocal.Should().BeFalse();
            r.CloudCoverage.Should().Be(12);
            primary.Calls.Should().Be(2);
            _pauses.Should().ContainSingle().Which.Should().Be(AnalysisRetry.Pause);
        }

        [Test]
        public async Task StillBusy_TheReserveModelAnswers() {
            var primary = new Scripted(Busy(), Busy());
            var reserve = new Scripted(Answer(15));
            using var frame = new Bitmap(4, 4);
            var r = await AnalysisRetry.AnalyzeAsync(primary, "gemini-flash-latest", () => reserve, "gemini-flash-lite-latest", frame, null, NoPause, CancellationToken.None);
            r.FellBackToLocal.Should().BeFalse();
            r.CloudCoverage.Should().Be(15);
            r.Model.Should().Be("gemini-flash-lite-latest");
            reserve.Calls.Should().Be(1);
        }

        [Test]
        public async Task StillBusyAndNoReserve_TheOfflineAnswerStands() {
            var primary = new Scripted(Busy(), Busy());
            using var frame = new Bitmap(4, 4);
            var r = await AnalysisRetry.AnalyzeAsync(primary, "gemini-flash-latest", null, null, frame, null, NoPause, CancellationToken.None);
            r.FellBackToLocal.Should().BeTrue();
            primary.Calls.Should().Be(2);
        }

        [Test]
        public async Task ARejectedRequestIsNotRetried() {
            // A 400 will be a 400 again; waiting changes nothing and costs a check.
            var primary = new Scripted(Rejected());
            var reserve = new Scripted(Answer(15));
            using var frame = new Bitmap(4, 4);
            var r = await AnalysisRetry.AnalyzeAsync(primary, "m", () => reserve, "r", frame, null, NoPause, CancellationToken.None);
            r.FellBackToLocal.Should().BeTrue();
            primary.Calls.Should().Be(1);
            reserve.Calls.Should().Be(0);
            _pauses.Should().BeEmpty();
        }
    }
}
