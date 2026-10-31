using System;
using AIWeather.Services;
using FluentAssertions;
using NUnit.Framework;

namespace NINA.Plugin.AIWeather.Test
{
    /// <summary>
    /// Two false parks under a full moon on a clear sky (#24): the model read 75% cloud while
    /// PHD2's guide star sat at 100% of its clear-sky mass and the sky meter had not moved.
    /// The guide star is a fact about the target itself; when it is unchanged, a cloud-only
    /// Unsafe call is held back. The rule here is pure: samples in, verdict out.
    /// </summary>
    [TestFixture]
    public class GuideStarVetoTest
    {
        private static readonly DateTime Now = new DateTime(2026, 10, 25, 1, 14, 0, DateTimeKind.Utc);

        /// <summary>Guide steps every two seconds from <paramref name="from"/> to <paramref name="to"/> minutes before now.</summary>
        private static GuideStarVeto Guiding(double from, double to, Func<DateTime, double> mass, Func<DateTime, double?>? weather = null, GuideStarVeto? into = null)
        {
            var veto = into ?? new GuideStarVeto();
            for (var t = Now.AddMinutes(-from); t <= Now.AddMinutes(-to); t = t.AddSeconds(2))
            {
                veto.Record(t, mass(t), weather?.Invoke(t));
            }
            return veto;
        }

        private static double Noisy(DateTime t, double level) => level * (1 + 0.02 * Math.Sin(t.Second));

        [Test]
        public void AGuideStarAtItsClearSkyMassHoldsTheSafeState() {
            // Dennis, 01:14: star mass 635k-648k, 100% of the clear-sky level, AI said 75%.
            var veto = Guiding(10, 0, t => Noisy(t, 640_000));

            var verdict = veto.Evaluate(Now, 70, weatherVote: false, weatherLimit: 0.2);

            verdict.Should().NotBeNull();
            verdict!.Holds.Should().BeTrue();
            verdict.StarMassPercent.Should().BeInRange(95, 105);
            verdict.Describe().Should().Contain("guide-star mass").And.Contain("% of recent level");
        }

        [Test]
        public void AStarThatCollapsedIntoTheTreesDoesNotHold() {
            // 22:22-22:50 the scope tracked into trees: star mass collapsed. The veto stands down.
            var veto = Guiding(10, 1, t => Noisy(t, 640_000));
            Guiding(1, 0, t => Noisy(t, 20_000), into: veto);

            var verdict = veto.Evaluate(Now, 70, weatherVote: false, weatherLimit: 0.2);

            verdict.Should().NotBeNull();
            verdict!.Holds.Should().BeFalse();
            verdict.StarMassPercent.Should().BeLessThan(10);
        }

        [Test]
        public void AStarAtTheThresholdHolds_JustBelowDoesNot() {
            var at = Guiding(10, 1, _ => 100_000); Guiding(1, 0, _ => 70_000, into: at);
            var below = Guiding(10, 1, _ => 100_000); Guiding(1, 0, _ => 69_000, into: below);

            at.Evaluate(Now, 70, false, 0.2)!.Holds.Should().BeTrue();
            below.Evaluate(Now, 70, false, 0.2)!.Holds.Should().BeFalse();
        }

        [Test]
        public void NoGuideStepsInTheLastMinuteMeansNoVeto() {
            // Guiding stopped three minutes ago (parked, dithering gone wrong, PHD2 closed): no
            // say in the matter, the plugin behaves exactly as without the feature.
            var veto = Guiding(13, 3, t => Noisy(t, 640_000));

            veto.Evaluate(Now, 70, false, 0.2).Should().BeNull();
        }

        [Test]
        public void ABaselineShorterThanTwentySamplesMeansNoVeto() {
            // Guiding started 90 s ago: nothing to compare the last minute with.
            var veto = Guiding(1.5, 0, t => Noisy(t, 640_000));

            veto.Evaluate(Now, 70, false, 0.2).Should().BeNull();
        }

        [Test]
        public void SamplesOlderThanTenMinutesAreForgotten() {
            // A star twice as bright an hour ago must not make the current one look weak.
            var veto = Guiding(70, 60, _ => 1_280_000);
            Guiding(10, 0, t => Noisy(t, 640_000), into: veto);

            veto.Evaluate(Now, 70, false, 0.2)!.Holds.Should().BeTrue();
        }

        [Test]
        public void ASkyMeterThatMovedBreaksTheVeto() {
            // Second vote on: the guide star is fine but SkyQuality dropped 0.4 mag/arcsec2
            // against a limit of 0.2. The veto wants both and gets one.
            var veto = Guiding(10, 1, t => Noisy(t, 640_000), _ => 18.40);
            Guiding(1, 0, t => Noisy(t, 640_000), _ => 18.00, into: veto);

            var verdict = veto.Evaluate(Now, 70, weatherVote: true, weatherLimit: 0.2);

            verdict!.Holds.Should().BeFalse();
            verdict.Describe().Should().Contain("18.00").And.Contain("18.40");
        }

        [Test]
        public void ASkyMeterThatDriftedWithTheMoonStillHolds() {
            // Dennis's SQM went 17.99 -> 18.6 over the night. Relative to the last ten minutes
            // it barely moves, and the baseline follows the drift.
            var veto = Guiding(10, 0, t => Noisy(t, 640_000), t => 18.40 + 0.005 * (t - Now.AddMinutes(-10)).TotalMinutes);

            veto.Evaluate(Now, 70, weatherVote: true, weatherLimit: 0.2)!.Holds.Should().BeTrue();
        }

        [Test]
        public void TheSkyMeterAloneCannotHold() {
            // pcampGH, 00:11: a moonlit cloud band on the target while the pyrometer, looking
            // elsewhere, read clear. The weather vote never overrides a collapsed guide star.
            var veto = Guiding(10, 1, t => Noisy(t, 640_000), _ => -18.0);
            Guiding(1, 0, t => Noisy(t, 20_000), _ => -18.0, into: veto);

            veto.Evaluate(Now, 70, weatherVote: true, weatherLimit: 1.0)!.Holds.Should().BeFalse();
        }

        [Test]
        public void ASecondVoteWithoutReadingsDoesNotHold() {
            // The vote is on but the weather device gave nothing: no evidence, no veto, and the
            // verdict says why.
            var veto = Guiding(10, 0, t => Noisy(t, 640_000));

            var verdict = veto.Evaluate(Now, 70, weatherVote: true, weatherLimit: 0.2);

            verdict!.Holds.Should().BeFalse();
            verdict.Describe().Should().Contain("no weather readings");
        }
    }
}
