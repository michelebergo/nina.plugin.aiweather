using AIWeather.Models;
using System;
using System.Text.Json;

namespace AIWeather.Services
{
    /// <summary>
    /// Secret-free projection of the encrypted failover configuration cached by a replica.
    /// The options page binds only to this type so camera passwords and provider keys can
    /// never be exposed by a TextBlock, copied from the visual tree, or captured in a normal
    /// screenshot of the read-only synchronized-settings panel.
    /// </summary>
    public sealed class AIWeatherReplicaConfigurationSummary
    {
        public CaptureMode CaptureMode { get; init; }
        public string CaptureSource { get; init; } = string.Empty;
        public bool CaptureCredentialsConfigured { get; init; }
        public int CheckIntervalMinutes { get; init; }
        public bool UseSunAltitudeLimit { get; init; }
        public double SunAltitudeLimitDegrees { get; init; }
        public double CloudCoverageThreshold { get; init; }
        public double CloudCoverageSafeThreshold { get; init; }
        public int MaxDataAgeMinutes { get; init; }
        public string AnalysisProvider { get; init; } = "Local";
        public string SelectedModel { get; init; } = string.Empty;
        public bool ApiCredentialRequired { get; init; }
        public bool ApiCredentialConfigured { get; init; }
        public int GeminiRequestEveryChecks { get; init; } = 1;
        public int GeminiFreeCycleCount { get; init; } = 2;
        public string GeminiFreeModelOrder { get; init; } = string.Empty;
        public string ProviderEndpoint { get; init; } = string.Empty;
        public bool OllamaDisableThinking { get; init; }
        public int CameraView { get; init; }
        public int SiteDarkness { get; init; }
        public bool SiteNotesConfigured { get; init; }
        public string Revision { get; init; } = string.Empty;
        public DateTime GeneratedUtc { get; init; }

        public static AIWeatherReplicaConfigurationSummary FromEncryptedCache(
            string serializedEnvelope,
            string token)
        {
            if (string.IsNullOrWhiteSpace(serializedEnvelope))
            {
                throw new InvalidOperationException("The synchronized failover configuration cache is empty.");
            }

            var envelope = JsonSerializer.Deserialize<AIWeatherFailoverConfigurationEnvelope>(serializedEnvelope)
                           ?? throw new InvalidOperationException(
                               "The synchronized failover configuration envelope is empty.");
            var configuration = AIWeatherClusterProtocol.DecryptFailoverConfiguration(envelope, token);
            return FromConfiguration(configuration, envelope.Revision, envelope.GeneratedUtc);
        }

        public static AIWeatherReplicaConfigurationSummary FromConfiguration(
            AIWeatherFailoverConfiguration configuration,
            string revision,
            DateTime generatedUtc)
        {
            ArgumentNullException.ThrowIfNull(configuration);
            var normalized = configuration.Normalize();
            if (!normalized.TryValidate(out var validationError))
            {
                throw new InvalidOperationException(validationError);
            }

            var captureMode = (CaptureMode)normalized.CaptureMode;
            var captureSource = captureMode switch
            {
                CaptureMode.RTSPStream => RedactUrlForDisplay(normalized.RtspUrl),
                CaptureMode.INDICamera => RedactUrlForDisplay(normalized.HttpImageUrl),
                CaptureMode.FolderWatch => normalized.FolderPath,
                _ => string.Empty
            } ?? string.Empty;

            var provider = string.IsNullOrWhiteSpace(normalized.AnalysisProvider)
                ? "Local"
                : normalized.AnalysisProvider.Trim();
            var (credentialRequired, credentialConfigured) = ProviderCredentialState(
                provider,
                normalized);

            return new AIWeatherReplicaConfigurationSummary
            {
                CaptureMode = captureMode,
                CaptureSource = captureSource,
                CaptureCredentialsConfigured = HasCaptureCredentials(normalized, captureMode),
                CheckIntervalMinutes = normalized.CheckIntervalMinutes,
                UseSunAltitudeLimit = normalized.UseSunAltitudeLimit,
                SunAltitudeLimitDegrees = normalized.SunAltitudeLimitDegrees,
                CloudCoverageThreshold = normalized.CloudCoverageThreshold,
                CloudCoverageSafeThreshold = normalized.CloudCoverageSafeThreshold,
                MaxDataAgeMinutes = normalized.MaxDataAgeMinutes,
                AnalysisProvider = provider,
                SelectedModel = normalized.SelectedModel,
                ApiCredentialRequired = credentialRequired,
                ApiCredentialConfigured = credentialConfigured,
                GeminiRequestEveryChecks = GeminiProviderProfile.IsPaid(provider)
                    ? normalized.GeminiPaidRequestEveryChecks
                    : normalized.GeminiRequestEveryChecks,
                GeminiFreeCycleCount = normalized.GeminiFreeCycleCount,
                GeminiFreeModelOrder = string.Join(" → ",
                    GeminiProviderProfile.ParseFreeModelOrder(normalized.GeminiFreeModelOrder)),
                ProviderEndpoint = string.Equals(provider, "Ollama", StringComparison.OrdinalIgnoreCase)
                    ? RedactUrlForDisplay(normalized.OllamaBaseUrl)
                    : string.Empty,
                OllamaDisableThinking = normalized.OllamaDisableThinking,
                CameraView = normalized.CameraView,
                SiteDarkness = normalized.SiteDarkness,
                SiteNotesConfigured = !string.IsNullOrWhiteSpace(normalized.SiteNotes),
                Revision = revision?.Trim() ?? string.Empty,
                GeneratedUtc = generatedUtc.Kind == DateTimeKind.Utc
                    ? generatedUtc
                    : generatedUtc.ToUniversalTime()
            };
        }

