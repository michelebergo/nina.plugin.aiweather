using System.Drawing;
using System.IO;
using System.Reflection;
using System.Runtime.CompilerServices;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using AIWeather.Equipment;
using AIWeather.Models;
using AIWeather.Services;
using FluentAssertions;
using NUnit.Framework;

namespace NINA.Plugin.AIWeather.Test;

[TestFixture]
[NonParallelizable]
public class UpstreamIntegration1158Test
{
    private const string Token = "offline-test-shared-token-only-1158";
    private const string UnknownJson = """{"condition":"Unknown","cloudCoverage":0,"confidence":90,"isSafe":true,"rainDetected":false,"fogDetected":false,"description":"Sky not visible"}""";
    private static WeatherAnalysisResult Unreadable()
    {
        var result = WeatherResponseParser.Parse(UnknownJson);
        result.Provenance = AnalysisMetadata.Online(AnalysisOrigin.Gemini, "Gemini", "test-model", 1, 5, 200);
        return result;
    }
    private static AIWeatherFailoverConfiguration Configuration() => new()
    {
        RtspUrl = "rtsp://example.invalid/sky", AnalysisProvider = "Local"
    };

    [Test]
    public void ExplicitUnknownIsUnsafeObservationButNotCloudMeasurementOrReusableReading()
    {
        var result = Unreadable();
        result.IsSkyUnreadable.Should().BeTrue();
        result.IsSafeForImaging.Should().BeFalse();
        result.CloudCoverage.Should().Be(100);
        result.Confidence.Should().Be(30);
        result.Clone().IsSkyUnreadable.Should().BeTrue();
        WeatherAnalysisValidator.IsValidProviderResult(result, out _).Should().BeTrue();
        WeatherAnalysisValidator.IsValidTeacherResult(result, out _).Should().BeFalse();
        WeatherAnalysisValidator.IsInternallyConsistent(result, out _).Should().BeFalse();
        AnalysisThrottle.CanApplyResult(result).Should().BeTrue();
        AnalysisThrottle.HasUsableMeasurement(result).Should().BeFalse();
        AnalysisThrottle.ShouldReuseResult(result, DateTime.UtcNow, DateTime.UtcNow, 1, 1).Should().BeFalse();
    }

    [TestCase("BadLabel")]
    [TestCase("6")]
    [TestCase("999")]
    public void InvalidConditionIsNotTreatedAsAnExplicitUnreadableSky(string condition)
    {
        var result = WeatherResponseParser.Parse(UnknownJson.Replace("Unknown", condition));
        result.IsSkyUnreadable.Should().BeFalse();
        WeatherAnalysisValidator.IsValidProviderResult(result, out _).Should().BeFalse();
    }

    [Test]
    public void FailedOrFallbackUnknownCannotRefreshTheVerdict()
    {
        var result = Unreadable();
        result.Provenance.OnlineSucceeded = false;
        AnalysisThrottle.CanApplyResult(result).Should().BeFalse();
        result.Provenance.OnlineSucceeded = true;
        result.Provenance.IsFallback = true;
        AnalysisThrottle.CanApplyResult(result).Should().BeFalse();
        result.Provenance.IsFallback = false;
        result.Provenance.FailureCategory = AnalysisFailureCategory.Timeout;
        AnalysisThrottle.CanApplyResult(result).Should().BeFalse();
    }

    [Test]
    public void UnreadableSkyRevokesAnExistingSafeStateWithoutConnectingAnyEquipment()
    {
        // Bypass the singleton constructor: no timers, capture services or device access.
        var monitor = (AIWeatherSafetyMonitor)RuntimeHelpers.GetUninitializedObject(typeof(AIWeatherSafetyMonitor));
        var flags = BindingFlags.NonPublic | BindingFlags.Instance;
        var safe = typeof(AIWeatherSafetyMonitor).GetField("_isCurrentlySafe", flags)!;
        safe.SetValue(monitor, true);
        typeof(AIWeatherSafetyMonitor).GetMethod("UpdateSafetyState", flags)!.Invoke(monitor, new object[] { Unreadable() });
        safe.GetValue(monitor).Should().Be(false);
    }

