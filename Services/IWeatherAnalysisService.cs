using AIWeather.Models;
using System.Drawing;
using System.Threading;
using System.Threading.Tasks;

namespace AIWeather.Services
{
    /// <summary>
    /// Interface for weather analysis services
    /// </summary>
    public interface IWeatherAnalysisService
    {
        Task<WeatherAnalysisResult> AnalyzeImageAsync(Bitmap image, CancellationToken cancellationToken = default);
        Task<bool> InitializeAsync(CancellationToken cancellationToken = default);
    }
}
