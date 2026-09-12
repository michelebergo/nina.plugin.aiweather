using AIWeather.Models;
using AIWeather.Services;
using FluentAssertions;
using NUnit.Framework;

namespace NINA.Plugin.AIWeather.Test
{
    /// <summary>
    /// The parser is the last thing standing between a model's answer and a safety verdict,
    /// so the rule it has to keep is narrow and absolute: either it read the analysis, or it
    /// says so. It must never return a reading of its own invention.
    ///
    /// Issue #16: a Gemini answer arrived truncated, the parser answered with a made-up
    /// "50% cloud coverage, confidence 0, unsafe", the monitor reported that number as a
    /// measurement, and the sequence stopped every five minutes until the owner switched the
    /// safety monitor off for the night.
    /// </summary>
    [TestFixture]
    public class WeatherResponseParserTest
    {
        private const string ValidAnswer = @"{
  ""condition"": ""PartlyCloudy"",
  ""cloudCoverage"": 35,
  ""rainDetected"": false,
  ""fogDetected"": false,
  ""isSafe"": true,
  ""confidence"": 90,
  ""description"": ""Thin cloud to the south, stars visible elsewhere.""
}";

        /// <summary>
        /// The shape of what issue #16 actually received: the answer stops in the middle of
        /// the description string, six lines in, because the reasoning phase used up the
        /// output budget.
        /// </summary>
        private const string TruncatedAnswer = @"{
  ""condition"": ""PartlyCloudy"",
  ""cloudCoverage"": 35,
  ""rainDetected"": false,
  ""fogDetected"": false,
  ""description"": ""Thin cloud to the south, the horizon is br";

        [Test]
        public void ValidAnswer_IsRead() {
            var result = WeatherResponseParser.Parse(ValidAnswer);

            result.Condition.Should().Be(WeatherCondition.PartlyCloudy);
            result.CloudCoverage.Should().Be(35);
            result.Confidence.Should().Be(90);
            result.IsSafeForImaging.Should().BeTrue();
            result.RainDetected.Should().BeFalse();
            result.FogDetected.Should().BeFalse();
        }

        [Test]
        public void TruncatedAnswer_Throws_InsteadOfInventingAReading() {
            var act = () => WeatherResponseParser.Parse(TruncatedAnswer);

            act.Should().Throw<WeatherResponseParseException>();
        }

        [Test]
        public void TruncatedAnswer_SaysTheAnswerWasCutOff() {
            var act = () => WeatherResponseParser.Parse(TruncatedAnswer);

            // The owner of an observatory can act on "the answer was cut off" - it points at
            // the model and its budget. A byte offset in a payload they never see does not.
            act.Should().Throw<WeatherResponseParseException>()
                .WithMessage("*cut off*");
        }

        [TestCase("")]
        [TestCase("   ")]
        [TestCase(null)]
        [TestCase("I am sorry, I cannot analyse this image.")]
        [TestCase("{ \"condition\": \"Clear\" }")]
        [TestCase("{ \"condition\": \"Clear\", \"cloudCoverage\": \"quite a lot\", \"rainDetected\": false, \"fogDetected\": false, \"isSafe\": true }")]
        public void UnreadableAnswer_NeverProducesAResult(string? answer) {
            // Every unreadable answer must leave the caller with nothing to report. The one
            // outcome this parser may not have is a WeatherAnalysisResult nobody measured.
            var act = () => WeatherResponseParser.Parse(answer);

            act.Should().Throw<WeatherResponseParseException>();
        }

        [Test]
        public void FencedAnswer_IsRead() {
            var result = WeatherResponseParser.Parse("```json\n" + ValidAnswer + "\n```");

            result.CloudCoverage.Should().Be(35);
        }

        [Test]
        public void AnswerAfterAReasoningBlock_IsRead() {
            var withThinking = "<think>The sky is mostly dark with some haze to the south.</think>\n" + ValidAnswer;

            var result = WeatherResponseParser.Parse(withThinking);

            result.CloudCoverage.Should().Be(35);
        }

        [Test]
        public void AnswerWrappedInProse_IsRead() {
            var withProse = "Here is my analysis of the all-sky image:\n" + ValidAnswer + "\nLet me know if you need more detail.";

            var result = WeatherResponseParser.Parse(withProse);

            result.CloudCoverage.Should().Be(35);
        }

        [Test]
        public void AnswerWithoutConfidence_UsesTheDefault() {
            var noConfidence = @"{
  ""condition"": ""Clear"",
  ""cloudCoverage"": 5,
  ""rainDetected"": false,
  ""fogDetected"": false,
  ""isSafe"": true,
  ""description"": ""Clear sky.""
}";

            var result = WeatherResponseParser.Parse(noConfidence);

            result.Confidence.Should().Be(85);
        }

        [Test]
        public void RainAndFogFlags_SurviveTheParse() {
            var raining = @"{
  ""condition"": ""Rainy"",
  ""cloudCoverage"": 100,
  ""rainDetected"": true,
  ""fogDetected"": true,
  ""isSafe"": false,
  ""confidence"": 95,
  ""description"": ""Droplets on the dome.""
}";

            var result = WeatherResponseParser.Parse(raining);

            result.RainDetected.Should().BeTrue();
            result.FogDetected.Should().BeTrue();
            result.IsSafeForImaging.Should().BeFalse();
            result.Condition.Should().Be(WeatherCondition.Rainy);
        }

        [Test]
        public void UnknownConditionName_IsNotAParseFailure() {
            // A condition word the plugin does not know is still an answer: the numbers and
            // the flags are there, and they are what the safety decision is made of.
            var oddCondition = @"{
  ""condition"": ""Stratocumulus"",
  ""cloudCoverage"": 60,
  ""rainDetected"": false,
  ""fogDetected"": false,
  ""isSafe"": false,
  ""description"": ""Layered cloud.""
}";

            var result = WeatherResponseParser.Parse(oddCondition);

            result.Condition.Should().Be(WeatherCondition.Unknown);
            result.CloudCoverage.Should().Be(60);
        }

        [Test]
        public void TruncationIsToldApartFromAnAnswerThatIsMerelyWrong() {
            WeatherResponseParser.LooksTruncated("{ \"a\": 1").Should().BeTrue();
            WeatherResponseParser.LooksTruncated("{ \"a\": [1, 2").Should().BeTrue();
            WeatherResponseParser.LooksTruncated("{ \"a\": 1 }").Should().BeFalse();
            WeatherResponseParser.LooksTruncated("not json at all").Should().BeFalse();
        }
    }
}
