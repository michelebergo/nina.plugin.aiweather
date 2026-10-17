using System;

namespace AIWeather.Models
{
    /// <summary>
    /// Weather condition analysis result
    /// </summary>
    public class WeatherAnalysisResult
    {
        public DateTime Timestamp { get; set; }
        public WeatherCondition Condition { get; set; }
        public double CloudCoverage { get; set; } // 0-100%
        public double Confidence { get; set; } // 0-100%
        public bool IsSafeForImaging { get; set; }
        public string Description { get; set; } = string.Empty;
        public double? Brightness { get; set; } // Optional: for detecting dawn/dusk
        public bool RainDetected { get; set; }
        public bool FogDetected { get; set; }

        /// <summary>An explicit parsed Unknown answer, not an API failure envelope.
        /// CloudCoverage is a conservative sentinel, never a training measurement.</summary>
        public bool IsSkyUnreadable { get; set; }
        
        /// <summary>
        /// Additional metadata from the analysis
        /// </summary>
        public string? RawAnalysisData { get; set; }

        /// <summary>
        /// Strongly typed source/failure metadata. Kept as an additive property so existing
        /// N.I.N.A. bindings and third-party consumers of WeatherAnalysisResult keep working.
        /// </summary>
        public AnalysisProvenance Provenance { get; set; } = new AnalysisProvenance();

        public WeatherAnalysisResult Clone(bool includeRawAnalysisData = true)
        {
            return new WeatherAnalysisResult
            {
                Timestamp = Timestamp,
                Condition = Condition,
                CloudCoverage = CloudCoverage,
                Confidence = Confidence,
                IsSafeForImaging = IsSafeForImaging,
                Description = Description,
                Brightness = Brightness,
                RainDetected = RainDetected,
                FogDetected = FogDetected,
                IsSkyUnreadable = IsSkyUnreadable,
                RawAnalysisData = includeRawAnalysisData ? RawAnalysisData : null,
                Provenance = Provenance.Clone()
            };
        }
    }

    public enum WeatherCondition
    {
        Clear,
        PartlyCloudy,
        MostlyCloudy,
        Overcast,
        Rainy,
        Foggy,
        Unknown
    }
}
