using System;
using System.Windows.Controls;
using NINA.Core.Utility;
using System.Runtime.InteropServices;
using Microsoft.Win32;

namespace AIWeather
{
    /// <summary>
    /// Interaction logic for AIWeatherOptionsView.xaml
    /// </summary>
    public partial class AIWeatherOptionsView : System.Windows.Controls.UserControl
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

        private void BrowseFolder_Click(object sender, System.Windows.RoutedEventArgs e)
        {
            var dialog = new Microsoft.Win32.OpenFileDialog
            {
                ValidateNames = false,
                CheckFileExists = false,
                CheckPathExists = true,
                FileName = "Folder Selection",
                Title = "Select folder to monitor for sky images"
            };
            
            if (DataContext is AIWeatherOptions options && !string.IsNullOrEmpty(options.FolderPath))
            {
                dialog.InitialDirectory = options.FolderPath;
            }

            if (dialog.ShowDialog() == true)
            {
                if (DataContext is AIWeatherOptions opts)
                {
                    // Get the folder from the selected file path
                    var folder = System.IO.Path.GetDirectoryName(dialog.FileName);
                    if (!string.IsNullOrEmpty(folder))
                    {
                        opts.FolderPath = folder;
                    }
                }
            }
        }

        private void ChooseAscomSafetyMonitor_Click(object sender, System.Windows.RoutedEventArgs e)
        {
            try
            {
                if (DataContext is not AIWeatherOptions opts)
                {
                    return;
                }

                // Use ASCOM Chooser via COM (no compile-time dependency on ASCOM assemblies).
                var chooserType = Type.GetTypeFromProgID("ASCOM.Utilities.Chooser");
                if (chooserType == null)
                {
                    Logger.Error("ASCOM.Utilities.Chooser COM component not found. Is ASCOM Platform installed?");
                    return;
                }

                dynamic chooser = Activator.CreateInstance(chooserType);
                chooser.DeviceType = "SafetyMonitor";
                var selected = (string)chooser.Choose(opts.AscomSafetyMonitorProgId);
                if (!string.IsNullOrWhiteSpace(selected))
                {
                    opts.AscomSafetyMonitorProgId = selected;
                }

                try
                {
                    Marshal.FinalReleaseComObject(chooser);
                }
                catch
                {
                    // best-effort
                }
            }
            catch (Exception ex)
            {
                Logger.Error($"Error choosing ASCOM SafetyMonitor: {ex.Message}");
            }
        }

        private void BrowseSafetyStatusFile_Click(object sender, System.Windows.RoutedEventArgs e)
        {
            try
            {
                if (DataContext is not AIWeatherOptions opts)
                {
                    return;
                }

                var existingPath = opts.SafetyStatusFilePath;
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
                    opts.SafetyStatusFilePath = dialog.FileName;

                    // Persist immediately for NINA's settings system.
                    CoreUtil.SaveSettings(Properties.Settings.Default);
                    Properties.Settings.Default.Save();
                }
            }
            catch (Exception ex)
            {
                Logger.Error($"Error choosing safety status file path: {ex.Message}");
            }
        }
    }
}
