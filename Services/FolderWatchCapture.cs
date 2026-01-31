using System;
using System.Drawing;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using NINA.Core.Utility;

namespace AIWeather.Services
{
    /// <summary>
    /// Monitors a folder for the latest sky image
    /// </summary>
    public class FolderWatchCapture
    {
        private string _folderPath;
        private readonly string[] _supportedExtensions = { ".jpg", ".jpeg", ".png", ".bmp", ".tif", ".tiff" };

        public string FolderPath
        {
            get => _folderPath;
            set => _folderPath = value;
        }

        /// <summary>
        /// Gets the latest image from the monitored folder
        /// </summary>
        public async Task<Bitmap> CaptureImageAsync()
        {
            return await Task.Run(() =>
            {
                try
                {
                    if (string.IsNullOrEmpty(_folderPath) || !Directory.Exists(_folderPath))
                    {
                        Logger.Warning($"Folder watch: path does not exist: {_folderPath}");
                        return null;
                    }

                    // Find the most recently modified image file
                    var latestFile = GetLatestImageFile();
                    if (latestFile == null)
                    {
                        Logger.Warning($"Folder watch: no image files found in {_folderPath}");
                        return null;
                    }

                    Logger.Info($"Folder watch: loading latest image: {Path.GetFileName(latestFile)}");

                    // Load the image
                    using (var fileStream = new FileStream(latestFile, FileMode.Open, FileAccess.Read, FileShare.ReadWrite))
                    {
                        // Create a copy so we can close the file stream
                        var image = new Bitmap(fileStream);
                        var copy = new Bitmap(image);
                        image.Dispose();
                        return copy;
                    }
                }
                catch (Exception ex)
                {
                    Logger.Error($"Folder watch error: {ex.Message}");
                    return null;
                }
            });
        }

        /// <summary>
        /// Gets the path to the latest image file in the folder
        /// </summary>
        private string GetLatestImageFile()
        {
            try
            {
                var directory = new DirectoryInfo(_folderPath);
                
                var latestFile = directory.GetFiles()
                    .Where(f => _supportedExtensions.Contains(f.Extension.ToLowerInvariant()))
                    .OrderByDescending(f => f.LastWriteTime)
                    .FirstOrDefault();

                return latestFile?.FullName;
            }
            catch (Exception ex)
            {
                Logger.Error($"Error finding latest image: {ex.Message}");
                return null;
            }
        }

        /// <summary>
        /// Checks if the folder exists and is accessible
        /// </summary>
        public bool IsValid()
        {
            try
            {
                Logger.Info($"FolderWatchCapture.IsValid() - _folderPath: '{_folderPath}'");
                Logger.Info($"FolderWatchCapture.IsValid() - IsNullOrEmpty: {string.IsNullOrEmpty(_folderPath)}");
                Logger.Info($"FolderWatchCapture.IsValid() - Directory.Exists: {Directory.Exists(_folderPath)}");
                
                var result = !string.IsNullOrEmpty(_folderPath) && Directory.Exists(_folderPath);
                Logger.Info($"FolderWatchCapture.IsValid() - Returning: {result}");
                return result;
            }
            catch (Exception ex)
            {
                Logger.Error($"FolderWatchCapture.IsValid() - Exception: {ex.Message}");
                return false;
            }
        }

        /// <summary>
        /// Gets information about the latest image file
        /// </summary>
        public FileInfo GetLatestImageInfo()
        {
            try
            {
                var latestPath = GetLatestImageFile();
                return latestPath != null ? new FileInfo(latestPath) : null;
            }
            catch
            {
                return null;
            }
        }
    }
}