    [Test]
    public async Task PromptScopeSurvivesAwaitAndDoesNotOverwriteLocalSettings()
    {
        var settings = global::AIWeather.Properties.Settings.Default;
        var before = (settings.CameraView, settings.SiteDarkness, settings.SiteNotes);
        var originalPrompt = WeatherAnalysisPrompts.BuildSystemPrompt();
        var config = Configuration();
        config.CameraView = 1;
        config.SiteDarkness = 1;
        config.SiteNotes = "Synced notes for this test";
        using (WeatherAnalysisPrompts.UseConfiguration(config))
        {
            await Task.Yield();
            WeatherAnalysisPrompts.BuildSystemPrompt().Should().Contain("FIXED CAMERA").And.Contain("clouds are NOT lit from below");
            WeatherAnalysisPrompts.BuildPromptPrefix(null).Should().Contain(config.SiteNotes);
            AnalysisMetadata.Online(AnalysisOrigin.Gemini, "Gemini", "test", 1, 0).PromptVersion.Should().EndWith("-view1-site1");
            using (WeatherAnalysisPrompts.UseConfiguration(Configuration()))
                WeatherAnalysisPrompts.BuildSystemPrompt().Should().Contain("fisheye lens pointing upward");
            WeatherAnalysisPrompts.BuildSystemPrompt().Should().Contain("FIXED CAMERA");
        }
        WeatherAnalysisPrompts.BuildSystemPrompt().Should().Be(originalPrompt);
        (settings.CameraView, settings.SiteDarkness, settings.SiteNotes).Should().Be(before);
    }

    [Test]
    public async Task ConcurrentPromptScopesAreIsolated()
    {
        async Task<string> Run(int view, int site)
        {
            var config = Configuration(); config.CameraView = view; config.SiteDarkness = site;
            using var scope = WeatherAnalysisPrompts.UseConfiguration(config);
            await Task.Delay(10);
            return WeatherAnalysisPrompts.CurrentPromptVersion;
        }
        var versions = await Task.WhenAll(Run(0, 1), Run(1, 0));
        versions[0].Should().EndWith("-view0-site1");
        versions[1].Should().EndWith("-view1-site0");
    }

    [Test]
    public void PromptSettingsRoundTripEncryptedAndSummaryDoesNotExposeNotes()
    {
        var config = Configuration();
        config.CameraView = 1; config.SiteDarkness = 1; config.SiteNotes = "private-site-note";
        var baseline = AIWeatherClusterProtocol.ComputeConfigurationRevision(Configuration(), Token);
        var envelope = AIWeatherClusterProtocol.EncryptFailoverConfiguration(config, Token, "test-node", "test-session");
        envelope.Revision.Should().NotBe(baseline);
        JsonSerializer.Serialize(envelope).Should().NotContain(config.SiteNotes);
        var decoded = AIWeatherClusterProtocol.DecryptFailoverConfiguration(envelope, Token);
        decoded.CameraView.Should().Be(1); decoded.SiteDarkness.Should().Be(1); decoded.SiteNotes.Should().Be(config.SiteNotes);
        var summary = AIWeatherReplicaConfigurationSummary.FromConfiguration(decoded, envelope.Revision, envelope.GeneratedUtc);
        summary.CameraView.Should().Be(1); summary.SiteDarkness.Should().Be(1); summary.SiteNotesConfigured.Should().BeTrue();
        JsonSerializer.Serialize(summary).Should().NotContain(config.SiteNotes);
        foreach (var change in new Action<AIWeatherFailoverConfiguration>[]
        {
            c => c.CameraView = 1, c => c.SiteDarkness = 1, c => c.SiteNotes = "new note"
        })
        {
            var changed = Configuration(); change(changed);
            AIWeatherClusterProtocol.ComputeConfigurationRevision(changed, Token).Should().NotBe(baseline);
        }
    }

