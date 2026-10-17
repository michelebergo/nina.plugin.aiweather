using AIWeather.Models;
using System;
using System.Collections.Generic;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.Drawing.Imaging;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Threading;
using System.Threading.Channels;
using System.Threading.Tasks;

namespace AIWeather.Services
{
    public interface IDatasetRecorderLogger
    {
        void Info(string message);
        void Warning(string message);
        void Error(string message, Exception exception);
    }

    internal sealed class NullDatasetRecorderLogger : IDatasetRecorderLogger
    {
        public void Info(string message) { }
        public void Warning(string message) { }
        public void Error(string message, Exception exception) { }
    }

    /// <summary>
    /// Failure-isolated, bounded, single-writer dataset recorder. No exception from this
    /// class is allowed to escape into the safety-monitor check path.
    /// </summary>
    [System.Runtime.Versioning.SupportedOSPlatform("windows")]
    public sealed class TeacherStudentDatasetRecorder
    {
        private const int EventQueueCapacity = 8;
        private const int PeriodicQueueCapacity = 24;
        private static readonly TimeSpan ErrorLogInterval = TimeSpan.FromMinutes(5);

        private readonly Channel<DatasetSampleEnvelope> _eventQueue =
            Channel.CreateBounded<DatasetSampleEnvelope>(new BoundedChannelOptions(EventQueueCapacity)
            {
                // Wait mode makes TryWrite return false when full. DropWrite can report
                // success while silently discarding the IDisposable envelope.
                FullMode = BoundedChannelFullMode.Wait,
                SingleReader = true,
                SingleWriter = false
            });

        private readonly Channel<DatasetSampleEnvelope> _periodicQueue =
            Channel.CreateBounded<DatasetSampleEnvelope>(new BoundedChannelOptions(PeriodicQueueCapacity)
            {
                FullMode = BoundedChannelFullMode.Wait,
                SingleReader = true,
                SingleWriter = false
            });

        private readonly CancellationTokenSource _writerCts = new CancellationTokenSource();
        private readonly Task _writerTask;
        private readonly Func<DatasetRecorderOptions> _optionsProvider;
        private readonly IDatasetRecorderLogger _log;
        private readonly object _selectionGate = new object();
        private readonly object _statusGate = new object();
        private readonly SemaphoreSlim _initializationGate = new SemaphoreSlim(1, 1);
        private readonly HashSet<string> _initializedRoots =
            new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        private int _successfulTeacherChecksSincePeriodic;
        private WeatherAnalysisResult? _previousTeacher;
        private string? _previousSunState;
        private bool _hasPreviousDecision;
        private bool _previousEffectiveSafe;
        private bool _previousVisualSafe;
        private bool? _previousExternalSafetyMonitorSafe;
        private ulong? _lastStoredPerceptualHash;
        private string? _lastStoredSha256;
        private string _state = "Disabled";
        private string _rootDirectory = string.Empty;
        private string? _lastError;
        private DateTime _lastErrorLogUtc = DateTime.MinValue;
        private DateTime? _lastWriteUtc;
        private double? _lastDifference;
        private string _lastTeacher = "none";
        private long _totalSamples;
        private long _trainableSamples;
        private long _quarantineSamples;
        private long _reviewSamples;
        private long _todaySamples;
        private long _droppedSamples;
        private long _errorCount;
        private long _currentBytes;
        private long _freeBytes;
        private bool _stopping;

        private static readonly JsonSerializerOptions PrettyJson = CreateJsonOptions(indented: true);
        private static readonly JsonSerializerOptions CompactJson = CreateJsonOptions(indented: false);

        public event EventHandler? StatusChanged;

        public TeacherStudentDatasetRecorder(
            Func<DatasetRecorderOptions>? optionsProvider = null,
            IDatasetRecorderLogger? logger = null)
        {
            _optionsProvider = optionsProvider ?? DatasetRecorderOptions.FromSettings;
            _log = logger ?? new NullDatasetRecorderLogger();
            _writerTask = Task.Run(() => WriterLoopAsync(_writerCts.Token));
            RefreshConfigurationStatus(_optionsProvider());
            _ = InitializeConfiguredRootAsync();
        }

        public DatasetStatusSnapshot Status
        {
            get
            {
                var options = _optionsProvider();
                lock (_statusGate)
                {
                    return new DatasetStatusSnapshot
                    {
                        Enabled = options.Enabled,
                        Paused = options.Paused,
                        State = options.Enabled
                            ? options.Paused ? "Paused" : _state
                            : "Disabled",
                        RootDirectory = options.RootDirectory,
                        TotalSamples = _totalSamples,
                        TrainableSamples = _trainableSamples,
                        QuarantineSamples = _quarantineSamples,
                        ReviewSamples = _reviewSamples,
                        TodaySamples = _todaySamples,
                        DroppedSamples = _droppedSamples,
                        ErrorCount = _errorCount,
                        CurrentBytes = _currentBytes,
                        FreeBytes = _freeBytes,
                        LastWriteUtc = _lastWriteUtc,
                        LastTeacherStudentDifference = _lastDifference,
                        LastTeacher = _lastTeacher,
                        LastError = _lastError
                    };
                }
            }
        }

        public void NotifyConfigurationChanged()
        {
            try
            {
                RefreshConfigurationStatus(_optionsProvider());
                RaiseStatusChanged();
                _ = InitializeConfiguredRootAsync();
            }
            catch (Exception ex)
            {
                RecordError("Unable to load dataset settings", ex);
            }
        }

