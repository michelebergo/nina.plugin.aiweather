using System;
using System.Windows.Controls;
using NINA.Core.Utility;

namespace AIWeather
{
    /// <summary>
    /// Interaction logic for AIWeatherOptionsView.xaml
    /// </summary>
    public partial class AIWeatherOptionsView : UserControl
    {
        public AIWeatherOptionsView()
        {
            InitializeComponent();
        }

        private void RtspPasswordBox_Loaded(object sender, System.Windows.RoutedEventArgs e)
        {
            if (sender is PasswordBox passwordBox)
            {
                // Load saved password - reload from disk to get latest
                Properties.Settings.Default.Reload();
                var savedPassword = Properties.Settings.Default.RtspPassword;
                if (!string.IsNullOrEmpty(savedPassword))
                {
                    passwordBox.Password = savedPassword;
                    Logger.Info($"Options - Password box loaded from settings: length {savedPassword.Length}");
                }
            }
        }

        private void RtspPasswordBox_PasswordChanged(object sender, System.Windows.RoutedEventArgs e)
        {
            if (sender is PasswordBox passwordBox)
            {
                try
                {
                    // Save password to settings IMMEDIATELY - every keystroke
                    var password = passwordBox.Password ?? string.Empty;
                    Properties.Settings.Default.RtspPassword = password;
                    CoreUtil.SaveSettings(Properties.Settings.Default);
                    // Force flush to disk
                    Properties.Settings.Default.Save();
                    Logger.Info($"Options - Password saved immediately, length: {password.Length}");
                }
                catch (Exception ex)
                {
                    Logger.Error($"Error saving password: {ex.Message}");
                }
            }
        }
    }
}
