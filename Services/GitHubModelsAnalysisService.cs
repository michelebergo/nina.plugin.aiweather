using AIWeather.Models;
using NINA.Core.Utility;
using System;
using System.Drawing;
using System.Threading;
using System.Threading.Tasks;

namespace AIWeather.Services
{
    /// <summary>
    /// GitHub Models-based weather analysis service — RETIRED.
    /// GitHub shut the GitHub Models service down for every customer on July 30, 2026
    /// (playground, model catalog, inference API and BYOK): the endpoint this service
    /// called returns 404 unconditionally. Instead of paying a failed network round-trip
    /// and a confusing error on every monitoring cycle, the analysis goes straight to the
    /// local heuristic fallback and the result says why. The type is kept so stored
    /// configurations that still select this provider keep loading.
    /// </summary>
    [System.Runtime.Versioning.SupportedOSPlatform("windows")]
    public class GitHubModelsAnalysisService : IWeatherAnalysisService
    {
        internal const string RetirementMessage =
            "GitHub Models was retired by GitHub on July 30, 2026 and no longer works for anyone. " +
            "Switch provider in the AI Weather options: Ollama/Custom (local, free), Google Gemini or OpenAI.";

        private static bool _retirementLogged;

        public GitHubModelsAnalysisService(string githubToken, string modelName)
        {
            // Parameters intentionally ignored: there is no service left to call.
        }

        public Task<bool> InitializeAsync(CancellationToken cancellationToken = default)
        {
            if (!_retirementLogged)
            {
                _retirementLogged = true;
                Logger.Warning(RetirementMessage);
            }
            // Initialization "succeeds" so a stored GitHub configuration keeps loading;
            // every analysis then runs the local fallback with an explanatory description.
            return Task.FromResult(true);
        }

        public async Task<WeatherAnalysisResult> AnalyzeImageAsync(Bitmap image, AstroContext? astroContext = null, CancellationToken cancellationToken = default)
        {
            if (!_retirementLogged)
            {
                _retirementLogged = true;
                Logger.Warning(RetirementMessage);
            }
            var fallback = new LocalWeatherAnalysisService();
            var result = await fallback.AnalyzeImageAsync(image, astroContext, cancellationToken);
            result.Description = $"[Fallback: Local] GitHub Models is retired — switch provider in the options. {result.Description}";
            return result;
        }
    }
}
