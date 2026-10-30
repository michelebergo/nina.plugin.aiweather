using NINA.Equipment.Interfaces;
using NINA.Core.Utility;
using NINA.Profile.Interfaces;
using NINA.Image.Interfaces;
using System;
using System.Collections.Generic;
using System.ComponentModel.Composition;
using System.Drawing;
using System.IO;
using System.Threading;
using System.Threading.Tasks;
using AIWeather.Models;
using AIWeather.Services;

namespace AIWeather.Equipment
{
    /// <summary>
    /// All Sky Camera Weather Monitor
    /// Monitors weather conditions and writes status to file
    /// </summary>
    [System.Runtime.Versioning.SupportedOSPlatform("windows")]
    public class AIWeatherSafetyMonitor : BaseINPC, ISafetyMonitor
    {
        private static AIWeatherSafetyMonitor? _instance;
        public static AIWeatherSafetyMonitor Instance => _instance ??= new AIWeatherSafetyMonitor();

        private readonly UnifiedCaptureService _captureService;
        private IWeatherAnalysisService _analysisService;
        private Timer? _monitoringTimer;
        private WeatherAnalysisResult? _lastResult;
        private Bitmap? _lastImage;
        private bool _isMonitoring = false;
        private bool _isCurrentlySafe = false;
        private CancellationTokenSource? _cts;
        private readonly SemaphoreSlim _checkGate = new SemaphoreSlim(1, 1);
        private IProfileService? _profileService;

        // When the last analysis actually succeeded. The sky verdict expires: a monitor that
        // keeps answering with the last known state after its camera died reports SAFE all
        // night on data from before the failure, which is the one thing a safety monitor
        // must never do.
        private DateTime _lastAnalysisUtc = DateTime.MinValue;
        private bool _staleLogged;

        // Optional external ASCOM safety monitor, combined with the sky verdict with AND or
        // OR as the owner chose, and an optional environment monitor (humidity, dew point,
        // rain sensor) that is always ANDed on top: (sky OR cloud sensor) AND environment.
        private readonly ExternalSafetyDevice _externalDevice;
        private readonly ExternalSafetyDevice _environmentDevice;

        /// <summary>The clock every age check runs against; the tests move it by hand.</summary>
        internal Func<DateTime> UtcNow = () => DateTime.UtcNow;

        // How many checks in a row the chosen provider failed to answer. Shown in the
        // panel from the first one: a fallback the owner cannot see is a bill for nothing.
        private readonly ProviderHealthTracker _providerHealth = new ProviderHealthTracker();

        // Identity of the last captured frame. A stream that keeps handing back the same
        // frame is indistinguishable from a static sky by the numbers alone; the
        // fingerprint makes it visible in the log without changing any verdict.
        private ulong? _lastFrameFingerprint;
        private int _identicalFrameRun;

        /// <summary>Floor for the automatic data-age limit, whatever the check interval.</summary>
        private static readonly TimeSpan MinimumAutomaticDataAge = TimeSpan.FromMinutes(10);

        /// <summary>
        /// Fired after Connect succeeds and periodic monitoring has started.
        /// </summary>
        public event EventHandler? MonitoringStarted;

        public AIWeatherSafetyMonitor() : this(new AscomSafetyMonitorClient(), new AscomSafetyMonitorClient())
        {
        }

        internal AIWeatherSafetyMonitor(IExternalSafetyMonitor externalMonitor, IExternalSafetyMonitor environmentMonitor)
        {
            _externalDevice = new ExternalSafetyDevice(externalMonitor, "external ASCOM safety monitor");
            _environmentDevice = new ExternalSafetyDevice(environmentMonitor, "environment ASCOM safety monitor");
            _captureService = new UnifiedCaptureService(cameraMediator: null);
            _analysisService = new LocalWeatherAnalysisService();
            
            // Subscribe to settings changes
            Properties.Settings.Default.PropertyChanged += async (s, e) =>
            {
                if (e.PropertyName == nameof(Properties.Settings.Default.UseGitHubModels)
                    || e.PropertyName == nameof(Properties.Settings.Default.AnalysisProvider)
                    || e.PropertyName == nameof(Properties.Settings.Default.SelectedModel)
                    || e.PropertyName == nameof(Properties.Settings.Default.GitHubToken)
                    || e.PropertyName == nameof(Properties.Settings.Default.OpenAIKey)
                    || e.PropertyName == nameof(Properties.Settings.Default.GeminiKey)
                    || e.PropertyName == nameof(Properties.Settings.Default.AnthropicKey))
                {
                    // Initialised here and now, not at the next connect: a model or key changed
                    // while the monitor was running must reach the next check. A service that
                    // is only constructed declines every frame and hands the night to the
                    // offline analyzer without the panel saying so (#21).
                    await RefreshAnalysisServiceAsync(CancellationToken.None);
                }
            };
        }

        /// <summary>
        /// Injects NINA's image data factory for proper FITS/TIFF loading with debayering and stretching.
        /// Called from the MEF-constructed provider.
        /// </summary>
        public void SetImageDataFactory(IImageDataFactory imageDataFactory)
        {
            _captureService.SetImageDataFactory(imageDataFactory);
        }

        /// <summary>
        /// Injects NINA's profile service for accessing observer location (lat/lon/elevation).
        /// Called from the MEF-constructed provider.
        /// </summary>
        public void SetProfileService(IProfileService profileService)
        {
            _profileService = profileService;
        }

        /// <summary>The analysis service the next check will use.</summary>
        internal IWeatherAnalysisService AnalysisService => _analysisService;

