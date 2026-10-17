using AIWeather.Models;
using System;
using System.Text.Json.Serialization;

namespace AIWeather.Services
{
    public sealed class AIWeatherFailoverConfiguration
    {
        public int ConfigurationVersion { get; set; } = 1;
        public int CaptureMode { get; set; }
        public string RtspUrl { get; set; } = string.Empty;
        public string RtspUsername { get; set; } = string.Empty;
        public string RtspPassword { get; set; } = string.Empty;
        public string HttpImageUrl { get; set; } = string.Empty;
        public string FolderPath { get; set; } = string.Empty;
        public int CheckIntervalMinutes { get; set; } = 5;
        public bool UseSunAltitudeLimit { get; set; }
        public double SunAltitudeLimitDegrees { get; set; } = -6;
        public double CloudCoverageThreshold { get; set; } = 70;
        public double CloudCoverageSafeThreshold { get; set; } = 60;
        public int MaxDataAgeMinutes { get; set; }
        public string AnalysisProvider { get; set; } = "Local";
        public string SelectedModel { get; set; } = string.Empty;
        public bool UseGitHubModels { get; set; }
        public string GitHubToken { get; set; } = string.Empty;
        public string OpenAIKey { get; set; } = string.Empty;
        public string GeminiKey { get; set; } = string.Empty;
        public int GeminiRequestEveryChecks { get; set; } = 1;
        public string GeminiFreeModelOrder { get; set; } = string.Empty;
        public int GeminiFreeCycleCount { get; set; } = 2;
        public string GeminiPaidKey { get; set; } = string.Empty;
        public int GeminiPaidRequestEveryChecks { get; set; } = 1;
        public string AnthropicKey { get; set; } = string.Empty;
        public string OllamaBaseUrl { get; set; } = string.Empty;
        public bool OllamaDisableThinking { get; set; } = true;

