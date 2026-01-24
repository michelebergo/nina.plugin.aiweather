using System.ComponentModel.Composition;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;

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
                // Note: Settings are auto-saved by AIWeather.RtspPassword property
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
    }
}
