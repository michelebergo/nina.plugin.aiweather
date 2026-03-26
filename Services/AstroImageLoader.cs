using System;
using System.Drawing;
using System.Drawing.Imaging;
using System.IO;
using System.Runtime.InteropServices;
using System.Text;
using NINA.Core.Utility;

namespace AIWeather.Services
{
    /// <summary>
    /// Handles loading FITS files and normalizing raw TIFF images for AI analysis.
    /// Supports 8-bit, 16-bit, and 32-bit float FITS data with optional debayering.
    /// </summary>
    public static class AstroImageLoader
    {
        private static readonly string[] FitsExtensions = { ".fit", ".fits", ".fts" };
        private static readonly string[] TiffExtensions = { ".tif", ".tiff" };

        public static bool IsFitsFile(string path)
        {
            var ext = Path.GetExtension(path)?.ToLowerInvariant();
            return Array.IndexOf(FitsExtensions, ext) >= 0;
        }

        public static bool IsTiffFile(string path)
        {
            var ext = Path.GetExtension(path)?.ToLowerInvariant();
            return Array.IndexOf(TiffExtensions, ext) >= 0;
        }

        /// <summary>
        /// Loads a FITS file and returns a standard 8-bit Bitmap suitable for AI vision analysis.
        /// Parses the FITS header for dimensions, bit depth, and optional Bayer pattern.
        /// </summary>
        public static Bitmap LoadFitsFile(string path)
        {
            Logger.Info($"AstroImageLoader: loading FITS file {Path.GetFileName(path)}");

            byte[] fileBytes = File.ReadAllBytes(path);

            // Parse FITS header
            int width = 0, height = 0, bitpix = 0;
            string bayerPat = null;
            int headerEnd = ParseFitsHeader(fileBytes, out width, out height, out bitpix, out bayerPat);

            if (width <= 0 || height <= 0)
                throw new InvalidDataException($"Invalid FITS dimensions: {width}x{height}");

            Logger.Info($"AstroImageLoader: FITS {width}x{height}, BITPIX={bitpix}, BAYERPAT={bayerPat ?? "none"}");

            // Read pixel data into double array (normalized 0..1)
            double[] pixels = ReadPixelData(fileBytes, headerEnd, width, height, bitpix);

            // Auto-stretch
            AutoStretch(pixels);

            // Debayer if pattern is present (produces RGB), otherwise grayscale
            Bitmap result;
            if (!string.IsNullOrEmpty(bayerPat))
            {
                result = DebayerToColor(pixels, width, height, bayerPat);
            }
            else
            {
                result = GrayscaleToBitmap(pixels, width, height);
            }

            return result;
        }

        /// <summary>
        /// Normalizes a TIFF image that may be 16-bit or 32-bit (raw astro camera output).
        /// If the TIFF is already 8-bit/24-bit, returns the bitmap as-is.
        /// </summary>
        public static Bitmap NormalizeTiff(Bitmap source)
        {
            // Check if the image is already in a standard 8-bit format
            if (source.PixelFormat == PixelFormat.Format24bppRgb ||
                source.PixelFormat == PixelFormat.Format32bppArgb ||
                source.PixelFormat == PixelFormat.Format8bppIndexed)
            {
                return source;
            }

            Logger.Info($"AstroImageLoader: normalizing TIFF, pixel format={source.PixelFormat}");

            int width = source.Width;
            int height = source.Height;

            // Lock bitmap data for direct pixel access
            var rect = new Rectangle(0, 0, width, height);
            BitmapData bmpData = source.LockBits(rect, ImageLockMode.ReadOnly, source.PixelFormat);

            try
            {
                int bytesPerPixel = Image.GetPixelFormatSize(source.PixelFormat) / 8;
                int stride = bmpData.Stride;
                byte[] rawData = new byte[Math.Abs(stride) * height];
                Marshal.Copy(bmpData.Scan0, rawData, 0, rawData.Length);

                double[] pixels;

                if (source.PixelFormat == PixelFormat.Format16bppGrayScale)
                {
                    // 16-bit grayscale
                    pixels = new double[width * height];
                    for (int y = 0; y < height; y++)
                    {
                        int rowOffset = y * Math.Abs(stride);
                        for (int x = 0; x < width; x++)
                        {
                            int idx = rowOffset + x * 2;
                            ushort val = BitConverter.ToUInt16(rawData, idx);
                            pixels[y * width + x] = val / 65535.0;
                        }
                    }
                }
                else if (source.PixelFormat == PixelFormat.Format48bppRgb)
                {
                    // 48-bit RGB → convert to grayscale luminance
                    pixels = new double[width * height];
                    for (int y = 0; y < height; y++)
                    {
                        int rowOffset = y * Math.Abs(stride);
                        for (int x = 0; x < width; x++)
                        {
                            int idx = rowOffset + x * 6;
                            ushort b = BitConverter.ToUInt16(rawData, idx);
                            ushort g = BitConverter.ToUInt16(rawData, idx + 2);
                            ushort r = BitConverter.ToUInt16(rawData, idx + 4);
                            // Luminance weighting
                            pixels[y * width + x] = (0.2126 * r + 0.7152 * g + 0.0722 * b) / 65535.0;
                        }
                    }
                }
                else
                {
                    // Unknown format — just return original
                    Logger.Warning($"AstroImageLoader: unsupported TIFF pixel format {source.PixelFormat}, returning as-is");
                    return source;
                }

                // Auto-stretch and convert to 8-bit grayscale bitmap
                AutoStretch(pixels);
                return GrayscaleToBitmap(pixels, width, height);
            }
            finally
            {
                source.UnlockBits(bmpData);
            }
        }

