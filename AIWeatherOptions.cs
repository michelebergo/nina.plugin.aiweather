using NINA.Core.Utility;
using NINA.Profile;
using NINA.Profile.Interfaces;
using AIWeather.Localization;
using AIWeather.Services;
using System;
using System.ComponentModel;
using System.Globalization;
using System.Runtime.CompilerServices;
using System.Windows;
using AIWeather.Models;

namespace AIWeather
{
    /// <summary>
    /// Plugin configuration options
    /// </summary>
    public class AIWeatherOptions : BaseINPC
    {
        private readonly IProfileService _profileService;
        private AIWeatherReplicaConfigurationSummary? _replicaConfigurationSummary;
        private string _replicaConfigurationLoadError = string.Empty;

        // Centralized external link (single point of truth; see .github/FUNDING.yml and README)
        public const string BuyMeACoffeeUrl = "https://buymeacoffee.com/michelebergo";

        public System.Windows.Input.ICommand OpenSupportPageCommand { get; } = new RelayCommand(_ => OpenExternalUrl(BuyMeACoffeeUrl));

        private static void OpenExternalUrl(string url)
        {
            if (!System.Uri.TryCreate(url, System.UriKind.Absolute, out var uri))
            {
                return;
            }

            try
            {
                System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo
                {
                    FileName = uri.AbsoluteUri,
                    UseShellExecute = true
                });
            }
            catch (System.Exception ex)
            {
                Logger.Error($"Unable to open external URL: {uri}", ex);
            }
        }

        public AIWeatherOptions(IProfileService profileService)
        {
            _profileService = profileService;
            
            // Initialize default settings
            InitializeOptions();
            Properties.Settings.Default.PropertyChanged += Settings_PropertyChanged;
            RefreshReplicaConfigurationView();
        }

        private void InitializeOptions()
        {
            // Load saved settings or set defaults
            if (Properties.Settings.Default.RtspUrl == null)
            {
                Properties.Settings.Default.RtspUrl = "rtsp://192.168.1.100:554/stream";
                Properties.Settings.Default.CheckIntervalMinutes = 5;
                Properties.Settings.Default.CloudCoverageThreshold = 70.0;
                Properties.Settings.Default.CloudCoverageSafeThreshold = 60.0;
                Properties.Settings.Default.UseGitHubModels = false;
                Properties.Settings.Default.SelectedModel = "gpt-4o";
                Properties.Settings.Default.CaptureMode = 0; // Default to RTSP
                Properties.Settings.Default.FolderPath = "";
                CoreUtil.SaveSettings(Properties.Settings.Default);
            }
        }

        public ClusterNodeMode ClusterNodeMode
        {
            get => ClusterNodeModeParser.Parse(Properties.Settings.Default.ClusterNodeMode);
            set
            {
                Properties.Settings.Default.ClusterNodeMode = value.ToString();
                CoreUtil.SaveSettings(Properties.Settings.Default);
                RaisePropertyChanged();
                RaisePropertyChanged(nameof(ClusterNodeModeIndex));
                RaisePropertyChanged(nameof(IsClusterPrimary));
                RaisePropertyChanged(nameof(IsClusterReplica));
                RaisePropertyChanged(nameof(IsClusterLocalNode));
                RaisePropertyChanged(nameof(IsClusterNetworked));
                RaiseReplicaConfigurationProperties();
                RefreshReplicaConfigurationView();
            }
        }

        public int ClusterNodeModeIndex
        {
            get => (int)ClusterNodeMode;
            set => ClusterNodeMode = System.Enum.IsDefined(typeof(Models.ClusterNodeMode), value)
                ? (Models.ClusterNodeMode)value
                : Models.ClusterNodeMode.Standalone;
        }

        public bool IsClusterPrimary => ClusterNodeMode == Models.ClusterNodeMode.Primary;
        public bool IsClusterReplica => ClusterNodeMode == Models.ClusterNodeMode.Replica;
        public bool IsClusterLocalNode => !IsClusterReplica;
        public bool IsClusterNetworked => ClusterNodeMode != Models.ClusterNodeMode.Standalone;

