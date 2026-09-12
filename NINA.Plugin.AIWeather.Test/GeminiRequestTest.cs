using System.Text.Json;
using AIWeather.Services;
using FluentAssertions;
using NUnit.Framework;

namespace NINA.Plugin.AIWeather.Test
{
    /// <summary>
    /// Issue #16: every analysis came back as half a JSON object. The request asked for at
    /// most 512 output tokens, and current Gemini Flash models spend that budget reasoning
    /// before they answer - so the answer itself was cut off mid-field, every five minutes,
    /// all night.
    /// </summary>
    [TestFixture]
    public class GeminiRequestTest
    {
        private const string Prompt = "Analyse this all-sky image.";
        private const string Image = "QUJD";

        private static JsonElement Config(bool disableThinking) {
            var body = GeminiAnalysisService.BuildRequestBody(Prompt, Image, disableThinking);
            using var doc = JsonDocument.Parse(body);
            return doc.RootElement.GetProperty("generationConfig").Clone();
        }

        [Test]
        public void TheAnswerHasRoomToBeWrittenInFull() {
            // The analysis is a few hundred tokens; the reasoning phase is paid out of the
            // same budget. 512 was not enough for both, which is the whole of issue #16.
            Config(disableThinking: true).GetProperty("maxOutputTokens").GetInt32()
                .Should().BeGreaterThanOrEqualTo(1024);
        }

        [Test]
        public void TheAnswerIsRequestedAsJson() {
            // Asking for JSON is what stops the model from wrapping the object in prose or a
            // code fence, which the parser then has to guess its way out of.
            Config(disableThinking: true).GetProperty("responseMimeType").GetString()
                .Should().Be("application/json");
        }

        [Test]
        public void ReasoningIsSwitchedOffWhenTheModelAllowsIt() {
            Config(disableThinking: true).GetProperty("thinkingConfig").GetProperty("thinkingBudget").GetInt32()
                .Should().Be(0);
        }

        [Test]
        public void TheRetryDropsTheThinkingConfigEntirely() {
            // A model that refuses the parameter must still be usable: the second attempt
            // leaves the choice to the model and relies on the budget alone.
            Config(disableThinking: false).TryGetProperty("thinkingConfig", out _)
                .Should().BeFalse();
        }

        [Test]
        public void TheImageAndThePromptBothTravel() {
            var body = GeminiAnalysisService.BuildRequestBody(Prompt, Image, disableThinking: true);
            using var doc = JsonDocument.Parse(body);

            var parts = doc.RootElement.GetProperty("contents")[0].GetProperty("parts");
            parts[0].GetProperty("text").GetString().Should().Be(Prompt);
            parts[1].GetProperty("inlineData").GetProperty("data").GetString().Should().Be(Image);
            parts[1].GetProperty("inlineData").GetProperty("mimeType").GetString().Should().Be("image/jpeg");
        }

        [Test]
        public void OnlyAComplaintAboutThinkingIsWorthASecondAttempt() {
            GeminiAnalysisService.IsThinkingConfigRejected(
                "HTTP 400: {\"error\":{\"message\":\"thinking_config is not supported for this model\"}}")
                .Should().BeTrue();

            GeminiAnalysisService.IsThinkingConfigRejected(
                "HTTP 400: {\"error\":{\"message\":\"Budget 0 is invalid: thought budget must be at least 128\"}}")
                .Should().BeTrue();

            // Anything else is a real failure and retrying only wastes another call.
            GeminiAnalysisService.IsThinkingConfigRejected(
                "HTTP 429: {\"error\":{\"message\":\"Resource has been exhausted (e.g. check quota).\"}}")
                .Should().BeFalse();
            GeminiAnalysisService.IsThinkingConfigRejected("HTTP 403: {\"error\":{\"message\":\"API key not valid\"}}")
                .Should().BeFalse();
            GeminiAnalysisService.IsThinkingConfigRejected(null).Should().BeFalse();
        }

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