        /// <summary>
        /// Selects and clones a frame quickly. Compression, hashing and all file I/O happen
        /// on the writer task. Returns false when no sample is due or the bounded queue is
        /// full; both are normal and never affect the safety result.
        /// </summary>
        public bool TryEnqueue(
            Bitmap frame,
            DateTime capturedUtc,
            AstroContext? astroContext,
            WeatherAnalysisBundle analysis,
            bool effectiveSafe,
            bool visualSafe,
            bool? externalSafetyMonitorSafe,
            double highThreshold,
            double lowThreshold,
            bool manualReview = false)
        {
            DatasetSampleEnvelope? envelope = null;
            try
            {
                var options = _optionsProvider();
                RefreshConfigurationStatus(options);
                if (!options.Enabled || options.Paused || _stopping)
                {
                    return false;
                }

                var decision = SelectSample(
                    options,
                    astroContext,
                    analysis,
                    effectiveSafe,
                    visualSafe,
                    externalSafetyMonitorSafe,
                    highThreshold,
                    lowThreshold,
                    manualReview);
                if (!decision.Selected)
                {
                    return false;
                }

                // Clone only after selection. The monitor disposes its frame immediately
                // after this method returns; the writer owns this independent bitmap.
                var clonedFrame = new Bitmap(frame);
                envelope = new DatasetSampleEnvelope
                {
                    Image = clonedFrame,
                    CapturedUtc = capturedUtc,
                    AstroContext = astroContext,
                    Analysis = CloneBundle(analysis, options.SaveTeacherRaw),
                    Options = options,
                    SelectionReasons = decision.Reasons,
                    IsPriorityEvent = decision.Priority,
                    Quarantined = decision.Quarantined,
                    QuarantineReason = decision.QuarantineReason,
                    EffectiveSafe = effectiveSafe,
                    VisualSafe = visualSafe,
                    ExternalSafetyMonitorSafe = externalSafetyMonitorSafe,
                    HighThreshold = highThreshold,
                    LowThreshold = lowThreshold
                };

                var accepted = decision.Priority
                    ? _eventQueue.Writer.TryWrite(envelope)
                    : _periodicQueue.Writer.TryWrite(envelope);
                if (!accepted)
                {
                    envelope.Dispose();
                    envelope = null;
                    Interlocked.Increment(ref _droppedSamples);
                    SetState("Queue full");
                    _log.Warning(
                        $"AI Weather dataset queue full; dropped a " +
                        $"{(decision.Priority ? "priority" : "periodic")} sample without delaying safety analysis");
                    RaiseStatusChanged();
                    return false;
                }

                envelope = null; // ownership transferred to the channel
                return true;
            }
            catch (Exception ex)
            {
                envelope?.Dispose();
                RecordError("Dataset enqueue failed", ex);
                return false;
            }
        }

        public async Task StopAsync(TimeSpan timeout)
        {
            if (_stopping)
            {
                return;
            }

            _stopping = true;
            _eventQueue.Writer.TryComplete();
            _periodicQueue.Writer.TryComplete();

            try
            {
                var completed = await Task.WhenAny(_writerTask, Task.Delay(timeout));
                if (completed != _writerTask)
                {
                    _writerCts.Cancel();
                    _log.Warning("Dataset writer did not flush within the shutdown timeout; abandoning queued samples safely");
                }
                else
                {
                    await _writerTask;
                }
            }
            catch (Exception ex)
            {
                RecordError("Dataset writer shutdown failed", ex);
            }
            finally
            {
                _writerCts.Cancel();
                DrainAndDispose(_eventQueue.Reader);
                DrainAndDispose(_periodicQueue.Reader);
                SetState("Stopped");
            }
        }

