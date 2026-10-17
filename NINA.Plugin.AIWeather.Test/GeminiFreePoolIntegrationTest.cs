using System.Drawing;
using System.Net;
using System.Net.Http;
using System.Text.Json;
using AIWeather.Models;
using AIWeather.Services;
using AIWeather.Localization;
using NUnit.Framework;
using FluentAssertions;

namespace NINA.Plugin.AIWeather.Test;

[TestFixture]
public class GeminiFreePoolIntegrationTest
{
    private const string First = "gemini-3.5-flash-lite";
    private const string Second = "gemini-3.1-flash-lite";
    private static readonly DateTimeOffset Now = new(2026, 9, 16, 12, 0, 0, TimeSpan.Zero);
    private const string DailyQuota = """{"error":{"code":429,"status":"RESOURCE_EXHAUSTED","details":[{"@type":"type.googleapis.com/google.rpc.QuotaFailure","violations":[{"quotaMetric":"generate_content_free_tier_requests","quotaId":"GenerateRequestsPerDayPerProjectPerModel-FreeTier"}]}]}}""";
    private const string Success = """{"candidates":[{"finishReason":"STOP","content":{"parts":[{"text":"{\"condition\":\"Clear\",\"cloudCoverage\":10,\"confidence\":90,\"rainDetected\":false,\"fogDetected\":false,\"isSafe\":true,\"description\":\"Clear sky\"}"}]}}]}""";