        /// <summary>
        /// Builds the analysis service the options describe and initialises it; when that fails
        /// (no key, for instance) the offline analyzer takes over outright and the log says so.
        /// One path for the connect and for a settings change while connected.
        /// </summary>
        private async Task RefreshAnalysisServiceAsync(CancellationToken token)
        {
            IWeatherAnalysisService service;
            bool ready;
            try
            {
                service = AnalysisServiceFactory.CreateFromSettings();
                ready = await service.InitializeAsync(token);
            }
            catch (Exception ex)
            {
                Logger.Error($"Analysis service could not be prepared: {ex.Message}", ex);
                service = new LocalWeatherAnalysisService();
                ready = false;
            }

            if (!ready)
            {
                Logger.Warning("Selected analysis provider failed to initialize; falling back to local analysis");
                service = new LocalWeatherAnalysisService();
                await service.InitializeAsync(token);
            }

            _analysisService = service;
        }

        #region ISafetyMonitor Implementation

        public string Category => "All Sky Camera";
        public bool HasSetupDialog => true;
        public string Id => "AIWeatherSafetyMonitor";
        public string Name => "All Sky Camera Safety Monitor";
        public string Description => "Monitors all-sky camera for weather conditions and provides safety status for imaging";
        public string DriverInfo => "All Sky Camera Plugin v1.0";
        public string DriverVersion => "1.0.0";

        private bool _connected = false;
        public bool Connected
        {
            get => _connected;
            internal set
            {
                _connected = value;
                RaisePropertyChanged();
            }
        }

        public async Task<bool> Connect(CancellationToken token)
        {
            try
            {
                Logger.Info("Connecting to All Sky Camera Safety Monitor");

                // Get capture mode from settings
                var captureMode = (CaptureMode)Properties.Settings.Default.CaptureMode;
                _captureService.CurrentMode = captureMode;
                Logger.Info($"Safety Monitor - Capture Mode: {captureMode}");

                bool success = false;

                if (captureMode == CaptureMode.RTSPStream)
                {
                    // RTSP mode
                    var rtspUrl = Properties.Settings.Default.RtspUrl;
                    var username = Properties.Settings.Default.RtspUsername;
                    var password = Properties.Settings.Default.RtspPassword;

                    Logger.Info($"Safety Monitor - RTSP URL: '{rtspUrl}'");
                    _captureService.ConfigureRTSP(rtspUrl ?? "", username, password);
                    success = !string.IsNullOrWhiteSpace(rtspUrl);
                }
                else if (captureMode == CaptureMode.INDICamera)
                {
                    // HTTP Image Download mode
                    var imageUrl = Properties.Settings.Default.INDIDeviceName;
                    var username = Properties.Settings.Default.RtspUsername;
                    var password = Properties.Settings.Default.RtspPassword;
                    
                    Logger.Info($"Safety Monitor - HTTP Image URL: '{imageUrl}'");
                    _captureService.ConfigureINDI(imageUrl ?? "", username, password);
                    success = !string.IsNullOrWhiteSpace(imageUrl);
                }
                else if (captureMode == CaptureMode.FolderWatch)
                {
                    // Folder Watch mode
                    var folderPath = Properties.Settings.Default.FolderPath;
                    Logger.Info($"Safety Monitor - Folder Path: '{folderPath}'");
                    _captureService.ConfigureFolderWatch(folderPath ?? "");
                    success = !string.IsNullOrWhiteSpace(folderPath) && Directory.Exists(folderPath);
                }

                if (!success)
                {
                    Logger.Error($"Safety Monitor - Failed to configure {captureMode} mode");
                    return false;
                }

                await RefreshAnalysisServiceAsync(token);

                // A fresh connection starts with no verdict at all: unsafe until the first
                // analysis succeeds, never inheriting the state of a previous session.
                _lastAnalysisUtc = DateTime.MinValue;
                _providerHealth.Reset();
                _lastFrameFingerprint = null;
                _identicalFrameRun = 0;
                _isCurrentlySafe = false;
                _staleLogged = false;
                _lastDecisionSafe = null;

                if (Properties.Settings.Default.UseAscomSafetyMonitor)
                {
                    _externalDevice.ConnectNow(Properties.Settings.Default.AscomSafetyMonitorProgId, UtcNow());
                }
                if (Properties.Settings.Default.UseEnvironmentSafetyMonitor)
                {
                    _environmentDevice.ConnectNow(Properties.Settings.Default.EnvironmentSafetyMonitorProgId, UtcNow());
                }

                // Mark as connected BEFORE starting periodic monitoring
                // so that UI handlers can see Connected=true when the first check completes
                Connected = true;
                Logger.Info($"All Sky Camera Safety Monitor connected using {captureMode} mode");

                // Start periodic monitoring (first check runs immediately)
                StartPeriodicMonitoring();

                MonitoringStarted?.Invoke(this, EventArgs.Empty);
                return true;
            }
            catch (Exception ex)
            {
                Logger.Error($"Error connecting to safety monitor: {ex.Message}", ex);
                return false;
            }
        }

