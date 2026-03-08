using System.ComponentModel.Composition;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using Microsoft.Win32;

namespace AIWeather
{
    /// <summary>
    /// Code-behind for Options.xaml resource dictionary
    /// This class exports the Options DataTemplate so NINA can discover it
    /// </summary>
    [Export(typeof(ResourceDictionary))]
    public partial class Options : ResourceDictionary
    {
        public Options()
        {
            InitializeComponent();
        }

        private void RtspPasswordBox_PasswordChanged(object sender, RoutedEventArgs e)
        {
            var passwordBox = sender as PasswordBox;
            if (passwordBox != null)
            {
                // Save directly to settings (simpler than accessing DataContext)
                Properties.Settings.Default.RtspPassword = passwordBox.Password;
                NINA.Core.Utility.CoreUtil.SaveSettings(Properties.Settings.Default);
            }
        }

        private void RtspPasswordBox_Loaded(object sender, RoutedEventArgs e)
        {
            if (sender is PasswordBox pb)
            {
                var current = Properties.Settings.Default.RtspPassword ?? string.Empty;
                if (!string.Equals(pb.Password, current))
                {
                    pb.Password = current;
                }
            }
        }

        private void BrowseFolder_Click(object sender, RoutedEventArgs e)
        {
            try
            {
                using var dialog = new System.Windows.Forms.FolderBrowserDialog
                {
                    Description = "Select folder to monitor for sky images",
                    UseDescriptionForTitle = true,
                    ShowNewFolderButton = false
                };

                if (sender is FrameworkElement fe && fe.DataContext is AIWeather plugin
                    && !string.IsNullOrEmpty(plugin.Options.FolderPath))
                {
                    dialog.SelectedPath = plugin.Options.FolderPath;
                }

                if (dialog.ShowDialog() == System.Windows.Forms.DialogResult.OK
                    && !string.IsNullOrEmpty(dialog.SelectedPath))
                {
                    if (sender is FrameworkElement fe2 && fe2.DataContext is AIWeather p)
                    {
                        p.Options.FolderPath = dialog.SelectedPath;
                    }
                }
            }
            catch (System.Exception ex)
            {
                NINA.Core.Utility.Logger.Error($"BrowseFolder error: {ex.Message}");
            }
        }

        private void HttpPasswordBox_Loaded(object sender, RoutedEventArgs e)
        {
            if (sender is PasswordBox pb)
            {
                var current = Properties.Settings.Default.RtspPassword ?? string.Empty;
                if (!string.Equals(pb.Password, current))
                {
                    pb.Password = current;
                }
            }
        }

        private void HttpPasswordBox_PasswordChanged(object sender, RoutedEventArgs e)
        {
            var passwordBox = sender as PasswordBox;
            if (passwordBox != null)
            {
                Properties.Settings.Default.RtspPassword = passwordBox.Password;
                NINA.Core.Utility.CoreUtil.SaveSettings(Properties.Settings.Default);
            }
        }

        private void GitHubTokenBox_Loaded(object sender, RoutedEventArgs e)
        {
            if (sender is PasswordBox pb && pb.DataContext is AIWeather plugin)
            {
                var current = plugin.GitHubToken ?? string.Empty;
                if (!string.Equals(pb.Password, current))
                {
                    pb.Password = current;
                }
            }
        }

        private void GitHubTokenBox_PasswordChanged(object sender, RoutedEventArgs e)
        {
            if (sender is PasswordBox pb && pb.DataContext is AIWeather plugin)
            {
                if (!string.Equals(plugin.GitHubToken ?? string.Empty, pb.Password))
                {
                    plugin.GitHubToken = pb.Password;
                }
            }
        }

        private void OpenAIKeyBox_Loaded(object sender, RoutedEventArgs e)
        {
            if (sender is PasswordBox pb && pb.DataContext is AIWeather plugin)
            {
                var current = plugin.OpenAIKey ?? string.Empty;
                if (!string.Equals(pb.Password, current))
                {
                    pb.Password = current;
                }
            }
        }

        private void OpenAIKeyBox_PasswordChanged(object sender, RoutedEventArgs e)
        {
            if (sender is PasswordBox pb && pb.DataContext is AIWeather plugin)
            {
                if (!string.Equals(plugin.OpenAIKey ?? string.Empty, pb.Password))
                {
                    plugin.OpenAIKey = pb.Password;
                }
            }
        }

        private void GeminiKeyBox_Loaded(object sender, RoutedEventArgs e)
        {
            if (sender is PasswordBox pb && pb.DataContext is AIWeather plugin)
            {
                var current = plugin.GeminiKey ?? string.Empty;
                if (!string.Equals(pb.Password, current))
                {
                    pb.Password = current;
                }
            }
        }

