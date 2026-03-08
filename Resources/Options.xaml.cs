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

                // Try to pre-populate from current path via DataContext
                if (sender is FrameworkElement fe && fe.DataContext is AIWeather plugin
                    && !string.IsNullOrEmpty(plugin.Options.FolderPath))
                {
                    dialog.SelectedPath = plugin.Options.FolderPath;
                }

                if (dialog.ShowDialog() == System.Windows.Forms.DialogResult.OK
                    && !string.IsNullOrEmpty(dialog.SelectedPath))
                {
                    // Set value via sibling TextBox in the DockPanel - the TwoWay binding
                    // with UpdateSourceTrigger=PropertyChanged pushes the value to the model.
                    // This avoids depending on the DataContext type which may differ across NINA versions.
                    if (sender is FrameworkElement btn && btn.Parent is System.Windows.Controls.Panel panel)
                    {
                        foreach (UIElement child in panel.Children)
                        {
                            if (child is TextBox tb)
                            {
                                tb.Text = dialog.SelectedPath;
                                break;
                            }
                        }
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
                    await plugin.RefreshAvailableModelsAsync(forceRefresh: true);
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
            // Refresh model list when the Options UI is opened:
            //  - If empty: first load, always fetch.
            //  - If cache is stale (>1 hour): background refresh without blanking.
            //  - If cache is fresh: skip (keeps scroll position & selection intact).
            try
            {
                if (sender is FrameworkElement fe && fe.DataContext is AIWeather plugin)
                {
                    if (plugin.AvailableModels.Count == 0)
                    {
                        await plugin.RefreshAvailableModelsAsync();
                    }
                    else
                    {
                        // Silently refresh if cache is stale; SyncModelsCollection
                        // updates in-place without blanking.
                        await plugin.RefreshAvailableModelsAsync();
                    }
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
                var initialDirectory = string.Empty;
                var initialFileName = "sky_conditions.txt";

                // Try to get existing path from sibling TextBox
                if (sender is FrameworkElement btn && btn.Parent is System.Windows.Controls.Panel parentPanel)
                {
                    foreach (UIElement child in parentPanel.Children)
                    {
                        if (child is TextBox existingTb && !string.IsNullOrWhiteSpace(existingTb.Text))
                        {
                            try
                            {
                                initialDirectory = System.IO.Path.GetDirectoryName(existingTb.Text) ?? string.Empty;
                                var name = System.IO.Path.GetFileName(existingTb.Text);
                                if (!string.IsNullOrWhiteSpace(name))
                                {
                                    initialFileName = name;
                                }
                            }
                            catch
                            {
                                // best-effort
                            }
                            break;
                        }
                    }
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
                    // Set value via sibling TextBox — binding pushes to model
                    if (sender is FrameworkElement btn2 && btn2.Parent is System.Windows.Controls.Panel panel2)
                    {
                        foreach (UIElement child in panel2.Children)
                        {
                            if (child is TextBox tb)
                            {
                                tb.Text = dialog.FileName;
                                break;
                            }
                        }
                    }
                }
            }
            catch (System.Exception ex)
            {
                NINA.Core.Utility.Logger.Error($"BrowseSafetyStatusFile error: {ex.Message}");
            }
        }
    }
}