        public void Disconnect()
        {
            try
            {
                Logger.Info("Disconnecting All Sky Camera Safety Monitor");

                StopPeriodicMonitoring();
                _captureService.Dispose();

                // Values are no longer being refreshed: blank the sequencer symbols so an
                // expression cannot keep acting on a stale reading.
                SequencerSymbolPublisher.ClearValues();

                // Drop the verdict with the connection, so a reconnect cannot start from a
                // SAFE inherited from before the disconnect.
                _lastAnalysisUtc = DateTime.MinValue;
                _isCurrentlySafe = false;

                _externalDevice.Disconnect();
                _environmentDevice.Disconnect();

                Connected = false;
                Logger.Info("All Sky Camera Safety Monitor disconnected");
            }
            catch (Exception ex)
            {
                Logger.Error($"Error disconnecting: {ex.Message}", ex);
            }
        }

        /// <summary>
        /// The state NINA acts on: the sky verdict from the latest analysis, that verdict
        /// still being recent enough to describe the current sky, and the external ASCOM
        /// safety monitor (when one is configured) combined with AND or OR as the owner
        /// chose. Anything unknown counts as unsafe on its own side — a missing answer is
        /// not a permission to keep imaging. The truth table lives in SafetyVerdict.
        /// </summary>
        public bool IsSafe => SafetyVerdict.Combine(
            ExternalMode,
            _isCurrentlySafe,
            IsAnalysisFresh(),
            _lastResult is { } r && (r.RainDetected || r.FogDetected),
            Properties.Settings.Default.UseAscomSafetyMonitor,
            IsExternalMonitorSafe(),
            Properties.Settings.Default.UseEnvironmentSafetyMonitor,
            IsEnvironmentMonitorSafe());

        private static ExternalMonitorMode ExternalMode => (ExternalMonitorMode)Properties.Settings.Default.ExternalMonitorMode;

        /// <summary>The sky verdict alone, without freshness or the external monitor.</summary>
        public bool IsSkyConditionSafe => _isCurrentlySafe;

        /// <summary>Why the last capture returned nothing, when the source could say; null after a capture.</summary>
        public string? LastCaptureFailure { get; private set; }

        /// <summary>Which frame the last analysis looked at (file and time for Folder Watch), for the panel.</summary>
        public string? LastFrameSource { get; private set; }

        /// <summary>
        /// The plugin's own folder under N.I.N.A.'s local data: rolling captures and the
        /// decision archive. It was "AllSkyCameraPlugin", the plugin's name at birth.
        /// </summary>
        public static string PluginDataFolder => Path.Combine(CoreUtil.APPLICATIONTEMPPATH, "AIWeather");

        /// <summary>The rolling debug captures, pruned to the newest few.</summary>
        public static string CaptureFolder => Path.Combine(PluginDataFolder, "captures");

        /// <summary>Where the frames behind the decisions go (#21).</summary>
        public static string DefaultDecisionFolder => Path.Combine(PluginDataFolder, "decisions");

        /// <summary>The decision archive; the tests point it at a temporary folder.</summary>
        internal DecisionArchive Decisions = new DecisionArchive(DefaultDecisionFolder);

        // The combined state the last archived decision reported, so only a change is kept.
        private bool? _lastDecisionSafe;

        /// <summary>
        /// One line for the panel when the chosen provider is not the one answering; empty
        /// while it is. The offline fallback keeps the night safe, this keeps it honest.
        /// </summary>
        public string ProviderHealth => _providerHealth.Describe();

        /// <summary>
        /// Compare the captured frame with the previous one. Identical pixels twice in a row
        /// on a live camera is not a sky, it is a decoder that stalled; say so in the log,
        /// with the run length, and leave the verdict alone - first the fact, then the cure.
        /// </summary>
        private void NoteFrameIdentity(Bitmap frame)
        {
            try
            {
                var fingerprint = FrameFingerprint.Compute(frame);
                if (_lastFrameFingerprint == fingerprint)
                {
                    _identicalFrameRun++;
                    Logger.Warning($"Captured frame is identical to the previous one ({_identicalFrameRun} in a row) - the camera source may be frozen and the analysis is repeating an old image");
                }
                else
                {
                    if (_identicalFrameRun > 0)
                    {
                        Logger.Info($"Captured frame changed again after {_identicalFrameRun} identical frame(s)");
                    }
                    _identicalFrameRun = 0;
                }
                _lastFrameFingerprint = fingerprint;
            }
            catch (Exception ex)
            {
                Logger.Debug($"Frame fingerprint skipped: {ex.Message}");
            }
        }

