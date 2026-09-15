using System.Text.Json;
using AIWeather.Services;
using FluentAssertions;
using NUnit.Framework;

namespace NINA.Plugin.AIWeather.Test
{
    /// <summary>
    /// Issue #16, twice. First every analysis came back as half a JSON object: 512 output
    /// tokens, and current Flash models spend that budget reasoning before they answer.
    /// Then, with the budget fixed, a Gemini 3.5 model rejected every request with a bare
    /// 400 INVALID_ARGUMENT: the plugin sent it a temperature and a thinking budget, both
    /// retired between the 2.5 and 3.x generations, and the "retry without thinking" never
    /// fired because it looked for a word Google does not write.
    ///
    /// The rule that comes out of it: the plugin does not assume what a model accepts.
    /// </summary>
    [TestFixture]
    public class GeminiRequestTest
    {
        private const string Prompt = "Analyse this all-sky image.";
        private const string Image = "QUJD";
        private const string Ok = "{\"candidates\":[{\"finishReason\":\"STOP\",\"content\":{\"parts\":[{\"text\":\"{}\"}]}}]}";
        private const string Rejected = "{\"error\":{\"code\":400,\"message\":\"Request contains an invalid argument.\",\"status\":\"INVALID_ARGUMENT\"}}";

        private static JsonElement Config(GeminiRequestProfile profile, int budget = GeminiAnalysisService.DefaultMaxOutputTokens) {
            var body = GeminiAnalysisService.BuildRequestBody(Prompt, Image, profile, budget);
            using var doc = JsonDocument.Parse(body);
            return doc.RootElement.GetProperty("generationConfig").Clone();
        }

        // ---- which shape to start from ------------------------------------------------

        [TestCase("gemini-2.5-flash", GeminiRequestProfile.Gemini2)]
        [TestCase("gemini-2.5-pro", GeminiRequestProfile.Gemini2)]
        [TestCase("gemini-1.5-pro", GeminiRequestProfile.Gemini2)]
        [TestCase("gemini-3.5-flash-lite", GeminiRequestProfile.Lean)]
        [TestCase("gemini-3.6-flash", GeminiRequestProfile.Lean)]
        [TestCase("gemini-flash-latest", GeminiRequestProfile.Lean)]
        [TestCase("", GeminiRequestProfile.Lean)]
        public void TheStartingShapeFollowsTheModelGeneration(string model, GeminiRequestProfile expected) {
            // The -latest alias points at the newest Flash, which is 3.x: starting from the
            // 2.x shape there would cost a wasted 400 on every fresh service.
            GeminiAnalysisService.StartingProfileFor(model).Should().Be(expected);
        }

        // ---- what each shape sends ----------------------------------------------------

        [Test]
        public void TheGemini2ShapeSwitchesReasoningOffAndPinsTheTemperature() {
            var config = Config(GeminiRequestProfile.Gemini2);

            config.GetProperty("thinkingConfig").GetProperty("thinkingBudget").GetInt32().Should().Be(0);
            config.GetProperty("temperature").GetDouble().Should().Be(0.1);
            config.GetProperty("responseMimeType").GetString().Should().Be("application/json");
        }

        [Test]
        public void TheLeanShapeSendsNothingThe3xGenerationRetired() {
            // Google's own 3.5 migration note: drop temperature/top_p/top_k, replace
            // thinking_budget. A 3.x model answers either of them with a bare 400.
            var config = Config(GeminiRequestProfile.Lean);

            config.TryGetProperty("temperature", out _).Should().BeFalse();
            config.TryGetProperty("thinkingConfig", out _).Should().BeFalse();
            config.GetProperty("responseMimeType").GetString().Should().Be("application/json");
        }

        [Test]
        public void TheBareShapeIsTheBudgetAlone() {
            var config = Config(GeminiRequestProfile.Bare);

            config.EnumerateObject().Should().HaveCount(1);
            config.GetProperty("maxOutputTokens").GetInt32().Should().BePositive();
        }

