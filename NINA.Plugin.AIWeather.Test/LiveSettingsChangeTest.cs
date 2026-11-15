using System;
using System.IO;
using System.Threading;
using AIWeather.Equipment;
using AIWeather.Models;
using AIWeather.Services;
using FluentAssertions;
using NUnit.Framework;

namespace NINA.Plugin.AIWeather.Test
{
    /// <summary>
    /// A model changed in the options while the monitor was connected never reached the night:
    /// the service was rebuilt but not initialised, so every check logged "Gemini service not
    /// initialized" and ran on the offline analyzer until a reconnect (#21, 25-26/10).
    /// </summary>
    [TestFixture]
    public class LiveSettingsChangeTest
    {
        private sealed class Idle : IExternalSafetyMonitor
        {
            public string ProgId => string.Empty;
            public bool Connected => false;
            public bool TryConnect(string progId) => false;
            public bool TryGetIsSafe(out bool isSafe) { isSafe = true; return false; }
            public void Disconnect() { }
        }

        private (string provider, string key, string model, int interval, int mode, string folder) _before;
        private AIWeatherSafetyMonitor _monitor = null!;
        private string _emptyFolder = null!;

        [SetUp]
        public void SetUp() {
            var s = global::AIWeather.Properties.Settings.Default;
            _before = (s.AnalysisProvider, s.GeminiKey, s.SelectedModel, s.CheckIntervalMinutes, s.CaptureMode, s.FolderPath);
            s.AnalysisProvider = "Gemini";
            s.GeminiKey = "not-a-real-key";
            s.SelectedModel = "gemini-flash-latest";
            // A folder with nothing in it: a check that runs finds no image and changes nothing.
            _emptyFolder = Path.Combine(Path.GetTempPath(), "aiweather-interval-" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(_emptyFolder);
            s.CaptureMode = (int)CaptureMode.FolderWatch;
            s.FolderPath = _emptyFolder;
            _monitor = new AIWeatherSafetyMonitor(new Idle(), new Idle());
        }

        [TearDown]
        public void TearDown() {
            _monitor.StopPeriodicMonitoring();
            var s = global::AIWeather.Properties.Settings.Default;
            (s.AnalysisProvider, s.GeminiKey, s.SelectedModel, s.CheckIntervalMinutes, s.CaptureMode, s.FolderPath) = _before;
            try { Directory.Delete(_emptyFolder, true); } catch { }
        }

        [Test]
        public void ACheckIntervalChangedWhileConnectedAppliesAtOnce() {
            // #25: the interval was changed from 5 to 2 minutes and the checks kept coming every
            // 5 until a reconnect; the timer was born at connect and nobody told it.
            global::AIWeather.Properties.Settings.Default.CheckIntervalMinutes = 5;
            _monitor.StartPeriodicMonitoring();
            _monitor.CheckInterval.Should().Be(TimeSpan.FromMinutes(5));

            global::AIWeather.Properties.Settings.Default.CheckIntervalMinutes = 2;

            _monitor.CheckInterval.Should().Be(TimeSpan.FromMinutes(2));
        }

        [Test]
        public void TheRunningTimerFollowsTheNewInterval() {
            // Not just the number: the timer itself. Every check on the empty folder fails to
            // capture and announces IsSafe, so the announcements count the timer's ticks.
            global::AIWeather.Properties.Settings.Default.CheckIntervalMinutes = 5;
            var ticks = 0;
            _monitor.PropertyChanged += (s, e) => { if (e.PropertyName == nameof(AIWeatherSafetyMonitor.IsSafe)) Interlocked.Increment(ref ticks); };
            _monitor.StartPeriodicMonitoring();
            Thread.Sleep(500);
            var afterStart = ticks;
            afterStart.Should().BeGreaterThan(0, "the first check runs at connect");

            _monitor.ApplyCheckInterval(TimeSpan.FromMilliseconds(150));
            Thread.Sleep(1200);

            (ticks - afterStart).Should().BeGreaterThan(2, "with the timer reprogrammed the checks come every 150 ms instead of every 5 minutes");
        }

        private IWeatherAnalysisService Settled() {
            // The settings handler initialises the new service; give it a moment in case it ever
            // yields to the thread pool.
            var deadline = DateTime.UtcNow.AddSeconds(2);
            while (DateTime.UtcNow < deadline && _monitor.AnalysisService is GeminiAnalysisService { IsInitialized: false }) Thread.Sleep(20);
            return _monitor.AnalysisService;
        }

        [Test]
        public void AModelChangedWhileConnectedIsReadyForTheNextCheck() {
            global::AIWeather.Properties.Settings.Default.SelectedModel = "gemini-flash-lite-latest";

            Settled().Should().BeOfType<GeminiAnalysisService>().Which.IsInitialized.Should().BeTrue();
        }

        [Test]
        public void AKeyRemovedWhileConnectedHandsTheNightToTheOfflineAnalyzerOutright() {
            // Not a Gemini service that will decline every frame one by one.
            global::AIWeather.Properties.Settings.Default.GeminiKey = string.Empty;

            Settled().Should().BeOfType<LocalWeatherAnalysisService>();
        }
    }
}