        /// <summary>
        /// When encrypted synchronization is enabled on a replica, the primary's cached
        /// failover configuration is the only operational configuration that may be used
        /// for local takeover. The normal local fields therefore must not look editable.
        /// Manual pre-provisioning remains available when synchronization is disabled.
        /// </summary>
        public bool ShowReplicaSynchronizedConfiguration =>
            IsClusterReplica && ClusterFailoverConfigSyncEnabled;

        public bool ShowManualReplicaConfigurationNotice =>
            IsClusterReplica && !ClusterFailoverConfigSyncEnabled;

        public bool AreLocalOperationalSettingsVisible =>
            !ShowReplicaSynchronizedConfiguration;

        public string ReplicaConfigurationStatus
        {
            get
            {
                if (!IsClusterReplica)
                {
                    return string.Empty;
                }
                if (!ClusterFailoverConfigSyncEnabled)
                {
                    return UiLocalization.Text("Options.ReplicaSyncDisabled");
                }
                if (_replicaConfigurationSummary != null)
                {
                    return UiLocalization.Text("Options.ReplicaSyncReady");
                }
                if (!string.IsNullOrWhiteSpace(_replicaConfigurationLoadError))
                {
                    return UiLocalization.Text(
                        "Options.ReplicaSyncInvalid",
                        _replicaConfigurationLoadError);
                }
                if (!AIWeatherClusterProtocol.IsTokenUsable(ClusterSharedToken))
                {
                    return UiLocalization.Text("Options.ReplicaSyncMissingToken");
                }
                return UiLocalization.Text("Options.ReplicaSyncWaiting");
            }
        }

        public string ReplicaConfigurationRevision =>
            _replicaConfigurationSummary == null
                ? "—"
                : ShortRevision(_replicaConfigurationSummary.Revision);

        public string ReplicaConfigurationUpdated =>
            _replicaConfigurationSummary == null
                ? "—"
                : _replicaConfigurationSummary.GeneratedUtc
                    .ToLocalTime()
                    .ToString("yyyy-MM-dd HH:mm:ss", CultureInfo.CurrentCulture);

        public string ReplicaConfigurationCaptureMode =>
            _replicaConfigurationSummary?.CaptureMode switch
            {
                Models.CaptureMode.RTSPStream => UiLocalization.Text("Options.RtspMode"),
                Models.CaptureMode.INDICamera => UiLocalization.Text("Options.HttpMode"),
                Models.CaptureMode.FolderWatch => UiLocalization.Text("Options.FolderMode"),
                _ => "—"
            };

        public string ReplicaConfigurationCaptureSource =>
            DisplayOrDash(_replicaConfigurationSummary?.CaptureSource);

        public string ReplicaConfigurationCaptureCredentials =>
            _replicaConfigurationSummary == null
                ? "—"
                : UiLocalization.Text(
                    _replicaConfigurationSummary.CaptureCredentialsConfigured
                        ? "Options.ReplicaCredentialConfigured"
                        : "Options.ReplicaCredentialNotConfigured");

        public string ReplicaConfigurationCheckInterval =>
            _replicaConfigurationSummary == null
                ? "—"
                : UiLocalization.Text(
                    "Options.ReplicaMinutesValue",
                    _replicaConfigurationSummary.CheckIntervalMinutes);

        public string ReplicaConfigurationSolarGuard =>
            _replicaConfigurationSummary == null
                ? "—"
                : UiLocalization.Text(
                    _replicaConfigurationSummary.UseSunAltitudeLimit
                        ? "Options.ReplicaEnabled"
                        : "Options.ReplicaDisabled");

        public string ReplicaConfigurationSunAltitude =>
            _replicaConfigurationSummary == null
                ? "—"
                : _replicaConfigurationSummary.UseSunAltitudeLimit
                    ? $"{_replicaConfigurationSummary.SunAltitudeLimitDegrees.ToString("0.#", CultureInfo.CurrentCulture)}°"
                    : UiLocalization.Text("Options.ReplicaNotApplicable");

        public string ReplicaConfigurationCloudThresholds =>
            _replicaConfigurationSummary == null
                ? "—"
                : UiLocalization.Text(
                    "Options.ReplicaThresholdsValue",
                    _replicaConfigurationSummary.CloudCoverageThreshold.ToString("0.#", CultureInfo.CurrentCulture),
                    _replicaConfigurationSummary.CloudCoverageSafeThreshold.ToString("0.#", CultureInfo.CurrentCulture));

