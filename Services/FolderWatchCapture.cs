using System;
using System.Drawing;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using System.Windows.Media.Imaging;
using NINA.Core.Enum;
using NINA.Core.Utility;
using NINA.Image.Interfaces;

namespace AIWeather.Services
{
    /// <summary>
    /// Monitors a folder for the latest sky image
    /// </summary>
    public class FolderWatchCapture
    {
        private string _folderPath;
        private readonly string[] _supportedExtensions = { ".jpg", ".jpeg", ".png", ".bmp", ".gif", ".tif", ".tiff", ".fit", ".fits", ".fts", ".xisf" };
        private static readonly string[] NinaExtensions = { ".tif", ".tiff", ".fit", ".fits", ".fts", ".xisf" };

        public string FolderPath
        {
            get => _folderPath;
            set => _folderPath = value;
        }

        /// <summary>
        /// NINA's image data factory for loading FITS/TIFF with proper debayering and stretching.
        /// </summary>
        public IImageDataFactory ImageDataFactory { get; set; }

        /// <summary>
        /// Gets the latest image from the monitored folder
        /// </summary>
        public async Task<Bitmap> CaptureImageAsync()
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

                // Use NINA's image pipeline for FITS, TIFF, and XISF
                var ext = Path.GetExtension(latestFile)?.ToLowerInvariant();
                if (ImageDataFactory != null && Array.IndexOf(NinaExtensions, ext) >= 0)
                {
                    return await LoadWithNinaPipelineAsync(latestFile);
                }

                // Load standard image formats (JPG, PNG, BMP, GIF)
                return await Task.Run(() =>
                {
                    using (var fileStream = new FileStream(latestFile, FileMode.Open, FileAccess.Read, FileShare.ReadWrite))
                    {
                        var image = new Bitmap(fileStream);
                        var copy = new Bitmap(image);
                        image.Dispose();
                        return copy;
                    }
                });
            }
            catch (Exception ex)
            {
                Logger.Error($"Folder watch error: {ex.Message}");
                return null;
            }
        }

        /// <summary>
        /// Loads an astro image file using NINA's native image pipeline.
        /// Handles FITS, TIFF, and XISF with proper debayering and auto-stretching.
        /// </summary>
        private async Task<Bitmap> LoadWithNinaPipelineAsync(string path)
        {
            Logger.Info($"FolderWatch: loading with NINA pipeline: {Path.GetFileName(path)}");

            var imageData = await ImageDataFactory.CreateFromFile(
                path,
                bitDepth: 16,
                isBayered: true,
                rawConverter: RawConverterEnum.FREEIMAGE);

            Logger.Info($"FolderWatch: loaded {imageData.Properties.Width}x{imageData.Properties.Height}, " +
                        $"BitDepth={imageData.Properties.BitDepth}, IsBayered={imageData.Properties.IsBayered}");

            var rendered = imageData.RenderImage();

            // Debayer if the image is flagged as Bayer data
            if (imageData.Properties.IsBayered)
            {
                rendered = rendered.Debayer();
                Logger.Info("FolderWatch: debayered image");
            }

            // Auto-stretch for visual display
            rendered = await rendered.Stretch(factor: 0.2, blackClipping: -2.8, unlinked: false);
            Logger.Info("FolderWatch: auto-stretched image");

            // Convert WPF BitmapSource to GDI+ Bitmap for the analysis pipeline
            return BitmapSourceToBitmap(rendered.Image);
        }

        /// <summary>
        /// Converts a WPF BitmapSource to a GDI+ Bitmap.
        /// </summary>
        private static Bitmap BitmapSourceToBitmap(BitmapSource source)
        {
            var encoder = new PngBitmapEncoder();
            encoder.Frames.Add(BitmapFrame.Create(source));

            using (var ms = new MemoryStream())
            {
                encoder.Save(ms);
                ms.Position = 0;
                // Clone to detach from the MemoryStream
                using (var temp = new Bitmap(ms))
                {
                    return new Bitmap(temp);
                }
            }
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