        private SelectionDecision SelectSample(
            DatasetRecorderOptions options,
            AstroContext? astroContext,
            WeatherAnalysisBundle analysis,
            bool effectiveSafe,
            bool visualSafe,
            bool? externalSafetyMonitorSafe,
            double highThreshold,
            double lowThreshold,
            bool manualReview)
        {
            var reasons = new List<string>();
            var priority = false;
            var quarantined = false;
            string? quarantineReason = null;
            var teacher = analysis.Teacher;

            lock (_selectionGate)
            {
                if (manualReview)
                {
                    reasons.Add("manualReview");
                    priority = true;
                }

                if (teacher?.Success == true && teacher.Result != null
                    && teacher.Provenance.OnlineSucceeded
                    && !teacher.Provenance.IsFallback)
                {
                    var current = teacher.Result;

                    if (_previousTeacher == null)
                    {
                        reasons.Add("initial");
                    }
                    else
                    {
                        _successfulTeacherChecksSincePeriodic++;

                        if (_previousTeacher.Condition != current.Condition)
                        {
                            reasons.Add("conditionChanged");
                            priority = true;
                        }

                        var crossedHigh = _previousTeacher.CloudCoverage < highThreshold
                                          && current.CloudCoverage >= highThreshold;
                        var crossedLow = _previousTeacher.CloudCoverage >= lowThreshold
                                         && current.CloudCoverage < lowThreshold;
                        if (crossedHigh || crossedLow)
                        {
                            reasons.Add("thresholdCrossing");
                            priority = true;
                        }
                    }

                    if (_previousTeacher != null
                        && _successfulTeacherChecksSincePeriodic >= options.PeriodicEveryChecks)
                    {
                        reasons.Add("periodic");
                    }

                    var difference = analysis.TeacherStudentCloudDifference;
                    if (difference.HasValue && difference.Value >= options.DisagreementThreshold)
                    {
                        reasons.Add("teacherStudentDisagreement");
                        priority = true;
                    }

                    if (current.IsSafeForImaging != analysis.Student.IsSafeForImaging)
                    {
                        reasons.Add("teacherStudentSafetyDisagreement");
                        priority = true;
                    }

                    if (current.Confidence < 50)
                    {
                        reasons.Add("teacherLowConfidence");
                        priority = true;
                    }

                    if (astroContext != null
                        && !string.IsNullOrEmpty(_previousSunState)
                        && !string.Equals(_previousSunState, astroContext.SunState, StringComparison.Ordinal))
                    {
                        reasons.Add("sunStateChanged");
                        priority = true;
                    }

                    if (_hasPreviousDecision)
                    {
                        if (_previousEffectiveSafe != effectiveSafe)
                        {
                            reasons.Add("effectiveSafetyChanged");
                            priority = true;
                        }

                        if (_previousVisualSafe != visualSafe)
                        {
                            reasons.Add("visualSafetyChanged");
                            priority = true;
                        }

                        if (_previousExternalSafetyMonitorSafe != externalSafetyMonitorSafe)
                        {
                            reasons.Add("externalSafetyChanged");
                            priority = true;
                        }
                    }

                    if (!WeatherAnalysisValidator.IsInternallyConsistent(current, out var consistencyReason))
                    {
                        quarantined = true;
                        quarantineReason = consistencyReason;
                        reasons.Add("teacherInconsistent");
                        priority = true;
                    }

                    if (quarantined && !options.RecordQuarantine && !manualReview)
                    {
                        return SelectionDecision.NotSelected;
                    }

                    _previousTeacher = current.Clone(includeRawAnalysisData: false);
                    _previousSunState = astroContext?.SunState;
                    _previousEffectiveSafe = effectiveSafe;
                    _previousVisualSafe = visualSafe;
                    _previousExternalSafetyMonitorSafe = externalSafetyMonitorSafe;
                    _hasPreviousDecision = true;

                    if (reasons.Contains("initial", StringComparer.Ordinal)
                        || reasons.Contains("periodic", StringComparer.Ordinal))
                    {
                        _successfulTeacherChecksSincePeriodic = 0;
                    }
                }
                else
                {
                    var category = teacher?.Provenance.FailureCategory
                                   ?? AnalysisFailureCategory.Unknown;
                    var recordInvalidResponse = options.RecordQuarantine
                                                && category is AnalysisFailureCategory.MalformedResponse
                                                    or AnalysisFailureCategory.SchemaRejected;
                    if (recordInvalidResponse)
                    {
                        quarantined = true;
                        quarantineReason = teacher?.FailureMessage ?? category.ToString();
                        reasons.Add("teacherInvalidResponse");
                        priority = true;
                    }
                    else if (!manualReview)
                    {
                        // Rate limits, network failures and local-only analyses are useful in
                        // operational logs but are not labels. Avoid filling the dataset with
                        // one untrainable frame per polling cycle.
                        return SelectionDecision.NotSelected;
                    }
                    else
                    {
                        quarantined = true;
                        quarantineReason = teacher?.FailureMessage ?? category.ToString();
                        reasons.Add("teacherUnavailable");
                        priority = true;
                    }

                    if (!options.RecordQuarantine && quarantined)
                    {
                        return SelectionDecision.NotSelected;
                    }
                }
            }

            if (reasons.Count == 0)
            {
                return SelectionDecision.NotSelected;
            }

            return new SelectionDecision(true, reasons, priority, quarantined, quarantineReason);
        }

        private async Task WriterLoopAsync(CancellationToken cancellationToken)
        {
            try
            {
                while (!cancellationToken.IsCancellationRequested)
                {
                    DatasetSampleEnvelope? envelope = null;
                    if (_eventQueue.Reader.TryRead(out var priority))
                    {
                        envelope = priority;
                    }
                    else if (_periodicQueue.Reader.TryRead(out var periodic))
                    {
                        envelope = periodic;
                    }
                    else
                    {
                        var eventReady = _eventQueue.Reader.WaitToReadAsync(cancellationToken).AsTask();
                        var periodicReady = _periodicQueue.Reader.WaitToReadAsync(cancellationToken).AsTask();
                        await Task.WhenAny(eventReady, periodicReady);

                        if (_eventQueue.Reader.Completion.IsCompleted
                            && _periodicQueue.Reader.Completion.IsCompleted)
                        {
                            break;
                        }
                        continue;
                    }

                    using (envelope)
                    {
                        try
                        {
                            await WriteSampleAsync(envelope, cancellationToken);
                        }
                        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
                        {
                            break;
                        }
                        catch (Exception ex)
                        {
                            RecordError("Dataset sample write failed", ex);
                        }
                    }
                }

                // Both writers were completed during a normal shutdown. Drain remaining
                // priority samples first, then periodic samples, within the caller timeout.
                while (_eventQueue.Reader.TryRead(out var priority))
                {
                    using (priority)
                    {
                        await WriteSampleAsync(priority, cancellationToken);
                    }
                }
                while (_periodicQueue.Reader.TryRead(out var periodic))
                {
                    using (periodic)
                    {
                        await WriteSampleAsync(periodic, cancellationToken);
                    }
                }
            }
            catch (OperationCanceledException)
            {
                // Expected during bounded shutdown.
            }
            catch (Exception ex)
            {
                RecordError("Dataset writer loop stopped unexpectedly", ex);
            }
        }