        public string ReplicaConfigurationMaxDataAge =>
            _replicaConfigurationSummary == null
                ? "—"
                : _replicaConfigurationSummary.MaxDataAgeMinutes <= 0
                    ? UiLocalization.Text("Options.ReplicaAutomatic")
                    : UiLocalization.Text(
                        "Options.ReplicaMinutesValue",
                        _replicaConfigurationSummary.MaxDataAgeMinutes);

        public string ReplicaConfigurationProvider
        {
            get
            {
                var provider = _replicaConfigurationSummary?.AnalysisProvider;
                if (Services.GeminiProviderProfile.IsPaid(provider))
                {
                    return UiLocalization.Text("Options.ProviderNameGeminiPaid");
                }
                if (Services.GeminiProviderProfile.IsFree(provider))
                {
                    return UiLocalization.Text("Options.ProviderNameGeminiFree");
                }
                return DisplayOrDash(provider);
            }
        }

        public string ReplicaConfigurationModel =>
            _replicaConfigurationSummary == null
                ? "—"
                : string.Equals(_replicaConfigurationSummary.AnalysisProvider, "Local", StringComparison.OrdinalIgnoreCase)
                  || Services.GeminiProviderProfile.IsFree(_replicaConfigurationSummary.AnalysisProvider)
                    ? UiLocalization.Text("Options.ReplicaNotApplicable")
                    : DisplayOrDash(_replicaConfigurationSummary.SelectedModel);

        public string ReplicaConfigurationPrompt => _replicaConfigurationSummary == null ? "—"
            : UiLocalization.Text("Options.ReplicaPromptValue",
                UiLocalization.Text(_replicaConfigurationSummary.CameraView == 1 ? "Options.CameraFixed" : "Options.CameraAllSky"),
                UiLocalization.Text(_replicaConfigurationSummary.SiteDarkness == 1 ? "Options.SiteDark" : "Options.SiteLightPolluted"),
                UiLocalization.Text(_replicaConfigurationSummary.SiteNotesConfigured ? "Options.ReplicaEnabled" : "Options.ReplicaDisabled"));

        public string ReplicaConfigurationApiCredential
        {
            get
            {
                if (_replicaConfigurationSummary == null)
                {
                    return "—";
                }
                if (!_replicaConfigurationSummary.ApiCredentialRequired)
                {
                    return UiLocalization.Text("Options.ReplicaCredentialNotRequired");
                }
                return UiLocalization.Text(
                    _replicaConfigurationSummary.ApiCredentialConfigured
                        ? "Options.ReplicaCredentialConfigured"
                        : "Options.ReplicaCredentialNotConfigured");
            }
        }

        public string ReplicaConfigurationProviderDetails
        {
            get
            {
                if (_replicaConfigurationSummary == null)
                {
                    return "—";
                }
                if (Services.GeminiProviderProfile.IsFree(
                        _replicaConfigurationSummary.AnalysisProvider))
                {
                    return UiLocalization.Text(
                        "Options.ReplicaGeminiFreeDetailsValue",
                        _replicaConfigurationSummary.GeminiRequestEveryChecks,
                        _replicaConfigurationSummary.GeminiFreeCycleCount,
                        _replicaConfigurationSummary.GeminiFreeModelOrder);
                }
                if (Services.GeminiProviderProfile.IsPaid(
                        _replicaConfigurationSummary.AnalysisProvider))
                {
                    return UiLocalization.Text(
                        "Options.ReplicaGeminiPacingValue",
                        _replicaConfigurationSummary.GeminiRequestEveryChecks);
                }
                if (string.Equals(
                        _replicaConfigurationSummary.AnalysisProvider,
                        "Ollama",
                        StringComparison.OrdinalIgnoreCase))
                {
                    return UiLocalization.Text(
                        "Options.ReplicaOllamaDetailsValue",
                        DisplayOrDash(_replicaConfigurationSummary.ProviderEndpoint),
                        UiLocalization.Text(
                            _replicaConfigurationSummary.OllamaDisableThinking
                                ? "Options.ReplicaEnabled"
                                : "Options.ReplicaDisabled"));
                }
                return UiLocalization.Text("Options.ReplicaNotApplicable");
            }
        }