        #region FITS Header Parsing

        /// <summary>
        /// Parses FITS header blocks (2880-byte aligned, 80-char keyword cards).
        /// Returns the byte offset where pixel data begins.
        /// </summary>
        private static int ParseFitsHeader(byte[] data, out int width, out int height, out int bitpix, out string bayerPat)
        {
            width = 0;
            height = 0;
            bitpix = 0;
            bayerPat = null;

            int offset = 0;
            bool endFound = false;

            while (offset < data.Length && !endFound)
            {
                // Each header block is 2880 bytes = 36 cards of 80 chars
                int blockEnd = Math.Min(offset + 2880, data.Length);

                for (int cardStart = offset; cardStart < blockEnd; cardStart += 80)
                {
                    if (cardStart + 80 > data.Length) break;

                    string card = Encoding.ASCII.GetString(data, cardStart, 80);

                    if (card.StartsWith("END     ") || card.TrimEnd() == "END")
                    {
                        endFound = true;
                        break;
                    }

                    string keyword = card.Substring(0, 8).Trim();
                    string valueStr = null;

                    if (card.Length > 10 && card[8] == '=' && card[9] == ' ')
                    {
                        // Value field starts at column 10, comment after '/'
                        string raw = card.Substring(10);
                        int slashIdx = raw.IndexOf('/');
                        valueStr = (slashIdx >= 0 ? raw.Substring(0, slashIdx) : raw).Trim();

                        // Remove surrounding quotes for string values
                        if (valueStr.StartsWith("'") && valueStr.EndsWith("'"))
                            valueStr = valueStr.Substring(1, valueStr.Length - 2).Trim();
                    }

                    if (valueStr == null) continue;

                    switch (keyword)
                    {
                        case "NAXIS1":
                            int.TryParse(valueStr, out width);
                            break;
                        case "NAXIS2":
                            int.TryParse(valueStr, out height);
                            break;
                        case "BITPIX":
                            int.TryParse(valueStr, out bitpix);
                            break;
                        case "BAYERPAT":
                            bayerPat = valueStr.ToUpperInvariant();
                            break;
                    }
                }

                // Advance to next 2880-byte block
                offset += 2880;
            }

            // Pixel data starts at the next 2880-byte aligned offset after END
            return offset;
        }

        #endregion

        #region Pixel Data Reading

