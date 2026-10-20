using System;
using System.Drawing;
using System.Drawing.Imaging;
using System.Globalization;
using System.IO;
using System.Text;
using AIWeather.Models;
using NINA.Core.Utility;

namespace AIWeather.Services
{
    /// <summary>What was decided, and on what, written next to the frame.</summary>
    public sealed class DecisionRecord
    {
        public string Transition { get; set; } = string.Empty;
        public DateTime Utc { get; set; }
        public string Provider { get; set; } = string.Empty;
        public string Model { get; set; } = string.Empty;
        public WeatherCondition Condition { get; set; }
        public double CloudCoverage { get; set; }
        public double Confidence { get; set; }
        public bool RainDetected { get; set; }
        public bool FogDetected { get; set; }
        public string Description { get; set; } = string.Empty;
        public double HighThreshold { get; set; }
        public double LowThreshold { get; set; }
        public bool SkySafe { get; set; }
        public string ExternalMonitor { get; set; } = string.Empty;
        public string EnvironmentMonitor { get; set; } = string.Empty;
        public bool CombinedSafe { get; set; }
        public string Reason { get; set; } = string.Empty;
    }

    /// <summary>
    /// The frame behind each change of state, kept with the decision written next to it.
    /// An Unsafe alert at 01:21 is indefensible the next morning if the analysed frame has
    /// already left the rolling capture folder; with this, the owner can put the frame
    /// and the verdict side by side and say whether the AI was wrong (#21). Only changes
    /// are kept - an overcast night is one decision, not a hundred copies of it - and
    /// files older than the retention are removed on the next save.
    /// </summary>
    public sealed class DecisionArchive
    {
        private const string Prefix = "decision_";

        public DecisionArchive(string folder)
        {
            Folder = folder;
        }

        public string Folder { get; }

        /// <summary>
        /// Writes the explanation and, when there is one, the frame. Returns the image path,
        /// or null when no frame was available. Never throws: a full disk must not stop a
        /// safety monitor.
        /// </summary>
        public string? Save(Bitmap? frame, DecisionRecord record, DateTime localTimestamp)
        {
            try
            {
                Directory.CreateDirectory(Folder);
                var stem = Path.Combine(Folder, Prefix + localTimestamp.ToString("yyyyMMdd_HHmmss", CultureInfo.InvariantCulture));
                File.WriteAllText(stem + ".txt", Describe(record, localTimestamp));

                if (frame == null)
                {
                    return null;
                }
                frame.Save(stem + ".jpg", ImageFormat.Jpeg);
                return stem + ".jpg";
            }
            catch (Exception ex)
            {
                Logger.Warning($"Could not keep the decision frame: {ex.Message}");
                return null;
            }
        }

        /// <summary>Removes the archive's own files older than the retention; returns how many.</summary>
        public int Prune(TimeSpan retention, DateTime now)
        {
            try
            {
                if (!Directory.Exists(Folder)) return 0;
                var removed = 0;
                foreach (var file in Directory.GetFiles(Folder, Prefix + "*"))
                {
                    if (now - File.GetLastWriteTime(file) > retention)
                    {
                        File.Delete(file);
                        removed++;
                    }
                }
                return removed;
            }
            catch (Exception ex)
            {
                Logger.Warning($"Could not prune the decision archive: {ex.Message}");
                return 0;
            }
        }

        public static string Describe(DecisionRecord r, DateTime localTimestamp)
        {
            var sb = new StringBuilder();
            sb.AppendLine($"Decision: {r.Transition}");
            sb.AppendLine($"Time: {localTimestamp:yyyy-MM-dd HH:mm:ss} local ({r.Utc:yyyy-MM-dd HH:mm:ss} UTC)");
            sb.AppendLine($"Provider: {r.Provider}{(string.IsNullOrWhiteSpace(r.Model) ? string.Empty : $" ({r.Model})")}");
            sb.AppendLine($"Condition: {r.Condition}");
            sb.AppendLine($"Cloud coverage: {r.CloudCoverage:F0}%");
            sb.AppendLine($"Confidence: {r.Confidence:F0}%");
            sb.AppendLine($"Rain: {(r.RainDetected ? "yes" : "no")}, fog: {(r.FogDetected ? "yes" : "no")}");
            sb.AppendLine($"Description: {r.Description}");
            sb.AppendLine($"Thresholds: unsafe at {r.HighThreshold:F0}%, safe below {r.LowThreshold:F0}%");
            sb.AppendLine($"Sky: {(r.SkySafe ? "Safe" : "Unsafe")}");
            sb.AppendLine($"External monitor: {r.ExternalMonitor}");
            sb.AppendLine($"Environment monitor: {r.EnvironmentMonitor}");
            sb.AppendLine($"Combined: {(r.CombinedSafe ? "Safe" : "Unsafe")}");
            sb.AppendLine($"Reason: {r.Reason}");
            return sb.ToString();
        }
    }
}