        public int ClusterListenPort
        {
            get => Properties.Settings.Default.ClusterListenPort;
            set
            {
                Properties.Settings.Default.ClusterListenPort = System.Math.Clamp(value, 1, 65535);
                CoreUtil.SaveSettings(Properties.Settings.Default);
                RaisePropertyChanged();
            }
        }

        public string ClusterPrimaryUrl
        {
            get => Properties.Settings.Default.ClusterPrimaryUrl ?? string.Empty;
            set
            {
                Properties.Settings.Default.ClusterPrimaryUrl = value?.Trim() ?? string.Empty;
                CoreUtil.SaveSettings(Properties.Settings.Default);
                RaisePropertyChanged();
            }
        }

        public string ClusterSharedToken
        {
            get => Properties.Settings.Default.ClusterSharedToken ?? string.Empty;
            set
            {
                Properties.Settings.Default.ClusterSharedToken = value ?? string.Empty;
                CoreUtil.SaveSettings(Properties.Settings.Default);
                RaisePropertyChanged();
                RefreshReplicaConfigurationView();
            }
        }

        public int ClusterPollSeconds
        {
            get => Properties.Settings.Default.ClusterPollSeconds;
            set
            {
                Properties.Settings.Default.ClusterPollSeconds = System.Math.Clamp(value, 1, 300);
                CoreUtil.SaveSettings(Properties.Settings.Default);
                RaisePropertyChanged();
            }
        }

        public int ClusterStaleSeconds
        {
            get => Properties.Settings.Default.ClusterStaleSeconds;
            set
            {
                Properties.Settings.Default.ClusterStaleSeconds = System.Math.Clamp(value, 3, 3600);
                CoreUtil.SaveSettings(Properties.Settings.Default);
                RaisePropertyChanged();
            }
        }

        public bool ClusterAutomaticFailoverEnabled
        {
            get => Properties.Settings.Default.ClusterAutomaticFailoverEnabled;
            set
            {
                Properties.Settings.Default.ClusterAutomaticFailoverEnabled = value;
                CoreUtil.SaveSettings(Properties.Settings.Default);
                RaisePropertyChanged();
            }
        }

        public int ClusterFailoverAfterSeconds
        {
            get => Properties.Settings.Default.ClusterFailoverAfterSeconds;
            set
            {
                Properties.Settings.Default.ClusterFailoverAfterSeconds = System.Math.Clamp(value, 15, 3600);
                CoreUtil.SaveSettings(Properties.Settings.Default);
                RaisePropertyChanged();
            }
        }

        public int ClusterRecoveryStableSeconds
        {
            get => Properties.Settings.Default.ClusterRecoveryStableSeconds;
            set
            {
                Properties.Settings.Default.ClusterRecoveryStableSeconds = System.Math.Clamp(value, 5, 600);
                CoreUtil.SaveSettings(Properties.Settings.Default);
                RaisePropertyChanged();
            }
        }

        public bool ClusterFailoverConfigSyncEnabled
        {
            get => Properties.Settings.Default.ClusterFailoverConfigSyncEnabled;
            set
            {
                Properties.Settings.Default.ClusterFailoverConfigSyncEnabled = value;
                CoreUtil.SaveSettings(Properties.Settings.Default);
                RaisePropertyChanged();
                RaiseReplicaConfigurationProperties();
                RefreshReplicaConfigurationView();
            }
        }

