using System;
using System.Threading;
using AIWeather.Equipment;
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

        private (string provider, string key, string model) _before;
        private AIWeatherSafetyMonitor _monitor = null!;

        [SetUp]
        public void SetUp() {
            var s = global::AIWeather.Properties.Settings.Default;
            _before = (s.AnalysisProvider, s.GeminiKey, s.SelectedModel);
            s.AnalysisProvider = "Gemini";
            s.GeminiKey = "not-a-real-key";
            s.SelectedModel = "gemini-flash-latest";
            _monitor = new AIWeatherSafetyMonitor(new Idle(), new Idle());
        }

        [TearDown]
        public void TearDown() {
            var s = global::AIWeather.Properties.Settings.Default;
            (s.AnalysisProvider, s.GeminiKey, s.SelectedModel) = _before;
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