        private async Task WriteSampleAsync(
            DatasetSampleEnvelope envelope,
            CancellationToken cancellationToken)
        {
            var options = envelope.Options;
            await EnsureDatasetRootAsync(options.RootDirectory, cancellationToken);

            SetState("Writing");
            var freeBytes = GetFreeBytes(options.RootDirectory);
            lock (_statusGate)
            {
                _freeBytes = freeBytes;
            }

            if (_currentBytes >= options.MaximumBytes)
            {
                Interlocked.Increment(ref _droppedSamples);
                SetState("Quota reached");
                LogCapacityWarningOnce(
                    $"AI Weather dataset reached its configured limit " +
                    $"({options.MaximumBytes / 1024d / 1024d / 1024d:F1} GB); sample collection paused");
                RaiseStatusChanged();
                return;
            }

            if (freeBytes >= 0 && freeBytes < options.MinimumFreeBytes)
            {
                Interlocked.Increment(ref _droppedSamples);
                SetState("Low disk space");
                LogCapacityWarningOnce(
                    $"AI Weather dataset stopped before disk exhaustion: " +
                    $"{freeBytes / 1024d / 1024d / 1024d:F1} GB free, " +
                    $"minimum is {options.MinimumFreeBytes / 1024d / 1024d / 1024d:F1} GB");
                RaiseStatusChanged();
                return;
            }

            var sourceWidth = envelope.Image.Width;
            var sourceHeight = envelope.Image.Height;
            using var resized = ResizeByPercentage(envelope.Image, options.ImageScalePercent);
            var perceptualHashValue = ComputeAverageHash(resized);
            var perceptualHash = perceptualHashValue.ToString("x16", CultureInfo.InvariantCulture);
            var jpeg = EncodeJpeg(resized, options.JpegQuality);
            var sha256 = Convert.ToHexString(SHA256.HashData(jpeg)).ToLowerInvariant();

            var nearDuplicate = false;
            var possibleFrozen = false;
            if (_lastStoredPerceptualHash.HasValue)
            {
                var distance = HammingDistance(_lastStoredPerceptualHash.Value, perceptualHashValue);
                nearDuplicate = distance <= options.NearDuplicateHammingDistance;
                possibleFrozen = string.Equals(_lastStoredSha256, sha256, StringComparison.Ordinal);
            }

            if (nearDuplicate && !MustRetainNearDuplicate(envelope.SelectionReasons))
            {
                Interlocked.Increment(ref _droppedSamples);
                SetState("Ready (near-duplicate skipped)");
                RaiseStatusChanged();
                return;
            }

            var date = envelope.CapturedUtc;
            var shardA = sha256.Substring(0, 2);
            var shardB = sha256.Substring(2, 2);
            var imageRelative = Path.Combine(
                "images",
                date.ToString("yyyy", CultureInfo.InvariantCulture),
                date.ToString("MM", CultureInfo.InvariantCulture),
                date.ToString("dd", CultureInfo.InvariantCulture),
                shardA,
                shardB,
                sha256 + ".jpg");
            var imagePath = Path.Combine(options.RootDirectory, imageRelative);

            long bytesAdded = 0;
            if (!File.Exists(imagePath))
            {
                await AtomicWriteBytesAsync(imagePath, jpeg, cancellationToken);
                bytesAdded += jpeg.LongLength;
            }

            var sampleId = $"{date:yyyyMMddTHHmmssfffZ}-{sha256.Substring(0, 16)}-" +
                           Guid.NewGuid().ToString("N").Substring(0, 8);

            var record = BuildRecord(
                envelope,
                sampleId,
                NormalizeRelativePath(imageRelative),
                sha256,
                perceptualHash,
                resized.Width,
                resized.Height,
                sourceWidth,
                sourceHeight,
                nearDuplicate,
                possibleFrozen);

            var labelRoot = envelope.Quarantined
                ? Path.Combine("quarantine", "labels")
                : "labels";
            var labelRelative = Path.Combine(
                labelRoot,
                date.ToString("yyyy", CultureInfo.InvariantCulture),
                date.ToString("MM", CultureInfo.InvariantCulture),
                date.ToString("dd", CultureInfo.InvariantCulture),
                shardA,
                shardB,
                sampleId + ".json");
            var labelPath = Path.Combine(options.RootDirectory, labelRelative);
            var labelBytes = JsonSerializer.SerializeToUtf8Bytes(record, PrettyJson);
            await AtomicWriteBytesAsync(labelPath, labelBytes, cancellationToken);
            bytesAdded += labelBytes.LongLength;

            var indexPath = Path.Combine(
                options.RootDirectory,
                "index",
                $"samples-{date:yyyy-MM}.jsonl");
            var indexLine = JsonSerializer.Serialize(record, CompactJson);
            await AppendIndexLineAsync(indexPath, indexLine, cancellationToken);
            bytesAdded += Encoding.UTF8.GetByteCount(indexLine) + Environment.NewLine.Length;

            _lastStoredPerceptualHash = perceptualHashValue;
            _lastStoredSha256 = sha256;
            Interlocked.Increment(ref _totalSamples);
            if (envelope.Quarantined)
            {
                Interlocked.Increment(ref _quarantineSamples);
            }
            else
            {
                Interlocked.Increment(ref _trainableSamples);
            }
            if (envelope.SelectionReasons.Contains("manualReview", StringComparer.Ordinal))
            {
                Interlocked.Increment(ref _reviewSamples);
            }
            if (date.ToLocalTime().Date == DateTime.Now.Date)
            {
                Interlocked.Increment(ref _todaySamples);
            }

            lock (_statusGate)
            {
                _currentBytes += bytesAdded;
                _lastWriteUtc = DateTime.UtcNow;
                _lastDifference = envelope.Analysis.TeacherStudentCloudDifference;
                _lastTeacher = envelope.Analysis.Teacher == null
                    ? "none"
                    : $"{envelope.Analysis.Teacher.Provenance.Provider}/" +
                      envelope.Analysis.Teacher.Provenance.Model;
                _lastError = null;
                _state = envelope.Quarantined ? "Ready (quarantine written)" : "Ready";
            }

            _log.Info(
                $"Dataset sample written: {sampleId} " +
                $"({string.Join(",", envelope.SelectionReasons)}, " +
                $"{(envelope.Quarantined ? "quarantine" : "trainable")}, " +
                $"teacher={_lastTeacher}, image={sha256.Substring(0, 12)})");
            RaiseStatusChanged();
        }

