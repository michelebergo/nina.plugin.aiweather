using System;
using System.IO;
using AIWeather.Equipment;
using AIWeather.Models;
using AIWeather.Services;
using FluentAssertions;
using NUnit.Framework;

namespace NINA.Plugin.AIWeather.Test
{
    /// <summary>
    /// Issue #19 replayed through the real monitor, result by result, with a fake ASCOM
    /// device and a clock moved by hand: the reporter's dome has a narrow-field IR cloud
    /// sensor that cries cloudy for clouds far from the target, and AI Weather judges a
    /// crop around the pointing. Every scenario checks the state NINA acts on, the reason
    /// line in the panel and the status file third-party software reads.
    /// </summary>
    [TestFixture]
    public class ExternalMonitorNightTest
    {
        private sealed class FakeDevice : IExternalSafetyMonitor
        {
            public bool CanConnect = true;
            public bool Readable = true;
            public bool Safe;
            public string ProgId { get; private set; } = string.Empty;
            public bool Connected { get; private set; }
            public bool TryConnect(string progId) { ProgId = progId; Connected = CanConnect; return Connected; }
            public bool TryGetIsSafe(out bool isSafe) { isSafe = Safe; return Readable; }
            public void Disconnect() { Connected = false; }
        }

        private FakeDevice _device = null!;
        private AIWeatherSafetyMonitor _monitor = null!;
        private DateTime _clock;
        private string _statusFile = null!;
        private string _wikiRoot = null!;
        private (bool use, int mode, string progId, double high, double low, int maxAge, int interval, bool writeFile, string path) _before;

        [SetUp]
        public void SetUp() {
            var s = global::AIWeather.Properties.Settings.Default;
            _before = (s.UseAscomSafetyMonitor, s.ExternalMonitorMode, s.AscomSafetyMonitorProgId, s.CloudCoverageThreshold, s.CloudCoverageSafeThreshold,
                       s.MaxDataAgeMinutes, s.CheckIntervalMinutes, s.WriteSafetyStatusFile, s.SafetyStatusFilePath);
            _statusFile = Path.Combine(Path.GetTempPath(), "aiweather-test-" + Guid.NewGuid().ToString("N") + ".txt");
            _wikiRoot = Path.Combine(Path.GetTempPath(), "aiweather-wiki-" + Guid.NewGuid().ToString("N"));
            LlmWikiRawWriter.RootOverride = _wikiRoot;

            s.UseAscomSafetyMonitor = true;
            s.AscomSafetyMonitorProgId = "ASCOM.Fake.SafetyMonitor";
            s.CloudCoverageThreshold = 50;      // high: Safe -> Unsafe
            s.CloudCoverageSafeThreshold = 30;  // low: Unsafe -> Safe
            s.MaxDataAgeMinutes = 15;
            s.CheckIntervalMinutes = 5;
            s.WriteSafetyStatusFile = true;
            s.SafetyStatusFilePath = _statusFile;

            _device = new FakeDevice();
            _clock = new DateTime(2026, 10, 16, 22, 0, 0, DateTimeKind.Utc);
            _monitor = new AIWeatherSafetyMonitor(_device) { Connected = true };
            _monitor.UtcNow = () => _clock;
        }

        [TearDown]
        public void TearDown() {
            var s = global::AIWeather.Properties.Settings.Default;
            (s.UseAscomSafetyMonitor, s.ExternalMonitorMode, s.AscomSafetyMonitorProgId, s.CloudCoverageThreshold, s.CloudCoverageSafeThreshold,
             s.MaxDataAgeMinutes, s.CheckIntervalMinutes, s.WriteSafetyStatusFile, s.SafetyStatusFilePath) = _before;
            LlmWikiRawWriter.RootOverride = null;
            try { File.Delete(_statusFile); } catch { }
            try { Directory.Delete(_wikiRoot, true); } catch { }
        }

        private static void Mode(ExternalMonitorMode mode) => global::AIWeather.Properties.Settings.Default.ExternalMonitorMode = (int)mode;

        /// <summary>One check: the camera delivered, the AI answered. Five minutes pass before it.</summary>
        private void Analysis(double coverage, bool rain = false, bool fog = false) {
            _clock = _clock.AddMinutes(5);
            _monitor.ApplyAnalysisResult(new WeatherAnalysisResult
            {
                Timestamp = _clock,
                CloudCoverage = coverage,
                Condition = rain ? WeatherCondition.Rainy : fog ? WeatherCondition.Foggy : coverage < 15 ? WeatherCondition.Clear : coverage < 50 ? WeatherCondition.PartlyCloudy : coverage < 85 ? WeatherCondition.MostlyCloudy : WeatherCondition.Overcast,
                RainDetected = rain,
                FogDetected = fog,
                IsSafeForImaging = !rain && !fog && coverage < 50,
                Confidence = 85,
                Provider = "Test",
            });
        }