        // Omit defaults to preserve the canonical JSON/HMAC of pre-1.15.8 configurations.
        // Non-default prompt settings require upgraded replicas (old peers fail closed).
        [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingDefault)]
        public int CameraView { get; set; }
        [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingDefault)]
        public int SiteDarkness { get; set; }
        [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
        public string? SiteNotes { get; set; }

        public static AIWeatherFailoverConfiguration FromSettings()
        {
            var settings = Properties.Settings.Default;
            return new AIWeatherFailoverConfiguration
            {
                CaptureMode = settings.CaptureMode,
                RtspUrl = settings.RtspUrl ?? string.Empty,
                RtspUsername = settings.RtspUsername ?? string.Empty,
                RtspPassword = settings.RtspPassword ?? string.Empty,
                HttpImageUrl = settings.INDIDeviceName ?? string.Empty,
                FolderPath = settings.FolderPath ?? string.Empty,
                CheckIntervalMinutes = settings.CheckIntervalMinutes,
                UseSunAltitudeLimit = settings.UseSunAltitudeLimit,
                SunAltitudeLimitDegrees = settings.SunAltitudeLimitDegrees,
                CloudCoverageThreshold = settings.CloudCoverageThreshold,
                CloudCoverageSafeThreshold = settings.CloudCoverageSafeThreshold,
                MaxDataAgeMinutes = settings.MaxDataAgeMinutes,
                AnalysisProvider = settings.AnalysisProvider ?? "Local",
                SelectedModel = settings.SelectedModel ?? string.Empty,
                UseGitHubModels = settings.UseGitHubModels,
                GitHubToken = settings.GitHubToken ?? string.Empty,
                OpenAIKey = settings.OpenAIKey ?? string.Empty,
                GeminiKey = settings.GeminiKey ?? string.Empty,
                GeminiRequestEveryChecks = settings.GeminiRequestEveryChecks,
                GeminiFreeModelOrder = settings.GeminiFreeModelOrder ?? string.Empty,
                GeminiFreeCycleCount = settings.GeminiFreeCycleCount,
                GeminiPaidKey = settings.GeminiPaidKey ?? string.Empty,
                GeminiPaidRequestEveryChecks = settings.GeminiPaidRequestEveryChecks,
                AnthropicKey = settings.AnthropicKey ?? string.Empty,
                OllamaBaseUrl = settings.OllamaBaseUrl ?? string.Empty,
                OllamaDisableThinking = settings.OllamaDisableThinking,
                CameraView = settings.CameraView,
                SiteDarkness = settings.SiteDarkness,
                SiteNotes = settings.SiteNotes
            }.Normalize();
        }

        public AIWeatherFailoverConfiguration Normalize()
        {
            return new AIWeatherFailoverConfiguration
            {
                ConfigurationVersion = 1,
                CaptureMode = Math.Clamp(CaptureMode, 0, 2),
                RtspUrl = Clamp(RtspUrl, 4096),
                RtspUsername = Clamp(RtspUsername, 1024),
                RtspPassword = Clamp(RtspPassword, 4096),
                HttpImageUrl = Clamp(HttpImageUrl, 4096),
                FolderPath = Clamp(FolderPath, 4096),
                CheckIntervalMinutes = Math.Clamp(CheckIntervalMinutes, 1, 1440),
                UseSunAltitudeLimit = UseSunAltitudeLimit,
                SunAltitudeLimitDegrees = Math.Clamp(SunAltitudeLimitDegrees, -90, 90),
                CloudCoverageThreshold = Math.Clamp(CloudCoverageThreshold, 0, 100),
                CloudCoverageSafeThreshold = Math.Clamp(CloudCoverageSafeThreshold, 0, 100),
                MaxDataAgeMinutes = Math.Clamp(MaxDataAgeMinutes, 0, 10080),
                AnalysisProvider = Clamp(AnalysisProvider, 64),
                SelectedModel = Clamp(SelectedModel, 256),
                UseGitHubModels = UseGitHubModels,
                GitHubToken = Clamp(GitHubToken, 8192),
                OpenAIKey = Clamp(OpenAIKey, 8192),
                GeminiKey = Clamp(GeminiKey, 8192),
                GeminiRequestEveryChecks = Math.Clamp(GeminiRequestEveryChecks, 1, 10000),
                GeminiFreeModelOrder = GeminiProviderProfile.SerializeFreeModelOrder(
                    GeminiProviderProfile.ParseFreeModelOrder(GeminiFreeModelOrder)),
                GeminiFreeCycleCount = Math.Clamp(GeminiFreeCycleCount, 1, 10),
                GeminiPaidKey = Clamp(GeminiPaidKey, 8192),
                GeminiPaidRequestEveryChecks = Math.Clamp(GeminiPaidRequestEveryChecks, 1, 10000),
                AnthropicKey = Clamp(AnthropicKey, 8192),
                OllamaBaseUrl = Clamp(OllamaBaseUrl, 4096),
                OllamaDisableThinking = OllamaDisableThinking,
                CameraView = CameraView == 1 ? 1 : 0,
                SiteDarkness = SiteDarkness == 1 ? 1 : 0,
                SiteNotes = string.IsNullOrWhiteSpace(SiteNotes)
                    ? null : Clamp(SiteNotes, WeatherAnalysisPrompts.MaxSiteNotesLength)
            };
        }

        public bool TryValidate(out string error)
        {
            error = string.Empty;
            if (ConfigurationVersion != 1 || !Enum.IsDefined(typeof(CaptureMode), CaptureMode))
            {
                error = "Failover configuration version or capture mode is invalid.";
                return false;
            }
            if (CloudCoverageSafeThreshold > CloudCoverageThreshold)
            {
                error = "Failover cloud safe threshold cannot exceed the unsafe threshold.";
                return false;
            }

            switch ((Models.CaptureMode)CaptureMode)
            {
                case Models.CaptureMode.RTSPStream:
                    if (!Uri.TryCreate(RtspUrl, UriKind.Absolute, out var rtsp)
                        || (!string.Equals(rtsp.Scheme, "rtsp", StringComparison.OrdinalIgnoreCase)
                            && !string.Equals(rtsp.Scheme, "rtsps", StringComparison.OrdinalIgnoreCase)))
                    {
                        error = "Failover RTSP URL is missing or invalid.";
                        return false;
                    }
                    break;
                case Models.CaptureMode.INDICamera:
                    if (!Uri.TryCreate(HttpImageUrl, UriKind.Absolute, out var http)
                        || (http.Scheme != Uri.UriSchemeHttp && http.Scheme != Uri.UriSchemeHttps))
                    {
                        error = "Failover HTTP image URL is missing or invalid.";
                        return false;
                    }
                    break;
                case Models.CaptureMode.FolderWatch:
                    if (string.IsNullOrWhiteSpace(FolderPath))
                    {
                        error = "Failover folder path is missing.";
                        return false;
                    }
                    break;
            }

            if (string.IsNullOrWhiteSpace(AnalysisProvider))
            {
                error = "Failover analysis provider is missing.";
                return false;
            }
            return true;
        }

        private static string Clamp(string? value, int maximum)
        {
            var normalized = value?.Trim() ?? string.Empty;
            return normalized.Length <= maximum ? normalized : normalized[..maximum];
        }
    }

    public enum AIWeatherFailoverObservation
    {
        PrimaryReachable,
        NetworkUnavailable,
        FatalConfigurationFailure
    }

    public enum AIWeatherFailoverTransition
    {
        None,
        ActivateLocal,
        ReturnToPrimary
    }

    /// <summary>
    /// Pure timing state used by the replica runtime and smoke tests. It deliberately knows
    /// nothing about cameras or N.I.N.A.; those resources are changed only after a transition.
    /// </summary>
    public sealed class AIWeatherFailoverStateMachine
    {
        private DateTime? _unavailableSinceUtc;
        private DateTime? _recoverySinceUtc;

        public bool LocalActive { get; private set; }
        public DateTime? UnavailableSinceUtc => _unavailableSinceUtc;
        public DateTime? RecoverySinceUtc => _recoverySinceUtc;

        public AIWeatherFailoverTransition Observe(
            AIWeatherFailoverObservation observation,
            DateTime utcNow,
            bool enabled,
            bool configurationReady,
            TimeSpan failoverAfter,
            TimeSpan recoveryStable)
        {
            utcNow = DateTime.SpecifyKind(utcNow.ToUniversalTime(), DateTimeKind.Utc);
            if (observation == AIWeatherFailoverObservation.PrimaryReachable)
            {
                _unavailableSinceUtc = null;
                if (!LocalActive)
                {
                    _recoverySinceUtc = null;
                    return AIWeatherFailoverTransition.None;
                }
                _recoverySinceUtc ??= utcNow;
                if (utcNow - _recoverySinceUtc.Value >= recoveryStable)
                {
                    LocalActive = false;
                    _recoverySinceUtc = null;
                    return AIWeatherFailoverTransition.ReturnToPrimary;
                }
                return AIWeatherFailoverTransition.None;
            }

            _recoverySinceUtc = null;
            if (observation == AIWeatherFailoverObservation.FatalConfigurationFailure)
            {
                _unavailableSinceUtc = null;
                return AIWeatherFailoverTransition.None;
            }

            _unavailableSinceUtc ??= utcNow;
            if (!LocalActive
                && enabled
                && configurationReady
                && utcNow - _unavailableSinceUtc.Value >= failoverAfter)
            {
                LocalActive = true;
                return AIWeatherFailoverTransition.ActivateLocal;
            }
            return AIWeatherFailoverTransition.None;
        }

        public void AbortLocalActivation()
        {
            LocalActive = false;
            _recoverySinceUtc = null;
        }

        public void Reset()
        {
            LocalActive = false;
            _unavailableSinceUtc = null;
            _recoverySinceUtc = null;
        }
    }
}