    [Test]
    public void DefaultPromptFieldsDoNotAlterLegacyCanonicalConfiguration()
    {
        var normalized = Configuration().Normalize();
        var options = new JsonSerializerOptions { PropertyNamingPolicy = JsonNamingPolicy.CamelCase };
        var json = JsonSerializer.Serialize(normalized, options);
        json.Should().NotContain("cameraView").And.NotContain("siteDarkness").And.NotContain("siteNotes");
        using var hmac = new HMACSHA256(Encoding.UTF8.GetBytes(Token));
        var legacyRevision = Convert.ToHexString(hmac.ComputeHash(Encoding.UTF8.GetBytes(json)).AsSpan(0, 16));
        AIWeatherClusterProtocol.ComputeConfigurationRevision(normalized, Token).Should().Be(legacyRevision);
        var oldSettings = JsonSerializer.Deserialize<AIWeatherFailoverConfiguration>(json, options)!;
        oldSettings.CameraView.Should().Be(0); oldSettings.SiteDarkness.Should().Be(0);
        oldSettings.SiteNotes.Should().BeNull();
    }

    [Test]
    public async Task UnreadableSkyIsWrittenOnlyAsQuarantineWithInvalidTeacherLabel()
    {
        var root = Path.Combine(Path.GetTempPath(), "AIWeatherUnreadableSkyTest", Guid.NewGuid().ToString("N"));
        var options = new DatasetRecorderOptions
        {
            Enabled = true, RootDirectory = root, MinimumFreeBytes = 1,
            MaximumBytes = 10 * 1024 * 1024, RecordQuarantine = true
        };
        var recorder = new TeacherStudentDatasetRecorder(() => options);
        var teacher = Unreadable();
        var bundle = new WeatherAnalysisBundle
        {
            EffectiveResult = teacher, Teacher = OnlineAnalysisAttempt.Succeeded(teacher),
            Student = new WeatherAnalysisResult { Condition = WeatherCondition.Clear, CloudCoverage = 10, Confidence = 80 }
        };
        using var frame = new Bitmap(64, 64);
        try
        {
            recorder.TryEnqueue(frame, DateTime.UtcNow, null, bundle, false, false, null, 70, 60).Should().BeTrue();
            var deadline = DateTime.UtcNow.AddSeconds(5);
            while (recorder.Status.TotalSamples == 0 && DateTime.UtcNow < deadline)
                await Task.Delay(25);
        }
        finally { await recorder.StopAsync(TimeSpan.FromSeconds(5)); }
        recorder.Status.TotalSamples.Should().Be(1);
        recorder.Status.QuarantineSamples.Should().Be(1);
        Directory.GetFiles(Path.Combine(root, "labels"), "*.json", SearchOption.AllDirectories).Should().BeEmpty();
        var labels = Directory.GetFiles(Path.Combine(root, "quarantine"), "*.json", SearchOption.AllDirectories);
        labels.Should().ContainSingle();
        using var label = JsonDocument.Parse(await File.ReadAllTextAsync(labels[0]));
        label.RootElement.GetProperty("teacher").GetProperty("valid").GetBoolean().Should().BeFalse();
    }

    [Test]
    public void ConfigurationNormalizesInvalidChoicesAndBoundsNotes()
    {
        var config = Configuration();
        config.CameraView = -1; config.SiteDarkness = 10;
        config.SiteNotes = new string('x', 2000);
        var normalized = config.Normalize();
        normalized.CameraView.Should().Be(0); normalized.SiteDarkness.Should().Be(0);
        normalized.SiteNotes!.Length.Should().Be(WeatherAnalysisPrompts.MaxSiteNotesLength);
    }
}