        /// <summary>
        /// Reads FITS pixel data into a normalized double array (0..1).
        /// FITS stores data in big-endian byte order.
        /// </summary>
        private static double[] ReadPixelData(byte[] data, int offset, int width, int height, int bitpix)
        {
            int pixelCount = width * height;
            double[] pixels = new double[pixelCount];

            switch (bitpix)
            {
                case 8:
                    for (int i = 0; i < pixelCount && offset + i < data.Length; i++)
                    {
                        pixels[i] = data[offset + i] / 255.0;
                    }
                    break;

                case 16:
                    for (int i = 0; i < pixelCount; i++)
                    {
                        int idx = offset + i * 2;
                        if (idx + 1 >= data.Length) break;
                        // Big-endian signed 16-bit
                        short val = (short)((data[idx] << 8) | data[idx + 1]);
                        // FITS 16-bit values are signed (-32768..32767), shift to unsigned range
                        pixels[i] = (val + 32768.0) / 65535.0;
                    }
                    break;

                case 32:
                    for (int i = 0; i < pixelCount; i++)
                    {
                        int idx = offset + i * 4;
                        if (idx + 3 >= data.Length) break;
                        // Big-endian signed 32-bit integer
                        int val = (data[idx] << 24) | (data[idx + 1] << 16) | (data[idx + 2] << 8) | data[idx + 3];
                        pixels[i] = (val + 2147483648.0) / 4294967295.0;
                    }
                    break;

                case -32:
                    // IEEE 754 single-precision float, big-endian
                    byte[] floatBuf = new byte[4];
                    for (int i = 0; i < pixelCount; i++)
                    {
                        int idx = offset + i * 4;
                        if (idx + 3 >= data.Length) break;
                        // Reverse byte order (big-endian → little-endian)
                        floatBuf[0] = data[idx + 3];
                        floatBuf[1] = data[idx + 2];
                        floatBuf[2] = data[idx + 1];
                        floatBuf[3] = data[idx];
                        float fval = BitConverter.ToSingle(floatBuf, 0);
                        pixels[i] = fval; // Will be stretched later
                    }
                    break;

                case -64:
                    // IEEE 754 double-precision float, big-endian
                    byte[] doubleBuf = new byte[8];
                    for (int i = 0; i < pixelCount; i++)
                    {
                        int idx = offset + i * 8;
                        if (idx + 7 >= data.Length) break;
                        for (int b = 0; b < 8; b++)
                            doubleBuf[7 - b] = data[idx + b];
                        pixels[i] = BitConverter.ToDouble(doubleBuf, 0);
                    }
                    break;

                default:
                    throw new NotSupportedException($"Unsupported FITS BITPIX: {bitpix}");
            }

            return pixels;
        }

        #endregion

        #region Auto Stretch

        /// <summary>
        /// Applies percentile-based auto-stretch (1st/99th percentile clipping with gamma correction).
        /// Modifies the array in place, mapping values to 0..1.
        /// </summary>
        private static void AutoStretch(double[] pixels)
        {
            if (pixels.Length == 0) return;

            // Sample up to 100k pixels for percentile calculation (performance)
            int sampleSize = Math.Min(pixels.Length, 100000);
            double[] sample = new double[sampleSize];
            int step = Math.Max(1, pixels.Length / sampleSize);
            for (int i = 0, s = 0; s < sampleSize && i < pixels.Length; i += step, s++)
            {
                sample[s] = pixels[i];
            }
            Array.Sort(sample);

            double low = sample[(int)(sampleSize * 0.01)];    // 1st percentile
            double high = sample[(int)(sampleSize * 0.99)];   // 99th percentile

            if (high <= low) high = low + 1.0;

            double range = high - low;
            double gamma = 1.0 / 2.2; // Gamma correction for visual display

            for (int i = 0; i < pixels.Length; i++)
            {
                double val = (pixels[i] - low) / range;
                val = Math.Max(0, Math.Min(1, val));
                pixels[i] = Math.Pow(val, gamma);
            }
        }

        #endregion

        #region Debayer

