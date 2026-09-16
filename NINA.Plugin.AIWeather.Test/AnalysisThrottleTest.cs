using AIWeather.Services;
using FluentAssertions;
using NUnit.Framework;

namespace NINA.Plugin.AIWeather.Test
{
    /// <summary>
    /// A user testing a camera pressed start and stop every few seconds and paid a Gemini
    /// call for every start. The immediate check that follows a start is skipped when the
    /// last analysis is recent enough to be the same sky.
    /// </summary>
    [TestFixture]
    public class AnalysisThrottleTest
    {
        private static readonly DateTime Now = new DateTime(2026, 9, 16, 19, 40, 0, DateTimeKind.Utc);

        [Test]
        public void NoAnalysisYet_RunsNow() {
            AnalysisThrottle.ShouldReuse(DateTime.MinValue, Now).Should().BeFalse();
        }

        [Test]
        public void ARecentAnalysisIsReused() {
            AnalysisThrottle.ShouldReuse(Now.AddSeconds(-4), Now).Should().BeTrue();
            AnalysisThrottle.ShouldReuse(Now.AddSeconds(-29), Now).Should().BeTrue();
        }

        [Test]
        public void AnOldAnalysisIsNotReused() {
            AnalysisThrottle.ShouldReuse(Now.AddSeconds(-30), Now).Should().BeFalse();
            AnalysisThrottle.ShouldReuse(Now.AddMinutes(-5), Now).Should().BeFalse();
        }

        [Test]
        public void AClockThatWentBackwardsDoesNotReuseForever() {
            // A last-analysis time in the future (clock change) must not freeze the monitor.
            AnalysisThrottle.ShouldReuse(Now.AddMinutes(10), Now).Should().BeFalse();
        }

        [Test]
        public void TheGapIsThirtySeconds() {
            AnalysisThrottle.MinimumGap.Should().Be(TimeSpan.FromSeconds(30));
        }
    }
}
