using AIWeather.Models;
using NINA.Core.Utility;
using System;
using System.IO;
using System.Text;

namespace AIWeather.Services
{
    /// <summary>
    /// Writes an append-only daily weather digest into the shared NINA LLM wiki
    /// (raw/aiweather-YYYY-MM-DD.md). Only condition or safety CHANGES are recorded,
    /// so a night produces a compact timeline instead of one line per polling cycle.
    /// The ingest agent later consolidates these files into site pages; this writer
    /// never touches consolidated wiki pages.
    /// </summary>
    public static class LlmWikiRawWriter
    {
        private static readonly object Sync = new object();
        private static WeatherCondition? _lastCondition;
        private static bool? _lastSafe;
        private static int _lastCoverageBucket = -1;

        private static string WikiRoot =>
            Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "NINA", "llmwiki");

        /// <summary>
        /// Records the analysis result if it represents a state change. Never throws.
        /// </summary>
        public static void RecordAnalysis(WeatherAnalysisResult result)
        {
            try
            {
                lock (Sync)
                {
                    // Coverage is only interesting in coarse steps (20% buckets),
                    // otherwise noise around a threshold floods the digest.
                    var coverageBucket = (int)(Math.Clamp(result.CloudCoverage, 0, 100) / 20);
                    var changed = result.Condition != _lastCondition
                                  || result.IsSafeForImaging != _lastSafe
                                  || coverageBucket != _lastCoverageBucket;
                    if (!changed)
                    {
                        return;
                    }

                    _lastCondition = result.Condition;
                    _lastSafe = result.IsSafeForImaging;
                    _lastCoverageBucket = coverageBucket;

                    var rawDir = Path.Combine(WikiRoot, "raw");
                    Directory.CreateDirectory(rawDir);

                    var fileName = $"aiweather-{DateTime.Now:yyyy-MM-dd}.md";
                    var file = Path.Combine(rawDir, fileName);
                    var isNew = !File.Exists(file);

                    var sb = new StringBuilder();
                    if (isNew)
                    {
                        sb.AppendLine($"# aiweather — {DateTime.Now:yyyy-MM-dd}");
                        sb.AppendLine();
                        sb.AppendLine("Sky condition timeline from the AI Weather safety monitor (changes only).");
                        sb.AppendLine();
                    }

                    var flags = "";
                    if (result.RainDetected) flags += ", rain";
                    if (result.FogDetected) flags += ", fog";
                    sb.AppendLine($"- {DateTime.Now:HH:mm} — {result.Condition}, clouds {result.CloudCoverage:F0}%{flags}, {(result.IsSafeForImaging ? "SAFE" : "UNSAFE")}");

                    File.AppendAllText(file, sb.ToString());

                    if (isNew)
                    {
                        try
                        {
                            File.AppendAllText(Path.Combine(WikiRoot, "log.md"),
                                $"- {DateTime.Now:yyyy-MM-dd} — raw/{fileName} created by aiweather.{Environment.NewLine}");
                        }
                        catch
                        {
                            // best-effort
                        }
                    }
                }
            }
            catch (Exception ex)
            {
                Logger.Debug($"LlmWiki raw digest write skipped: {ex.Message}");
            }
        }
    }
}
