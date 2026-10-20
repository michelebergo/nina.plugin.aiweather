using System;
using System.IO;
using System.Threading.Tasks;
using AIWeather.Services;
using FluentAssertions;
using NUnit.Framework;

namespace NINA.Plugin.AIWeather.Test
{
    /// <summary>
    /// A crop script that deletes the image on purpose when the telescope points outside
    /// the all-sky field got "Failed to capture frame" in the panel, as if something broke.
    /// The capture now says why it has nothing (#19).
    /// </summary>
    [TestFixture]
    public class FolderWatchCaptureTest
    {
        [Test]
        public async Task AnEmptyFolderSaysSo() {
            var folder = Path.Combine(Path.GetTempPath(), "aiweather-empty-" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(folder);
            try
            {
                var capture = new FolderWatchCapture { FolderPath = folder };
                (await capture.CaptureImageAsync()).Should().BeNull();
                capture.LastFailure.Should().Be($"No image in folder {folder}");
            }
            finally { Directory.Delete(folder, true); }
        }

        [Test]
        public async Task AMissingFolderSaysSo() {
            var folder = Path.Combine(Path.GetTempPath(), "aiweather-missing-" + Guid.NewGuid().ToString("N"));
            var capture = new FolderWatchCapture { FolderPath = folder };
            (await capture.CaptureImageAsync()).Should().BeNull();
            capture.LastFailure.Should().Be($"Folder does not exist: {folder}");
        }
    }
}
