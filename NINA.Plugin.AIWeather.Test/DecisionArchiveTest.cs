using System;
using System.Drawing;
using System.IO;
using System.Linq;
using AIWeather.Models;
using AIWeather.Services;
using FluentAssertions;
using NUnit.Framework;

namespace NINA.Plugin.AIWeather.Test
{
    /// <summary>
    /// Issue #21: an Unsafe alert at 01:21 and, by morning, the analysed frame already
    /// pruned from the rolling capture folder. The frame behind a decision is kept, with
    /// the decision written next to it, for a number of days.
    /// </summary>
    [TestFixture]
    public class DecisionArchiveTest
    {
        private string _folder = null!;

        [SetUp]
        public void SetUp() { _folder = Path.Combine(Path.GetTempPath(), "aiweather-decisions-" + Guid.NewGuid().ToString("N")); }

        [TearDown]
        public void TearDown() { try { Directory.Delete(_folder, true); } catch { } }

        private static DecisionRecord Record(DateTime utc) => new DecisionRecord
        {
            Transition = "Safe -> Unsafe",
            Utc = utc,
            Provider = "Gemini",
            Model = "gemini-flash-latest",
            Condition = WeatherCondition.MostlyCloudy,
            CloudCoverage = 75,
            Confidence = 80,
            RainDetected = false,
            FogDetected = false,
            Description = "Thick clouds over most of the sky",
            HighThreshold = 50,
            LowThreshold = 30,
            SkySafe = false,
            ExternalMonitor = "not configured",
            EnvironmentMonitor = "not configured",
            CombinedSafe = false,
            Reason = "Sky cloud coverage 75% (safe below 30%)",
        };

        [Test]
        public void ADecisionLeavesTheFrameAndItsExplanation() {
            var archive = new DecisionArchive(_folder);
            using var frame = new Bitmap(8, 8);
            var utc = new DateTime(2026, 10, 19, 23, 16, 42, DateTimeKind.Utc);

            var imagePath = archive.Save(frame, Record(utc), localTimestamp: new DateTime(2026, 10, 20, 1, 16, 42));

            imagePath.Should().Be(Path.Combine(_folder, "decision_20261020_011642.jpg"));
            File.Exists(imagePath).Should().BeTrue();
            var text = File.ReadAllText(Path.Combine(_folder, "decision_20261020_011642.txt"));
            text.Should().Contain("Decision: Safe -> Unsafe");
            text.Should().Contain("Provider: Gemini (gemini-flash-latest)");
            text.Should().Contain("Condition: MostlyCloudy");
            text.Should().Contain("Cloud coverage: 75%");
            text.Should().Contain("Confidence: 80%");
            text.Should().Contain("Thresholds: unsafe at 50%, safe below 30%");
            text.Should().Contain("Combined: Unsafe");
            text.Should().Contain("Reason: Sky cloud coverage 75% (safe below 30%)");
        }

        [Test]
        public void WithoutAFrameTheExplanationIsStillKept() {
            var archive = new DecisionArchive(_folder);
            var imagePath = archive.Save(null, Record(DateTime.UtcNow), localTimestamp: new DateTime(2026, 10, 20, 1, 16, 42));

            imagePath.Should().BeNull();
            File.Exists(Path.Combine(_folder, "decision_20261020_011642.txt")).Should().BeTrue();
        }

        [Test]
        public void OldDecisionsAreRemovedAfterTheRetention() {
            var archive = new DecisionArchive(_folder);
            Directory.CreateDirectory(_folder);
            var old = Path.Combine(_folder, "decision_20261001_000000.txt"); File.WriteAllText(old, "old");
            var oldJpg = Path.Combine(_folder, "decision_20261001_000000.jpg"); File.WriteAllText(oldJpg, "old");
            var recent = Path.Combine(_folder, "decision_20261019_000000.txt"); File.WriteAllText(recent, "recent");
            var stranger = Path.Combine(_folder, "notes.txt"); File.WriteAllText(stranger, "mine");
            var now = new DateTime(2026, 10, 20, 12, 0, 0);
            File.SetLastWriteTime(old, now.AddDays(-19)); File.SetLastWriteTime(oldJpg, now.AddDays(-19));
            File.SetLastWriteTime(recent, now.AddDays(-1)); File.SetLastWriteTime(stranger, now.AddDays(-40));

            archive.Prune(TimeSpan.FromDays(7), now).Should().Be(2);

            File.Exists(old).Should().BeFalse(); File.Exists(oldJpg).Should().BeFalse();
            File.Exists(recent).Should().BeTrue();
            File.Exists(stranger).Should().BeTrue("only the archive's own files are touched");
        }
    }
}
