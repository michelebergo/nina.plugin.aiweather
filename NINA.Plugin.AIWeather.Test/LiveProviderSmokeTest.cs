using System.Drawing;
using AIWeather.Services;
using FluentAssertions;
using NUnit.Framework;

namespace NINA.Plugin.AIWeather.Test
{
    /// <summary>
    /// Calls each cloud provider with the plugin's real request on a small synthetic sky,
    /// and fails when a provider stops answering - which is how the plugin finds out about
    /// a model update before its users do. Runs only when the corresponding key is present
    /// in the environment; without it every test here is skipped, so a plain
    /// <c>dotnet test</c> never needs network or secrets. The weekly workflow provides them.
    ///
    /// Environment variables: AIWEATHER_GEMINI_KEY, AIWEATHER_OPENAI_KEY,
    /// AIWEATHER_ANTHROPIC_KEY, and optionally AIWEATHER_&lt;PROVIDER&gt;_MODEL.
    /// </summary>
    [TestFixture]
    [Category("Live")]
    public class LiveProviderSmokeTest
    {
        /// <summary>
        /// A night sky nobody photographed: a dark blue-black gradient with a few bright
        /// points. Enough for a vision model to answer the question; small enough (a few KB
        /// as JPEG) that a weekly run costs next to nothing.
        /// </summary>
        private static Bitmap SyntheticSky() {
            const int size = 256;
            var bitmap = new Bitmap(size, size);
            using (var g = Graphics.FromImage(bitmap))
            {
                g.Clear(Color.FromArgb(6, 8, 18));
                using var glow = new System.Drawing.Drawing2D.LinearGradientBrush(
                    new Rectangle(0, 0, size, size), Color.FromArgb(6, 8, 18), Color.FromArgb(18, 22, 40), 90f);
                g.FillRectangle(glow, 0, size / 2, size, size / 2);
                using var star = new SolidBrush(Color.FromArgb(230, 230, 240));
                foreach (var (x, y) in new[] { (40, 30), (120, 70), (200, 45), (80, 150), (170, 190), (220, 130), (30, 210) })
                {
                    g.FillEllipse(star, x, y, 3, 3);
                }
            }
            return bitmap;
        }

        private static string Require(string variable) {
            var value = Environment.GetEnvironmentVariable(variable);
            if (string.IsNullOrWhiteSpace(value))
            {
                Assert.Ignore($"{variable} is not set; live provider check skipped.");
            }
            return value!;
        }

        private static string ModelOrDefault(string variable, string fallback) {
            var value = Environment.GetEnvironmentVariable(variable);
            return string.IsNullOrWhiteSpace(value) ? fallback : value;
        }

        private static async Task AssertProviderAnswers(IWeatherAnalysisService service, string providerName) {
            (await service.InitializeAsync()).Should().BeTrue();

            using var sky = SyntheticSky();
            var result = await service.AnalyzeImageAsync(sky);

            result.FellBackToLocal.Should().BeFalse(because: $"{providerName} should have answered; it did not: {result.ProviderError}");
            result.Provider.Should().Be(providerName);
            result.CloudCoverage.Should().BeInRange(0, 100);
        }

        [Test]
        public async Task GeminiAnswersThePluginsRealRequest() {
            var key = Require("AIWEATHER_GEMINI_KEY");
            var model = ModelOrDefault("AIWEATHER_GEMINI_MODEL", "gemini-flash-latest");

            await AssertProviderAnswers(new GeminiAnalysisService(key, model), GeminiAnalysisService.ProviderName);
        }

        [Test]
        public async Task OpenAIAnswersThePluginsRealRequest() {
            var key = Require("AIWEATHER_OPENAI_KEY");
            var model = ModelOrDefault("AIWEATHER_OPENAI_MODEL", "gpt-4o-mini");

            await AssertProviderAnswers(new OpenAIAnalysisService(key, model), "OpenAI");
        }

        [Test]
        public async Task AnthropicAnswersThePluginsRealRequest() {
            var key = Require("AIWEATHER_ANTHROPIC_KEY");
            var model = ModelOrDefault("AIWEATHER_ANTHROPIC_MODEL", "claude-haiku-4-5-20251001");

            await AssertProviderAnswers(new AnthropicAnalysisService(key, model), "Anthropic");
        }
    }
}
