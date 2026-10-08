using AIWeather.Models;
using Microsoft.ML.OnnxRuntime;
using Microsoft.ML.OnnxRuntime.Tensors;
using NINA.Core.Utility;
using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.Drawing.Imaging;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Security.Cryptography;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;

namespace AIWeather.Services
{
    /// <summary>
    /// Site-trained local weather analysis. The bundled ONNX model replaces the legacy
    /// brightness/color heuristic for both the Local provider and online-provider fallback.
    /// The N.I.N.A. safety monitor still owns cloud-threshold hysteresis and fail-safe expiry.
    /// </summary>
    [System.Runtime.Versioning.SupportedOSPlatform("windows")]
    public class LocalWeatherAnalysisService : IWeatherAnalysisService
    {
        internal const string ModelFileName = "aiweather_mobilenetv3_test_v1.onnx";
        internal const string ModelSha256 = "C9283C12CEA58889E3411A2E3FC6041A440B8B1311C99C126E43265AB3916630";

        private const string InputName = "image";
        private const string CloudOutputName = "cloud_coverage_pct";
        private const string OrdinalOutputName = "ordinal_logits";
        private const string ConditionOutputName = "condition_logits";
        private const string RainFogOutputName = "rain_fog_logits";
        private const int InputWidth = 384;
        private const int InputHeight = 216;
        private const float RainFogThreshold = 0.5f;

        private static readonly float[] ImageNetMean = { 0.485f, 0.456f, 0.406f };
        private static readonly float[] ImageNetStd = { 0.229f, 0.224f, 0.225f };
        private static readonly Lazy<InferenceSession> SharedSession = new(
            CreateSession,
            LazyThreadSafetyMode.ExecutionAndPublication);

        private bool _isInitialized;

        public Task<bool> InitializeAsync(CancellationToken cancellationToken = default)
        {
            cancellationToken.ThrowIfCancellationRequested();

            try
            {
                _ = SharedSession.Value;
                _isInitialized = true;
                Logger.Info(
                    $"Local ONNX weather analysis initialized: {AnalysisMetadata.LocalOnnxModelVersion} " +
                    $"({InputWidth}x{InputHeight}, CPU)");
                return Task.FromResult(true);
            }
            catch (Exception ex)
            {
                _isInitialized = false;
                Logger.Error($"Local ONNX weather model could not be initialized: {ex.Message}", ex);
                return Task.FromResult(false);
            }
        }

        public async Task<WeatherAnalysisResult> AnalyzeImageAsync(
            Bitmap image,
            AstroContext? astroContext = null,
            CancellationToken cancellationToken = default)
        {
            ArgumentNullException.ThrowIfNull(image);
            var stopwatch = Stopwatch.StartNew();

            if (!_isInitialized && !await InitializeAsync(cancellationToken).ConfigureAwait(false))
            {
                return CreateFailureResult(
                    "Local ONNX model is unavailable; analysis failed closed",
                    AnalysisFailureCategory.ModelUnavailable,
                    stopwatch.ElapsedMilliseconds);
            }

            try
            {
                var result = await Task.Run(
                    () => AnalyzeCore(image, cancellationToken),
                    cancellationToken).ConfigureAwait(false);

                cancellationToken.ThrowIfCancellationRequested();
                result.Provenance = AnalysisMetadata.LocalOnnx(stopwatch.ElapsedMilliseconds);
                Logger.Debug(
                    $"Local ONNX analysis complete: {result.Condition}, " +
                    $"Cloud Coverage: {result.CloudCoverage:F1}%, " +
                    $"Rain: {result.RainDetected}, Fog: {result.FogDetected}, " +
                    $"Safe: {result.IsSafeForImaging}");
                return result;
            }
            catch (OperationCanceledException)
            {
                // A stop is not a cloud measurement or a provider failure.
                throw;
            }
            catch (Exception ex)
            {
                Logger.Error($"Local ONNX weather analysis failed: {ex.Message}", ex);
                return CreateFailureResult(
                    $"Local ONNX analysis failed closed: {ex.Message}",
                    AnalysisFailureCategory.Unknown,
                    stopwatch.ElapsedMilliseconds);
            }
        }

        internal static string ResolveModelPath()
        {
            var diagnosticOverride = Environment.GetEnvironmentVariable("AIWEATHER_ONNX_MODEL");
            if (!string.IsNullOrWhiteSpace(diagnosticOverride))
            {
                return Path.GetFullPath(diagnosticOverride);
            }

            var assemblyDirectory = Path.GetDirectoryName(Assembly.GetExecutingAssembly().Location)
                ?? AppContext.BaseDirectory;
            return Path.Combine(assemblyDirectory, "models", ModelFileName);
        }

