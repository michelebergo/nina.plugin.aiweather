using System;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using AIWeather.Equipment;
using AIWeather.Models;
using AIWeather.Services;
using FluentAssertions;
using NUnit.Framework;

namespace NINA.Plugin.AIWeather.Test
{
    /// <summary>
    /// The guide-star veto (#24) through the real monitor: a cloud-only Safe -> Unsafe call is
    /// held when PHD2's star is unchanged, without spending a second frame; rain, a collapsed
    /// star or no guiding at all leave the plugin behaving exactly as before.
    /// </summary>
    [TestFixture]
    public class GuideStarVetoNightTest
    {
        private sealed class Idle : IExternalSafetyMonitor
        {
            public string ProgId => string.Empty;
            public bool Connected => false;
            public bool TryConnect(string progId) => false;
            public bool TryGetIsSafe(out bool isSafe) { isSafe = true; return false; }
            public void Disconnect() { }
        }

        private (bool ascom, bool env, bool confirm, bool veto, double pct, bool weather, bool keep, double high, double low) _before;
        private AIWeatherSafetyMonitor _monitor = null!;
        private DateTime _clock;
        private string _folder = null!;

        [SetUp]
        public void SetUp() {
            var s = global::AIWeather.Properties.Settings.Default;
            _before = (s.UseAscomSafetyMonitor, s.UseEnvironmentSafetyMonitor, s.ConfirmTransitions, s.GuideStarVetoEnabled, s.GuideStarVetoPercent, s.WeatherVoteEnabled, s.KeepDecisionFrames, s.CloudCoverageThreshold, s.CloudCoverageSafeThreshold);
            s.UseAscomSafetyMonitor = false;
            s.UseEnvironmentSafetyMonitor = false;
            s.ConfirmTransitions = true;
            s.GuideStarVetoEnabled = true;
            s.GuideStarVetoPercent = 70;
            s.WeatherVoteEnabled = false;
            s.KeepDecisionFrames = true;
            s.CloudCoverageThreshold = 70;
            s.CloudCoverageSafeThreshold = 60;

            _clock = new DateTime(2026, 10, 25, 1, 0, 0, DateTimeKind.Utc);
            _folder = Path.Combine(Path.GetTempPath(), "aiweather-veto-" + Guid.NewGuid().ToString("N"));
            _monitor = new AIWeatherSafetyMonitor(new Idle(), new Idle()) { Connected = true };
            _monitor.UtcNow = () => _clock;
            _monitor.Decisions = new DecisionArchive(_folder);
        }

        [TearDown]
        public void TearDown() {
            var s = global::AIWeather.Properties.Settings.Default;
            (s.UseAscomSafetyMonitor, s.UseEnvironmentSafetyMonitor, s.ConfirmTransitions, s.GuideStarVetoEnabled, s.GuideStarVetoPercent, s.WeatherVoteEnabled, s.KeepDecisionFrames, s.CloudCoverageThreshold, s.CloudCoverageSafeThreshold) = _before;
            try { Directory.Delete(_folder, true); } catch { }
        }

        private static WeatherAnalysisResult Reading(double coverage, bool rain = false) => new WeatherAnalysisResult
        {
            CloudCoverage = coverage, Condition = coverage < 50 ? WeatherCondition.Clear : WeatherCondition.MostlyCloudy,
            IsSafeForImaging = coverage < 50 && !rain, RainDetected = rain, Confidence = 80, Provider = "Test", Model = "test-model",
        };

        /// <summary>Ten minutes of guiding up to now at the given star mass; the clock moves on.</summary>
        private void Guiding(double mass, double lastMinuteMass = double.NaN) {
            var start = _clock;
            for (var t = start; t <= start.AddMinutes(10); t = t.AddSeconds(2))
            {
                var m = double.IsNaN(lastMinuteMass) || t < start.AddMinutes(9) ? mass : lastMinuteMass;
                _monitor.RecordGuideStep(t, m, null);
            }
            _clock = start.AddMinutes(10);
        }

        private string DecisionTexts() => string.Join("\n---\n", Directory.Exists(_folder) ? Directory.GetFiles(_folder, "decision_*.txt").OrderBy(f => f).Select(File.ReadAllText) : Array.Empty<string>());

