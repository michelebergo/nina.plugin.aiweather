using System;
using System.IO;

namespace AIWeather.Services
{
    /// <summary>
    /// Where the Safe/Unsafe status file goes. The settings default used to be the
    /// author's own Documents folder, baked in by the designer, which exists on exactly
    /// one PC (#19). Nothing configured now means the current user's Documents.
    /// </summary>
    public static class SafetyStatusFile
    {
        /// <summary>The path shipped in the designer default up to 1.15.11.0; treated as "not configured".</summary>
        private const string LegacyBakedInPath = @"C:\Users\miche\Documents\N.I.N.A\sky_conditions.txt";

        public static string DefaultPath =>
            Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.MyDocuments), "N.I.N.A", "sky_conditions.txt");

        public static string Resolve(string? configured)
        {
            if (string.IsNullOrWhiteSpace(configured)
                || string.Equals(configured.Trim(), LegacyBakedInPath, StringComparison.OrdinalIgnoreCase))
            {
                return DefaultPath;
            }
            return configured.Trim();
        }
    }
}