        /// <summary>The device changed its mind; the monitor re-reads it after its 5 s cache.</summary>
        private void DeviceSays(bool safe) { _device.Safe = safe; _clock = _clock.AddSeconds(6); }

        private string StatusFile => File.Exists(_statusFile) ? File.ReadAllText(_statusFile) : "(not written)";

        // ---- the reporter's night ----

        [Test]
        public void Or_IrSensorCriesCloudyWhileTheTargetIsClear_ImagingContinues() {
            Mode(ExternalMonitorMode.Or);
            DeviceSays(false);          // low clouds far from the target
            Analysis(10);               // the crop around the pointing is clear

            _monitor.IsSafe.Should().BeTrue();
            _monitor.SafetyStateReason.Should().Be("Safe on the sky analysis alone — external monitor unsafe or unreadable");
            StatusFile.Should().Be("Safe");
        }

        [Test]
        public void And_TheSameNightStopsImaging_AsItAlwaysDid() {
            Mode(ExternalMonitorMode.And);
            DeviceSays(false);
            Analysis(10);

            _monitor.IsSafe.Should().BeFalse();
            _monitor.SafetyStateReason.Should().Be("External safety monitor reports unsafe");
            StatusFile.Should().Be("Unsafe");
        }

        [Test]
        public void Or_RealOvercast_BothAgree_ImagingStops() {
            Mode(ExternalMonitorMode.Or);
            DeviceSays(false);
            Analysis(95);

            _monitor.IsSafe.Should().BeFalse();
            _monitor.SafetyStateReason.Should().Be("Sky cloud coverage 95% (safe below 30%); external safety monitor reports unsafe");
            StatusFile.Should().Be("Unsafe");
        }

        [Test]
        public void Or_RainOnTheLensWhileTheIrSensorSaysClear_ImagingStops() {
            Mode(ExternalMonitorMode.Or);
            DeviceSays(true);
            Analysis(20, rain: true);

            _monitor.IsSafe.Should().BeFalse();
            _monitor.SafetyStateReason.Should().Be("Sky shows rain — rain and fog stop imaging whatever the external monitor says");
            StatusFile.Should().Be("Unsafe");

            Analysis(20, fog: true);
            _monitor.IsSafe.Should().BeFalse();
            _monitor.SafetyStateReason.Should().StartWith("Sky shows fog");
        }

        [Test]
        public void Or_TheCameraDies_TheIrSensorAloneDecides() {
            Mode(ExternalMonitorMode.Or);
            DeviceSays(true);
            Analysis(10);
            _monitor.IsSafe.Should().BeTrue();

            _clock = _clock.AddMinutes(20);     // no analysis for longer than the 15 min limit
            _monitor.IsSafe.Should().BeTrue("the sensor says clear and the sky is unknown, not unsafe");
            _monitor.SafetyStateReason.Should().Be("Safe on the external monitor alone — sky data is stale");

            DeviceSays(false);                  // now the sensor sees clouds too
            _monitor.IsSafe.Should().BeFalse();
            _monitor.SafetyStateReason.Should().Be("Sky data is stale; external safety monitor reports unsafe");
        }

        [Test]
        public void Or_RainSeenAnHourAgoDoesNotOutliveTheData() {
            Mode(ExternalMonitorMode.Or);
            DeviceSays(true);
            Analysis(20, rain: true);
            _monitor.IsSafe.Should().BeFalse();

            _clock = _clock.AddMinutes(60);     // camera gone since; the sensor says clear
            _monitor.IsSafe.Should().BeTrue("a rain reading from an hour ago says nothing about now");
            _monitor.SafetyStateReason.Should().Be("Safe on the external monitor alone — sky data is stale");
        }

        [Test]
        public void And_TheCameraDies_ImagingStopsWhateverTheSensorSays() {
            Mode(ExternalMonitorMode.And);
            DeviceSays(true);
            Analysis(10);
            _clock = _clock.AddMinutes(20);

            _monitor.IsSafe.Should().BeFalse();
            _monitor.SafetyStateReason.Should().StartWith("Stale data: last analysis 20 min ago, limit 15 min");
        }

