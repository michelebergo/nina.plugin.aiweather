using System;
using System.IO;

namespace AIWeather
{
    public static class PluginInfo
    {
        public const string PLUGIN_NAME = "AI Weather";
        public const string PLUGIN_VERSION = "1.4.0.0";
        public const string PLUGIN_AUTHOR = "Michele Bergo";
        public const string PLUGIN_DESCRIPTION = "AI-powered all-sky camera weather monitoring with automatic safety protection";
        
        public static readonly Guid PLUGIN_ID = new Guid("A1B2C3D4-E5F6-4A5B-8C9D-0E1F2A3B4C5D");
        
        public static string GetPluginDataPath()
        {
            var path = Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
                "NINA",
                "Plugins",
            "AIWeather");
            
            if (!Directory.Exists(path))
            {
                Directory.CreateDirectory(path);
            }
            
            return path;
        }
        
        public static string GetCaptureImagePath()
        {
            var path = Path.Combine(GetPluginDataPath(), "Captures");
            
            if (!Directory.Exists(path))
            {
                Directory.CreateDirectory(path);
            }
            
            return path;
        }
    }
}