        /// <summary>
        /// Why the monitor is reporting what it reports, in one line for the panel. Until
        /// now this only existed in the log, which meant a user seeing UNSAFE on a clear
        /// night had no way to tell a cloudy verdict from a dead camera or an unreachable
        /// external device. Conditions are reported in the order they are evaluated, so the
        /// first thing that is actually wrong is the thing shown.
        /// </summary>
        public string SafetyStateReason
        {
            get
            {
                if (!Connected)
                {
                    return "Not connected";
                }

                if (_lastAnalysisUtc == DateTime.MinValue)
                {
                    return "Waiting for the first sky analysis";
                }

                if (Properties.Settings.Default.UseEnvironmentSafetyMonitor && !IsEnvironmentMonitorSafe())
                {
                    return _environmentDevice.Connected
                        ? "Environment safety monitor reports unsafe"
                        : "Environment safety monitor cannot be connected or read";
                }

                var externalEnabled = Properties.Settings.Default.UseAscomSafetyMonitor;
                var orMode = externalEnabled && ExternalMode == ExternalMonitorMode.Or;

                if (orMode && IsSafe)
                {
                    // One side carried the verdict: say which, so a sensor that is quietly
                    // unreadable or a camera that stopped is not hidden behind a green state.
                    if (!IsAnalysisFresh()) return "Safe on the external monitor alone — sky data is stale";
                    if (!IsExternalMonitorSafe()) return "Safe on the sky analysis alone — external monitor unsafe or unreadable";
                    if (!_isCurrentlySafe) return "Safe on the external monitor alone — sky " + SkyUnsafeReason();
                }

                if (!IsAnalysisFresh() && !orMode)
                {
                    var age = (UtcNow() - _lastAnalysisUtc).TotalMinutes;
                    return $"Stale data: last analysis {age:F0} min ago, limit {MaxDataAge().TotalMinutes:F0} min — check the camera source";
                }

                if (externalEnabled && !IsExternalMonitorSafe() && !orMode)
                {
                    return _externalDevice.Connected
                        ? "External safety monitor reports unsafe"
                        : "External safety monitor cannot be connected or read";
                }

                if (orMode && !IsSafe)
                {
                    if (IsExternalMonitorSafe())
                    {
                        // Only rain or fog get here: the one sky verdict a clear-reading sensor cannot override.
                        return "Sky " + SkyUnsafeReason() + " — rain and fog stop imaging whatever the external monitor says";
                    }
                    var external = _externalDevice.Connected ? "reports unsafe" : "cannot be connected or read";
                    var sky = IsAnalysisFresh() ? SkyUnsafeReason() : "data is stale";
                    return $"Sky {sky}; external safety monitor {external}";
                }

                if (!_isCurrentlySafe)
                {
                    return "Sky " + SkyUnsafeReason();
                }

                var coverage = _lastResult?.CloudCoverage ?? 0;
                return $"Sky safe: cloud coverage {coverage:F0}% (unsafe at {Properties.Settings.Default.CloudCoverageThreshold:F0}%)";
            }
        }

        /// <summary>Why the sky alone is unsafe, from the latest analysis; lower case, to be quoted.</summary>
        private string SkyUnsafeReason()
        {
            var r = _lastResult;
            if (r == null)
            {
                return "has no usable analysis";
            }
            if (r.RainDetected)
            {
                return "shows rain";
            }
            if (r.FogDetected)
            {
                return "shows fog";
            }
            return $"cloud coverage {r.CloudCoverage:F0}% (safe below {Properties.Settings.Default.CloudCoverageSafeThreshold}%)";
        }

        /// <summary>
        /// Maximum age of the latest successful analysis. Configurable; 0 means automatic
        /// (three check intervals, never below ten minutes) so a long polling interval
        /// cannot make the state permanently stale by construction.
        /// </summary>
        private static TimeSpan MaxDataAge()
        {
            var configured = Properties.Settings.Default.MaxDataAgeMinutes;
            if (configured > 0)
            {
                return TimeSpan.FromMinutes(configured);
            }

            var interval = Math.Max(1, Properties.Settings.Default.CheckIntervalMinutes);
            var automatic = TimeSpan.FromMinutes(interval * 3);
            return automatic < MinimumAutomaticDataAge ? MinimumAutomaticDataAge : automatic;
        }

        /// <summary>
        /// Whether the latest analysis is recent enough to be acted upon. Logged once per
        /// transition rather than per call: NINA polls IsSafe continuously.
        /// </summary>
        private bool IsAnalysisFresh()
        {
            if (_lastAnalysisUtc == DateTime.MinValue)
            {
                return false; // nothing analysed yet since connecting
            }

            var age = UtcNow() - _lastAnalysisUtc;
            if (age <= MaxDataAge())
            {
                return true;
            }

            if (!_staleLogged)
            {
                _staleLogged = true;
                Logger.Warning($"Safety monitor reporting UNSAFE: no successful sky analysis for {age.TotalMinutes:F1} minutes " +
                               $"(limit {MaxDataAge().TotalMinutes:F0} min). Check the all-sky camera source.");
            }
            return false;
        }

        /// <summary>The external ASCOM safety monitor's verdict, or true when the feature is off.</summary>
        private bool IsExternalMonitorSafe()
        {
            return !Properties.Settings.Default.UseAscomSafetyMonitor
                || _externalDevice.IsSafe(Properties.Settings.Default.AscomSafetyMonitorProgId, UtcNow());
        }

        /// <summary>The environment ASCOM safety monitor's verdict, or true when the feature is off.</summary>
        private bool IsEnvironmentMonitorSafe()
        {
            return !Properties.Settings.Default.UseEnvironmentSafetyMonitor
                || _environmentDevice.IsSafe(Properties.Settings.Default.EnvironmentSafetyMonitorProgId, UtcNow());
        }

