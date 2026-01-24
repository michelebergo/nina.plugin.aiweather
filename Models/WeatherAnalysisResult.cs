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
        
        /// <summary>
        /// Additional metadata from the analysis
        /// </summary>
        public string? RawAnalysisData { get; set; }
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