        private static bool MustRetainNearDuplicate(IReadOnlyList<string> reasons)
        {
            // Persistent disagreement or low confidence can last for hours, especially
            // while a local student may still be a weak baseline. Do not let those
            // unchanged frames dominate the dataset. A near-duplicate is retained only
            // when a human requested it or when the label/operational state actually
            // changed, matching the design's event-deduplication rule.
            return reasons.Any(reason => reason is
                "manualReview" or
                "initial" or
                "conditionChanged" or
                "thresholdCrossing" or
                "sunStateChanged" or
                "effectiveSafetyChanged" or
                "visualSafetyChanged" or
                "externalSafetyChanged");
        }

        private DatasetSampleRecord BuildRecord(
            DatasetSampleEnvelope envelope,
            string sampleId,
            string imageRelativePath,
            string sha256,
            string perceptualHash,
            int width,
            int height,
            int sourceWidth,
            int sourceHeight,
            bool nearDuplicate,
            bool possibleFrozen)
        {
            var teacher = envelope.Analysis.Teacher;
            var teacherResult = teacher?.Result;
            var rawResponse = envelope.Options.SaveTeacherRaw
                ? LogRedactor.RedactSensitiveText(teacherResult?.RawAnalysisData)
                : null;

            return new DatasetSampleRecord
            {
                SampleId = sampleId,
                CapturedUtc = envelope.CapturedUtc,
                Image = new DatasetImageRecord
                {
                    RelativePath = imageRelativePath,
                    Sha256 = sha256,
                    PerceptualHash = perceptualHash,
                    Width = width,
                    Height = height,
                    SourceWidth = sourceWidth,
                    SourceHeight = sourceHeight,
                    ScalePercent = Math.Min(
                        width / (double)sourceWidth,
                        height / (double)sourceHeight) * 100.0,
                    JpegQuality = envelope.Options.JpegQuality,
                    RoiVersion = envelope.RoiVersion,
                    PossibleFrozenFrame = possibleFrozen
                },
                Astro = envelope.AstroContext == null
                    ? null
                    : new DatasetAstroRecord
                    {
                        SunAltitude = envelope.AstroContext.SunAltitude,
                        SunState = envelope.AstroContext.SunState,
                        MoonAltitude = envelope.AstroContext.MoonAltitude,
                        MoonIllumination = envelope.AstroContext.MoonIllumination,
                        MoonPhase = envelope.AstroContext.MoonPhase
                    },
                Teacher = new DatasetAnalysisRecord
                {
                    Valid = teacher?.Success == true
                            && WeatherAnalysisValidator.IsValidTeacherResult(teacherResult, out _),
                    Provenance = teacher?.Provenance.Clone() ?? new AnalysisProvenance(),
                    Result = teacherResult == null ? null : DatasetWeatherResult.From(teacherResult),
                    RawResponse = string.IsNullOrWhiteSpace(rawResponse) ? null : rawResponse,
                    FailureMessage = LogRedactor.RedactSensitiveText(teacher?.FailureMessage)
                },
                Student = new DatasetAnalysisRecord
                {
                    Valid = envelope.Analysis.Student.Condition != WeatherCondition.Unknown
                            && envelope.Analysis.Student.Confidence > 0,
                    Provenance = envelope.Analysis.Student.Provenance.Clone(),
                    Result = DatasetWeatherResult.From(envelope.Analysis.Student)
                },
                Decision = new DatasetDecisionRecord
                {
                    EffectiveSource = envelope.Analysis.EffectiveResult.Provenance.Origin,
                    EffectiveSafe = envelope.EffectiveSafe,
                    VisualSafe = envelope.VisualSafe,
                    HighThreshold = envelope.HighThreshold,
                    LowThreshold = envelope.LowThreshold,
                    ExternalSafetyMonitorSafe = envelope.ExternalSafetyMonitorSafe,
                    UsedFallback = envelope.Analysis.UsedFallback
                },
                Selection = new DatasetSelectionRecord
                {
                    Reason = envelope.SelectionReasons,
                    NearDuplicate = nearDuplicate,
                    Quarantined = envelope.Quarantined,
                    QuarantineReason = envelope.QuarantineReason
                },
                Review = new DatasetReviewRecord
                {
                    Status = envelope.Quarantined
                             || envelope.SelectionReasons.Contains("manualReview", StringComparer.Ordinal)
                        ? "needsReview"
                        : "unreviewed"
                }
            };
        }

