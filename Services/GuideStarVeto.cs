using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;

namespace AIWeather.Services
{
    /// <summary>
    /// The guide star as a witness for the target (#24). Under a full moon the model read a
    /// clear sky as 75% cloud twice in one night and parked the rig, while PHD2's guide star
    /// sat at 100% of its clear-sky mass. A cloud has to cross the guide star to reach the
    /// target, and when it does the star mass collapses within a few frames. So, before a
    /// cloud-only Safe -> Unsafe change, the median star mass of the last minute is compared
    /// with the median of the ten minutes before it: still at or above the threshold, the
    /// call is held. Too few samples on either side - guiding stopped, just started, star
    /// lost - and there is no say: the plugin behaves as without the feature.
    ///
    /// An optional second vote from the weather device (sky quality, sky temperature, cloud
    /// cover) can only make the veto stricter: when it is on, the veto wants the guide star
    /// and the device reading both unchanged. A sensor looking at another part of the sky
    /// must never hold a Safe against a cloud that is on the target.
    /// </summary>
    public sealed class GuideStarVeto
    {
        public static readonly TimeSpan RecentWindow = TimeSpan.FromSeconds(60);
        public static readonly TimeSpan BaselineWindow = TimeSpan.FromMinutes(10);
        public const int MinimumRecentSamples = 5;
        public const int MinimumBaselineSamples = 20;

        private readonly object _lock = new object();
        private readonly List<(DateTime Utc, double StarMass, double? Weather)> _samples = new();

        /// <summary>One guide step: PHD2's star mass and, when the second vote is on, the device reading taken with it.</summary>
        public void Record(DateTime utc, double starMass, double? weather)
        {
            if (double.IsNaN(starMass) || double.IsInfinity(starMass)) return;
            lock (_lock)
            {
                _samples.Add((utc, starMass, weather));
                // Keep a little beyond the baseline so a clock that moves on a bit still has it.
                var cutoff = utc - BaselineWindow - TimeSpan.FromMinutes(1);
                _samples.RemoveAll(s => s.Utc < cutoff);
            }
        }

        /// <summary>Forget everything: a reconnect must not inherit last night's guide star.</summary>
        public void Clear()
        {
            lock (_lock) _samples.Clear();
        }

        /// <summary>
        /// Null when the guide star cannot speak (no guiding in the last minute, or less than
        /// ten minutes of it to compare with); otherwise the verdict and its numbers.
        /// </summary>
        public VetoVerdict? Evaluate(DateTime now, double starMassPercent, bool weatherVote, double weatherLimit, string weatherName = "weather")
        {
            List<(DateTime Utc, double StarMass, double? Weather)> recent, baseline;
            lock (_lock)
            {
                recent = _samples.Where(s => s.Utc > now - RecentWindow && s.Utc <= now).ToList();
                baseline = _samples.Where(s => s.Utc > now - BaselineWindow && s.Utc <= now - RecentWindow).ToList();
            }
            if (recent.Count < MinimumRecentSamples || baseline.Count < MinimumBaselineSamples) return null;

            var recentMedian = Median(recent.Select(s => s.StarMass));
            var baselineMedian = Median(baseline.Select(s => s.StarMass));
            var percent = baselineMedian > 0 ? 100.0 * recentMedian / baselineMedian : 0.0;
            var verdict = new VetoVerdict
            {
                StarMassPercent = percent,
                Threshold = starMassPercent,
                RecentMedian = recentMedian,
                BaselineMedian = baselineMedian,
                Holds = percent >= starMassPercent,
            };

            if (weatherVote)
            {
                verdict.WeatherName = weatherName;
                verdict.WeatherLimit = weatherLimit;
                var recentWeather = recent.Where(s => s.Weather.HasValue).Select(s => s.Weather!.Value).ToList();
                var baselineWeather = baseline.Where(s => s.Weather.HasValue).Select(s => s.Weather!.Value).ToList();
                if (recentWeather.Count == 0 || baselineWeather.Count == 0)
                {
                    verdict.WeatherNote = "no weather readings";
                    verdict.Holds = false;
                }
                else
                {
                    verdict.WeatherNow = Median(recentWeather);
                    verdict.WeatherBaseline = Median(baselineWeather);
                    var moved = Math.Abs(verdict.WeatherNow.Value - verdict.WeatherBaseline.Value);
                    verdict.Holds = verdict.Holds && moved <= weatherLimit;
                }
            }

            return verdict;
        }

        private static double Median(IEnumerable<double> values)
        {
            var sorted = values.OrderBy(v => v).ToList();
            var n = sorted.Count;
            return n % 2 == 1 ? sorted[n / 2] : (sorted[n / 2 - 1] + sorted[n / 2]) / 2.0;
        }
    }

    /// <summary>What the guide star (and the weather device, when asked) said about a cloud call.</summary>
    public sealed class VetoVerdict
    {
        public bool Holds { get; set; }
        public double StarMassPercent { get; set; }
        public double Threshold { get; set; }
        public double RecentMedian { get; set; }
        public double BaselineMedian { get; set; }
        public string? WeatherName { get; set; }
        public double? WeatherLimit { get; set; }
        public double? WeatherNow { get; set; }
        public double? WeatherBaseline { get; set; }
        public string? WeatherNote { get; set; }

        /// <summary>One line for the log, the panel and the decision file: the numbers, so the morning after can check them.</summary>
        public string Describe()
        {
            var inv = CultureInfo.InvariantCulture;
            var text = string.Format(inv, "guide-star mass {0:F0}% of recent level (threshold {1:F0}%)", StarMassPercent, Threshold);
            if (WeatherName == null) return text;
            if (WeatherNote != null) return text + ", " + WeatherNote;
            return text + string.Format(inv, ", {0} {1:F2} vs {2:F2} recent (limit {3:F2})", WeatherName, WeatherNow, WeatherBaseline, WeatherLimit);
        }
    }
}