        [Test]
        public async Task AnUnchangedGuideStarHoldsSafeWithoutASecondFrame() {
            _monitor.ApplyAnalysisResult(Reading(10));
            _monitor.IsSafe.Should().BeTrue();
            Guiding(640_000);

            var asked = 0;
            var chosen = await _monitor.ResolveTransitionAsync(Reading(75), () => { asked++; return Task.FromResult<WeatherAnalysisResult?>(Reading(75)); });
            asked.Should().Be(0, "the guide star already answered; no second frame is spent");
            chosen.Veto.Should().Contain("guide-star mass");

            _monitor.ApplyAnalysisResult(chosen);
            _monitor.IsSafe.Should().BeTrue();
            _monitor.SafetyStateReason.Should().StartWith("Sky safe: cloud call 75% vetoed").And.Contain("guide-star mass");
            DecisionTexts().Should().Contain("Decision: Safe held (cloud call vetoed)").And.Contain("Veto: guide-star mass");
        }

        [Test]
        public async Task ACollapsedGuideStarLetsTheCallThroughToTheSecondFrame() {
            _monitor.ApplyAnalysisResult(Reading(10));
            Guiding(640_000, lastMinuteMass: 20_000);

            var asked = 0;
            var chosen = await _monitor.ResolveTransitionAsync(Reading(75), () => { asked++; return Task.FromResult<WeatherAnalysisResult?>(Reading(80)); });
            asked.Should().Be(1);
            chosen.Veto.Should().BeNull();

            _monitor.ApplyAnalysisResult(chosen);
            _monitor.IsSafe.Should().BeFalse();
        }

        [Test]
        public async Task RainIsNeverVetoed() {
            global::AIWeather.Properties.Settings.Default.ConfirmTransitions = false;
            _monitor.ApplyAnalysisResult(Reading(10));
            Guiding(640_000);

            var chosen = await _monitor.ResolveTransitionAsync(Reading(10, rain: true), () => Task.FromResult<WeatherAnalysisResult?>(null));
            chosen.Veto.Should().BeNull();

            _monitor.ApplyAnalysisResult(chosen);
            _monitor.IsSafe.Should().BeFalse();
        }

        [Test]
        public async Task WithoutGuidingTheCallGoesThroughAsBefore() {
            _monitor.ApplyAnalysisResult(Reading(10));
            _clock = _clock.AddMinutes(10);

            var asked = 0;
            var chosen = await _monitor.ResolveTransitionAsync(Reading(75), () => { asked++; return Task.FromResult<WeatherAnalysisResult?>(Reading(80)); });
            asked.Should().Be(1);
            chosen.Veto.Should().BeNull();
        }

        [Test]
        public async Task TheVetoWorksWithoutTheSecondFrameFeature() {
            global::AIWeather.Properties.Settings.Default.ConfirmTransitions = false;
            _monitor.ApplyAnalysisResult(Reading(10));
            Guiding(640_000);

            var chosen = await _monitor.ResolveTransitionAsync(Reading(75), () => throw new InvalidOperationException("no second frame when confirmation is off"));
            chosen.Veto.Should().NotBeNull();

            _monitor.ApplyAnalysisResult(chosen);
            _monitor.IsSafe.Should().BeTrue();
        }

        [Test]
        public async Task SwitchedOffNothingChanges() {
            global::AIWeather.Properties.Settings.Default.GuideStarVetoEnabled = false;
            _monitor.ApplyAnalysisResult(Reading(10));
            Guiding(640_000);

            var asked = 0;
            var chosen = await _monitor.ResolveTransitionAsync(Reading(75), () => { asked++; return Task.FromResult<WeatherAnalysisResult?>(Reading(80)); });
            asked.Should().Be(1);
            chosen.Veto.Should().BeNull();
        }

        [Test]
        public void GuideStepsAreDroppedWithTheConnection() {
            Guiding(640_000);
            _monitor.GuideVeto.Evaluate(_clock, 70, false, 0.2).Should().NotBeNull();

            _monitor.Disconnect();

            _monitor.GuideVeto.Evaluate(_clock, 70, false, 0.2).Should().BeNull("a reconnect must not inherit last night's guide star");
        }
    }
}
