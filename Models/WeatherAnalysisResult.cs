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

        /// <summary>The provider that was asked - "Gemini", "OpenAI", "Local"...</summary>
        public string Provider { get; set; } = "Local";

        /// <summary>
        /// True when the provider did not answer and the offline analyzer produced this
        /// reading instead. The reading is real - it measured the captured image - but the
        /// owner is paying for a provider that is not being heard, and must be told.
        /// </summary>
        public bool FellBackToLocal { get; set; }

        /// <summary>Why the provider did not answer, when it did not. One line, for the panel.</summary>
        public string? ProviderError { get; set; }
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
