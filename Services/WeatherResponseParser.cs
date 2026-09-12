using System;
using System.Collections.Generic;
using System.Text.Json;
using System.Text.RegularExpressions;
using AIWeather.Models;

namespace AIWeather.Services
{
    /// <summary>
    /// Thrown when a provider's answer cannot be read as a weather analysis. It is an
    /// exception rather than a result on purpose: a reading that could not be taken is
    /// an absence, not a value, and the provider services already know how to handle a
    /// failure — they fall back to the offline analyzer.
    /// </summary>
    public class WeatherResponseParseException : Exception
    {
        /// <summary>The text that could not be parsed, after cleanup. For logs only.</summary>
        public string RawResponse { get; }

        public WeatherResponseParseException(string message, string rawResponse, Exception? inner = null)
            : base(message, inner)
        {
            RawResponse = rawResponse ?? string.Empty;
        }
    }

    /// <summary>
    /// Reads the JSON weather analysis out of whatever a vision model actually sends back.
    /// One parser for every provider: the same answer shape is requested from all of them,
    /// and four copies of this logic meant four copies of every bug in it.
    ///
    /// Tolerant about the packaging - code fences, reasoning blocks, prose around the JSON -
    /// and strict about the content: if the analysis cannot be read, it throws.
    /// </summary>
    public static class WeatherResponseParser
    {
        /// <summary>
        /// Parse a model answer into a weather analysis.
        /// </summary>
        /// <exception cref="WeatherResponseParseException">
        /// The answer is not a readable analysis - truncated, empty, or missing a field.
        /// </exception>
        public static WeatherAnalysisResult Parse(string? response)
        {
            var cleaned = Clean(response);

            if (string.IsNullOrWhiteSpace(cleaned))
            {
                throw new WeatherResponseParseException("The model returned an empty answer", cleaned);
            }

            try
            {
                using var json = JsonDocument.Parse(cleaned);
                var root = json.RootElement;

                var conditionStr = root.GetProperty("condition").GetString() ?? "Unknown";
                var condition = Enum.TryParse<WeatherCondition>(conditionStr, true, out var parsedCondition)
                    ? parsedCondition
                    : WeatherCondition.Unknown;

                var cloudCoverage = root.GetProperty("cloudCoverage").GetDouble();
                var rainDetected = root.GetProperty("rainDetected").GetBoolean();
                var fogDetected = root.GetProperty("fogDetected").GetBoolean();
                var isSafe = root.GetProperty("isSafe").GetBoolean();
                var description = root.TryGetProperty("description", out var descProp)
                    ? descProp.GetString() ?? string.Empty
                    : string.Empty;
                var confidence = root.TryGetProperty("confidence", out var confProp) ? confProp.GetDouble() : 85.0;

                return new WeatherAnalysisResult
                {
                    Timestamp = DateTime.UtcNow,
                    Condition = condition,
                    CloudCoverage = cloudCoverage,
                    Confidence = confidence,
                    IsSafeForImaging = isSafe,
                    Description = description,
                    RainDetected = rainDetected,
                    FogDetected = fogDetected,
                    RawAnalysisData = cleaned
                };
            }
            catch (Exception ex) when (ex is JsonException || ex is KeyNotFoundException || ex is InvalidOperationException || ex is FormatException)
            {
                throw new WeatherResponseParseException(DescribeFailure(ex, cleaned), cleaned, ex);
            }
        }

        /// <summary>
        /// A message the owner of an observatory can act on. A raw JSON reader error names a
        /// byte offset in a payload they will never see; what they need to know is whether the
        /// answer arrived incomplete - which is a budget or a reasoning problem, not a fluke.
        /// </summary>
        private static string DescribeFailure(Exception ex, string cleaned)
        {
            if (ex is JsonException && LooksTruncated(cleaned))
            {
                return $"The model's answer was cut off before it ended ({ex.Message})";
            }

            return ex is JsonException
                ? $"The model's answer was not valid JSON ({ex.Message})"
                : $"The model's answer was missing an expected field ({ex.Message})";
        }

        /// <summary>
        /// An answer that opens more braces or brackets than it closes stopped mid-way. The
        /// check is deliberately crude: it only has to separate "arrived incomplete" from
        /// "arrived complete and wrong", and a brace inside a string is rare enough in this
        /// schema to be worth the false positive.
        /// </summary>
        public static bool LooksTruncated(string text)
        {
            if (string.IsNullOrEmpty(text)) { return false; }

            var depth = 0;
            foreach (var c in text)
            {
                if (c == '{' || c == '[') { depth++; }
                else if (c == '}' || c == ']') { depth--; }
            }

            return depth > 0;
        }

        /// <summary>
        /// Strip the packaging models wrap their answer in: markdown code fences, inline
        /// reasoning blocks, and any prose before or after the object.
        /// </summary>
        private static string Clean(string? response)
        {
            if (string.IsNullOrWhiteSpace(response)) { return string.Empty; }

            var text = StripThinkingTags(response.Trim());

            if (text.StartsWith("```json", StringComparison.OrdinalIgnoreCase))
            {
                text = text.Substring(7);
            }
            if (text.StartsWith("```", StringComparison.Ordinal))
            {
                text = text.Substring(3);
            }
            if (text.EndsWith("```", StringComparison.Ordinal))
            {
                text = text.Substring(0, text.Length - 3);
            }
            text = text.Trim();

            // Tolerate prose around the JSON (e.g. an answer recovered from a reasoning
            // field): fall back to the outermost {...} block.
            if (!text.StartsWith("{", StringComparison.Ordinal))
            {
                var start = text.IndexOf('{');
                var end = text.LastIndexOf('}');
                if (start >= 0 && end > start)
                {
                    text = text.Substring(start, end - start + 1);
                }
                else if (start >= 0)
                {
                    // Opening brace but no closing one: a truncated answer. Keep it as it is
                    // so the parse fails and the caller is told the answer was cut off.
                    text = text.Substring(start);
                }
            }

            return text.Trim();
        }

        /// <summary>
        /// Some models (e.g. Qwen 3.x) emit their reasoning inline as &lt;think&gt;...&lt;/think&gt;
        /// blocks inside the content; the answer follows the closing tag.
        /// </summary>
        private static string StripThinkingTags(string text)
        {
            if (string.IsNullOrEmpty(text)) { return text; }

            return Regex.Replace(
                text, "<think>.*?</think>", string.Empty,
                RegexOptions.Singleline | RegexOptions.IgnoreCase).Trim();
        }
    }
}
