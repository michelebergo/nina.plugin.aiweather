using System;
using System.Drawing;
using System.Drawing.Imaging;
using System.Runtime.InteropServices;

namespace AIWeather.Services
{
    /// <summary>
    /// A frame that a stream reconnect left half smeared was analysed and scored
    /// "Clear 5%" (#21). A decoder that loses data repeats the last good row; a camera
    /// never produces long runs of identical rows, noise sees to that. A frame whose
    /// longest run of identical rows covers a quarter of its height is not a frame.
    /// </summary>
    public static class FrameSanity
    {
        /// <summary>Fraction of the height that identical consecutive rows may cover before the frame is rejected.</summary>
        public const double RepeatedRowsLimit = 0.25;

        public static bool LooksCorrupt(Bitmap frame, out string reason)
        {
            reason = string.Empty;
            if (frame.Width < 8 || frame.Height < 8) return false;

            var rect = new Rectangle(0, 0, frame.Width, frame.Height);
            var data = frame.LockBits(rect, ImageLockMode.ReadOnly, PixelFormat.Format24bppRgb);
            try
            {
                // Every fourth pixel of a row is enough to tell a repeated row from a real one.
                var samples = Math.Max(2, frame.Width / 4);
                var step = frame.Width / samples * 3;
                var previous = new byte[samples * 3];
                var current = new byte[samples * 3];
                var rowBytes = new byte[Math.Abs(data.Stride)];
                var longest = 0;
                var run = 0;

                for (var y = 0; y < frame.Height; y++)
                {
                    Marshal.Copy(data.Scan0 + y * data.Stride, rowBytes, 0, rowBytes.Length);
                    for (var i = 0; i < samples; i++)
                    {
                        Array.Copy(rowBytes, i * step, current, i * 3, 3);
                    }

                    if (y > 0 && ((ReadOnlySpan<byte>)current).SequenceEqual(previous))
                    {
                        run++;
                        if (run > longest) longest = run;
                    }
                    else
                    {
                        run = 0;
                    }
                    (previous, current) = (current, previous);
                }

                var fraction = (double)(longest + 1) / frame.Height;
                if (fraction >= RepeatedRowsLimit)
                {
                    reason = $"Frame looks corrupt: {fraction * 100:F0}% of its rows are repeated (a decoder smear, not a sky)";
                    return true;
                }
                return false;
            }
            finally
            {
                frame.UnlockBits(data);
            }
        }
    }
}
