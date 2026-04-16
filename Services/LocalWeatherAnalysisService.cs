using AIWeather.Models;
using NINA.Core.Utility;
using System;
using System.Drawing;
using System.Drawing.Imaging;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
namespace AIWeather.Services
{
    /// <summary>
    /// Local AI-based weather analysis using image processing algorithms
    /// This provides a basic implementation without requiring cloud services
    /// </summary>
    [System.Runtime.Versioning.SupportedOSPlatform("windows")]
    public class LocalWeatherAnalysisService : IWeatherAnalysisService
    {
        private bool _isInitialized = false;

        public Task<bool> InitializeAsync(CancellationToken cancellationToken = default)
        {
            _isInitialized = true;
            Logger.Info("Local weather analysis service initialized");
            return Task.FromResult(true);
        }

        public async Task<WeatherAnalysisResult> AnalyzeImageAsync(Bitmap image, AstroContext? astroContext = null, CancellationToken cancellationToken = default)
        {
            if (!_isInitialized)
            {
                await InitializeAsync(cancellationToken);
            }

            try
            {
                Logger.Debug("Starting local weather analysis");

                var result = await Task.Run(() =>
                {
                    // Analyze brightness and color distribution
                    var (avgBrightness, avgBlue, cloudScore) = AnalyzeImageCharacteristics(image);

                    // Detect rain patterns (look for streaks or water droplets)
                    var rainDetected = DetectRainPatterns(image);

                    // Detect fog (uniform grayness, low contrast)
                    var fogDetected = DetectFog(avgBrightness, cloudScore);

                    // Determine if it's nighttime from astro context
                    bool isNighttime = astroContext != null && astroContext.SunAltitude < -6;

                    // Determine cloud coverage based on brightness variance and color
                    var cloudCoverage = CalculateCloudCoverage(avgBrightness, avgBlue, cloudScore, isNighttime);

                    // Classify the weather condition
                    var condition = ClassifyWeatherCondition(cloudCoverage, rainDetected, fogDetected);

                    // Determine if it's safe for imaging
                    var isSafe = DetermineSafety(condition, cloudCoverage, rainDetected);

                    return new WeatherAnalysisResult
                    {
                        Timestamp = DateTime.UtcNow,
                        Condition = condition,
                        CloudCoverage = cloudCoverage,
                        Confidence = CalculateConfidence(cloudScore),
                        IsSafeForImaging = isSafe,
                        Description = GenerateDescription(condition, cloudCoverage, rainDetected, fogDetected),
                        Brightness = avgBrightness,
                        RainDetected = rainDetected,
                        FogDetected = fogDetected
                    };
                }, cancellationToken);

                Logger.Info($"Weather analysis complete: {result.Condition}, Cloud Coverage: {result.CloudCoverage:F1}%, Safe: {result.IsSafeForImaging}");
                return result;
            }
            catch (Exception ex)
            {
                Logger.Error($"Error analyzing image: {ex.Message}", ex);
                return new WeatherAnalysisResult
                {
                    Timestamp = DateTime.UtcNow,
                    Condition = WeatherCondition.Unknown,
                    CloudCoverage = 0,
                    Confidence = 0,
                    IsSafeForImaging = false,
                    Description = $"Analysis failed: {ex.Message}"
                };
            }
        }

        private (double brightness, double blue, double cloudScore) AnalyzeImageCharacteristics(Bitmap image)
        {
            double totalBrightness = 0;
            double totalBlue = 0;
            double totalVariance = 0;
            int pixelCount = 0;

            // Sample pixels (for performance, we don't analyze every pixel)
            int stepSize = Math.Max(1, image.Width / 100); // Sample ~100x100 grid

            // Fisheye circle masking: only sample pixels within the inscribed circle
            // to avoid black corners that bias averages downward
            int centerX = image.Width / 2;
            int centerY = image.Height / 2;
            int radius = Math.Min(centerX, centerY);
            int radiusSq = radius * radius;

            BitmapData data = image.LockBits(
                new Rectangle(0, 0, image.Width, image.Height),
                ImageLockMode.ReadOnly,
                PixelFormat.Format24bppRgb);

            try
            {
                unsafe
                {
                    byte* ptr = (byte*)data.Scan0;
                    int bytesPerPixel = 3;

                    for (int y = 0; y < image.Height; y += stepSize)
                    {
                        byte* row = ptr + (y * data.Stride);
                        int dy = y - centerY;
                        for (int x = 0; x < image.Width; x += stepSize)
                        {
                            // Skip pixels outside the inscribed circle (fisheye mask)
                            int dx = x - centerX;
                            if (dx * dx + dy * dy > radiusSq)
                                continue;

                            int offset = x * bytesPerPixel;
                            byte b = row[offset];
                            byte g = row[offset + 1];
                            byte r = row[offset + 2];

                            // Skip very dark pixels (likely outside fisheye or lens obstruction)
                            double brightness = (0.299 * r + 0.587 * g + 0.114 * b);
                            if (brightness < 3)
                                continue;

                            totalBrightness += brightness;
                            totalBlue += b;

                            // Calculate variance (for cloud detection)
                            totalVariance += Math.Abs(r - g) + Math.Abs(g - b) + Math.Abs(b - r);

                            pixelCount++;
                        }
                    }
                }
            }
            finally
            {
                image.UnlockBits(data);
            }

            if (pixelCount == 0)
            {
                return (0, 0, 0);
            }

            double avgBrightness = totalBrightness / pixelCount;
            double avgBlue = totalBlue / pixelCount;
            double cloudScore = totalVariance / pixelCount;

            return (avgBrightness, avgBlue, cloudScore);
        }