        [Test]
        public void EveryShapeLeavesRoomForReasoningAndAnswer() {
            // On 3.x the reasoning cannot be switched off, so the budget must hold both.
            foreach (var profile in new[] { GeminiRequestProfile.Gemini2, GeminiRequestProfile.Lean, GeminiRequestProfile.Bare })
            {
                Config(profile).GetProperty("maxOutputTokens").GetInt32().Should().BeGreaterThanOrEqualTo(4096, because: $"the {profile} shape must not truncate the answer");
            }
        }

        [Test]
        public void TheBudgetTravelsAsGiven() {
            Config(GeminiRequestProfile.Lean, budget: 1234).GetProperty("maxOutputTokens").GetInt32().Should().Be(1234);
        }

        [Test]
        public void TheImageAndThePromptBothTravel() {
            var body = GeminiAnalysisService.BuildRequestBody(Prompt, Image, GeminiRequestProfile.Lean, 4096);
            using var doc = JsonDocument.Parse(body);

            var parts = doc.RootElement.GetProperty("contents")[0].GetProperty("parts");
            parts[0].GetProperty("text").GetString().Should().Be(Prompt);
            parts[1].GetProperty("inlineData").GetProperty("data").GetString().Should().Be(Image);
            parts[1].GetProperty("inlineData").GetProperty("mimeType").GetString().Should().Be("image/jpeg");
        }

        // ---- stepping down --------------------------------------------------------------

        [Test]
        public void TheShapesStepDownInOrderAndThenStop() {
            GeminiAnalysisService.NextProfile(GeminiRequestProfile.Gemini2).Should().Be(GeminiRequestProfile.Lean);
            GeminiAnalysisService.NextProfile(GeminiRequestProfile.Lean).Should().Be(GeminiRequestProfile.Bare);
            GeminiAnalysisService.NextProfile(GeminiRequestProfile.Bare).Should().BeNull();
        }

        [TestCase(400, true)]
        [TestCase(401, false)]
        [TestCase(403, false)]
        [TestCase(404, false)]
        [TestCase(429, false)]
        [TestCase(500, false)]
        [TestCase(503, false)]
        public void OnlyA400IsAboutTheShapeOfTheRequest(int status, bool expected) {
            // A bad key, an exhausted quota or a down server are not fixed by sending less;
            // retrying them only spends more calls on the same answer.
            GeminiAnalysisService.IsRequestRejected(status).Should().Be(expected);
        }

        [Test]
        public async Task ARejectedShapeIsRetriedWithThePoorerOne() {
            var service = new GeminiAnalysisService("key", "gemini-2.5-flash");
            var sent = new List<string>();

            var (body, profile) = await service.NegotiateAsync(request => {
                sent.Add(request);
                return Task.FromResult(sent.Count == 1 ? (Rejected, 400) : (Ok, 200));
            }, Prompt, Image);

            body.Should().Be(Ok);
            profile.Should().Be(GeminiRequestProfile.Lean);
            sent.Should().HaveCount(2);
            sent[0].Should().Contain("thinkingConfig");
            sent[1].Should().NotContain("thinkingConfig").And.NotContain("temperature");
        }

        [Test]
        public async Task TheAcceptedShapeIsRememberedSoTheNextCycleCostsOneCall() {
            // The steady state must be one request per check, or the negotiation would
            // double the bill - and the latency - of every analysis, all night.
            var service = new GeminiAnalysisService("key", "gemini-2.5-flash");
            var calls = 0;

            Task<(string, int)> Post(string request) {
                calls++;
                return Task.FromResult(request.Contains("thinkingConfig") ? (Rejected, 400) : (Ok, 200));
            }

            await service.NegotiateAsync(Post, Prompt, Image);
            calls.Should().Be(2);

            var (_, profile) = await service.NegotiateAsync(Post, Prompt, Image);
            calls.Should().Be(3);
            profile.Should().Be(GeminiRequestProfile.Lean);
            service.CurrentProfile.Should().Be(GeminiRequestProfile.Lean);
        }

