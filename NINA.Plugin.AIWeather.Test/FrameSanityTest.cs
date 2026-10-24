using System;
using System.Drawing;
using AIWeather.Services;
using FluentAssertions;
using NUnit.Framework;

namespace NINA.Plugin.AIWeather.Test
{
    /// <summary>
    /// A frame with its bottom half smeared by a stream reconnect was analysed and scored
    /// "Clear 5%" (#21). A decoder repeats rows when it loses data; a camera never does.
    /// </summary>
    [TestFixture]
    public class FrameSanityTest
    {
        private static Bitmap Noise(int w, int h, int seed) {
            var bmp = new Bitmap(w, h);
            var rng = new Random(seed);
            for (var y = 0; y < h; y++)
                for (var x = 0; x < w; x++)
                {
                    var v = 20 + rng.Next(40);
                    bmp.SetPixel(x, y, Color.FromArgb(v, v + rng.Next(10), v + rng.Next(10)));
                }
            return bmp;
        }

        [Test]
        public void ANoisySkyIsNotCorrupt() {
            using var frame = Noise(160, 120, 1);
            FrameSanity.LooksCorrupt(frame, out var reason).Should().BeFalse(reason);
        }

        [Test]
        public void AGradientIsNotCorrupt() {
            using var frame = new Bitmap(160, 120);
            for (var y = 0; y < 120; y++)
                for (var x = 0; x < 160; x++)
                    frame.SetPixel(x, y, Color.FromArgb(y * 2, y * 2, y * 2 + 10));
            FrameSanity.LooksCorrupt(frame, out var reason).Should().BeFalse(reason);
        }

        [Test]
        public void ASmearedBottomHalfIsCorrupt() {
            using var frame = Noise(160, 120, 2);
            // The decoder repeats the last good row over the lost half of the picture.
            for (var y = 60; y < 120; y++)
                for (var x = 0; x < 160; x++)
                    frame.SetPixel(x, y, frame.GetPixel(x, 59));

            FrameSanity.LooksCorrupt(frame, out var reason).Should().BeTrue();
            reason.Should().Contain("% of its rows are repeated");
        }

        [Test]
        public void AShortRepeatedBandIsTolerated() {
            // A flat-painted dome edge or an overlay bar at the bottom is a few rows, not half a frame.
            using var frame = Noise(160, 120, 3);
            for (var y = 110; y < 120; y++)
                for (var x = 0; x < 160; x++)
                    frame.SetPixel(x, y, Color.FromArgb(5, 5, 5));
            FrameSanity.LooksCorrupt(frame, out var reason).Should().BeFalse(reason);
        }
    }
}
