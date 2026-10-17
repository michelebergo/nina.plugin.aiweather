namespace AIWeather.Services
{
    /// <summary>
    /// What the safety monitor needs from the external ASCOM device. The real one wraps a
    /// COM driver; the tests replay a night with a fake that reports, fails or goes quiet.
    /// </summary>
    internal interface IExternalSafetyMonitor
    {
        string ProgId { get; }
        bool Connected { get; }
        bool TryConnect(string progId);
        bool TryGetIsSafe(out bool isSafe);
        void Disconnect();
    }
}