        /// <summary>
        /// One analysis has arrived: the verdict, the freshness clock, the sequencer symbols,
        /// the log, the wiki digest and the status file all follow from it here. Internal so
        /// a night can be replayed in the tests result by result, external device included.
        /// </summary>
        internal void ApplyAnalysisResult(WeatherAnalysisResult result)
        {
            _lastResult = result;

            if (_providerHealth.Record(result))
            {
                if (_providerHealth.IsFailing)
                {
                    Logger.Warning($"{result.Provider} stopped answering; the offline analyzer is being used instead. Reason: {result.ProviderError}");
                }
                else
                {
                    Logger.Info($"{result.Provider} is answering again");
                }
            }
            RaisePropertyChanged(nameof(ProviderHealth));

            // The clock the freshness check runs against. Set only on a real result:
            // every provider falls back to the offline local analyzer internally, so
            // reaching this line means the sky was actually assessed.
            _lastAnalysisUtc = UtcNow();
            _staleLogged = false;

            // Update Safety State (Hysteresis)
            UpdateSafetyState(result);

            KeepDecisionIfChanged(result);

            // Expose the reading to the Advanced Sequencer's Symbols sidebar (N.I.N.A. 3.3+).
            // The published Safe symbol is the composite state, so an expression in the
            // sequencer sees the same verdict NINA's safety monitor sees.
            SequencerSymbolPublisher.Publish(result, IsSafe);

            // Log the results
            Logger.Info($"Weather Analysis - Condition: {result.Condition}, " +
                      $"Cloud Coverage: {result.CloudCoverage:F1}%, " +
                      $"Safe: {result.IsSafeForImaging}, " +
                      $"Confidence: {result.Confidence:F1}%" +
                      (LastFrameSource != null ? $", Frame: {LastFrameSource}" : string.Empty));

            if (Properties.Settings.Default.UseAscomSafetyMonitor || Properties.Settings.Default.UseEnvironmentSafetyMonitor)
            {
                var external = Properties.Settings.Default.UseAscomSafetyMonitor
                    ? $"external ASCOM monitor ({ExternalMode}): {(IsExternalMonitorSafe() ? "SAFE" : "UNSAFE")}, " : string.Empty;
                var environment = Properties.Settings.Default.UseEnvironmentSafetyMonitor
                    ? $"environment ASCOM monitor: {(IsEnvironmentMonitorSafe() ? "SAFE" : "UNSAFE")}, " : string.Empty;
                Logger.Info($"Safety state - sky: {(_isCurrentlySafe ? "SAFE" : "UNSAFE")}, " + external + environment +
                            $"combined: {(IsSafe ? "SAFE" : "UNSAFE")}");
            }

            // Append state changes to the shared LLM wiki daily digest (raw/)
            LlmWikiRawWriter.RecordAnalysis(result);

            // Raise property changed to notify NINA of safety status change
            RaisePropertyChanged(nameof(IsSafe));
            RaisePropertyChanged(nameof(SafetyStateReason));

            // Write safety status to file if enabled
            WriteSafetyStatusFile();
        }

        /// <summary>
        /// The frame behind a change of the combined state, kept with the decision written
        /// next to it. A change caused by a device between two analyses is caught at the
        /// next analysis, with that frame.
        /// </summary>
        private void KeepDecisionIfChanged(WeatherAnalysisResult result)
        {
            if (!Properties.Settings.Default.KeepDecisionFrames)
            {
                return;
            }

            var combined = IsSafe;
            if (_lastDecisionSafe == combined)
            {
                return;
            }
            var transition = $"{(_lastDecisionSafe is bool prev ? (prev ? "Safe" : "Unsafe") : "Start")} -> {(combined ? "Safe" : "Unsafe")}";
            _lastDecisionSafe = combined;

            string Device(bool enabled, bool safe, ExternalSafetyDevice device) =>
                !enabled ? "not configured" : safe ? "Safe" : device.Connected ? "Unsafe" : "cannot be connected or read";

            var record = new DecisionRecord
            {
                Transition = transition,
                Utc = UtcNow(),
                // Evidence names who actually looked at the frame: a verdict from the offline
                // analyzer after the provider failed is filed as the analyzer's, with the failure.
                Provider = result.FellBackToLocal
                    ? $"Local (offline fallback - {result.Provider} failed: {result.ProviderError ?? "no answer"})"
                    : result.Provider,
                Model = result.FellBackToLocal || string.Equals(result.Provider, "Local", StringComparison.OrdinalIgnoreCase)
                    ? string.Empty
                    : result.Model ?? Properties.Settings.Default.SelectedModel ?? string.Empty,
                Condition = result.Condition,
                CloudCoverage = result.CloudCoverage,
                Confidence = result.Confidence,
                RainDetected = result.RainDetected,
                FogDetected = result.FogDetected,
                Description = result.Description,
                HighThreshold = Properties.Settings.Default.CloudCoverageThreshold,
                LowThreshold = Properties.Settings.Default.CloudCoverageSafeThreshold,
                SkySafe = _isCurrentlySafe,
                ExternalMonitor = Properties.Settings.Default.UseAscomSafetyMonitor
                    ? $"{Device(true, IsExternalMonitorSafe(), _externalDevice)} ({ExternalMode})"
                    : "not configured",
                EnvironmentMonitor = Device(Properties.Settings.Default.UseEnvironmentSafetyMonitor, IsEnvironmentMonitorSafe(), _environmentDevice),
                CombinedSafe = combined,
                Reason = SafetyStateReason,
            };

            var path = Decisions.Save(_lastImage, record, UtcNow().ToLocalTime());
            Logger.Info($"Decision kept ({transition}): {path ?? "no frame"}, {Decisions.Folder}");
            Decisions.Prune(TimeSpan.FromDays(Math.Max(1, Properties.Settings.Default.DecisionRetentionDays)), UtcNow().ToLocalTime());
        }

        /// <summary>
        /// The sky side of the verdict after one analysis: rain or fog is unsafe, otherwise
        /// hysteresis - a safe sky stays safe until coverage reaches the high threshold, an
        /// unsafe one stays unsafe until coverage drops below the low one. Pure, so a change
        /// can be evaluated before it is committed.
        /// </summary>
        internal static bool NextSkyState(bool currentlySafe, WeatherAnalysisResult result, double unsafeThreshold, double safeThreshold)
        {
            if (result == null) return false;
            if (!result.IsSafeForImaging || result.RainDetected || result.FogDetected) return false;
            return currentlySafe ? result.CloudCoverage < unsafeThreshold : result.CloudCoverage < safeThreshold;
        }

