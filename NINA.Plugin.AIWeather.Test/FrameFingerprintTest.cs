using System.Drawing;
using AIWeather.Services;
using FluentAssertions;
using NUnit.Framework;

namespace NINA.Plugin.AIWeather.Test
{
    /// <summary>
    /// Two and a half hours of "34.1% cloud coverage" at twilight, identical to the decimal
    /// on thirty consecutive captures, before the decoder started returning empty frames
    /// outright. A live sky does not do that; a stalled decoder handing back its last frame
    /// does. The fingerprint is how the plugin can tell the two apart.
    /// </summary>
    [TestFixture]
    public class FrameFingerprintTest
    {
        private static Bitmap Frame(int width, int height, Color fill) {
            var bitmap = new Bitmap(width, height);
            using var g = Graphics.FromImage(bitmap);
            g.Clear(fill);
            return bitmap;
        }

        [Test]
        public void TheSamePixelsGiveTheSameFingerprint() {
            using var a = Frame(64, 48, Color.FromArgb(20, 30, 60));
            using var b = Frame(64, 48, Color.FromArgb(20, 30, 60));

            FrameFingerprint.Compute(a).Should().Be(FrameFingerprint.Compute(b));
        }

        [Test]
        public void OneChangedPixelChangesTheFingerprint() {
            using var a = Frame(64, 48, Color.FromArgb(20, 30, 60));
            using var b = Frame(64, 48, Color.FromArgb(20, 30, 60));
            b.SetPixel(17, 23, Color.FromArgb(21, 30, 60));

            FrameFingerprint.Compute(a).Should().NotBe(FrameFingerprint.Compute(b));
        }

        [Test]
        public void ADifferentSizeIsADifferentFrameEvenWhenBlank() {
            using var a = Frame(64, 48, Color.Black);
            using var b = Frame(48, 64, Color.Black);

            FrameFingerprint.Compute(a).Should().NotBe(FrameFingerprint.Compute(b));
        }

        [Test]
        public void TheFingerprintDoesNotDependOnTheBitmapInstance() {
            using var a = Frame(32, 32, Color.DarkSlateBlue);
            using var copy = new Bitmap(a);

            FrameFingerprint.Compute(copy).Should().Be(FrameFingerprint.Compute(a));
        }
    }
}