        private async Task EnsureDatasetRootAsync(string root, CancellationToken cancellationToken)
        {
            await _initializationGate.WaitAsync(cancellationToken);
            try
            {
                if (_initializedRoots.Contains(root))
                {
                    return;
                }

                Directory.CreateDirectory(root);
                Directory.CreateDirectory(Path.Combine(root, "images"));
                Directory.CreateDirectory(Path.Combine(root, "labels"));
                Directory.CreateDirectory(Path.Combine(root, "index"));
                Directory.CreateDirectory(Path.Combine(root, "quarantine"));
                Directory.CreateDirectory(Path.Combine(root, "review"));
                Directory.CreateDirectory(Path.Combine(root, "exports"));

                RecoverTemporaryFiles(root);
                RepairJsonlIndexes(root);
                await EnsureManifestAsync(root, cancellationToken);

                var total = CountFiles(Path.Combine(root, "labels"), "*.json");
                var quarantine = CountFiles(Path.Combine(root, "quarantine", "labels"), "*.json");
                lock (_statusGate)
                {
                    _rootDirectory = root;
                    _trainableSamples = total;
                    _quarantineSamples = quarantine;
                    _totalSamples = total + quarantine;
                    _reviewSamples = CountReviewSamples(root);
                    _todaySamples = CountSamplesCapturedOnLocalDate(root, DateTime.Now.Date);
                    _currentBytes = ComputeDirectorySize(root);
                    _freeBytes = GetFreeBytes(root);
                    _state = "Ready";
                    _lastError = null;
                }

                _initializedRoots.Add(root);
                _log.Info($"AI Weather dataset initialized at {root} ({_totalSamples} samples)");
                RaiseStatusChanged();
            }
            finally
            {
                _initializationGate.Release();
            }
        }

        private async Task InitializeConfiguredRootAsync()
        {
            try
            {
                var options = _optionsProvider();
                if (options.Enabled)
                {
                    await EnsureDatasetRootAsync(options.RootDirectory, _writerCts.Token);
                }
            }
            catch (OperationCanceledException) when (_writerCts.IsCancellationRequested)
            {
                // Normal plugin shutdown while the startup scan is still running.
            }
            catch (Exception ex)
            {
                RecordError("Dataset startup indexing failed", ex);
            }
        }

        private static async Task EnsureManifestAsync(string root, CancellationToken cancellationToken)
        {
            var manifestPath = Path.Combine(root, "dataset.json");
            if (File.Exists(manifestPath))
            {
                using var document = JsonDocument.Parse(await File.ReadAllTextAsync(manifestPath, cancellationToken));
                if (!document.RootElement.TryGetProperty("schemaVersion", out var schema)
                    || schema.GetInt32() != 1)
                {
                    throw new InvalidDataException("Unsupported AI Weather dataset schema version");
                }
                return;
            }

            var manifest = new DatasetManifest
            {
                PluginVersion = Assembly.GetExecutingAssembly().GetName().Version?.ToString()
                                ?? "unknown"
            };
            var bytes = JsonSerializer.SerializeToUtf8Bytes(manifest, PrettyJson);
            await AtomicWriteBytesAsync(manifestPath, bytes, cancellationToken);
        }

        private void RecoverTemporaryFiles(string root)
        {
            var incompleteRoot = Path.Combine(root, "quarantine", "incomplete");
            Directory.CreateDirectory(incompleteRoot);
            var recovered = 0;

            foreach (var temp in Directory.EnumerateFiles(root, "*.tmp", SearchOption.AllDirectories).ToArray())
            {
                if (temp.StartsWith(incompleteRoot, StringComparison.OrdinalIgnoreCase))
                {
                    continue;
                }

                try
                {
                    var name = DateTime.UtcNow.ToString("yyyyMMddTHHmmssfffZ-", CultureInfo.InvariantCulture)
                               + Guid.NewGuid().ToString("N").Substring(0, 8)
                               + "-" + Path.GetFileName(temp);
                    File.Move(temp, Path.Combine(incompleteRoot, name));
                    recovered++;
                }
                catch (Exception ex)
                {
                    _log.Warning($"Could not quarantine incomplete dataset file {Path.GetFileName(temp)}: {ex.Message}");
                }
            }

            if (recovered > 0)
            {
                _log.Warning($"Recovered {recovered} incomplete dataset temporary file(s) into quarantine/incomplete");
            }
        }

        private void RepairJsonlIndexes(string root)
        {
            var indexRoot = Path.Combine(root, "index");
            if (!Directory.Exists(indexRoot))
            {
                return;
            }

            foreach (var indexPath in Directory.EnumerateFiles(indexRoot, "*.jsonl", SearchOption.TopDirectoryOnly))
            {
                var validLines = new List<string>();
                var invalidLines = new List<string>();
                foreach (var line in File.ReadLines(indexPath))
                {
                    if (string.IsNullOrWhiteSpace(line))
                    {
                        continue;
                    }
                    try
                    {
                        using var _ = JsonDocument.Parse(line);
                        validLines.Add(line);
                    }
                    catch (JsonException)
                    {
                        invalidLines.Add(line);
                    }
                }

                if (invalidLines.Count == 0)
                {
                    continue;
                }

                var quarantinePath = Path.Combine(
                    root,
                    "quarantine",
                    $"broken-{Path.GetFileName(indexPath)}-{DateTime.UtcNow:yyyyMMddTHHmmssZ}.txt");
                File.WriteAllLines(quarantinePath, invalidLines, new UTF8Encoding(false));
                var repairPath = indexPath + ".repair.tmp";
                File.WriteAllLines(repairPath, validLines, new UTF8Encoding(false));
                File.Move(repairPath, indexPath, overwrite: true);
                _log.Warning($"Repaired {Path.GetFileName(indexPath)} and quarantined {invalidLines.Count} invalid line(s)");
            }
        }