        private void Settings_PropertyChanged(object? sender, PropertyChangedEventArgs e)
        {
            if (!string.IsNullOrEmpty(e.PropertyName)
                && e.PropertyName != nameof(Properties.Settings.Default.ClusterNodeMode)
                && e.PropertyName != nameof(Properties.Settings.Default.ClusterSharedToken)
                && e.PropertyName != nameof(Properties.Settings.Default.ClusterFailoverConfigSyncEnabled)
                && e.PropertyName != nameof(Properties.Settings.Default.ClusterFailoverConfigCache))
            {
                return;
            }

            var dispatcher = Application.Current?.Dispatcher;
            if (dispatcher != null && !dispatcher.CheckAccess())
            {
                dispatcher.BeginInvoke(new Action(() => Settings_PropertyChanged(sender, e)));
                return;
            }

            if (string.IsNullOrEmpty(e.PropertyName)
                || e.PropertyName == nameof(Properties.Settings.Default.ClusterNodeMode))
            {
                RaisePropertyChanged(nameof(ClusterNodeMode));
                RaisePropertyChanged(nameof(ClusterNodeModeIndex));
                RaisePropertyChanged(nameof(IsClusterPrimary));
                RaisePropertyChanged(nameof(IsClusterReplica));
                RaisePropertyChanged(nameof(IsClusterLocalNode));
                RaisePropertyChanged(nameof(IsClusterNetworked));
            }
            if (string.IsNullOrEmpty(e.PropertyName)
                || e.PropertyName == nameof(Properties.Settings.Default.ClusterSharedToken))
            {
                RaisePropertyChanged(nameof(ClusterSharedToken));
            }
            if (string.IsNullOrEmpty(e.PropertyName)
                || e.PropertyName == nameof(Properties.Settings.Default.ClusterFailoverConfigSyncEnabled))
            {
                RaisePropertyChanged(nameof(ClusterFailoverConfigSyncEnabled));
            }

            RaiseReplicaConfigurationProperties();
            RefreshReplicaConfigurationView();
        }

        private void RefreshReplicaConfigurationView()
        {
            _replicaConfigurationSummary = null;
            _replicaConfigurationLoadError = string.Empty;

            if (!IsClusterReplica || !ClusterFailoverConfigSyncEnabled)
            {
                RaiseReplicaConfigurationProperties();
                return;
            }

            var token = Properties.Settings.Default.ClusterSharedToken ?? string.Empty;
            var cache = Properties.Settings.Default.ClusterFailoverConfigCache ?? string.Empty;
            if (!AIWeatherClusterProtocol.IsTokenUsable(token) || string.IsNullOrWhiteSpace(cache))
            {
                RaiseReplicaConfigurationProperties();
                return;
            }

            try
            {
                _replicaConfigurationSummary =
                    AIWeatherReplicaConfigurationSummary.FromEncryptedCache(cache, token);
            }
            catch (Exception ex)
            {
                // Do not copy exception details into the UI: cryptographic and JSON errors
                // can contain fragments of malformed input. A concise state is enough and
                // the runtime separately logs the diagnostic when it loads the cache.
                _replicaConfigurationLoadError = ex is System.Security.Cryptography.CryptographicException
                    ? UiLocalization.Text("Options.ReplicaSyncDecryptFailed")
                    : UiLocalization.Text("Options.ReplicaSyncCacheInvalid");
            }

            RaiseReplicaConfigurationProperties();
        }

        private void RaiseReplicaConfigurationProperties()
        {
            RaisePropertyChanged(nameof(ShowReplicaSynchronizedConfiguration));
            RaisePropertyChanged(nameof(ShowManualReplicaConfigurationNotice));
            RaisePropertyChanged(nameof(AreLocalOperationalSettingsVisible));
            RaisePropertyChanged(nameof(ReplicaConfigurationStatus));
            RaisePropertyChanged(nameof(ReplicaConfigurationRevision));
            RaisePropertyChanged(nameof(ReplicaConfigurationUpdated));
            RaisePropertyChanged(nameof(ReplicaConfigurationCaptureMode));
            RaisePropertyChanged(nameof(ReplicaConfigurationCaptureSource));
            RaisePropertyChanged(nameof(ReplicaConfigurationCaptureCredentials));
            RaisePropertyChanged(nameof(ReplicaConfigurationCheckInterval));
            RaisePropertyChanged(nameof(ReplicaConfigurationSolarGuard));
            RaisePropertyChanged(nameof(ReplicaConfigurationSunAltitude));
            RaisePropertyChanged(nameof(ReplicaConfigurationCloudThresholds));
            RaisePropertyChanged(nameof(ReplicaConfigurationMaxDataAge));
            RaisePropertyChanged(nameof(ReplicaConfigurationProvider));
            RaisePropertyChanged(nameof(ReplicaConfigurationModel));
            RaisePropertyChanged(nameof(ReplicaConfigurationApiCredential));
            RaisePropertyChanged(nameof(ReplicaConfigurationProviderDetails));
            RaisePropertyChanged(nameof(ReplicaConfigurationPrompt));
        }

