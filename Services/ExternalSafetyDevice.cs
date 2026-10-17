using System;
using NINA.Core.Utility;

namespace AIWeather.Services
{
    /// <summary>
    /// One external ASCOM safety device as the monitor reads it. Every failure mode - not
    /// configured, driver missing, connection lost, read error - reports unsafe, because a
    /// device that cannot be read is exactly the situation its owner installed it to be
    /// protected from. Reads are cached briefly and reconnects are rate-limited so a polled
    /// property cannot hammer a COM driver. Two of these exist: the external monitor the
    /// owner combines with AND or OR, and the environment monitor that is always ANDed (#19).
    /// </summary>
    internal sealed class ExternalSafetyDevice
    {
        /// <summary>IsSafe is polled often; a COM read per poll would hammer the driver.</summary>
        private static readonly TimeSpan ReadCacheDuration = TimeSpan.FromSeconds(5);

        /// <summary>How long to wait before retrying a driver that failed to connect or read.</summary>
        private static readonly TimeSpan ReconnectInterval = TimeSpan.FromSeconds(30);

        private readonly IExternalSafetyMonitor _device;
        private readonly string _label;
        private readonly object _gate = new object();
        private bool _safeCached;
        private DateTime _readUtc = DateTime.MinValue;
        private DateTime _connectAttemptUtc = DateTime.MinValue;
        private bool _failureLogged;

        // The last state the device was logged in, so a clean SAFE/UNSAFE flip leaves one
        // line in the log. Without it an UNSAFE at dawn from a rain sensor is
        // indistinguishable from anything else after the fact.
        private bool? _stateLogged;

        /// <param name="label">How the log names it, lower case: "external ASCOM safety monitor".</param>
        public ExternalSafetyDevice(IExternalSafetyMonitor device, string label)
        {
            _device = device;
            _label = label;
        }

        public bool Connected => _device.Connected;

        private string Label => char.ToUpperInvariant(_label[0]) + _label.Substring(1);

        /// <summary>
        /// Best-effort connect at monitor connect time: the lazy path in IsSafe retries on
        /// its own schedule, but connecting here surfaces a wrong ProgID in the log at once.
        /// </summary>
        public void ConnectNow(string? progId, DateTime now)
        {
            lock (_gate)
            {
                // A fresh session logs the device's state afresh, never inheriting the last one.
                _stateLogged = null;
                _connectAttemptUtc = now;
                _safeCached = _device.TryConnect(progId ?? string.Empty)
                              && _device.TryGetIsSafe(out var s) && s;
                _readUtc = now;
            }
            Logger.Info($"{Label} enabled ('{progId}'): " +
                        $"{(_device.Connected ? "connected" : "NOT connected - the monitor will report UNSAFE until it is")}");
        }

        public void Disconnect()
        {
            lock (_gate)
            {
                _device.Disconnect();
                _safeCached = false;
                _readUtc = DateTime.MinValue;
            }
        }

        /// <summary>The device's verdict now; false for every way of not having one.</summary>
        public bool IsSafe(string? progId, DateTime now)
        {
            lock (_gate)
            {
                if (now - _readUtc < ReadCacheDuration)
                {
                    return _safeCached;
                }
                _readUtc = now;

                if (string.IsNullOrWhiteSpace(progId))
                {
                    _safeCached = false;
                    LogFailureOnce($"the {_label} is enabled but no driver is selected");
                    return false;
                }

                if (!_device.Connected || !string.Equals(_device.ProgId, progId, StringComparison.OrdinalIgnoreCase))
                {
                    if (now - _connectAttemptUtc < ReconnectInterval)
                    {
                        _safeCached = false;
                        return false;
                    }

                    _connectAttemptUtc = now;
                    if (!_device.TryConnect(progId))
                    {
                        _safeCached = false;
                        LogFailureOnce($"cannot connect to the {_label} '{progId}'");
                        return false;
                    }
                }

                if (!_device.TryGetIsSafe(out var safe))
                {
                    // The driver answered before and does not now: drop the connection so the
                    // next cycle rebuilds it instead of polling a dead object forever.
                    _device.Disconnect();
                    _safeCached = false;
                    LogFailureOnce($"cannot read IsSafe from the {_label} '{progId}'");
                    return false;
                }

                if (_failureLogged)
                {
                    _failureLogged = false;
                    Logger.Info($"{Label} '{progId}' is readable again");
                }

                if (_stateLogged != safe)
                {
                    Logger.Info($"{Label} '{progId}' reports {(safe ? "SAFE" : "UNSAFE")}");
                    _stateLogged = safe;
                }

                _safeCached = safe;
                return safe;
            }
        }

        private void LogFailureOnce(string message)
        {
            if (_failureLogged)
            {
                return;
            }
            _failureLogged = true;
            Logger.Warning($"Safety monitor reporting UNSAFE: {message}");
        }
    }
}