        [Test]
        public void Or_TheIrDriverStopsAnswering_TheSkyAloneDecides() {
            Mode(ExternalMonitorMode.Or);
            DeviceSays(true);
            Analysis(10);
            _monitor.IsSafe.Should().BeTrue();

            _device.Readable = false;           // the COM driver answers no more
            _clock = _clock.AddSeconds(6);
            _monitor.IsSafe.Should().BeTrue();
            _monitor.SafetyStateReason.Should().Be("Safe on the sky analysis alone — external monitor unsafe or unreadable");

            Analysis(70);                       // clouds over the target
            _monitor.IsSafe.Should().BeFalse();
            _monitor.SafetyStateReason.Should().Be("Sky cloud coverage 70% (safe below 30%); external safety monitor cannot be connected or read");
            StatusFile.Should().Be("Unsafe");
        }

        [Test]
        public void Or_TheIrDriverComesBack_AfterTheReconnectPause() {
            Mode(ExternalMonitorMode.Or);
            DeviceSays(true);
            Analysis(70);                       // sky unsafe: the device carries the verdict
            _monitor.IsSafe.Should().BeTrue();

            _device.Readable = false;
            _clock = _clock.AddSeconds(6);
            _monitor.IsSafe.Should().BeFalse("both sides are unsafe or unknown");

            _device.Readable = true;
            _clock = _clock.AddSeconds(6);
            _monitor.IsSafe.Should().BeFalse("reconnects are rate-limited to one every 30 s");

            _clock = _clock.AddSeconds(31);
            _monitor.IsSafe.Should().BeTrue("the driver answers again and says clear");
        }

        [Test]
        public void And_TheIrDriverStopsAnswering_ImagingStops() {
            Mode(ExternalMonitorMode.And);
            DeviceSays(true);
            Analysis(10);
            _device.Readable = false;
            _clock = _clock.AddSeconds(6);

            _monitor.IsSafe.Should().BeFalse();
            _monitor.SafetyStateReason.Should().Be("External safety monitor cannot be connected or read");
        }

        [Test]
        public void Or_CameraAndDriverBothDead_ImagingStops() {
            Mode(ExternalMonitorMode.Or);
            DeviceSays(true);
            Analysis(10);
            _device.Readable = false;
            _clock = _clock.AddMinutes(20);

            _monitor.IsSafe.Should().BeFalse("nothing is known about the sky from either side");
            _monitor.SafetyStateReason.Should().Be("Sky data is stale; external safety monitor cannot be connected or read");
        }

        [Test]
        public void Or_HysteresisStillGovernsTheSkySide() {
            Mode(ExternalMonitorMode.Or);
            DeviceSays(false);                  // the sensor cries cloudy all night
            Analysis(20); _monitor.IsSafe.Should().BeTrue();
            Analysis(55); _monitor.IsSafe.Should().BeFalse("above the high threshold");
            Analysis(40); _monitor.IsSafe.Should().BeFalse("between the thresholds the sky side keeps its last state");
            Analysis(25); _monitor.IsSafe.Should().BeTrue("below the low threshold");
            StatusFile.Should().Be("Safe");
        }

        [Test]
        public void SwitchingTheModeTakesEffectAtOnce() {
            Mode(ExternalMonitorMode.Or);
            DeviceSays(false);
            Analysis(10);
            _monitor.IsSafe.Should().BeTrue();

            Mode(ExternalMonitorMode.And);
            _monitor.IsSafe.Should().BeFalse("no new analysis is needed for the mode to apply");

            Mode(ExternalMonitorMode.Or);
            _monitor.IsSafe.Should().BeTrue();
        }

        [Test]
        public void WithoutAnExternalMonitorTheModeIsIrrelevant() {
            global::AIWeather.Properties.Settings.Default.UseAscomSafetyMonitor = false;
            Mode(ExternalMonitorMode.Or);
            Analysis(70);
            _monitor.IsSafe.Should().BeFalse();
            _monitor.SafetyStateReason.Should().Be("Sky cloud coverage 70% (safe below 30%)");

            Analysis(10);
            _monitor.IsSafe.Should().BeTrue();
            _monitor.SafetyStateReason.Should().Be("Sky clear and data current");
        }

        [Test]
        public void Or_BeforeTheFirstAnalysis_TheSensorAloneDecides() {
            // Just connected: no sky verdict yet. In OR mode the device can already say safe.
            Mode(ExternalMonitorMode.Or);
            DeviceSays(true);
            _monitor.IsSafe.Should().BeTrue();
            _monitor.SafetyStateReason.Should().Be("Waiting for the first sky analysis");

            DeviceSays(false);
            _monitor.IsSafe.Should().BeFalse();
        }
    }
}