        private static InferenceSession CreateSession()
        {
            var modelPath = ResolveModelPath();
            if (!File.Exists(modelPath))
            {
                throw new FileNotFoundException(
                    $"Bundled Local ONNX model was not found at '{modelPath}'",
                    modelPath);
            }

            var actualHash = Convert.ToHexString(SHA256.HashData(File.ReadAllBytes(modelPath)));
            if (!string.Equals(actualHash, ModelSha256, StringComparison.OrdinalIgnoreCase))
            {
                throw new InvalidDataException(
                    $"Local ONNX model hash mismatch. Expected {ModelSha256}, got {actualHash}.");
            }

            using var options = new SessionOptions
            {
                GraphOptimizationLevel = GraphOptimizationLevel.ORT_ENABLE_ALL,
                ExecutionMode = ExecutionMode.ORT_SEQUENTIAL,
                IntraOpNumThreads = 2,
                InterOpNumThreads = 1,
                EnableMemoryPattern = true
            };

            var session = new InferenceSession(modelPath, options);
            var missingInputs = new[] { InputName }
                .Where(name => !session.InputMetadata.ContainsKey(name))
                .ToArray();
            var missingOutputs = new[]
                {
                    CloudOutputName,
                    OrdinalOutputName,
                    ConditionOutputName,
                    RainFogOutputName
                }
                .Where(name => !session.OutputMetadata.ContainsKey(name))
                .ToArray();

            if (missingInputs.Length > 0 || missingOutputs.Length > 0)
            {
                session.Dispose();
                throw new InvalidDataException(
                    $"Local ONNX signature mismatch. Missing inputs [{string.Join(", ", missingInputs)}], " +
                    $"outputs [{string.Join(", ", missingOutputs)}].");
            }

            return session;
        }

        private static WeatherAnalysisResult AnalyzeCore(Bitmap image, CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var (tensor, averageBrightness) = CreateInputTensor(image, cancellationToken);
            var inputs = new List<NamedOnnxValue>
            {
                NamedOnnxValue.CreateFromTensor(InputName, tensor)
            };

            using var outputs = SharedSession.Value.Run(inputs);
            var cloudOutput = GetOutput(outputs, CloudOutputName, 1);
            var ordinalLogits = GetOutput(outputs, OrdinalOutputName, 5);
            var conditionLogits = GetOutput(outputs, ConditionOutputName, 6);
            var rainFogLogits = GetOutput(outputs, RainFogOutputName, 2);

            var cloudCoverage = Math.Clamp((double)cloudOutput[0], 0.0, 100.0);
            var conditionProbabilities = Softmax(conditionLogits);
            var rainProbability = Sigmoid(rainFogLogits[0]);
            var fogProbability = Sigmoid(rainFogLogits[1]);
            var rainDetected = rainProbability >= RainFogThreshold;
            var fogDetected = !rainDetected && fogProbability >= RainFogThreshold;

            // Rain/Fog are decided by their dedicated binary heads. With only two rainy
            // samples in this feasibility dataset, a six-way argmax can otherwise produce
            // unsafe false alarms. Ordinary cloud labels are derived from the better-
            // calibrated regression output (73% validation accuracy on the held-out split).
            var condition = rainDetected
                ? WeatherCondition.Rainy
                : fogDetected
                    ? WeatherCondition.Foggy
                    : ConditionFromCloudCoverage(cloudCoverage);

            var conditionIndex = condition switch
            {
                WeatherCondition.Clear => 0,
                WeatherCondition.PartlyCloudy => 1,
                WeatherCondition.MostlyCloudy => 2,
                WeatherCondition.Overcast => 3,
                WeatherCondition.Foggy => 4,
                WeatherCondition.Rainy => 5,
                _ => 0
            };
            var confidence = condition switch
            {
                WeatherCondition.Rainy => rainProbability * 100.0,
                WeatherCondition.Foggy => fogProbability * 100.0,
                _ => conditionProbabilities[conditionIndex] * 100.0
            };
            confidence = Math.Clamp(confidence, 1.0, 100.0);

            var description = rainDetected
                ? $"Local ONNX: rain detected - {cloudCoverage:F1}% cloud coverage"
                : fogDetected
                    ? $"Local ONNX: fog detected - {cloudCoverage:F1}% cloud coverage"
                    : $"Local ONNX: {condition} - {cloudCoverage:F1}% cloud coverage";

            return new WeatherAnalysisResult
            {
                Timestamp = DateTime.UtcNow,
                Condition = condition,
                CloudCoverage = cloudCoverage,
                Confidence = confidence,
                IsSafeForImaging = !rainDetected && !fogDetected && cloudCoverage < 70.0,
                Description = description,
                Brightness = averageBrightness,
                RainDetected = rainDetected,
                FogDetected = fogDetected,
                RawAnalysisData = JsonSerializer.Serialize(new
                {
                    model = AnalysisMetadata.LocalOnnxModelVersion,
                    modelSha256 = ModelSha256.ToLowerInvariant(),
                    input = new[] { 1, 3, InputHeight, InputWidth },
                    cloudCoveragePct = cloudCoverage,
                    ordinalProbabilities = ordinalLogits.Select(Sigmoid).ToArray(),
                    conditionProbabilities,
                    rainProbability,
                    fogProbability
                })
            };
        }

