using NINA.Core.Utility;
using NINA.Profile;
using NINA.Profile.Interfaces;
using System.ComponentModel;
using System.Runtime.CompilerServices;

namespace AIWeather
{
    /// <summary>
    /// Plugin configuration options
    /// </summary>
    public class AIWeatherOptions : BaseINPC
    {
        private readonly IProfileService _profileService;

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
                Properties.Settings.Default.UseGitHubModels = false;
                Properties.Settings.Default.SelectedModel = "gpt-4o";
                CoreUtil.SaveSettings(Properties.Settings.Default);
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
    }
}
