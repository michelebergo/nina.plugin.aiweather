using System;
using System.IO;

namespace AIWeather.Services
{
    /// <summary>
    /// Helper for identifying astronomical image file formats.
    /// Actual loading is handled by NINA's native image pipeline (IImageDataFactory).
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
    }
}
