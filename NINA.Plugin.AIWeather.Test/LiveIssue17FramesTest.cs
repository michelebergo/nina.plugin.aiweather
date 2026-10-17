using System.Drawing;
using System.IO;
using AIWeather.Models;
using AIWeather.Services;
using FluentAssertions;
using NUnit.Framework;

namespace NINA.Plugin.AIWeather.Test
{
    /// <summary>
    /// The five frames from issue #17 (a fixed Reolink at a Bortle 3 site), sent to the
    /// real providers with the prompt as it was (all-sky, light-polluted: the defaults)
    /// and as the owner should now set it (fixed camera, dark site). Prints one table per
    /// provider; asserts only what the release must guarantee: the overcast and the
    /// unreadable frames are no longer "Clear", and the clear frames stay safe.
    ///
    /// The frames are not in the repository. Set AIWEATHER_ISSUE17_FRAMES to a folder with
    /// img1.png ... img5.png, plus the provider keys used by <see cref="LiveProviderSmokeTest"/>.
    /// Explicit: it costs ten calls per provider and is run by hand before a release.
    /// </summary>
    [TestFixture]
    [Category("Live")]
    [Explicit("Costs real provider calls; run by hand with the issue #17 frames")]
    public class LiveIssue17FramesTest
    {
        // What each frame is, from the reporter's own words and timestamps.
        private static readonly (string File, string Truth, bool ExpectSafe)[] Frames =
        {
            ("img1.png", "19/09 23:32 overcast, frame black", false),
            ("img2.png", "20/09 22:01 partly cloudy, stars and cloud patches", true),
            ("img3.png", "20/09 23:11 no binning, fully black", false),
            ("img4.png", "22/09 04:26 clear, stars", true),
            ("img5.png", "22/09 04:31 clear, stars", true),
        };

        private static string FramesFolder() {
            var folder = Environment.GetEnvironmentVariable("AIWEATHER_ISSUE17_FRAMES");
            if (string.IsNullOrWhiteSpace(folder) || !Directory.Exists(folder))
            {
                Assert.Ignore("AIWEATHER_ISSUE17_FRAMES is not set or does not exist; fixture run skipped.");
            }
            return folder!;
        }

        private static string Require(string variable) {
            var value = Environment.GetEnvironmentVariable(variable);
            if (string.IsNullOrWhiteSpace(value))
            {
                Assert.Ignore($"{variable} is not set; live provider check skipped.");
            }
            return value!;
        }

        private static async Task<WeatherAnalysisResult> Analyze(IWeatherAnalysisService service, string path) {
            using var image = new Bitmap(path);
            for (var attempt = 1; ; attempt++)
            {
                var result = await service.AnalyzeImageAsync(image);
                var transient = !result.Provenance.OnlineSucceeded
                    && result.Provenance.FailureCategory is AnalysisFailureCategory.RateLimited
                        or AnalysisFailureCategory.ServiceUnavailable or AnalysisFailureCategory.Timeout;
                if (!transient || attempt == 3) return result;
                await Task.Delay(TimeSpan.FromSeconds(30));
            }
        }

        private static async Task RunBothPrompts(Func<IWeatherAnalysisService> newService, string providerName) {
            var folder = FramesFolder();
            var settings = global::AIWeather.Properties.Settings.Default;
            var before = (settings.CameraView, settings.SiteDarkness);
            var rows = new List<string>();
            var failures = new List<string>();
            try
            {
                foreach (var (view, site, label) in new[]
                {
                    (CameraView.AllSky, SiteDarkness.LightPolluted, "old (all-sky, light-polluted)"),
                    (CameraView.FixedView, SiteDarkness.DarkSite, "new (fixed camera, dark site)"),
                })
                {
                    settings.CameraView = (int)view;
                    settings.SiteDarkness = (int)site;
                    var service = newService();
                    (await service.InitializeAsync()).Should().BeTrue();

                    foreach (var frame in Frames)
                    {
                        var r = await Analyze(service, Path.Combine(folder, frame.File));
                        var verdict = !r.Provenance.OnlineSucceeded
                            ? $"PROVIDER ERROR: {r.Provenance.FailureCategory}"
                            : $"{r.Condition,-12} {r.CloudCoverage,3:F0}%  safe={r.IsSafeForImaging,-5} conf={r.Confidence:F0}  {Trim(r.Description)}";
                        rows.Add($"{providerName,-7} | {label,-30} | {frame.File} | {frame.Truth,-48} | {verdict}");

                        if (label.StartsWith("new") && !r.Provenance.OnlineSucceeded)
                            failures.Add($"{providerName} {frame.File}: no online result ({r.Provenance.FailureCategory})");
                        if (label.StartsWith("new") && r.Provenance.OnlineSucceeded && r.IsSafeForImaging != frame.ExpectSafe)
                        {
                            failures.Add($"{providerName} {frame.File} ({frame.Truth}): expected safe={frame.ExpectSafe}, got {r.Condition} {r.CloudCoverage:F0}% safe={r.IsSafeForImaging}");
                        }
                    }
                }
            }
            finally
            {
                settings.CameraView = before.CameraView;
                settings.SiteDarkness = before.SiteDarkness;
            }

            TestContext.Progress.WriteLine(string.Join(Environment.NewLine, rows));
            TestContext.Out.WriteLine(string.Join(Environment.NewLine, rows));
            failures.Should().BeEmpty();
        }

        private static string Trim(string? text) {
            if (string.IsNullOrWhiteSpace(text)) return string.Empty;
            var oneLine = text.Replace('\r', ' ').Replace('\n', ' ');
            return oneLine.Length <= 110 ? oneLine : oneLine.Substring(0, 107) + "...";
        }

        [Test]
        public async Task OpenAIOnTheIssue17Frames() {
            var key = Require("AIWEATHER_OPENAI_KEY");
            var model = Environment.GetEnvironmentVariable("AIWEATHER_OPENAI_MODEL") ?? "gpt-4o-mini";
            await RunBothPrompts(() => new OpenAIAnalysisService(key, model), "OpenAI");
        }

        [Test]
        public async Task GeminiOnTheIssue17Frames() {
            var key = Require("AIWEATHER_GEMINI_KEY");
            var model = Environment.GetEnvironmentVariable("AIWEATHER_GEMINI_MODEL") ?? "gemini-flash-latest";
            await RunBothPrompts(() => new GeminiAnalysisService(key, model), "Gemini");
        }
    }
}
