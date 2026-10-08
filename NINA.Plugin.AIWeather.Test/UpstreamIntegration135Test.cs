using System.Drawing;
using AIWeather.Models;
using AIWeather.Services;
using FluentAssertions;
using NUnit.Framework;

namespace NINA.Plugin.AIWeather.Test;

[TestFixture]
public class UpstreamIntegration135Test
{
    private static readonly DateTime Now = new(2026, 10, 9, 12, 0, 0, DateTimeKind.Utc);
    private static WeatherAnalysisResult Reading() => new()
    {
        Condition = WeatherCondition.Clear, CloudCoverage = 10, Confidence = 90,
        Timestamp = Now.AddSeconds(-5)
    };

    [Test]
    public void RecentResultRequiresSameConfigurationAndKeepsItsTimestamp()
    {
        var result = Reading();
        AnalysisThrottle.ShouldReuseResult(result, result.Timestamp, Now, 2, 2).Should().BeTrue();
        AnalysisThrottle.ShouldReuseResult(result, result.Timestamp, Now, 2, 3).Should().BeFalse();
        result.Timestamp.Should().Be(Now.AddSeconds(-5));
    }

    [Test]
    public void ReconnectOrExpiredResultMustNotBeReused()
    {
        AnalysisThrottle.ShouldReuseResult(Reading(), DateTime.MinValue, Now, 2, 2).Should().BeFalse();
        AnalysisThrottle.ShouldReuseResult(Reading(), Now.AddSeconds(-30), Now, 2, 2).Should().BeFalse();
        AnalysisThrottle.ShouldReuseResult(null, Now.AddSeconds(-5), Now, 2, 2).Should().BeFalse();
    }

    [TestCase(double.NaN, 80)]
    [TestCase(double.PositiveInfinity, 80)]
    [TestCase(-1, 80)]
    [TestCase(101, 80)]
    [TestCase(10, 0)]
    [TestCase(10, double.NaN)]
    [TestCase(10, 101)]
    public void MalformedResultsAreNotFreshMeasurements(double cloud, double confidence)
    {
        var result = Reading();
        result.CloudCoverage = cloud;
        result.Confidence = confidence;
        AnalysisThrottle.HasUsableMeasurement(result).Should().BeFalse();
        AnalysisThrottle.ShouldReuseResult(result, result.Timestamp, Now, 2, 2).Should().BeFalse();
    }

    [Test]
    public void UnknownFailureIsNotMeasurementButValidFallbackAndUnsafeWeatherAre()
    {
        var result = Reading();
        result.Condition = WeatherCondition.Unknown;
        AnalysisThrottle.HasUsableMeasurement(result).Should().BeFalse();
        result.Condition = WeatherCondition.Rainy;
        result.RainDetected = true;
        result.Provenance.IsFallback = true;
        result.Provenance.FailureCategory = AnalysisFailureCategory.ServiceUnavailable;
        AnalysisThrottle.HasUsableMeasurement(result).Should().BeTrue();
    }

    [TestCase(null, "saved", "saved")]
    [TestCase("", "saved", "saved")]
    [TestCase("new", "saved", "new")]
    [TestCase(" ", "saved", " ")]
    [TestCase(null, null, "")]
    public void StartingARowDoesNotEraseCredentials(string? entered, string? saved, string expected)
        => CameraCredentialPolicy.ForStart(entered, saved).Should().Be(expected);

    [Test]
    public void LocalCancellationPropagatesInsteadOfProducingACloudReading()
    {
        using var image = new Bitmap(8, 8);
        using var cancellation = new CancellationTokenSource();
        cancellation.Cancel();
        var service = new LocalWeatherAnalysisService();
        Assert.ThrowsAsync<OperationCanceledException>(async () =>
            await service.AnalyzeImageAsync(image, cancellationToken: cancellation.Token));
    }
}
