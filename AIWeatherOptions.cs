using System;
using NINA.Core.Utility;
using NINA.Profile;
using NINA.Profile.Interfaces;
using System.ComponentModel;
using System.Runtime.CompilerServices;
using AIWeather.Models;

namespace AIWeather
{
    /// <summary>
    /// Plugin configuration options
    /// </summary>
    public class AIWeatherOptions : BaseINPC
    {
        private readonly IProfileService _profileService;

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

        /// <summary>0 = AND (safe only when both say safe), 1 = OR (unsafe only when both say unsafe). See SafetyVerdict.</summary>
        public int ExternalMonitorMode
        {
            get => Properties.Settings.Default.ExternalMonitorMode;
            set
            {
                Properties.Settings.Default.ExternalMonitorMode = value;
                CoreUtil.SaveSettings(Properties.Settings.Default);
                RaisePropertyChanged();
            }
        }

        /// <summary>A second ASCOM device (humidity, dew point, rain sensor) that is always ANDed: (sky OR cloud sensor) AND environment.</summary>
        public bool UseEnvironmentSafetyMonitor
        {
            get => Properties.Settings.Default.UseEnvironmentSafetyMonitor;
            set
            {
                Properties.Settings.Default.UseEnvironmentSafetyMonitor = value;
                CoreUtil.SaveSettings(Properties.Settings.Default);
                RaisePropertyChanged();
            }
        }

        public string EnvironmentSafetyMonitorProgId
        {
            get => Properties.Settings.Default.EnvironmentSafetyMonitorProgId ?? string.Empty;
            set
            {
                Properties.Settings.Default.EnvironmentSafetyMonitorProgId = value;
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

        /// <summary>A change of the combined state is confirmed with a second frame before it is acted on (#21).</summary>
        public bool ConfirmTransitions
        {
            get => Properties.Settings.Default.ConfirmTransitions;
            set
            {
                Properties.Settings.Default.ConfirmTransitions = value;
                CoreUtil.SaveSettings(Properties.Settings.Default);
                RaisePropertyChanged();
            }
        }

        /// <summary>A cloud-only Safe -> Unsafe call is put to PHD2's guide star first (#24).</summary>
        public bool GuideStarVetoEnabled
        {
            get => Properties.Settings.Default.GuideStarVetoEnabled;
            set
            {
                Properties.Settings.Default.GuideStarVetoEnabled = value;
                CoreUtil.SaveSettings(Properties.Settings.Default);
                RaisePropertyChanged();
            }
        }

        /// <summary>Star mass of the last minute, as a percentage of the ten minutes before, at or above which the call is held (#24).</summary>
        public double GuideStarVetoPercent
        {
            get => Properties.Settings.Default.GuideStarVetoPercent;
            set
            {
                Properties.Settings.Default.GuideStarVetoPercent = Math.Max(1, Math.Min(100, value));
                CoreUtil.SaveSettings(Properties.Settings.Default);
                RaisePropertyChanged();
            }
        }

        /// <summary>The weather device's reading must be unchanged too for the veto to hold (#24).</summary>
        public bool WeatherVoteEnabled
        {
            get => Properties.Settings.Default.WeatherVoteEnabled;
            set
            {
                Properties.Settings.Default.WeatherVoteEnabled = value;
                CoreUtil.SaveSettings(Properties.Settings.Default);
                RaisePropertyChanged();
            }
        }

        /// <summary>0 SkyQuality, 1 SkyTemperature, 2 CloudCover: the ObservingConditions property the second vote reads (#24).</summary>
        public int WeatherVotePropertyIndex
        {
            get => Array.IndexOf(WeatherVoteProperties, Properties.Settings.Default.WeatherVoteProperty) is var i && i >= 0 ? i : 0;
            set
            {
                Properties.Settings.Default.WeatherVoteProperty = WeatherVoteProperties[Math.Max(0, Math.Min(WeatherVoteProperties.Length - 1, value))];
                CoreUtil.SaveSettings(Properties.Settings.Default);
                RaisePropertyChanged();
            }
        }

        public static readonly string[] WeatherVoteProperties = { "SkyQuality", "SkyTemperature", "CloudCover" };

        /// <summary>How far the reading may move from its ten-minute median before it stops vouching for a clear sky (#24).</summary>
        public double WeatherVoteLimit
        {
            get => Properties.Settings.Default.WeatherVoteLimit;
            set
            {
                Properties.Settings.Default.WeatherVoteLimit = Math.Max(0, value);
                CoreUtil.SaveSettings(Properties.Settings.Default);
                RaisePropertyChanged();
            }
        }

        /// <summary>Keep the frame behind every change of state, with the decision next to it (#21).</summary>
        public bool KeepDecisionFrames
        {
            get => Properties.Settings.Default.KeepDecisionFrames;
            set
            {
                Properties.Settings.Default.KeepDecisionFrames = value;
                CoreUtil.SaveSettings(Properties.Settings.Default);
                RaisePropertyChanged();
            }
        }

        public int DecisionRetentionDays
        {
            get => Properties.Settings.Default.DecisionRetentionDays;
            set
            {
                Properties.Settings.Default.DecisionRetentionDays = value < 1 ? 1 : value;
                CoreUtil.SaveSettings(Properties.Settings.Default);
                RaisePropertyChanged();
            }
        }

        /// <summary>Where the decisions go; the options page offers to open it.</summary>
        public string DecisionFolder => Equipment.AIWeatherSafetyMonitor.DefaultDecisionFolder;

        public string SafetyStatusFilePath
        {
            get => Services.SafetyStatusFile.Resolve(Properties.Settings.Default.SafetyStatusFilePath);
            set
            {
                Properties.Settings.Default.SafetyStatusFilePath = value;
                CoreUtil.SaveSettings(Properties.Settings.Default);
                RaisePropertyChanged();
            }
        }
    }
}
