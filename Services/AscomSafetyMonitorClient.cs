using System;
using System.Runtime.InteropServices;
using NINA.Core.Utility;

namespace AIWeather.Services
{
    [System.Runtime.Versioning.SupportedOSPlatform("windows")]
    internal sealed class AscomSafetyMonitorClient : IDisposable
    {
        private object? _driver;

        public string ProgId { get; private set; } = string.Empty;
        public bool Connected { get; private set; }

        public bool TryConnect(string progId)
        {
            Disconnect();

            if (string.IsNullOrWhiteSpace(progId))
            {
                return false;
            }

            try
            {
                var driverType = Type.GetTypeFromProgID(progId, throwOnError: false);
                if (driverType == null)
                {
                    Logger.Error($"ASCOM SafetyMonitor ProgID not found: '{progId}'");
                    return false;
                }

                _driver = Activator.CreateInstance(driverType);
                ProgId = progId;

                try
                {
                    ((dynamic)_driver).Connected = true;
                }
                catch (Exception ex)
                {
                    Logger.Error($"Failed to set Connected=true for ASCOM SafetyMonitor '{progId}': {ex.Message}");
                    Disconnect();
                    return false;
                }

                Connected = true;
                Logger.Info($"Connected to ASCOM SafetyMonitor '{progId}'");
                return true;
            }
            catch (Exception ex)
            {
                Logger.Error($"Error connecting ASCOM SafetyMonitor '{progId}': {ex.Message}");
                Disconnect();
                return false;
            }
        }

        public bool TryGetIsSafe(out bool isSafe)
        {
            isSafe = false;

            if (!Connected || _driver == null)
            {
                return false;
            }

            try
            {
                isSafe = (bool)((dynamic)_driver).IsSafe;
                return true;
            }
            catch (Exception ex)
            {
                Logger.Error($"Error reading IsSafe from ASCOM SafetyMonitor '{ProgId}': {ex.Message}");
                return false;
            }
        }

        public void Disconnect()
        {
            try
            {
                if (_driver != null)
                {
                    try
                    {
                        ((dynamic)_driver).Connected = false;
                    }
                    catch
                    {
                        // best-effort
                    }

                    try
                    {
                        Marshal.FinalReleaseComObject(_driver);
                    }
                    catch
                    {
                        // best-effort
                    }
                }
            }
            finally
            {
                _driver = null;
                Connected = false;
                ProgId = string.Empty;
            }
        }

        public void Dispose()
        {
            Disconnect();
        }
    }
}