        private static string DisplayOrDash(string? value) =>
            string.IsNullOrWhiteSpace(value) ? "—" : value.Trim();

        private static string ShortRevision(string revision) =>
            string.IsNullOrWhiteSpace(revision)
                ? "—"
                : revision[..Math.Min(12, revision.Length)];

        public CaptureMode CaptureMode
        {
            get => (CaptureMode)Properties.Settings.Default.CaptureMode;
            set
            {
                Properties.Settings.Default.CaptureMode = (int)value;
                CoreUtil.SaveSettings(Properties.Settings.Default);
                RaisePropertyChanged();
            }
        }

        // Helper property for ComboBox SelectedIndex binding (returns int directly)
        public int CaptureModeIndex
        {
            get => Properties.Settings.Default.CaptureMode;
            set
            {
                if (Properties.Settings.Default.CaptureMode != value)
                {
                    Properties.Settings.Default.CaptureMode = value;
                    CoreUtil.SaveSettings(Properties.Settings.Default);
                    RaisePropertyChanged();
                    RaisePropertyChanged(nameof(CaptureMode)); // Also notify CaptureMode changed
                    Logger.Info($"Capture mode changed to: {(CaptureMode)value}");
                }
            }
        }

        public string RtspUrl
        {
            get => Properties.Settings.Default.RtspUrl;
            set
            {
                Properties.Settings.Default.RtspUrl = value;
                CoreUtil.SaveSettings(Properties.Settings.Default);
                RaisePropertyChanged();
            }
        }

        public int CheckIntervalMinutes
        {
            get => Properties.Settings.Default.CheckIntervalMinutes;
            set
            {
                Properties.Settings.Default.CheckIntervalMinutes = value;
                CoreUtil.SaveSettings(Properties.Settings.Default);
                RaisePropertyChanged();
            }
        }

        public int GeminiRequestEveryChecks
        {
            get => Properties.Settings.Default.GeminiRequestEveryChecks;
            set
            {
                Properties.Settings.Default.GeminiRequestEveryChecks = System.Math.Clamp(value, 1, 10000);
                CoreUtil.SaveSettings(Properties.Settings.Default);
                RaisePropertyChanged();
            }
        }

        public bool UseSunAltitudeLimit
        {
            get => Properties.Settings.Default.UseSunAltitudeLimit;
            set
            {
                Properties.Settings.Default.UseSunAltitudeLimit = value;
                CoreUtil.SaveSettings(Properties.Settings.Default);
                RaisePropertyChanged();
            }
        }

        public double SunAltitudeLimitDegrees
        {
            get => Properties.Settings.Default.SunAltitudeLimitDegrees;
            set
            {
                Properties.Settings.Default.SunAltitudeLimitDegrees =
                    Services.SolarAltitudeGuard.NormalizeLimit(value);
                CoreUtil.SaveSettings(Properties.Settings.Default);
                RaisePropertyChanged();
            }
        }

        public double CloudCoverageThreshold
        {
            get => Properties.Settings.Default.CloudCoverageThreshold;
            set
            {
                Properties.Settings.Default.CloudCoverageThreshold = value;
                CoreUtil.SaveSettings(Properties.Settings.Default);
                RaisePropertyChanged();
            }
        }

        public double CloudCoverageSafeThreshold
        {
            get => Properties.Settings.Default.CloudCoverageSafeThreshold;
            set
            {
                Properties.Settings.Default.CloudCoverageSafeThreshold = value;
                CoreUtil.SaveSettings(Properties.Settings.Default);
                RaisePropertyChanged();
            }
        }

        public bool UseGitHubModels
        {
            get => Properties.Settings.Default.UseGitHubModels;
            set
            {
                Properties.Settings.Default.UseGitHubModels = value;
                CoreUtil.SaveSettings(Properties.Settings.Default);
                RaisePropertyChanged();
            }
        }

        public string GitHubToken
        {
            get => Properties.Settings.Default.GitHubToken ?? string.Empty;
            set
            {
                Properties.Settings.Default.GitHubToken = value;
                CoreUtil.SaveSettings(Properties.Settings.Default);
                RaisePropertyChanged();
            }
        }