        private static (DenseTensor<float> Tensor, double AverageBrightness) CreateInputTensor(
            Bitmap image,
            CancellationToken cancellationToken)
        {
            using var resized = new Bitmap(InputWidth, InputHeight, PixelFormat.Format24bppRgb);
            using (var graphics = Graphics.FromImage(resized))
            {
                graphics.CompositingMode = CompositingMode.SourceCopy;
                graphics.CompositingQuality = CompositingQuality.HighQuality;
                graphics.InterpolationMode = InterpolationMode.Bilinear;
                graphics.SmoothingMode = SmoothingMode.None;
                graphics.PixelOffsetMode = PixelOffsetMode.Half;
                graphics.DrawImage(
                    image,
                    new Rectangle(0, 0, InputWidth, InputHeight),
                    0,
                    0,
                    image.Width,
                    image.Height,
                    GraphicsUnit.Pixel);
            }

            var data = new float[3 * InputHeight * InputWidth];
            long brightnessSum = 0;
            var pixelCount = InputHeight * InputWidth;
            var bitmapData = resized.LockBits(
                new Rectangle(0, 0, InputWidth, InputHeight),
                ImageLockMode.ReadOnly,
                PixelFormat.Format24bppRgb);

            try
            {
                unsafe
                {
                    var scan0 = (byte*)bitmapData.Scan0;
                    for (var y = 0; y < InputHeight; y++)
                    {
                        if ((y & 31) == 0)
                        {
                            cancellationToken.ThrowIfCancellationRequested();
                        }

                        var row = scan0 + (y * bitmapData.Stride);
                        for (var x = 0; x < InputWidth; x++)
                        {
                            var pixelOffset = x * 3;
                            var b = row[pixelOffset];
                            var g = row[pixelOffset + 1];
                            var r = row[pixelOffset + 2];
                            var index = (y * InputWidth) + x;

                            data[index] = ((r / 255.0f) - ImageNetMean[0]) / ImageNetStd[0];
                            data[pixelCount + index] = ((g / 255.0f) - ImageNetMean[1]) / ImageNetStd[1];
                            data[(2 * pixelCount) + index] = ((b / 255.0f) - ImageNetMean[2]) / ImageNetStd[2];
                            brightnessSum += (299L * r) + (587L * g) + (114L * b);
                        }
                    }
                }
            }
            finally
            {
                resized.UnlockBits(bitmapData);
            }

            var averageBrightness = brightnessSum / (1000.0 * pixelCount);
            return (
                new DenseTensor<float>(data, new[] { 1, 3, InputHeight, InputWidth }),
                averageBrightness);
        }

        private static float[] GetOutput(
            IDisposableReadOnlyCollection<DisposableNamedOnnxValue> outputs,
            string name,
            int minimumLength)
        {
            var value = outputs.FirstOrDefault(item => item.Name == name)
                ?? throw new InvalidDataException($"Local ONNX output '{name}' is missing.");
            var values = value.AsTensor<float>().ToArray();
            if (values.Length < minimumLength)
            {
                throw new InvalidDataException(
                    $"Local ONNX output '{name}' has {values.Length} values; expected at least {minimumLength}.");
            }

            if (values.Any(value => float.IsNaN(value) || float.IsInfinity(value)))
            {
                throw new InvalidDataException($"Local ONNX output '{name}' contains a non-finite value.");
            }

            return values;
        }

        private static WeatherCondition ConditionFromCloudCoverage(double cloudCoverage)
        {
            if (cloudCoverage < 15.0)
            {
                return WeatherCondition.Clear;
            }

            if (cloudCoverage < 50.0)
            {
                return WeatherCondition.PartlyCloudy;
            }

            return cloudCoverage < 85.0
                ? WeatherCondition.MostlyCloudy
                : WeatherCondition.Overcast;
        }

        private static double[] Softmax(IReadOnlyList<float> logits)
        {
            var maximum = logits.Max();
            var exponentials = logits.Select(value => Math.Exp(value - maximum)).ToArray();
            var sum = exponentials.Sum();
            return exponentials.Select(value => value / sum).ToArray();
        }

        private static double Sigmoid(float value)
        {
            return value >= 0
                ? 1.0 / (1.0 + Math.Exp(-value))
                : Math.Exp(value) / (1.0 + Math.Exp(value));
        }

        private static WeatherAnalysisResult CreateFailureResult(
            string description,
            AnalysisFailureCategory failureCategory,
            long elapsedMilliseconds)
        {
            return new WeatherAnalysisResult
            {
                Timestamp = DateTime.UtcNow,
                Condition = WeatherCondition.Unknown,
                CloudCoverage = 100,
                Confidence = 0,
                IsSafeForImaging = false,
                Description = description,
                RainDetected = false,
                FogDetected = false,
                Provenance = AnalysisMetadata.LocalOnnx(
                    elapsedMilliseconds,
                    upstreamFailure: failureCategory)
            };
        }
    }
}