        private void GeminiKeyBox_PasswordChanged(object sender, RoutedEventArgs e)
        {
            if (sender is PasswordBox pb && pb.DataContext is AIWeather plugin)
            {
                if (!string.Equals(plugin.GeminiKey ?? string.Empty, pb.Password))
                {
                    plugin.GeminiKey = pb.Password;
                }
            }
        }

        private void AnthropicKeyBox_Loaded(object sender, RoutedEventArgs e)
        {
            if (sender is PasswordBox pb && pb.DataContext is AIWeather plugin)
            {
                var current = plugin.AnthropicKey ?? string.Empty;
                if (!string.Equals(pb.Password, current))
                {
                    pb.Password = current;
                }
            }
        }

        private void AnthropicKeyBox_PasswordChanged(object sender, RoutedEventArgs e)
        {
            if (sender is PasswordBox pb && pb.DataContext is AIWeather plugin)
            {
                if (!string.Equals(plugin.AnthropicKey ?? string.Empty, pb.Password))
                {
                    plugin.AnthropicKey = pb.Password;
                }
            }
        }

        private async void RefreshModels_Click(object sender, RoutedEventArgs e)
        {
            try
            {
                if (sender is FrameworkElement fe && fe.DataContext is AIWeather plugin)
                {
                    await plugin.RefreshAvailableModelsAsync();
                }
            }
            catch
            {
                // best-effort; detailed errors are surfaced via ModelsStatus + logs
            }
        }

        private async void TryToken_Click(object sender, RoutedEventArgs e)
        {
            try
            {
                if (sender is FrameworkElement fe && fe.DataContext is AIWeather plugin)
                {
                    await plugin.TryGitHubTokenAsync();
                }
            }
            catch
            {
                // best-effort; status is updated by the plugin
            }
        }

        private async void TryOpenAIKey_Click(object sender, RoutedEventArgs e)
        {
            try
            {
                if (sender is FrameworkElement fe && fe.DataContext is AIWeather plugin)
                {
                    await plugin.TryOpenAIKeyAsync();
                }
            }
            catch
            {
                // best-effort; status is updated by the plugin
            }
        }

        private async void TryGeminiKey_Click(object sender, RoutedEventArgs e)
        {
            try
            {
                if (sender is FrameworkElement fe && fe.DataContext is AIWeather plugin)
                {
                    await plugin.TryGeminiKeyAsync();
                }
            }
            catch
            {
                // best-effort; status is updated by the plugin
            }
        }

        private async void TryAnthropicKey_Click(object sender, RoutedEventArgs e)
        {
            try
            {
                if (sender is FrameworkElement fe && fe.DataContext is AIWeather plugin)
                {
                    await plugin.TryAnthropicKeyAsync();
                }
            }
            catch
            {
                // best-effort; status is updated by the plugin
            }
        }

        private async void ModelComboBox_Loaded(object sender, RoutedEventArgs e)
        {
            // Opportunistically refresh when the options UI is opened.
            try
            {
                if (sender is FrameworkElement fe && fe.DataContext is AIWeather plugin)
                {
                    await plugin.RefreshAvailableModelsAsync();
                }
            }
            catch
            {
                // best-effort
            }
        }

        private void BrowseSafetyStatusFile_Click(object sender, RoutedEventArgs e)
        {
            try
            {
                if (sender is not FrameworkElement fe || fe.DataContext is not AIWeather plugin)
                {
                    return;
                }

                var existingPath = plugin.Options.SafetyStatusFilePath;
                var initialDirectory = string.Empty;
                var initialFileName = "sky_conditions.txt";

                try
                {
                    if (!string.IsNullOrWhiteSpace(existingPath))
                    {
                        initialDirectory = System.IO.Path.GetDirectoryName(existingPath) ?? string.Empty;
                        var name = System.IO.Path.GetFileName(existingPath);
                        if (!string.IsNullOrWhiteSpace(name))
                        {
                            initialFileName = name;
                        }
                    }
                }
                catch
                {
                    // best-effort
                }

                var dialog = new SaveFileDialog
                {
                    Title = "Choose status file to write (Safe/Unsafe)",
                    Filter = "Text files (*.txt)|*.txt|All files (*.*)|*.*",
                    DefaultExt = ".txt",
                    AddExtension = true,
                    FileName = initialFileName,
                    OverwritePrompt = false
                };

                if (!string.IsNullOrWhiteSpace(initialDirectory) && System.IO.Directory.Exists(initialDirectory))
                {
                    dialog.InitialDirectory = initialDirectory;
                }

                if (dialog.ShowDialog() == true && !string.IsNullOrWhiteSpace(dialog.FileName))
                {
                    plugin.Options.SafetyStatusFilePath = dialog.FileName;
                }
            }
            catch
            {
                // best-effort
            }
        }
    }
}
