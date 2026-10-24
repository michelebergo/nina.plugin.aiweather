using System.Drawing;
using System.Threading.Tasks;
using AIWeather.Services;
using FluentAssertions;
using NUnit.Framework;

namespace NINA.Plugin.AIWeather.Test
{
    /// <summary>
    /// The offline analyzer reported 47% at confidence 100 under a full moon (#21). It
    /// measures brightness and texture, not clouds; its confidence is nominal and says so.
    /// </summary>
    [TestFixture]
    public class LocalAnalyzerConfidenceTest
    {
        [Test]
        public async Task TheOfflineAnalyzerNeverClaimsMoreThanNominalConfidence() {
            using var frame = new Bitmap(64, 64);
            using (var g = Graphics.FromImage(frame)) g.Clear(Color.FromArgb(120, 120, 130));
            var result = await new LocalWeatherAnalysisService().AnalyzeImageAsync(frame);
            result.Confidence.Should().BeLessThanOrEqualTo(LocalWeatherAnalysisService.NominalConfidence);
        }
    }
}