        /// <summary>
        /// Performs bilinear interpolation debayering on raw Bayer pattern data.
        /// Supports RGGB, BGGR, GRBG, GBRG patterns.
        /// </summary>
        private static Bitmap DebayerToColor(double[] pixels, int width, int height, string pattern)
        {
            // Determine color at each position in the 2x2 Bayer tile
            // Pattern string describes top-left 2x2: e.g., RGGB means:
            //   [0,0]=R  [1,0]=G
            //   [0,1]=G  [1,1]=B
            int rX, rY, b_X, b_Y;
            GetBayerOffsets(pattern, out rX, out rY, out b_X, out b_Y);

            var bmp = new Bitmap(width, height, PixelFormat.Format24bppRgb);
            var rect = new Rectangle(0, 0, width, height);
            BitmapData bmpData = bmp.LockBits(rect, ImageLockMode.WriteOnly, PixelFormat.Format24bppRgb);

            try
            {
                int stride = bmpData.Stride;
                byte[] rgb = new byte[stride * height];

                for (int y = 1; y < height - 1; y++)
                {
                    for (int x = 1; x < width - 1; x++)
                    {
                        double r, g, b;
                        int px = x % 2;
                        int py = y % 2;

                        double c = pixels[y * width + x];
                        double n = pixels[(y - 1) * width + x];
                        double s2 = pixels[(y + 1) * width + x];
                        double w = pixels[y * width + (x - 1)];
                        double e = pixels[y * width + (x + 1)];
                        double nw = pixels[(y - 1) * width + (x - 1)];
                        double ne = pixels[(y - 1) * width + (x + 1)];
                        double sw = pixels[(y + 1) * width + (x - 1)];
                        double se = pixels[(y + 1) * width + (x + 1)];

                        if (px == rX && py == rY)
                        {
                            // Red pixel
                            r = c;
                            g = (n + s2 + w + e) / 4.0;
                            b = (nw + ne + sw + se) / 4.0;
                        }
                        else if (px == b_X && py == b_Y)
                        {
                            // Blue pixel
                            b = c;
                            g = (n + s2 + w + e) / 4.0;
                            r = (nw + ne + sw + se) / 4.0;
                        }
                        else
                        {
                            // Green pixel - determine if red neighbors are horizontal or vertical
                            g = c;
                            if (py == rY)
                            {
                                // Red is in this row
                                r = (w + e) / 2.0;
                                b = (n + s2) / 2.0;
                            }
                            else
                            {
                                r = (n + s2) / 2.0;
                                b = (w + e) / 2.0;
                            }
                        }

                        int outIdx = y * stride + x * 3;
                        rgb[outIdx] = ToByte(b);
                        rgb[outIdx + 1] = ToByte(g);
                        rgb[outIdx + 2] = ToByte(r);
                    }
                }

                Marshal.Copy(rgb, 0, bmpData.Scan0, rgb.Length);
            }
            finally
            {
                bmp.UnlockBits(bmpData);
            }

            return bmp;
        }

        private static void GetBayerOffsets(string pattern, out int rX, out int rY, out int bX, out int bY)
        {
            // Default to RGGB
            rX = 0; rY = 0; bX = 1; bY = 1;

            switch (pattern)
            {
                case "RGGB":
                    rX = 0; rY = 0; bX = 1; bY = 1;
                    break;
                case "BGGR":
                    rX = 1; rY = 1; bX = 0; bY = 0;
                    break;
                case "GRBG":
                    rX = 1; rY = 0; bX = 0; bY = 1;
                    break;
                case "GBRG":
                    rX = 0; rY = 1; bX = 1; bY = 0;
                    break;
                default:
                    Logger.Warning($"AstroImageLoader: unknown Bayer pattern '{pattern}', defaulting to RGGB");
                    break;
            }
        }

        #endregion

        #region Bitmap Helpers

        /// <summary>
        /// Converts a normalized grayscale pixel array to an 8-bit RGB Bitmap.
        /// </summary>
        private static Bitmap GrayscaleToBitmap(double[] pixels, int width, int height)
        {
            var bmp = new Bitmap(width, height, PixelFormat.Format24bppRgb);
            var rect = new Rectangle(0, 0, width, height);
            BitmapData bmpData = bmp.LockBits(rect, ImageLockMode.WriteOnly, PixelFormat.Format24bppRgb);

            try
            {
                int stride = bmpData.Stride;
                byte[] rgb = new byte[stride * height];

                for (int y = 0; y < height; y++)
                {
                    for (int x = 0; x < width; x++)
                    {
                        byte val = ToByte(pixels[y * width + x]);
                        int idx = y * stride + x * 3;
                        rgb[idx] = val;     // B
                        rgb[idx + 1] = val; // G
                        rgb[idx + 2] = val; // R
                    }
                }

                Marshal.Copy(rgb, 0, bmpData.Scan0, rgb.Length);
            }
            finally
            {
                bmp.UnlockBits(bmpData);
            }

            return bmp;
        }

        private static byte ToByte(double value)
        {
            int v = (int)(value * 255.0 + 0.5);
            return (byte)(v < 0 ? 0 : v > 255 ? 255 : v);
        }

        #endregion
    }
}