        private bool DetectRainPatterns(Bitmap image)
        {
            // Simple rain detection: look for vertical streaks or droplet patterns
            // This is a basic implementation - could be enhanced with ML
            
            // For now, we'll use a placeholder that could be expanded
            // In a real implementation, you'd analyze edge patterns and vertical gradients
            
            return false; // TODO: Implement advanced rain detection
        }

        private bool DetectFog(double brightness, double cloudScore)
        {
            // Fog typically shows:
            // - Low contrast (low cloudScore)
            // - Uniform grayness
            // - Medium brightness
            
            return cloudScore < 15 && brightness > 80 && brightness < 180;
        }

        private double CalculateCloudCoverage(double brightness, double blue, double cloudScore, bool isNighttime)
        {
            double coverage = 0;

            if (isNighttime)
            {
                // Nighttime analysis: clear sky is dark with low brightness.
                // Clouds at night scatter light pollution and moonlight, making the sky BRIGHTER.
                // Higher brightness = more clouds. Low brightness = clear.
                
                // Factor 1: Brightness is the primary indicator at night
                // Clear night sky: brightness ~5-30, Cloudy night sky: brightness ~60-180
                if (brightness > 20)
                {
                    coverage += Math.Min(60, (brightness - 20) * 0.5);
                }

                // Factor 2: Cloud structures create color variance
                if (cloudScore > 15)
                {
                    coverage += Math.Min(25, (cloudScore - 15) * 0.8);
                }

                // Factor 3: Uniform gray (low variance + moderate brightness) suggests overcast
                if (cloudScore < 10 && brightness > 80)
                {
                    coverage += 20;
                }
            }
            else
            {
                // Daytime analysis: clouds are bright and reduce blue channel
                
                // Factor 1: Brightness (clouds reflect sunlight)
                if (brightness > 100)
                {
                    coverage += (brightness - 100) / 1.55;
                }

                // Factor 2: Low blue relative to brightness indicates clouds
                double blueRatio = blue / Math.Max(1, brightness);
                if (blueRatio < 0.8)
                {
                    coverage += Math.Min(40, (0.8 - blueRatio) * 100);
                }

                // Factor 3: High variance suggests cloud structures
                if (cloudScore > 20)
                {
                    coverage += Math.Min(30, cloudScore / 2);
                }
            }

            return Math.Min(100, Math.Max(0, coverage));
        }

        private WeatherCondition ClassifyWeatherCondition(double cloudCoverage, bool rainDetected, bool fogDetected)
        {
            if (rainDetected)
                return WeatherCondition.Rainy;

            if (fogDetected)
                return WeatherCondition.Foggy;

            if (cloudCoverage < 20)
                return WeatherCondition.Clear;
            else if (cloudCoverage < 50)
                return WeatherCondition.PartlyCloudy;
            else if (cloudCoverage < 80)
                return WeatherCondition.MostlyCloudy;
            else
                return WeatherCondition.Overcast;
        }

        private bool DetermineSafety(WeatherCondition condition, double cloudCoverage, bool rainDetected)
        {
            // Rain is never safe
            if (rainDetected)
                return false;

            // Determine safety based on cloud coverage
            // This threshold should be configurable via plugin settings
            return cloudCoverage < 70; // Default: safe if less than 70% clouds
        }

        private double CalculateConfidence(double cloudScore)
        {
            // Confidence based on image quality metrics
            // Higher variance in the image = more confident analysis
            
            return Math.Min(100, 50 + cloudScore);
        }

        private string GenerateDescription(WeatherCondition condition, double cloudCoverage, bool rain, bool fog)
        {
            if (rain)
                return "Rain detected - unsafe for imaging";

            if (fog)
                return "Fog detected - poor imaging conditions";

            return $"{condition} - {cloudCoverage:F1}% cloud coverage";
        }
    }
}