        public string SelectedModel
        {
            get => Properties.Settings.Default.SelectedModel ?? "gpt-4o";
            set
            {
                Properties.Settings.Default.SelectedModel = value;
                CoreUtil.SaveSettings(Properties.Settings.Default);
                RaisePropertyChanged();
            }
        }

        public string INDIDeviceName
        {
            get => Properties.Settings.Default.INDIDeviceName ?? string.Empty;
            set
            {
                Properties.Settings.Default.INDIDeviceName = value;
                CoreUtil.SaveSettings(Properties.Settings.Default);
                RaisePropertyChanged();
            }
        }

        public string FolderPath
        {
            get => Properties.Settings.Default.FolderPath ?? string.Empty;
            set
            {
                Properties.Settings.Default.FolderPath = value;
                CoreUtil.SaveSettings(Properties.Settings.Default);
                RaisePropertyChanged();
            }
        }

        /// <summary>
        /// How old the latest successful analysis may be before the safety monitor reports
        /// UNSAFE, in minutes. 0 means automatic: three check intervals, never below ten
        /// minutes. A safety monitor that keeps reporting the last known state after its
        /// camera died is worse than one that reports nothing, so the state always expires.
        /// </summary>
        public int MaxDataAgeMinutes
        {
            get => Properties.Settings.Default.MaxDataAgeMinutes;
            set
            {
                Properties.Settings.Default.MaxDataAgeMinutes = value < 0 ? 0 : value;
                CoreUtil.SaveSettings(Properties.Settings.Default);
                RaisePropertyChanged();
            }
        }

        public bool UseAscomSafetyMonitor
        {
            get => Properties.Settings.Default.UseAscomSafetyMonitor;
            set
            {
                Properties.Settings.Default.UseAscomSafetyMonitor = value;
                CoreUtil.SaveSettings(Properties.Settings.Default);
                RaisePropertyChanged();
            }
        }

        public string AscomSafetyMonitorProgId
        {
            get => Properties.Settings.Default.AscomSafetyMonitorProgId ?? string.Empty;
            set
            {
                Properties.Settings.Default.AscomSafetyMonitorProgId = value;
                CoreUtil.SaveSettings(Properties.Settings.Default);
                RaisePropertyChanged();
            }
        }

        public bool WriteSafetyStatusFile
        {
            get => Properties.Settings.Default.WriteSafetyStatusFile;
            set
            {
                Properties.Settings.Default.WriteSafetyStatusFile = value;
                CoreUtil.SaveSettings(Properties.Settings.Default);
                RaisePropertyChanged();
            }
        }

        public string SafetyStatusFilePath
        {
            get => Properties.Settings.Default.SafetyStatusFilePath ?? string.Empty;
            set
            {
                Properties.Settings.Default.SafetyStatusFilePath = value;
                CoreUtil.SaveSettings(Properties.Settings.Default);
                RaisePropertyChanged();
            }
        }

        public bool DatasetEnabled
        {
            get => Properties.Settings.Default.DatasetEnabled;
            set
            {
                Properties.Settings.Default.DatasetEnabled = value;
                CoreUtil.SaveSettings(Properties.Settings.Default);
                RaisePropertyChanged();
            }
        }

        public bool DatasetPaused
        {
            get => Properties.Settings.Default.DatasetPaused;
            set
            {
                Properties.Settings.Default.DatasetPaused = value;
                CoreUtil.SaveSettings(Properties.Settings.Default);
                RaisePropertyChanged();
            }
        }

        public string DatasetDirectory
        {
            get
            {
                var configured = Properties.Settings.Default.DatasetDirectory;
                return string.IsNullOrWhiteSpace(configured)
                    ? DatasetRecorderOptions.DefaultRootDirectory()
                    : configured;
            }
            set
            {
                Properties.Settings.Default.DatasetDirectory = value?.Trim() ?? string.Empty;
                CoreUtil.SaveSettings(Properties.Settings.Default);
                RaisePropertyChanged();
            }
        }

