using System;
using System.Drawing;
using System.IO;
using System.Threading;
using System.Threading.Tasks;
using AIWeather.Services;
using FluentAssertions;
using NUnit.Framework;

namespace NINA.Plugin.AIWeather.Test
{
    /// <summary>
    /// The preview reloads the newest capture_*.jpg every 5 s and sometimes caught it while the
    /// JPEG was still being written: "cannot access the file ... being used by another process"
    /// (#21, six times in one night). A capture must appear under its final name only once it
    /// is complete.
    /// </summary>
    [TestFixture]
    public class CaptureFileHandoffTest
    {
        private string _folder = null!;

        [SetUp]
        public void SetUp() {
            _folder = Path.Combine(Path.GetTempPath(), "aiweather-handoff-" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(_folder);
        }

        [TearDown]
        public void TearDown() {
            try { Directory.Delete(_folder, true); } catch { }
        }

        [Test]
        public async Task ACaptureIsReadableTheMomentItAppearsUnderItsName() {
            using var frame = new Bitmap(4000, 3000);
            using (var g = Graphics.FromImage(frame)) g.Clear(Color.DarkSlateGray);
            var service = new RtspCaptureService();

            var halfWritten = 0;
            using var done = new CancellationTokenSource();
            var reader = Task.Run(() =>
            {
                while (!done.IsCancellationRequested)
                {
                    foreach (var path in Directory.GetFiles(_folder, "capture_*.jpg"))
                    {
                        try
                        {
                            using var s = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read);
                        }
                        catch (IOException)
                        {
                            halfWritten++;
                        }
                    }
                }
            });

            // Every check writes a new name, as the monitor does with its timestamp.
            for (var i = 0; i < 10; i++)
            {
                var path = Path.Combine(_folder, $"capture_20261025_0114{i:00}.jpg");
                (await service.SaveFrameAsync(frame, path)).Should().BeTrue();
            }
            done.Cancel();
            await reader;

            halfWritten.Should().Be(0, "the preview must never see a capture that is still being written");
            Directory.GetFiles(_folder).Should().HaveCount(10, "no temporary file may be left behind");
        }
    }
}
