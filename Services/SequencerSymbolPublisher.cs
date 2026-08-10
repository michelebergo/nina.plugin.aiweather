using AIWeather.Models;
using NINA.Core.Utility;
using System;
using System.Linq;
using System.Reflection;

namespace AIWeather.Services
{
    /// <summary>
    /// Publishes the latest weather analysis to the Advanced Sequencer's Symbols sidebar
    /// (N.I.N.A. 3.3+ expression system), so sequences can reference values like
    /// <c>Cloud</c> and <c>Confidence</c> in expressions and message templates.
    ///
    /// The symbol broker API (<c>NINA.Sequencer.Logic.SymbolBroker</c>) only exists from
    /// N.I.N.A. 3.3 on, while this plugin still supports 3.0+. Everything here therefore
    /// goes through reflection against the host's loaded assemblies: on hosts that have
    /// the API the symbols appear, on older hosts every call is a cheap no-op. The plugin
    /// keeps its 3.0 minimum either way.
    /// </summary>
    internal static class SequencerSymbolPublisher
    {
        private const string ProviderName = "AIWeather";

        private static readonly object InitLock = new object();
        private static bool _initialized;
        private static bool _available;
        private static object? _provider;
        private static MethodInfo? _addOrUpdate;

        /// <summary>
        /// Pushes one analysis result into the sequencer symbols. Values published:
        /// Cloud (0-100), Confidence (0-100), Condition (text), Rain, Fog, Safe.
        /// <paramref name="effectiveSafe"/> is the monitor's hysteresis-adjusted verdict
        /// rather than the raw per-image flag, so the symbol always matches what the
        /// safety monitor itself would report.
        /// </summary>
        public static void Publish(WeatherAnalysisResult? result, bool effectiveSafe)
        {
            if (result == null) { return; }
            if (!EnsureProvider()) { return; }

            try
            {
                Set("Cloud", Math.Round(result.CloudCoverage, 1));
                Set("Confidence", Math.Round(result.Confidence, 1));
                Set("Condition", result.Condition.ToString());
                Set("Rain", result.RainDetected);
                Set("Fog", result.FogDetected);
                Set("Safe", effectiveSafe);
            }
            catch (Exception ex)
            {
                Logger.Warning($"Failed to publish sequencer symbols: {ex.Message}");
            }
        }

        /// <summary>
        /// Registers the provider and defines the six symbols with empty values as soon as
        /// the host's symbol broker exists, retrying for a while because the sequencer may
        /// construct after the plugin loads. Without this the AIWeather category only
        /// appeared in the Symbols sidebar after the first completed analysis — which reads
        /// as "the feature doesn't work" to anyone who checks the sidebar right after
        /// installing, before connecting the safety monitor (first field report did).
        /// Null-valued symbols are how N.I.N.A. itself represents a device that is not
        /// delivering data yet, so the blanks match core behaviour.
        /// </summary>
        public static void TryRegisterAtStartup()
        {
            _ = System.Threading.Tasks.Task.Run(async () =>
            {
                try
                {
                    // ~2 minutes of patience covers even a slow cold start.
                    for (var attempt = 0; attempt < 12; attempt++)
                    {
                        if (EnsureProvider())
                        {
                            ClearValues();
                            return;
                        }
                        if (_initialized)
                        {
                            return; // host has no symbol broker (N.I.N.A. < 3.3): stop retrying
                        }
                        await System.Threading.Tasks.Task.Delay(TimeSpan.FromSeconds(10)).ConfigureAwait(false);
                    }
                }
                catch (Exception ex)
                {
                    Logger.Warning($"Startup registration of sequencer symbols failed: {ex.Message}");
                }
            });
        }

        /// <summary>
        /// Clears the values (keeps the symbols defined) when monitoring stops, so an
        /// expression cannot keep acting on a reading that is no longer being refreshed.
        /// This mirrors how N.I.N.A. treats symbols of a disconnected device.
        /// </summary>
        public static void ClearValues()
        {
            if (!_available || _provider == null) { return; }
            try
            {
                Set("Cloud", null);
                Set("Confidence", null);
                Set("Condition", null);
                Set("Rain", null);
                Set("Fog", null);
                Set("Safe", null);
            }
            catch (Exception ex)
            {
                Logger.Warning($"Failed to clear sequencer symbols: {ex.Message}");
            }
        }

        private static void Set(string token, object? value)
        {
            _addOrUpdate!.Invoke(_provider, new object?[] { token, value });
        }

        private static bool EnsureProvider()
        {
            if (_initialized) { return _available; }
            lock (InitLock)
            {
                if (_initialized) { return _available; }
                try
                {
                    var sequencerAssembly = AppDomain.CurrentDomain.GetAssemblies()
                        .FirstOrDefault(a => string.Equals(a.GetName().Name, "NINA.Sequencer", StringComparison.OrdinalIgnoreCase));
                    var brokerType = sequencerAssembly?.GetType("NINA.Sequencer.Logic.SymbolBroker");
                    if (brokerType == null)
                    {
                        // Host older than 3.3: no expression system, nothing to publish to.
                        // This is a permanent condition for the session, so latch it —
                        // otherwise the message would repeat on every monitoring cycle.
                        Logger.Info("Sequencer symbol broker not present (N.I.N.A. < 3.3); AI Weather symbols are not published");
                        _available = false;
                        _initialized = true;
                        return false;
                    }

                    var broker = brokerType
                        .GetProperty("Instance", BindingFlags.Static | BindingFlags.NonPublic | BindingFlags.Public)?
                        .GetValue(null);
                    if (broker == null)
                    {
                        // Broker type exists but the sequencer has not constructed it yet;
                        // stay uninitialized so the next publish retries.
                        return false;
                    }

                    _provider = RegisterOrGetProvider(brokerType, broker);
                    _addOrUpdate = _provider?.GetType().GetMethod(
                        "AddOrUpdateSymbol", new[] { typeof(string), typeof(object) });
                    _available = _provider != null && _addOrUpdate != null;
                    if (_available)
                    {
                        Logger.Info($"AI Weather sequencer symbols registered (provider '{ProviderName}': Cloud, Confidence, Condition, Rain, Fog, Safe)");
                    }
                }
                catch (Exception ex)
                {
                    Logger.Warning($"Sequencer symbol integration unavailable: {ex.Message}");
                    _available = false;
                }
                _initialized = true;
                return _available;
            }
        }

        private static object? RegisterOrGetProvider(Type brokerType, object broker)
        {
            var register = brokerType.GetMethod("RegisterSymbolProvider", new[] { typeof(string) });
            try
            {
                return register?.Invoke(broker, new object[] { ProviderName });
            }
            catch (TargetInvocationException tie) when (tie.InnerException is ArgumentException)
            {
                // Already registered (e.g. the plugin was reloaded in the same session):
                // fetch the existing provider instead of failing forever.
                var getInternal = brokerType.GetMethod("GetInternalProvider",
                    BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic);
                return getInternal?.Invoke(broker, new object[] { ProviderName });
            }
        }
    }
}
