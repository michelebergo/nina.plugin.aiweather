using System;

namespace AIWeather.Services
{
    /// <summary>
    /// Builds the analysis service the options describe. One place, used by the safety
    /// monitor and by the "test analysis" button alike, so a test exercises exactly the
    /// request the monitor will send at night - a test that goes through a different path
    /// proves nothing about the night (a lesson from a Test button that once checked a
    /// hardcoded model).
    /// </summary>
    public static class AnalysisServiceFactory
    {
        /// <summary>The provider the options currently select, normalised.</summary>
        public static string SelectedProvider()
        {
            var provider = Properties.Settings.Default.AnalysisProvider;
            if (string.IsNullOrWhiteSpace(provider))
            {
                provider = Properties.Settings.Default.UseGitHubModels ? "GitHubModels" : "Local";
            }
            return provider.Trim();
        }

        public static IWeatherAnalysisService CreateFromSettings()
        {
            var provider = SelectedProvider();
            var model = Properties.Settings.Default.SelectedModel;

            if (string.Equals(provider, "GitHubModels", StringComparison.OrdinalIgnoreCase))
            {
                return new GitHubModelsAnalysisService(
                    Properties.Settings.Default.GitHubToken,
                    model);
            }

            if (string.Equals(provider, "OpenAI", StringComparison.OrdinalIgnoreCase))
            {
                return new OpenAIAnalysisService(
                    Properties.Settings.Default.OpenAIKey,
                    model);
            }

            if (string.Equals(provider, "Gemini", StringComparison.OrdinalIgnoreCase))
            {
                return new GeminiAnalysisService(
                    Properties.Settings.Default.GeminiKey,
                    model);
            }

            if (string.Equals(provider, "Anthropic", StringComparison.OrdinalIgnoreCase))
            {
                return new AnthropicAnalysisService(
                    Properties.Settings.Default.AnthropicKey,
                    model);
            }

            if (string.Equals(provider, "Ollama", StringComparison.OrdinalIgnoreCase))
            {
                return new OllamaAnalysisService(
                    Properties.Settings.Default.OllamaBaseUrl,
                    model,
                    Properties.Settings.Default.OllamaDisableThinking);
            }

            return new LocalWeatherAnalysisService();
        }
    }
}