        /// <summary>The pause before the second frame of a transition; the tests pass one that does not wait.</summary>
        internal Func<TimeSpan, CancellationToken, Task> Delay = Task.Delay;
        private static readonly TimeSpan ConfirmPause = TimeSpan.FromSeconds(20);

        /// <summary>
        /// A change of the combined state wants two frames. Three Safe -> Unsafe -> Safe cycles
        /// in one night, each from a single reading on frames that looked the same, parked a
        /// rig and cancelled an autofocus (#21). When an analysis would change the state, a
        /// second frame is taken and analysed, and the second frame decides: the change goes
        /// through only if it agrees. No change proposed, no second frame. Without a second
        /// frame the first reading stands - a camera that cannot deliver does not veto.
        /// </summary>
        internal async Task<WeatherAnalysisResult> ResolveTransitionAsync(WeatherAnalysisResult first, Func<Task<WeatherAnalysisResult?>> secondOpinion)
        {
            if (!Properties.Settings.Default.ConfirmTransitions) return first;
            if (_lastResult == null) return first;   // the first verdict after connecting: nothing to protect yet

            var high = Properties.Settings.Default.CloudCoverageThreshold;
            var low = Properties.Settings.Default.CloudCoverageSafeThreshold;
            var prospectiveSky = NextSkyState(_isCurrentlySafe, first, high, low);
            var prospective = SafetyVerdict.Combine(ExternalMode, prospectiveSky, true,
                first.RainDetected || first.FogDetected,
                Properties.Settings.Default.UseAscomSafetyMonitor, IsExternalMonitorSafe(),
                Properties.Settings.Default.UseEnvironmentSafetyMonitor, IsEnvironmentMonitorSafe());
            if (prospective == IsSafe) return first;

            Logger.Info($"Analysis proposes {(IsSafe ? "Safe" : "Unsafe")} -> {(prospective ? "Safe" : "Unsafe")} ({first.Condition}, {first.CloudCoverage:F0}%); asking a second frame before acting on it");
            var second = await secondOpinion();
            if (second == null)
            {
                Logger.Warning("No second frame; the first reading stands");
                return first;
            }

            var confirmedSky = NextSkyState(_isCurrentlySafe, second, high, low);
            var confirmed = SafetyVerdict.Combine(ExternalMode, confirmedSky, true,
                second.RainDetected || second.FogDetected,
                Properties.Settings.Default.UseAscomSafetyMonitor, IsExternalMonitorSafe(),
                Properties.Settings.Default.UseEnvironmentSafetyMonitor, IsEnvironmentMonitorSafe());
            Logger.Info(confirmed == prospective
                ? $"Second frame agrees ({second.Condition}, {second.CloudCoverage:F0}%): the change goes through"
                : $"Second frame disagrees ({second.Condition}, {second.CloudCoverage:F0}%): the state stays {(IsSafe ? "Safe" : "Unsafe")}");
            return second;
        }

        /// <summary>One analysis with the retry, the reserve model and the offline analyzer behind it.</summary>
        private Task<WeatherAnalysisResult> AnalyzeWithRetryAsync(Bitmap frame, AstroContext? astroContext, CancellationToken cancellationToken)
        {
            var reserveModel = Properties.Settings.Default.FallbackModel?.Trim();
            Func<IWeatherAnalysisService?>? reserve = string.IsNullOrWhiteSpace(reserveModel) || AnalysisServiceFactory.SelectedProvider() == "Local"
                ? null
                : () => AnalysisServiceFactory.CreateFromSettings(reserveModel);
            return AnalysisRetry.AnalyzeAsync(_analysisService, Properties.Settings.Default.SelectedModel ?? string.Empty,
                reserve, reserveModel, frame, astroContext, Delay, cancellationToken);
        }

        private void UpdateSafetyState(WeatherAnalysisResult result)
        {
            var unsafeThreshold = Properties.Settings.Default.CloudCoverageThreshold;
            var safeThreshold = Properties.Settings.Default.CloudCoverageSafeThreshold;
            _isCurrentlySafe = NextSkyState(_isCurrentlySafe, result, unsafeThreshold, safeThreshold);
            if (result == null) return;

            Logger.Debug($"Safety check: {(_isCurrentlySafe ? "SAFE" : "UNSAFE")} - " +
                       $"Cloud coverage: {result.CloudCoverage:F1}%, " +
                       $"Safe Threshold: {safeThreshold}%, Unsafe Threshold: {unsafeThreshold}%, " +
                       $"Rain: {result.RainDetected}, Fog: {result.FogDetected}, " +
                       $"Condition: {result.Condition}");
        }

        // IDevice methods required by interface
        public string Action(string actionName, string actionParameters)
        {
            return string.Empty;
        }

        public string SendCommandString(string command, bool raw = true)
        {
            return string.Empty;
        }

        public bool SendCommandBool(string command, bool raw = true)
        {
            return false;
        }

        public void SendCommandBlind(string command, bool raw = true)
        {
            // No-op
        }

        public string DisplayName
        {
            get => Name;
            set { }
        }

        public IList<string> SupportedActions => new List<string>();

        #endregion

