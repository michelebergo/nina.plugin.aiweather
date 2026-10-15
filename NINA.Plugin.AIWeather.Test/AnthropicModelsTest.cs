using AIWeather.Services;
using FluentAssertions;
using NUnit.Framework;

namespace NINA.Plugin.AIWeather.Test
{
    /// <summary>
    /// Issue #18: the Anthropic model list was filtered by a hand-kept prefix list
    /// (claude-3, claude-sonnet-4, ...) and dropped every Claude 5.x model the API
    /// returned. Anthropic retires text-only models long ago; every model /v1/models
    /// lists today accepts images, so the only filter worth keeping is the family prefix.
    /// </summary>
    [TestFixture]
    public class AnthropicModelsTest
    {
        [TestCase("claude-haiku-5-5")]
        [TestCase("claude-sonnet-5-5")]
        [TestCase("claude-opus-5-5")]
        [TestCase("claude-opus-5")]
        [TestCase("claude-fable-5-1")]
        [TestCase("claude-sonnet-4-6")]
        [TestCase("claude-haiku-4-5-20251001")]
        [TestCase("Claude-Haiku-5-5")]
        public void EveryClaudeModelIsOffered(string id) {
            AnthropicAnalysisService.IsVisionModel(id).Should().BeTrue();
        }

        [TestCase("claude-2.1")]
        [TestCase("claude-instant-1.2")]
        [TestCase("gpt-4o")]
        [TestCase("")]
        [TestCase(null)]
        public void TextOnlyAndForeignIdsAreNot(string? id) {
            AnthropicAnalysisService.IsVisionModel(id).Should().BeFalse();
        }

        [Test]
        public void TheDefaultModelIsNotOneAnthropicHasRetiredOrDeprecated() {
            // claude-sonnet-4-5-20250929 was deprecated on 2026-09-30 and retires on
            // 2026-11-30; a dateless 5.x ID is a pinned snapshot with a retirement date
            // at least a year out. Cheapest vision model: one analysis every five minutes
            // should not cost Sonnet money for a yes/no about clouds.
            AnthropicAnalysisService.DefaultModel.Should().Be("claude-haiku-5-5");
        }

        [Test]
        public void TheFallbackListHoldsOnlyLiveModels() {
            // The retired ones (3.5 Sonnet, 3.5 Haiku, 3 Opus, Sonnet 4) used to be here and
            // were offered to anyone whose /v1/models call failed.
            AnthropicAnalysisService.FallbackModels.Should().Equal(
                "claude-haiku-5-5", "claude-sonnet-5-5", "claude-opus-5-5", "claude-haiku-4-5-20251001");
        }
    }
}
