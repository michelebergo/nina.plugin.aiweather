using System;
using System.Drawing;
using System.Drawing.Imaging;
using System.Runtime.InteropServices;

namespace AIWeather.Services
{
    /// <summary>
    /// A cheap identity for a captured frame, so two consecutive captures can be told
    /// apart. A live sky never produces the same pixels twice; a decoder that has stalled
    /// and keeps handing back its last frame does, every cycle. The verdict is not changed
    /// on this - it is a diagnostic first, so the log can show whether a frozen stream is
    /// real before anything acts on it.
    /// </summary>
    [System.Runtime.Versioning.SupportedOSPlatform("windows")]
    public static class FrameFingerprint
    {
        private const ulong FnvOffset = 14695981039346656037UL;
        private const ulong FnvPrime = 1099511628211UL;

        /// <summary>FNV-1a over the frame's pixel bytes, size included.</summary>
        public static ulong Compute(Bitmap frame)
        {
            if (frame == null) { throw new ArgumentNullException(nameof(frame)); }

            var rect = new Rectangle(0, 0, frame.Width, frame.Height);
            var data = frame.LockBits(rect, ImageLockMode.ReadOnly, PixelFormat.Format24bppRgb);
            try
            {
                var hash = FnvOffset;
                hash = Mix(hash, (ulong)frame.Width);
                hash = Mix(hash, (ulong)frame.Height);

                var rowBytes = frame.Width * 3;
                var row = new byte[rowBytes];
                for (var y = 0; y < frame.Height; y++)
                {
                    Marshal.Copy(data.Scan0 + y * data.Stride, row, 0, rowBytes);
                    foreach (var b in row)
                    {
                        hash ^= b;
                        hash *= FnvPrime;
                    }
                }

                return hash;
            }
            finally
            {
                frame.UnlockBits(data);
            }
        }

        private static ulong Mix(ulong hash, ulong value)
        {
            for (var i = 0; i < 8; i++)
            {
                hash ^= (value >> (8 * i)) & 0xFF;
                hash *= FnvPrime;
            }
            return hash;
        }
    }
}