        private static async Task AtomicWriteBytesAsync(
            string destination,
            byte[] bytes,
            CancellationToken cancellationToken)
        {
            var directory = Path.GetDirectoryName(destination)
                            ?? throw new InvalidOperationException("Dataset destination has no parent directory");
            Directory.CreateDirectory(directory);
            var temporary = destination + "." + Guid.NewGuid().ToString("N") + ".tmp";

            try
            {
                await using (var stream = new FileStream(
                    temporary,
                    FileMode.CreateNew,
                    FileAccess.Write,
                    FileShare.None,
                    81920,
                    FileOptions.Asynchronous | FileOptions.WriteThrough))
                {
                    await stream.WriteAsync(bytes.AsMemory(), cancellationToken);
                    await stream.FlushAsync(cancellationToken);
                    stream.Flush(flushToDisk: true);
                }

                if (File.Exists(destination))
                {
                    File.Delete(temporary);
                }
                else
                {
                    File.Move(temporary, destination);
                }
            }
            catch
            {
                // Leave a .tmp file for startup recovery if the process dies or the move
                // fails; deleting is best-effort only for ordinary in-process exceptions.
                try
                {
                    if (File.Exists(temporary))
                    {
                        File.Delete(temporary);
                    }
                }
                catch
                {
                    // startup recovery owns it now
                }
                throw;
            }
        }

        private static async Task AppendIndexLineAsync(
            string indexPath,
            string json,
            CancellationToken cancellationToken)
        {
            Directory.CreateDirectory(Path.GetDirectoryName(indexPath)!);
            await using var stream = new FileStream(
                indexPath,
                FileMode.Append,
                FileAccess.Write,
                FileShare.Read,
                81920,
                FileOptions.Asynchronous | FileOptions.WriteThrough);
            await using var writer = new StreamWriter(stream, new UTF8Encoding(false));
            await writer.WriteLineAsync(json.AsMemory(), cancellationToken);
            await writer.FlushAsync(cancellationToken);
            stream.Flush(flushToDisk: true);
        }

        private static Bitmap ResizeByPercentage(Bitmap source, double scalePercent)
        {
            var normalizedPercent = double.IsFinite(scalePercent)
                ? Math.Clamp(scalePercent, 5.0, 100.0)
                : 50.0;
            var scale = normalizedPercent / 100.0;
            var width = Math.Max(1, (int)Math.Round(source.Width * scale));
            var height = Math.Max(1, (int)Math.Round(source.Height * scale));
            var target = new Bitmap(width, height, PixelFormat.Format24bppRgb);
            target.SetResolution(source.HorizontalResolution > 0 ? source.HorizontalResolution : 96,
                                 source.VerticalResolution > 0 ? source.VerticalResolution : 96);
            using var graphics = Graphics.FromImage(target);
            graphics.CompositingMode = CompositingMode.SourceCopy;
            graphics.CompositingQuality = CompositingQuality.HighQuality;
            graphics.InterpolationMode = InterpolationMode.HighQualityBicubic;
            graphics.SmoothingMode = SmoothingMode.HighQuality;
            graphics.PixelOffsetMode = PixelOffsetMode.HighQuality;
            graphics.DrawImage(source, new Rectangle(0, 0, width, height));
            return target;
        }

        private static byte[] EncodeJpeg(Bitmap image, int quality)
        {
            using var memory = new MemoryStream();
            var encoder = ImageCodecInfo.GetImageEncoders()
                .First(codec => codec.FormatID == ImageFormat.Jpeg.Guid);
            using var parameters = new EncoderParameters(1);
            parameters.Param[0] = new EncoderParameter(
                System.Drawing.Imaging.Encoder.Quality,
                (long)quality);
            image.Save(memory, encoder, parameters);
            return memory.ToArray();
        }

        private static ulong ComputeAverageHash(Bitmap source)
        {
            using var tiny = new Bitmap(8, 8, PixelFormat.Format24bppRgb);
            using (var graphics = Graphics.FromImage(tiny))
            {
                graphics.InterpolationMode = InterpolationMode.HighQualityBilinear;
                graphics.DrawImage(source, 0, 0, 8, 8);
            }

            var values = new double[64];
            double sum = 0;
            for (var y = 0; y < 8; y++)
            {
                for (var x = 0; x < 8; x++)
                {
                    var color = tiny.GetPixel(x, y);
                    var value = 0.299 * color.R + 0.587 * color.G + 0.114 * color.B;
                    values[y * 8 + x] = value;
                    sum += value;
                }
            }

            var average = sum / values.Length;
            ulong hash = 0;
            for (var i = 0; i < values.Length; i++)
            {
                if (values[i] >= average)
                {
                    hash |= 1UL << i;
                }
            }
            return hash;
        }

        internal static int HammingDistance(ulong left, ulong right)
        {
            return System.Numerics.BitOperations.PopCount(left ^ right);
        }

        private static WeatherAnalysisBundle CloneBundle(
            WeatherAnalysisBundle source,
            bool includeTeacherRaw)
        {
            OnlineAnalysisAttempt? teacher = null;
            if (source.Teacher != null)
            {
                var result = source.Teacher.Result?.Clone(includeTeacherRaw);
                teacher = source.Teacher.Success && result != null
                    ? OnlineAnalysisAttempt.Succeeded(result)
                    : OnlineAnalysisAttempt.Failed(
                        source.Teacher.Provenance.Clone(),
                        source.Teacher.FailureMessage);
            }

            return new WeatherAnalysisBundle
            {
                EffectiveResult = source.EffectiveResult.Clone(includeRawAnalysisData: false),
                Teacher = teacher,
                Student = source.Student.Clone(includeRawAnalysisData: false),
                UsedFallback = source.UsedFallback
            };
        }

        private void RefreshConfigurationStatus(DatasetRecorderOptions options)
        {
            lock (_statusGate)
            {
                _rootDirectory = options.RootDirectory;
                if (!options.Enabled)
                {
                    _state = "Disabled";
                }
                else if (options.Paused)
                {
                    _state = "Paused";
                }
                else if (_state is "Disabled" or "Paused" or "Stopped")
                {
                    _state = "Ready";
                }
            }
        }

