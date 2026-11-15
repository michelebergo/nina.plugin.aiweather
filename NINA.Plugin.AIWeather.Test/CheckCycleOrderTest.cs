using System;
using System.Drawing;
using System.Drawing.Imaging;
using System.IO;
using System.Threading;
using System.Threading.Tasks;
using AIWeather.Equipment;
using AIWeather.Models;
using AIWeather.Services;
using FluentAssertions;
using NUnit.Framework;

namespace NINA.Plugin.AIWeather.Test
{
    /// <summary>
    /// The panel showed the frame of the previous check (#25): the verdict was applied, the panel
    /// reloaded the newest capture on disk, and only then - 9 ms later in the field log - was the
    /// frame just analysed saved. The capture has to be on disk before anyone hears of the verdict.
    /// </summary>
    [TestFixture]
    public class CheckCycleOrderTest
    {
        private sealed class Idle : IExternalSafetyMonitor
        {
            public string ProgId => string.Empty;
            public bool Connected => false;
            public bool TryConnect(string progId) => false;
            public bool TryGetIsSafe(out bool isSafe) { isSafe = true; return false; }
            public void Disconnect() { }
        }

        private (int mode, string folder, bool ascom, bool env, bool confirm, bool keep, string provider) _before;
        private string _root = null!;

        [SetUp]
        public void SetUp() {
            var s = global::AIWeather.Properties.Settings.Default;
            _before = (s.CaptureMode, s.FolderPath, s.UseAscomSafetyMonitor, s.UseEnvironmentSafetyMonitor, s.ConfirmTransitions, s.KeepDecisionFrames, s.AnalysisProvider);
            _root = Path.Combine(Path.GetTempPath(), "aiweather-order-" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(Path.Combine(_root, "sky"));
            s.CaptureMode = (int)CaptureMode.FolderWatch;
            s.FolderPath = Path.Combine(_root, "sky");
            s.UseAscomSafetyMonitor = false;
            s.UseEnvironmentSafetyMonitor = false;
            s.ConfirmTransitions = false;
            s.KeepDecisionFrames = false;
            s.AnalysisProvider = "Local";
        }

        [TearDown]
        public void TearDown() {
            var s = global::AIWeather.Properties.Settings.Default;
            (s.CaptureMode, s.FolderPath, s.UseAscomSafetyMonitor, s.UseEnvironmentSafetyMonitor, s.ConfirmTransitions, s.KeepDecisionFrames, s.AnalysisProvider) = _before;
            try { Directory.Delete(_root, true); } catch { }
        }

        /// <summary>A dark frame with a sprinkle of stars: real enough for the sanity check and the offline analyzer.</summary>
        private static void WriteStarField(string path) {
            using var bmp = new Bitmap(160, 120, PixelFormat.Format24bppRgb);
            var rng = new Random(7);
            for (var y = 0; y < bmp.Height; y++)
                for (var x = 0; x < bmp.Width; x++)
                {
                    var v = rng.Next(8, 24);
                    bmp.SetPixel(x, y, Color.FromArgb(v, v, v));
                }
            for (var i = 0; i < 60; i++) bmp.SetPixel(rng.Next(bmp.Width), rng.Next(bmp.Height), Color.White);
            bmp.Save(path, ImageFormat.Png);
        }

        [Test]
        public async Task TheCaptureIsOnDiskBeforeTheVerdictIsAnnounced() {
            WriteStarField(Path.Combine(_root, "sky", "allsky.png"));
            var captures = Path.Combine(_root, "captures");
            var monitor = new AIWeatherSafetyMonitor(new Idle(), new Idle()) { CapturesFolder = captures };
            (await monitor.Connect(CancellationToken.None)).Should().BeTrue();
            monitor.StopPeriodicMonitoring();
            // Connecting runs a first check at once; let it finish (the check gate serialises us
            // behind it) and start from an empty capture folder.
            await monitor.PerformWeatherCheckAsync(CancellationToken.None);
            foreach (var f in Directory.GetFiles(captures, "capture_*.jpg")) File.Delete(f);

            // What the panel does on IsSafe: look for the newest capture on disk.
            var seenAtAnnouncement = -1;
            monitor.PropertyChanged += (s, e) =>
            {
                if (e.PropertyName == nameof(AIWeatherSafetyMonitor.IsSafe) && seenAtAnnouncement < 0)
                    seenAtAnnouncement = Directory.Exists(captures) ? Directory.GetFiles(captures, "capture_*.jpg").Length : 0;
            };

            await monitor.PerformWeatherCheckAsync(CancellationToken.None);

            Directory.GetFiles(captures, "capture_*.jpg").Should().HaveCount(1, "the analysed frame is kept");
            seenAtAnnouncement.Should().Be(1, "the panel reloads the newest capture when it hears the verdict, so the frame just analysed must already be there");
        }
    }
}