        [Test]
        public async Task AModelThatRefusesEveryShapeIsReportedAfterTheLastOne() {
            var service = new GeminiAnalysisService("key", "gemini-2.5-flash");
            var calls = 0;

            var act = () => service.NegotiateAsync(_ => { calls++; return Task.FromResult((Rejected, 400)); }, Prompt, Image);

            await act.Should().ThrowAsync<InvalidOperationException>().WithMessage("HTTP 400:*");
            calls.Should().Be(3, because: "Gemini2, Lean and Bare were each tried once");
        }

        [Test]
        public async Task AFailureThatIsNotAboutTheShapeIsNotRetried() {
            var service = new GeminiAnalysisService("key", "gemini-3.5-flash-lite");
            var calls = 0;

            var act = () => service.NegotiateAsync(_ => { calls++; return Task.FromResult(("{\"error\":{\"code\":429}}", 429)); }, Prompt, Image);

            await act.Should().ThrowAsync<InvalidOperationException>().WithMessage("HTTP 429:*");
            calls.Should().Be(1);
        }

        [Test]
        public async Task ThisIsWhatDennisNightWouldHaveLookedLike() {
            // gemini-3.5-flash-lite, 12 September: 210 requests, 210 bare 400s, no retry.
            // Now the model's generation picks the lean shape first, and if Google retires
            // something else tomorrow the bare shape is one 400 away.
            var service = new GeminiAnalysisService("key", "gemini-3.5-flash-lite");
            var sent = new List<string>();

            var (_, profile) = await service.NegotiateAsync(request => {
                sent.Add(request);
                var rejected = request.Contains("temperature") || request.Contains("thinkingConfig");
                return Task.FromResult(rejected ? (Rejected, 400) : (Ok, 200));
            }, Prompt, Image);

            profile.Should().Be(GeminiRequestProfile.Lean);
            sent.Should().HaveCount(1, because: "the starting shape already fits a 3.x model");
        }

        // ---- the model's own limit ----------------------------------------------------

        [Test]
        public void TheBudgetNeverExceedsWhatTheModelReports() {
            GeminiAnalysisService.ClampBudget(2048).Should().Be(2048);
            GeminiAnalysisService.ClampBudget(65536).Should().Be(GeminiAnalysisService.DefaultMaxOutputTokens);
            GeminiAnalysisService.ClampBudget(null).Should().Be(GeminiAnalysisService.DefaultMaxOutputTokens);
            GeminiAnalysisService.ClampBudget(0).Should().Be(GeminiAnalysisService.DefaultMaxOutputTokens);
        }

        [Test]
        public void TheModelsOutputLimitIsReadFromItsMetadata() {
            GeminiAnalysisService.ParseOutputTokenLimit("{\"name\":\"models/gemini-3.5-flash-lite\",\"outputTokenLimit\":65536,\"inputTokenLimit\":1048576}")
                .Should().Be(65536);
            GeminiAnalysisService.ParseOutputTokenLimit("{\"name\":\"models/x\"}").Should().BeNull();
            GeminiAnalysisService.ParseOutputTokenLimit("not json").Should().BeNull();
            GeminiAnalysisService.ParseOutputTokenLimit(null).Should().BeNull();
        }

        // ---- a truncated answer is recognised ------------------------------------------

        [TestCase("MAX_TOKENS", true)]
        [TestCase("STOP", false)]
        [TestCase("SAFETY", false)]
        public void RunningOutOfBudgetIsRecognisedAsSuch(string finishReason, bool expected) {
            var response = $"{{\"candidates\":[{{\"finishReason\":\"{finishReason}\",\"content\":{{\"parts\":[{{\"text\":\"{{\"}}]}}}}]}}";
            using var doc = JsonDocument.Parse(response);

            GeminiAnalysisService.WasCutShort(doc.RootElement).Should().Be(expected);
        }

        [Test]
        public void AResponseWithoutCandidatesIsNotTreatedAsTruncated() {
            using var doc = JsonDocument.Parse("{\"promptFeedback\":{\"blockReason\":\"SAFETY\"}}");

            GeminiAnalysisService.WasCutShort(doc.RootElement).Should().BeFalse();
        }
    }
}