        private void SetState(string state)
        {
            lock (_statusGate)
            {
                _state = state;
            }
        }

        private void RecordError(string context, Exception ex)
        {
            Interlocked.Increment(ref _errorCount);
            lock (_statusGate)
            {
                _state = "Error (safety unaffected)";
                _lastError = $"{context}: {ex.GetType().Name}: {ex.Message}";
            }

            var now = DateTime.UtcNow;
            if (now - _lastErrorLogUtc >= ErrorLogInterval)
            {
                _lastErrorLogUtc = now;
                _log.Error($"{context}; weather safety analysis continues without dataset recording: {ex.Message}", ex);
            }
            RaiseStatusChanged();
        }

        private void LogCapacityWarningOnce(string message)
        {
            var now = DateTime.UtcNow;
            if (now - _lastErrorLogUtc >= ErrorLogInterval)
            {
                _lastErrorLogUtc = now;
                _log.Warning(message);
            }
        }

        private void RaiseStatusChanged()
        {
            try
            {
                StatusChanged?.Invoke(this, EventArgs.Empty);
            }
            catch
            {
                // UI observers must never break the writer.
            }
        }

        private static long ComputeDirectorySize(string root)
        {
            try
            {
                return Directory.EnumerateFiles(root, "*", SearchOption.AllDirectories)
                    .Select(path =>
                    {
                        try { return new FileInfo(path).Length; }
                        catch { return 0L; }
                    })
                    .Sum();
            }
            catch
            {
                return 0;
            }
        }

        private static long CountFiles(string root, string pattern)
        {
            try
            {
                return Directory.Exists(root)
                    ? Directory.EnumerateFiles(root, pattern, SearchOption.AllDirectories).LongCount()
                    : 0;
            }
            catch
            {
                return 0;
            }
        }

        private static long CountReviewSamples(string root)
        {
            return CountFiles(Path.Combine(root, "review"), "*.json");
        }

        private static long CountSamplesCapturedOnLocalDate(string root, DateTime localDate)
        {
            try
            {
                var localStart = DateTime.SpecifyKind(localDate.Date, DateTimeKind.Local);
                var utcStart = localStart.ToUniversalTime();
                var utcEnd = localStart.AddDays(1).ToUniversalTime();
                var labelRoots = new[]
                {
                    Path.Combine(root, "labels"),
                    Path.Combine(root, "quarantine", "labels")
                };

                long count = 0;
                for (var utcDate = utcStart.Date; utcDate <= utcEnd.Date; utcDate = utcDate.AddDays(1))
                {
                    foreach (var labelRoot in labelRoots)
                    {
                        var candidateDirectory = Path.Combine(
                            labelRoot,
                            utcDate.ToString("yyyy", CultureInfo.InvariantCulture),
                            utcDate.ToString("MM", CultureInfo.InvariantCulture),
                            utcDate.ToString("dd", CultureInfo.InvariantCulture));
                        if (!Directory.Exists(candidateDirectory))
                        {
                            continue;
                        }

                        foreach (var labelPath in Directory.EnumerateFiles(
                                     candidateDirectory,
                                     "*.json",
                                     SearchOption.AllDirectories))
                        {
                            try
                            {
                                using var document = JsonDocument.Parse(File.ReadAllBytes(labelPath));
                                if (document.RootElement.TryGetProperty("capturedUtc", out var capturedElement)
                                    && capturedElement.TryGetDateTime(out var capturedUtc)
                                    && capturedUtc.ToLocalTime().Date == localDate.Date)
                                {
                                    count++;
                                }
                            }
                            catch
                            {
                                // Invalid labels are handled by startup repair/review tooling;
                                // one damaged file must not break the status counter.
                            }
                        }
                    }
                }
                return count;
            }
            catch
            {
                return 0;
            }
        }

        private static long GetFreeBytes(string root)
        {
            try
            {
                var fullPath = Path.GetFullPath(root);
                var pathRoot = Path.GetPathRoot(fullPath);
                return string.IsNullOrWhiteSpace(pathRoot)
                    ? -1
                    : new DriveInfo(pathRoot).AvailableFreeSpace;
            }
            catch
            {
                return -1;
            }
        }

        private static string NormalizeRelativePath(string path)
        {
            return path.Replace(Path.DirectorySeparatorChar, '/');
        }

        private static JsonSerializerOptions CreateJsonOptions(bool indented)
        {
            var options = new JsonSerializerOptions
            {
                PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
                WriteIndented = indented,
                DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull
            };
            options.Converters.Add(new JsonStringEnumConverter());
            return options;
        }

        private static void DrainAndDispose(ChannelReader<DatasetSampleEnvelope> reader)
        {
            while (reader.TryRead(out var envelope))
            {
                envelope.Dispose();
            }
        }

        private readonly struct SelectionDecision
        {
            public static SelectionDecision NotSelected =>
                new SelectionDecision(false, Array.Empty<string>(), false, false, null);

            public SelectionDecision(
                bool selected,
                IReadOnlyList<string> reasons,
                bool priority,
                bool quarantined,
                string? quarantineReason)
            {
                Selected = selected;
                Reasons = reasons;
                Priority = priority;
                Quarantined = quarantined;
                QuarantineReason = quarantineReason;
            }

            public bool Selected { get; }
            public IReadOnlyList<string> Reasons { get; }
            public bool Priority { get; }
            public bool Quarantined { get; }
            public string? QuarantineReason { get; }
        }
    }
}