    private sealed class Handler(Func<string, string, (int code, string body)> respond) : HttpMessageHandler
    {
        public List<string> Models { get; } = new();
        public List<string> Bodies { get; } = new();
        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken token)
        {
            var model = Uri.UnescapeDataString(request.RequestUri!.AbsolutePath.Split('/').Last().Split(':')[0]);
            var body = await request.Content!.ReadAsStringAsync(token);
            Models.Add(model);
            Bodies.Add(body);
            var reply = respond(model, body);
            return new HttpResponseMessage((HttpStatusCode)reply.code) { Content = new StringContent(reply.body) };
        }
    }

    private sealed class Fixture : IDisposable
    {
        public Handler Handler { get; }
        public HttpClient Http { get; }
        public Dictionary<string, GeminiQuotaCircuitBreaker> Circuits { get; } = new();
        public GeminiFreePoolAnalysisService Pool { get; }
        public Fixture(Func<string, string, (int, string)> respond)
        {
            Handler = new Handler(respond);
            Http = new HttpClient(Handler);
            Pool = new GeminiFreePoolAnalysisService("test-key", GeminiProviderProfile.DefaultFreeModelOrder, 2, 1,
                new FixedHttpClientProvider(Http), Circuit, () => Now);
        }
        public GeminiQuotaCircuitBreaker Circuit(string model)
        {
            if (!Circuits.TryGetValue(model, out var circuit))
                Circuits[model] = circuit = new GeminiQuotaCircuitBreaker();
            return circuit;
        }
        public void Dispose() => Http.Dispose();
    }

    [Test]
    public async Task ExplicitUnreadableSkyStopsPoolAndDoesNotFallBackToStudent()
    {
        var payload = Success.Replace("\\\"Clear\\\"", "\\\"Unknown\\\"");
        using var f = new Fixture((_, _) => (200, payload));
        await f.Pool.InitializeAsync();
        using var image = new Bitmap(32, 32);
        var bundle = await new WeatherAnalysisOrchestrator().AnalyzeAsync(f.Pool, image, null, CancellationToken.None);
        f.Handler.Models.Should().Equal(First);
        bundle.UsedFallback.Should().BeFalse();
        bundle.EffectiveResult.IsSkyUnreadable.Should().BeTrue();
        bundle.EffectiveResult.IsSafeForImaging.Should().BeFalse();
        bundle.TeacherStudentCloudDifference.Should().BeNull();
        AnalysisThrottle.CanApplyResult(bundle.EffectiveResult).Should().BeTrue();
    }

    [Test]
    public async Task DailyQuotaOnFirstModelStillTries31LiteAndNextCheckSkipsOnlyExhaustedModel()
    {
        using var f = new Fixture((model, _) => model == First ? (429, DailyQuota) : (200, Success));
        await f.Pool.InitializeAsync();
        using var image = new Bitmap(16, 16);
        var first = await f.Pool.TryAnalyzeOnlineOnlyAsync(image);
        first.Success.Should().BeTrue();
        first.Provenance.Model.Should().Be(Second);
        f.Handler.Models.Should().Equal(First, Second);
        var second = await f.Pool.TryAnalyzeOnlineOnlyAsync(image);
        second.Success.Should().BeTrue();
        f.Handler.Models.Should().Equal(First, Second, Second);
        f.Circuit(First).TryGetActive(Now, out _).Should().BeTrue();
        f.Circuit(Second).TryGetActive(Now, out _).Should().BeFalse();
    }

    [Test]
    public void CallerCancellationStopsThePoolWithoutTryingTheNextModel()
    {
        using var cancellation = new CancellationTokenSource();
        using var f = new Fixture((_, _) =>
        {
            cancellation.Cancel();
            throw new OperationCanceledException(cancellation.Token);
        });
        using var image = new Bitmap(16, 16);
        Assert.CatchAsync<OperationCanceledException>(async () =>
        {
            await f.Pool.InitializeAsync();
            await f.Pool.TryAnalyzeOnlineOnlyAsync(image, cancellationToken: cancellation.Token);
        });
        f.Handler.Models.Should().Equal(First);
        f.Circuit(First).TryGetActive(Now, out _).Should().BeFalse();
    }

    [Test]
    public async Task MixedQuotaAndBadRequestNeverAdvertisesAPoolWideDailyPause()
    {
        using var f = new Fixture((model, _) => model == First ? (429, DailyQuota) : (400, "{}"));
        await f.Pool.InitializeAsync();
        using var image = new Bitmap(16, 16);
        var result = await f.Pool.TryAnalyzeOnlineOnlyAsync(image);
        result.Success.Should().BeFalse();
        result.Provenance.RetryAfterUtc.Should().BeNull();
        result.Provenance.QuotaId.Should().BeNull();
        result.Provenance.ProviderFailureCode.Should().Be("free_pool_exhausted");
        f.Handler.Models.Count(model => model == Second).Should().Be(2);
        var summary = UiLocalization.FreePoolFailureSummary(result.Provenance);
        summary.Should().Contain(First).And.Contain(Second).And.Contain("400");
    }

    [Test]
    public async Task AllDailyQuotasAreTriedIndependentlyAndThenSuppressed()
    {
        using var f = new Fixture((_, _) => (429, DailyQuota));
        await f.Pool.InitializeAsync();
        using var image = new Bitmap(16, 16);
        var result = await f.Pool.TryAnalyzeOnlineOnlyAsync(image);
        f.Handler.Models.Should().Equal(GeminiProviderProfile.DefaultFreeModelOrder);
        result.Provenance.ProviderFailureCode.Should().Be("free_pool_daily_quota");
        result.Provenance.RetryAfterUtc.Should().BeAfter(Now.UtcDateTime);
        await f.Pool.TryAnalyzeOnlineOnlyAsync(image);
        f.Handler.Models.Count.Should().Be(GeminiProviderProfile.DefaultFreeModelOrder.Count);
    }

    [Test]
    public async Task FreePoolLearnsBareRequestOnNextCycleAndRemembersIt()
    {
        using var f = new Fixture((model, body) => model != Second ? (429, DailyQuota)
            : body.Contains("responseMimeType") ? (400, "{}") : (200, Success));
        await f.Pool.InitializeAsync();
        using var image = new Bitmap(16, 16);
        var result = await f.Pool.TryAnalyzeOnlineOnlyAsync(image);
        result.Success.Should().BeTrue();
        result.Provenance.Model.Should().Be(Second);
        f.Handler.Models.Count(model => model == Second).Should().Be(2);
        var before = f.Handler.Models.Count;
        (await f.Pool.TryAnalyzeOnlineOnlyAsync(image)).Success.Should().BeTrue();
        f.Handler.Models.Count.Should().Be(before + 1);
        f.Handler.Bodies.Last().Should().NotContain("temperature").And.NotContain("responseMimeType");
    }

    [Test]
    public async Task PaidGeminiStillMakesExactlyOneRequestAndDoesNotRotate()
    {
        using var f = new Fixture((_, _) => (400, "{}"));
        var service = new GeminiAnalysisService("test-key", First, f.Http, f.Circuit, () => Now,
            serviceTier: GeminiServiceTier.Paid);
        await service.InitializeAsync();
        using var image = new Bitmap(16, 16);
        (await service.TryAnalyzeOnlineOnlyAsync(image)).Success.Should().BeFalse();
        f.Handler.Models.Should().Equal(First);
    }

    [Test]
    public async Task TruncatedOrInvalidAnswerIsNeverASuccessful50PercentReading()
    {
        foreach (var body in new[] { """{"candidates":[{"finishReason":"MAX_TOKENS","content":{"parts":[{"text":"{"}]}}]}""", """{"candidates":[{"content":{"parts":[{"text":"{}"}]}}]}""" })
        {
            using var f = new Fixture((_, _) => (200, body));
            var service = new GeminiAnalysisService("test-key", First, f.Http, f.Circuit, () => Now);
            await service.InitializeAsync();
            using var image = new Bitmap(16, 16);
            var result = await service.TryAnalyzeOnlineOnlyAsync(image);
            result.Success.Should().BeFalse();
            result.Result.Should().BeNull();
            result.Provenance.FailureCategory.Should().Be(AnalysisFailureCategory.MalformedResponse);
        }
    }

    [Test]
    public void UpgradeInserts38Before37WithoutChangingExistingCustomOrder()
    {
        var old = new[] { Second, First, "gemini-3.7-flash", "gemini-3.6-flash", "gemini-3.5-flash", "gemini-3-flash" };
        var upgraded = GeminiProviderProfile.ParseFreeModelOrder(string.Join("\n", old));
        upgraded.Should().Equal(Second, First, "gemini-3.8-flash", "gemini-3.7-flash", "gemini-3.6-flash", "gemini-3.5-flash", "gemini-3-flash");
        var custom = upgraded.Reverse().ToArray();
        GeminiProviderProfile.ParseFreeModelOrder(string.Join("\n", custom)).Should().Equal(custom);
    }
}