        private void StartPeriodicMonitoring()
        {
            if (_isMonitoring) return;

            _cts = new CancellationTokenSource();
            _isMonitoring = true;

            var intervalMinutes = Properties.Settings.Default.CheckIntervalMinutes;
            var interval = TimeSpan.FromMinutes(intervalMinutes);

            var captureMode = (CaptureMode)Properties.Settings.Default.CaptureMode;
            Logger.Debug($"Starting periodic monitoring every {intervalMinutes} minutes (Mode: {captureMode})");

            _monitoringTimer = new Timer(_ =>
            {
                var currentMode = (CaptureMode)Properties.Settings.Default.CaptureMode;
                Logger.Debug($"Timer fired - Interval: {intervalMinutes} min, Mode: {currentMode}");
                
                if (_cts?.Token.IsCancellationRequested ?? true)
                {
                    Logger.Warning("Timer fired but cancellation was requested - skipping");
                    return;
                }

                try
                {
                    Logger.Debug($"Launching weather check task from timer (Mode: {currentMode})");
                    Task.Run(async () =>
                    {
                        try
                        {
                            Logger.Debug($"Executing periodic weather check (Mode: {currentMode})");
                            await PerformWeatherCheckAsync(_cts.Token);
                            Logger.Debug($"Weather check complete - next check in {intervalMinutes} min");
                        }
                        catch (OperationCanceledException)
                        {
                            // Our own stop, not a failure: monitoring was switched off mid-check.
                            Logger.Info("Weather check cancelled because monitoring stopped");
                        }
                        catch (Exception ex)
                        {
                            Logger.Error($"Error in periodic weather check: {ex.Message}", ex);
                        }
                    });
                }
                catch (Exception ex)
                {
                    Logger.Error($"Failed to start weather check task: {ex.Message}", ex);
                }
            }, null, TimeSpan.Zero, interval);
            
            Logger.Debug($"Timer created and started - first check will run immediately");
        }

        private void StopPeriodicMonitoring()
        {
            _isMonitoring = false;
            _cts?.Cancel();
            _monitoringTimer?.Dispose();
            _monitoringTimer = null;
            Logger.Info("Stopped periodic monitoring");
        }

        private async Task PerformWeatherCheckAsync(CancellationToken cancellationToken)
        {
            await _checkGate.WaitAsync(cancellationToken);
            try
            {
                var captureMode = (CaptureMode)Properties.Settings.Default.CaptureMode;
                Logger.Debug($"PerformWeatherCheckAsync - Mode: {captureMode}");

                Bitmap? frame = null;

                // Capture image from all modes
                Logger.Debug($"Capturing image from {captureMode} source");
                frame = await _captureService.CaptureImageAsync(cancellationToken);

                if (frame == null)
                {
                    // No new data. The state is deliberately NOT flipped here on a single
                    // miss - a dropped frame on an RTSP stream would make the monitor flap
                    // and abort sequences - but it is not silently kept either: the analysis
                    // ages, and IsSafe turns unsafe once it passes the maximum data age.
                    LastCaptureFailure = _captureService.LastFailure;
                    Logger.Warning($"{LastCaptureFailure ?? $"Failed to capture image from {captureMode} source"}; " +
                                   $"last successful analysis is {LastAnalysisAgeDescription()} old");
                    RaisePropertyChanged(nameof(IsSafe));
                RaisePropertyChanged(nameof(SafetyStateReason));
                    return;
                }

                Logger.Debug($"Image captured from {captureMode}, size: {frame.Width}x{frame.Height}");
                if (FrameSanity.LooksCorrupt(frame, out var corruptReason))
                {
                    LastCaptureFailure = corruptReason;
                    Logger.Warning($"{corruptReason}; not analysed. Last successful analysis is {LastAnalysisAgeDescription()} old");
                    frame.Dispose();
                    RaisePropertyChanged(nameof(IsSafe));
                    RaisePropertyChanged(nameof(SafetyStateReason));
                    return;
                }
                NoteFrameIdentity(frame);

                // Analyze the frame
                Logger.Debug($"Starting AI analysis using {_analysisService.GetType().Name}");

                // Compute astronomical context from observer location
                AstroContext? astroContext = null;
                try
                {
                    if (_profileService != null)
                    {
                        var astro = _profileService.ActiveProfile.AstrometrySettings;
                        astroContext = AstroContext.Compute(
                            astro.Latitude, astro.Longitude, astro.Elevation, UtcNow());
                        Logger.Info($"Astro context: Sun {astroContext.SunAltitude:F1}° ({astroContext.SunState}), " +
                                   $"Moon {astroContext.MoonIllumination:F0}% {astroContext.MoonPhase} at {astroContext.MoonAltitude:F1}°");
                    }
                }
                catch (Exception ex)
                {
                    Logger.Warning($"Failed to compute astronomical context: {ex.Message}");
                }

                var result = await AnalyzeWithRetryAsync(frame, astroContext, cancellationToken);
                Logger.Debug("AI analysis completed");
                LastCaptureFailure = null;
                LastFrameSource = _captureService.LastSourceDescription;

                // A proposed change of state is checked against a second frame, which then
                // becomes the frame behind the verdict.
                Bitmap? secondFrame = null;
                result = await ResolveTransitionAsync(result, async () =>
                {
                    await Delay(ConfirmPause, cancellationToken);
                    var again = await _captureService.CaptureImageAsync(cancellationToken);
                    if (again == null) return null;
                    if (FrameSanity.LooksCorrupt(again, out var why))
                    {
                        Logger.Warning($"Second frame: {why}");
                        again.Dispose();
                        return null;
                    }
                    secondFrame = again;
                    LastFrameSource = _captureService.LastSourceDescription;
                    return await AnalyzeWithRetryAsync(again, astroContext, cancellationToken);
                });
                if (secondFrame != null)
                {
                    frame.Dispose();
                    frame = secondFrame;
                }

                // Store a copy of the image for UI restoration
                _lastImage?.Dispose();
                _lastImage = new Bitmap(frame);

                ApplyAnalysisResult(result);

                // Save frame for debugging/logging (optional)
                var captureFolder = CaptureFolder;
                Directory.CreateDirectory(captureFolder);
                var imagePath = Path.Combine(
                    captureFolder,
                    $"capture_{DateTime.Now:yyyyMMdd_HHmmss}.jpg");

                // Save image (HTTP/Folder modes only, RTSP handled above)
                await _captureService.SaveImageAsync(frame, imagePath, cancellationToken);

                PruneCaptureFolder(captureFolder);

                frame.Dispose();
            }
            catch (OperationCanceledException)
            {
                // Monitoring stopped mid-check: our own doing, nothing to report.
                Logger.Info("Weather check cancelled because monitoring stopped");
            }
            catch (Exception ex)
            {
                // Same contract as a failed capture: no new verdict, so the existing one
                // keeps ageing toward the freshness limit rather than being trusted forever.
                Logger.Error($"Error performing weather check: {ex.Message}", ex);
                RaisePropertyChanged(nameof(IsSafe));
                RaisePropertyChanged(nameof(SafetyStateReason));
            }
            finally
            {
                _checkGate.Release();
            }
        }