        private static bool HasCaptureCredentials(
            AIWeatherFailoverConfiguration configuration,
            CaptureMode captureMode)
        {
            if (captureMode == CaptureMode.FolderWatch)
            {
                return false;
            }
            if (!string.IsNullOrWhiteSpace(configuration.RtspUsername)
                || !string.IsNullOrWhiteSpace(configuration.RtspPassword))
            {
                return true;
            }

            var source = captureMode == CaptureMode.INDICamera
                ? configuration.HttpImageUrl
                : configuration.RtspUrl;

            try
            {
                return Uri.TryCreate(source, UriKind.Absolute, out var uri)
                       && !string.IsNullOrWhiteSpace(uri.UserInfo);
            }
            catch
            {
                return false;
            }
        }

        private static (bool Required, bool Configured) ProviderCredentialState(
            string provider,
            AIWeatherFailoverConfiguration configuration)
        {
            if (string.Equals(provider, "GitHubModels", StringComparison.OrdinalIgnoreCase))
            {
                return (true, !string.IsNullOrWhiteSpace(configuration.GitHubToken));
            }
            if (string.Equals(provider, "OpenAI", StringComparison.OrdinalIgnoreCase))
            {
                return (true, !string.IsNullOrWhiteSpace(configuration.OpenAIKey));
            }
            if (GeminiProviderProfile.IsPaid(provider))
            {
                return (true, !string.IsNullOrWhiteSpace(configuration.GeminiPaidKey));
            }
            if (GeminiProviderProfile.IsFree(provider))
            {
                return (true, !string.IsNullOrWhiteSpace(configuration.GeminiKey));
            }
            if (string.Equals(provider, "Anthropic", StringComparison.OrdinalIgnoreCase))
            {
                return (true, !string.IsNullOrWhiteSpace(configuration.AnthropicKey));
            }

            // Local and Ollama/OpenAI-compatible local servers require no provider API key
            // in this plugin. Their endpoint remains visible after sensitive query values
            // are redacted.
            return (false, false);
        }

        private static string RedactUrlForDisplay(string? value)
        {
            if (string.IsNullOrWhiteSpace(value))
            {
                return string.Empty;
            }

            var sanitized = value;
            try
            {
                if (Uri.TryCreate(value, UriKind.Absolute, out var uri)
                    && !string.IsNullOrWhiteSpace(uri.UserInfo))
                {
                    sanitized = new UriBuilder(uri)
                    {
                        UserName = "***",
                        Password = "***"
                    }.Uri.ToString();
                }
            }
            catch
            {
                // The shared redactor still applies best-effort pattern handling below.
            }

            return LogRedactor.RedactSensitiveText(sanitized) ?? string.Empty;
        }
    }
}
