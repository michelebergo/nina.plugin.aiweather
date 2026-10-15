using AIWeather.Services;
using FluentAssertions;
using NUnit.Framework;

namespace NINA.Plugin.AIWeather.Test
{
    /// <summary>
    /// Issue #17: a Bortle 3 site, a fixed Reolink security camera, and a prompt written
    /// for a fisheye at a light-polluted site. The prompt told the model that dark means
    /// clear and to look for a circle that was not there; an overcast night came back as
    /// "Clear 0%". The prompt is now composed from what the owner tells us.
    /// </summary>
    [TestFixture]
    public class PromptCompositionTest
    {
        private static string Prompt(CameraView view, SiteDarkness site) => WeatherAnalysisPrompts.BuildSystemPrompt(view, site);

        [Test]
        public void TheDefaultIsTheFisheyeAtALightPollutedSite() {
            // Nobody's verdict changes unless they change a setting.
            var prompt = Prompt(CameraView.AllSky, SiteDarkness.LightPolluted);

            prompt.Should().Contain("fisheye lens pointing upward");
            prompt.Should().Contain("clouds appear as BRIGHT, milky");
            prompt.Should().NotContain("FIXED CAMERA");
            prompt.Should().NotContain("clouds are NOT lit from below");
        }

        [Test]
        public void AFixedCameraIsNotAskedToFindACircle() {
            var prompt = Prompt(CameraView.FixedView, SiteDarkness.LightPolluted);

            prompt.Should().Contain("FIXED CAMERA");
            prompt.Should().Contain("above the horizon line");
            prompt.Should().NotContain("fisheye lens pointing upward");
            prompt.Should().NotContain("circular");
        }

        [Test]
        public void ADarkSiteJudgesByStarsNotByBrightness() {
            var prompt = Prompt(CameraView.AllSky, SiteDarkness.DarkSite);

            prompt.Should().Contain("clouds are NOT lit from below");
            prompt.Should().Contain("ONLY reliable evidence of clear sky is STARS");
            prompt.Should().Contain("uniformly black with no stars at all is OVERCAST");
            // The light-polluted rule that read a black sky as clear must be gone.
            prompt.Should().NotContain("clouds appear as BRIGHT, milky");
            prompt.Should().NotContain("bright/milky sky with no stars = high cloud coverage");
        }

        [TestCase(CameraView.AllSky, SiteDarkness.LightPolluted)]
        [TestCase(CameraView.AllSky, SiteDarkness.DarkSite)]
        [TestCase(CameraView.FixedView, SiteDarkness.LightPolluted)]
        [TestCase(CameraView.FixedView, SiteDarkness.DarkSite)]
        public void EveryVariantCarriesTheRulesThatDoNotDependOnTheSite(CameraView view, SiteDarkness site) {
            var prompt = Prompt(view, site);

            // Noise is not stars, and a frame with nothing in it is not a clear sky.
            prompt.Should().Contain("sensor noise, NOT stars");
            prompt.Should().Contain("condition=\"Unknown\", cloudCoverage=100, isSafe=false");
            // Rain and fog keep priority, the JSON contract is unchanged.
            prompt.Should().Contain("PRIORITY: Rain Detection");
            prompt.Should().Contain("\"condition\": \"Clear|PartlyCloudy|MostlyCloudy|Overcast|Rainy|Foggy|Unknown\"");
            prompt.Should().Contain("\"cloudCoverage\": 0-100");
        }

        [TestCase(CameraView.AllSky, SiteDarkness.LightPolluted)]
        [TestCase(CameraView.AllSky, SiteDarkness.DarkSite)]
        [TestCase(CameraView.FixedView, SiteDarkness.LightPolluted)]
        [TestCase(CameraView.FixedView, SiteDarkness.DarkSite)]
        public void EachBlockAppearsOnce(CameraView view, SiteDarkness site) {
            // A block appended twice would silently double the prompt for every check.
            var prompt = Prompt(view, site);

            Count(prompt, "IMPORTANT CONTEXT - ").Should().Be(2, "one block for the camera view, one for day and night");
            Count(prompt, "AT NIGHT AT THIS SITE").Should().Be(1);
            Count(prompt, "SELF-CHECK before responding").Should().Be(1);
            Count(prompt, "Respond in JSON format").Should().Be(1);
        }

        [Test]
        public void ThePromptFollowsTheSettings() {
            var settings = global::AIWeather.Properties.Settings.Default;
            var before = (settings.CameraView, settings.SiteDarkness);
            try
            {
                settings.CameraView = (int)CameraView.FixedView;
                settings.SiteDarkness = (int)SiteDarkness.DarkSite;

                WeatherAnalysisPrompts.BuildSystemPrompt().Should().Be(Prompt(CameraView.FixedView, SiteDarkness.DarkSite));
            }
            finally
            {
                settings.CameraView = before.CameraView;
                settings.SiteDarkness = before.SiteDarkness;
            }
        }

        private static int Count(string text, string needle) {
            var count = 0;
            for (var i = text.IndexOf(needle, System.StringComparison.Ordinal); i >= 0; i = text.IndexOf(needle, i + needle.Length, System.StringComparison.Ordinal)) count++;
            return count;
        }
    }
}
