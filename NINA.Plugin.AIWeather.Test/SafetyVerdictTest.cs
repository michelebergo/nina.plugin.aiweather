using AIWeather.Services;
using FluentAssertions;
using NUnit.Framework;

namespace NINA.Plugin.AIWeather.Test
{
    /// <summary>
    /// Issue #19: a narrow-field IR cloud sensor that cries cloudy for clouds far from
    /// the target, and AI Weather judging a crop around the pointing. The owner wants
    /// unsafe only when both agree. The combination is one pure function so the truth
    /// table can be read here instead of inferred from the monitor.
    /// </summary>
    [TestFixture]
    public class SafetyVerdictTest
    {
        // Without an external monitor the mode is irrelevant: the sky decides, and only a fresh sky.
        [TestCase(ExternalMonitorMode.And, true, true, false, true)]
        [TestCase(ExternalMonitorMode.Or, true, true, false, true)]
        [TestCase(ExternalMonitorMode.And, true, false, false, false)]
        [TestCase(ExternalMonitorMode.Or, true, false, false, false)]
        [TestCase(ExternalMonitorMode.Or, false, true, false, false)]
        public void WithoutAnExternalMonitorTheSkyDecides(ExternalMonitorMode mode, bool skySafe, bool fresh, bool hazard, bool expected) {
            SafetyVerdict.Combine(mode, skySafe, fresh, hazard, externalEnabled: false, externalSafe: false).Should().Be(expected);
        }

        // AND, as it always was: everything must hold, anything unknown is unsafe.
        [TestCase(true, true, true, true)]
        [TestCase(false, true, true, false)]
        [TestCase(true, false, true, false)]
        [TestCase(true, true, false, false)]
        public void AndNeedsEveryConditionToHold(bool skySafe, bool fresh, bool externalSafe, bool expected) {
            SafetyVerdict.Combine(ExternalMonitorMode.And, skySafe, fresh, hazard: false, externalEnabled: true, externalSafe).Should().Be(expected);
        }

        // OR: unsafe only when both say unsafe. Stale sky counts as unsafe (the sensor alone
        // decides); an unreadable sensor is passed in as unsafe (the sky alone decides).
        [TestCase(true, true, true, true)]
        [TestCase(true, true, false, true)]
        [TestCase(false, true, true, true)]
        [TestCase(false, true, false, false)]
        [TestCase(true, false, true, true)]
        [TestCase(true, false, false, false)]
        public void OrIsUnsafeOnlyWhenBothAre(bool skySafe, bool fresh, bool externalSafe, bool expected) {
            SafetyVerdict.Combine(ExternalMonitorMode.Or, skySafe, fresh, hazard: false, externalEnabled: true, externalSafe).Should().Be(expected);
        }

        [Test]
        public void OrDoesNotLetAnotherSensorOverrideRainOrFog() {
            // Water on the lens is not a cloud far from the target; a cloud sensor that says
            // clear does not dry it. Rain and fog stay unsafe in every mode.
            SafetyVerdict.Combine(ExternalMonitorMode.Or, skySafe: false, fresh: true, hazard: true, externalEnabled: true, externalSafe: true)
                .Should().BeFalse();
        }

        [Test]
        public void AStaleHazardIsJustStaleData() {
            // Rain seen an hour ago says nothing about now; stale data makes the sky unknown,
            // and in OR mode the sensor alone decides.
            SafetyVerdict.Combine(ExternalMonitorMode.Or, skySafe: false, fresh: false, hazard: true, externalEnabled: true, externalSafe: true)
                .Should().BeTrue();
        }
    }
}