        public int DatasetSamplingIntervalMinutes
        {
            get => Properties.Settings.Default.DatasetSamplingIntervalMinutes;
            set
            {
                Properties.Settings.Default.DatasetSamplingIntervalMinutes = System.Math.Clamp(value, 1, 1440);
                CoreUtil.SaveSettings(Properties.Settings.Default);
                RaisePropertyChanged();
            }
        }

        public int DatasetSampleEveryChecks
        {
            get => Properties.Settings.Default.DatasetSampleEveryChecks;
            set
            {
                Properties.Settings.Default.DatasetSampleEveryChecks = System.Math.Clamp(value, 1, 10000);
                CoreUtil.SaveSettings(Properties.Settings.Default);
                RaisePropertyChanged();
            }
        }

        public double DatasetMaxSizeGb
        {
            get => Properties.Settings.Default.DatasetMaxSizeGb;
            set
            {
                Properties.Settings.Default.DatasetMaxSizeGb = System.Math.Clamp(value, 0.1, 10240);
                CoreUtil.SaveSettings(Properties.Settings.Default);
                RaisePropertyChanged();
            }
        }

        public double DatasetMinFreeSpaceGb
        {
            get => Properties.Settings.Default.DatasetMinFreeSpaceGb;
            set
            {
                Properties.Settings.Default.DatasetMinFreeSpaceGb = System.Math.Clamp(value, 0.1, 10240);
                CoreUtil.SaveSettings(Properties.Settings.Default);
                RaisePropertyChanged();
            }
        }

        public int DatasetImageWidth
        {
            get => Properties.Settings.Default.DatasetImageWidth;
            set
            {
                Properties.Settings.Default.DatasetImageWidth = System.Math.Clamp(value, 320, 7680);
                CoreUtil.SaveSettings(Properties.Settings.Default);
                RaisePropertyChanged();
            }
        }

        public int DatasetImageHeight
        {
            get => Properties.Settings.Default.DatasetImageHeight;
            set
            {
                Properties.Settings.Default.DatasetImageHeight = System.Math.Clamp(value, 180, 4320);
                CoreUtil.SaveSettings(Properties.Settings.Default);
                RaisePropertyChanged();
            }
        }

        public double DatasetImageScalePercent
        {
            get => Properties.Settings.Default.DatasetImageScalePercent;
            set
            {
                var normalized = double.IsFinite(value) ? value : 50;
                Properties.Settings.Default.DatasetImageScalePercent =
                    System.Math.Clamp(normalized, 5, 100);
                CoreUtil.SaveSettings(Properties.Settings.Default);
                RaisePropertyChanged();
            }
        }

        public int DatasetJpegQuality
        {
            get => Properties.Settings.Default.DatasetJpegQuality;
            set
            {
                Properties.Settings.Default.DatasetJpegQuality = System.Math.Clamp(value, 40, 100);
                CoreUtil.SaveSettings(Properties.Settings.Default);
                RaisePropertyChanged();
            }
        }

        public double DatasetDisagreementThreshold
        {
            get => Properties.Settings.Default.DatasetDisagreementThreshold;
            set
            {
                Properties.Settings.Default.DatasetDisagreementThreshold = System.Math.Clamp(value, 0, 100);
                CoreUtil.SaveSettings(Properties.Settings.Default);
                RaisePropertyChanged();
            }
        }

        public int DatasetNearDuplicateHammingDistance
        {
            get => Properties.Settings.Default.DatasetNearDuplicateHammingDistance;
            set
            {
                Properties.Settings.Default.DatasetNearDuplicateHammingDistance = System.Math.Clamp(value, 0, 64);
                CoreUtil.SaveSettings(Properties.Settings.Default);
                RaisePropertyChanged();
            }
        }

        public bool DatasetSaveTeacherRaw
        {
            get => Properties.Settings.Default.DatasetSaveTeacherRaw;
            set
            {
                Properties.Settings.Default.DatasetSaveTeacherRaw = value;
                CoreUtil.SaveSettings(Properties.Settings.Default);
                RaisePropertyChanged();
            }
        }

        public bool DatasetRecordQuarantine
        {
            get => Properties.Settings.Default.DatasetRecordQuarantine;
            set
            {
                Properties.Settings.Default.DatasetRecordQuarantine = value;
                CoreUtil.SaveSettings(Properties.Settings.Default);
                RaisePropertyChanged();
            }
        }
    }
}
