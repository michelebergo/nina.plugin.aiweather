namespace AIWeather.Models
{
    /// <summary>
    /// Defines the image capture mode for the AI Weather plugin
    /// </summary>
    public enum CaptureMode
    {
        /// <summary>
        /// Connect to RTSP stream and capture frames periodically
        /// </summary>
        RTSPStream = 0,

        /// <summary>
        /// Connect to INDI all-sky camera and capture images on demand
        /// </summary>
        INDICamera = 1,

        /// <summary>
        /// Monitor a folder for the latest image file
        /// </summary>
        FolderWatch = 2
    }
}