        private string LastAnalysisAgeDescription()
        {
            return _lastAnalysisUtc == DateTime.MinValue
                ? "no analysis yet"
                : $"{(UtcNow() - _lastAnalysisUtc).TotalMinutes:F1} min";
        }

        // Debug captures are only needed for recent history; keep the folder bounded
        // so an always-on monitor cannot fill the disk over long sessions.
        private const int MaxSavedCaptures = 25;

        private static void PruneCaptureFolder(string folder)
        {
            try
            {
                if (!Directory.Exists(folder))
                {
                    return;
                }

                var files = new DirectoryInfo(folder).GetFiles("capture_*.jpg");
                if (files.Length <= MaxSavedCaptures)
                {
                    return;
                }

                Array.Sort(files, (a, b) => b.LastWriteTimeUtc.CompareTo(a.LastWriteTimeUtc));
                for (var i = MaxSavedCaptures; i < files.Length; i++)
                {
                    try
                    {
                        files[i].Delete();
                    }
                    catch (Exception ex)
                    {
                        Logger.Debug($"Failed to delete old capture {files[i].Name}: {ex.Message}");
                    }
                }

                Logger.Debug($"Pruned capture folder to {MaxSavedCaptures} most recent images ({files.Length - MaxSavedCaptures} deleted)");
            }
            catch (Exception ex)
            {
                Logger.Warning($"Failed to prune capture folder: {ex.Message}");
            }
        }

        public void SetupDialog()
        {
            // This would open a settings dialog
            // For now, settings are managed through plugin options
            Logger.Info("Setup dialog requested - use NINA Plugin Options");
        }

        /// <summary>
        /// Get the latest weather analysis result
        /// </summary>
        public WeatherAnalysisResult? GetLatestResult() => _lastResult;

        /// <summary>
        /// Get the latest captured image
        /// </summary>
        public Bitmap? GetLatestImage() => _lastImage != null ? new Bitmap(_lastImage) : null;

        /// <summary>
        /// Force an immediate weather check
        /// </summary>
        public async Task<WeatherAnalysisResult?> ForceCheckAsync(CancellationToken cancellationToken = default)
        {
            // A start right after a start (a user pressing play and stop to test a camera)
            // does not need a fresh provider call: the sky of thirty seconds ago is the sky.
            if (AnalysisThrottle.ShouldReuse(_lastAnalysisUtc, UtcNow()) && _lastResult != null)
            {
                Logger.Info($"Immediate check skipped: the last analysis is {(UtcNow() - _lastAnalysisUtc).TotalSeconds:F0}s old and is reused");
                return _lastResult;
            }

            // Connecting runs a first check at once and the panel asks for one two seconds
            // later: the same image was analysed twice, three seconds apart, and the log
            // warned about an identical frame. A check in progress is the answer.
            if (_checkGate.CurrentCount == 0)
            {
                Logger.Info("Immediate check skipped: a check is already running; waiting for its result");
                await _checkGate.WaitAsync(cancellationToken);
                _checkGate.Release();
                return _lastResult;
            }

            await PerformWeatherCheckAsync(cancellationToken);
            return _lastResult;
        }

        /// <summary>
        /// Write safety status to file if enabled
        /// </summary>
        private void WriteSafetyStatusFile()
        {
            try
            {
                if (!Properties.Settings.Default.WriteSafetyStatusFile)
                {
                    return;
                }

                var filePath = SafetyStatusFile.Resolve(Properties.Settings.Default.SafetyStatusFilePath);
                Directory.CreateDirectory(Path.GetDirectoryName(filePath)!);

                // The exported status is the same composite state NINA acts on - hysteresis,
                // data freshness and the external monitor included. It used to be recomputed
                // from the raw result here, which could disagree with IsSafe and hand third
                // party software a different answer than the one driving the sequence.
                var status = IsSafe ? "Safe" : "Unsafe";

                // Write plain SAFE/UNSAFE — compatible with ASCOM Generic File SafetyMonitor
                File.WriteAllText(filePath, status);
                Logger.Debug($"Safety status written to file: {filePath} - Status: {status}");
            }
            catch (Exception ex)
            {
                Logger.Error($"Error writing safety status file: {ex.Message}", ex);
            }
        }
    }
}
