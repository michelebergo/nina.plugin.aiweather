using System;
using System.Collections.Generic;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

namespace AIWeather.Services
{
    public static class AIWeatherClusterProtocol
    {
        public const int SchemaVersion = 2;
        public const string Product = "ai-weather-advanced";
        public const int MinimumTokenLength = 16;
        public const int MaximumRequestHeaderBytes = 16 * 1024;
        public const string AuthenticationVersion = "AIW-HMAC-SHA256-V1";
        public const string AuthenticationVersionHeader = "X-AIWeather-Auth";
        public const string AuthenticationNodeHeader = "X-AIWeather-Node";
        public const string AuthenticationTimestampHeader = "X-AIWeather-Time";
        public const string AuthenticationNonceHeader = "X-AIWeather-Nonce";
        public const string AuthenticationSignatureHeader = "X-AIWeather-Signature";
        public static readonly TimeSpan AuthenticationClockSkew = TimeSpan.FromMinutes(5);

        private static readonly JsonSerializerOptions ConfigurationJsonOptions = new JsonSerializerOptions
        {
            PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
            PropertyNameCaseInsensitive = true
        };

        public static string GenerateSharedToken() =>
            Convert.ToHexString(RandomNumberGenerator.GetBytes(32));

        public static bool IsTokenUsable(string? token) =>
            !string.IsNullOrWhiteSpace(token) && token.Trim().Length >= MinimumTokenLength;

        public static bool FixedTimeTokenEquals(string? expected, string? supplied)
        {
            if (expected == null || supplied == null)
            {
                return false;
            }

            var expectedBytes = Encoding.UTF8.GetBytes(expected);
            var suppliedBytes = Encoding.UTF8.GetBytes(supplied);
            return expectedBytes.Length == suppliedBytes.Length
                   && CryptographicOperations.FixedTimeEquals(expectedBytes, suppliedBytes);
        }

        public static AIWeatherRequestAuthentication CreateRequestAuthentication(
            string token,
            string method,
            string path,
            string nodeId,
            DateTimeOffset? timestamp = null,
            string? nonce = null)
        {
            if (!IsTokenUsable(token))
            {
                throw new ArgumentException(
                    $"Cluster token must contain at least {MinimumTokenLength} characters.",
                    nameof(token));
            }

            var normalizedMethod = NormalizeMethod(method);
            var normalizedPath = NormalizePath(path);
            var normalizedNode = NormalizeNodeId(nodeId);
            var unixTime = (timestamp ?? DateTimeOffset.UtcNow).ToUnixTimeSeconds();
            var requestNonce = string.IsNullOrWhiteSpace(nonce)
                ? Convert.ToHexString(RandomNumberGenerator.GetBytes(16))
                : nonce.Trim();
            if (requestNonce.Length is < 16 or > 128)
            {
                throw new ArgumentException("Authentication nonce length is invalid.", nameof(nonce));
            }

            var signature = ComputeRequestSignature(
                token.Trim(), normalizedMethod, normalizedPath, normalizedNode, unixTime, requestNonce);
            return new AIWeatherRequestAuthentication
            {
                Version = AuthenticationVersion,
                NodeId = normalizedNode,
                UnixTimeSeconds = unixTime,
                Nonce = requestNonce,
                Signature = Convert.ToBase64String(signature)
            };
        }

        public static bool TryValidateRequestAuthentication(
            string token,
            string method,
            string path,
            IReadOnlyDictionary<string, string> headers,
            DateTimeOffset now,
            out AIWeatherRequestAuthentication authentication,
            out string error)
        {
            authentication = new AIWeatherRequestAuthentication();
            error = "Authentication failed.";
            if (!IsTokenUsable(token)
                || !headers.TryGetValue(AuthenticationVersionHeader, out var version)
                || !string.Equals(version, AuthenticationVersion, StringComparison.Ordinal)
                || !headers.TryGetValue(AuthenticationNodeHeader, out var nodeId)
                || !headers.TryGetValue(AuthenticationTimestampHeader, out var timestampText)
                || !long.TryParse(timestampText, out var timestamp)
                || !headers.TryGetValue(AuthenticationNonceHeader, out var nonce)
                || !headers.TryGetValue(AuthenticationSignatureHeader, out var signatureText))
            {
                return false;
            }

            string normalizedNode;
            try
            {
                normalizedNode = NormalizeNodeId(nodeId);
            }
            catch
            {
                return false;
            }

            if (nonce.Length is < 16 or > 128)
            {
                return false;
            }

            DateTimeOffset requestTime;
            try
            {
                requestTime = DateTimeOffset.FromUnixTimeSeconds(timestamp);
            }
            catch
            {
                return false;
            }
            if ((now - requestTime).Duration() > AuthenticationClockSkew)
            {
                error = "Authentication timestamp is outside the allowed clock window.";
                return false;
            }

            byte[] supplied;
            try
            {
                supplied = Convert.FromBase64String(signatureText);
            }
            catch
            {
                return false;
            }
            var expected = ComputeRequestSignature(
                token.Trim(), NormalizeMethod(method), NormalizePath(path), normalizedNode, timestamp, nonce);
            if (supplied.Length != expected.Length
                || !CryptographicOperations.FixedTimeEquals(supplied, expected))
            {
                return false;
            }

            authentication = new AIWeatherRequestAuthentication
            {
                Version = version,
                NodeId = normalizedNode,
                UnixTimeSeconds = timestamp,
                Nonce = nonce,
                Signature = signatureText
            };
            error = string.Empty;
            return true;
        }

        public static string ComputeConfigurationRevision(
            AIWeatherFailoverConfiguration configuration,
            string token)
        {
            ArgumentNullException.ThrowIfNull(configuration);
            if (!IsTokenUsable(token))
            {
                throw new ArgumentException("Cluster token is not usable.", nameof(token));
            }
            var plaintext = JsonSerializer.SerializeToUtf8Bytes(configuration.Normalize(), ConfigurationJsonOptions);
            using var hmac = new HMACSHA256(Encoding.UTF8.GetBytes(token.Trim()));
            return Convert.ToHexString(hmac.ComputeHash(plaintext).AsSpan(0, 16));
        }

        public static AIWeatherFailoverConfigurationEnvelope EncryptFailoverConfiguration(
            AIWeatherFailoverConfiguration configuration,
            string token,
            string primaryNodeId,
            string primarySessionId,
            DateTime? generatedUtc = null)
        {
            ArgumentNullException.ThrowIfNull(configuration);
            var normalized = configuration.Normalize();
            if (!normalized.TryValidate(out var validationError))
            {
                throw new ArgumentException(validationError, nameof(configuration));
            }
            if (!IsTokenUsable(token))
            {
                throw new ArgumentException("Cluster token is not usable.", nameof(token));
            }
            if (string.IsNullOrWhiteSpace(primarySessionId))
            {
                throw new ArgumentException("Primary session ID is required.", nameof(primarySessionId));
            }

            var utc = (generatedUtc ?? DateTime.UtcNow).ToUniversalTime();
            var revision = ComputeConfigurationRevision(normalized, token);
            var envelope = new AIWeatherFailoverConfigurationEnvelope
            {
                SchemaVersion = SchemaVersion,
                Product = Product,
                PrimaryNodeId = NormalizeNodeId(primaryNodeId),
                PrimarySessionId = primarySessionId.Trim(),
                GeneratedUtc = DateTime.SpecifyKind(utc, DateTimeKind.Utc),
                Revision = revision,
                Salt = Convert.ToBase64String(RandomNumberGenerator.GetBytes(16)),
                Nonce = Convert.ToBase64String(RandomNumberGenerator.GetBytes(12))
            };

            var plaintext = JsonSerializer.SerializeToUtf8Bytes(normalized, ConfigurationJsonOptions);
            var salt = Convert.FromBase64String(envelope.Salt);
            var nonce = Convert.FromBase64String(envelope.Nonce);
            var key = DeriveConfigurationKey(token.Trim(), salt);
            var ciphertext = new byte[plaintext.Length];
            var tag = new byte[16];
            var associatedData = BuildConfigurationAssociatedData(envelope);
            try
            {
                using var aes = new AesGcm(key, tag.Length);
                aes.Encrypt(nonce, plaintext, ciphertext, tag, associatedData);
            }
            finally
            {
                CryptographicOperations.ZeroMemory(key);
                CryptographicOperations.ZeroMemory(plaintext);
            }

            envelope.Ciphertext = Convert.ToBase64String(ciphertext);
            envelope.Tag = Convert.ToBase64String(tag);
            return envelope;
        }

        public static AIWeatherFailoverConfiguration DecryptFailoverConfiguration(
            AIWeatherFailoverConfigurationEnvelope envelope,
            string token)
        {
            ArgumentNullException.ThrowIfNull(envelope);
            if (!IsTokenUsable(token))
            {
                throw new CryptographicException("Cluster token is not usable.");
            }
            if (envelope.SchemaVersion != SchemaVersion
                || !string.Equals(envelope.Product, Product, StringComparison.Ordinal)
                || string.IsNullOrWhiteSpace(envelope.PrimaryNodeId)
                || string.IsNullOrWhiteSpace(envelope.PrimarySessionId)
                || string.IsNullOrWhiteSpace(envelope.Revision)
                || envelope.GeneratedUtc == default)
            {
                throw new CryptographicException("Failover configuration envelope identity is invalid.");
            }

            byte[] salt;
            byte[] nonce;
            byte[] ciphertext;
            byte[] tag;
            try
            {
                salt = Convert.FromBase64String(envelope.Salt);
                nonce = Convert.FromBase64String(envelope.Nonce);
                ciphertext = Convert.FromBase64String(envelope.Ciphertext);
                tag = Convert.FromBase64String(envelope.Tag);
            }
            catch (Exception ex)
            {
                throw new CryptographicException("Failover configuration envelope encoding is invalid.", ex);
            }
            if (salt.Length != 16 || nonce.Length != 12 || tag.Length != 16 || ciphertext.Length is < 2 or > 128 * 1024)
            {
                throw new CryptographicException("Failover configuration envelope size is invalid.");
            }

            var key = DeriveConfigurationKey(token.Trim(), salt);
            var plaintext = new byte[ciphertext.Length];
            try
            {
                using var aes = new AesGcm(key, tag.Length);
                aes.Decrypt(nonce, ciphertext, tag, plaintext, BuildConfigurationAssociatedData(envelope));
                var configuration = JsonSerializer.Deserialize<AIWeatherFailoverConfiguration>(plaintext, ConfigurationJsonOptions)
                                    ?? throw new CryptographicException("Failover configuration is empty.");
                configuration = configuration.Normalize();
                if (!configuration.TryValidate(out var validationError))
                {
                    throw new CryptographicException(validationError);
                }
                var expectedRevision = ComputeConfigurationRevision(configuration, token);
                if (!FixedTimeTokenEquals(expectedRevision, envelope.Revision))
                {
                    throw new CryptographicException("Failover configuration revision does not match its contents.");
                }
                return configuration;
            }
            catch (JsonException ex)
            {
                throw new CryptographicException("Failover configuration JSON is invalid.", ex);
            }
            finally
            {
                CryptographicOperations.ZeroMemory(key);
                CryptographicOperations.ZeroMemory(plaintext);
            }
        }

        private static byte[] ComputeRequestSignature(
            string token,
            string method,
            string path,
            string nodeId,
            long timestamp,
            string nonce)
        {
            var canonical = string.Join("\n", AuthenticationVersion, method, path, timestamp, nodeId, nonce);
            using var hmac = new HMACSHA256(Encoding.UTF8.GetBytes(token));
            return hmac.ComputeHash(Encoding.UTF8.GetBytes(canonical));
        }

        private static string NormalizeMethod(string method) =>
            string.IsNullOrWhiteSpace(method) ? "GET" : method.Trim().ToUpperInvariant();

        private static string NormalizePath(string path)
        {
            var normalized = string.IsNullOrWhiteSpace(path) ? "/" : path.Trim();
            var query = normalized.IndexOf('?');
            if (query >= 0)
            {
                normalized = normalized[..query];
            }
            return normalized.StartsWith('/') ? normalized : "/" + normalized;
        }

        private static string NormalizeNodeId(string nodeId)
        {
            var normalized = string.IsNullOrWhiteSpace(nodeId) ? Environment.MachineName : nodeId.Trim();
            if (normalized.Length is < 1 or > 128 || normalized.Contains('\r') || normalized.Contains('\n'))
            {
                throw new ArgumentException("Cluster node ID is invalid.", nameof(nodeId));
            }
            return normalized;
        }

        private static byte[] DeriveConfigurationKey(string token, byte[] salt) =>
            Rfc2898DeriveBytes.Pbkdf2(
                Encoding.UTF8.GetBytes(token),
                salt,
                100_000,
                HashAlgorithmName.SHA256,
                32);

        private static byte[] BuildConfigurationAssociatedData(AIWeatherFailoverConfigurationEnvelope envelope) =>
            Encoding.UTF8.GetBytes(string.Join(
                "\n",
                envelope.SchemaVersion,
                envelope.Product,
                envelope.PrimaryNodeId,
                envelope.PrimarySessionId,
                envelope.GeneratedUtc.ToUniversalTime().Ticks,
                envelope.Revision));
    }

    public sealed class AIWeatherRequestAuthentication
    {
        public string Version { get; set; } = string.Empty;
        public string NodeId { get; set; } = string.Empty;
        public long UnixTimeSeconds { get; set; }
        public string Nonce { get; set; } = string.Empty;
        public string Signature { get; set; } = string.Empty;
    }

    public sealed class AIWeatherClusterSnapshot
    {
        public int SchemaVersion { get; set; } = AIWeatherClusterProtocol.SchemaVersion;
        public string Product { get; set; } = AIWeatherClusterProtocol.Product;
        public string NodeId { get; set; } = string.Empty;
        public string SessionId { get; set; } = string.Empty;
        public long Sequence { get; set; }
        public DateTime GeneratedUtc { get; set; }
        public bool Connected { get; set; }
        public bool Monitoring { get; set; }
        public bool IsSafe { get; set; }
        public string SafetyReason { get; set; } = "unknown";
        public string WeatherCondition { get; set; } = "Unknown";
        public double CloudCoverage { get; set; }
        public double Confidence { get; set; }
        public bool RainDetected { get; set; }
        public bool FogDetected { get; set; }
        public string Provider { get; set; } = "Unknown";
        public string Model { get; set; } = "Unknown";
        public DateTime? AnalysisUtc { get; set; }
        public double? AnalysisAgeSeconds { get; set; }
        public bool SourceFresh { get; set; }
        // SourceFresh keeps its original sky-analysis meaning. New peers also understand
        // a fresh external-only OR verdict; old peers remain conservatively unsafe when
        // the sky is stale. Never forge AnalysisUtc just because a sensor is readable.
        [System.Text.Json.Serialization.JsonIgnore(Condition = System.Text.Json.Serialization.JsonIgnoreCondition.WhenWritingNull)]
        public bool? SafetyDecisionFresh { get; set; }
        [System.Text.Json.Serialization.JsonIgnore]
        public bool HasFreshSafetyDecision => SafetyDecisionFresh ?? SourceFresh;
        public bool FailoverConfigurationAvailable { get; set; }
        public string FailoverConfigurationRevision { get; set; } = string.Empty;
    }

    public sealed class AIWeatherClusterHealth
    {
        public int SchemaVersion { get; set; } = AIWeatherClusterProtocol.SchemaVersion;
        public string Product { get; set; } = AIWeatherClusterProtocol.Product;
        public string NodeId { get; set; } = string.Empty;
        public string SessionId { get; set; } = string.Empty;
        public DateTime GeneratedUtc { get; set; }
    }

    public sealed class ClusterErrorResponse
    {
        public string Code { get; set; } = "unknown";
        public string Message { get; set; } = "Unknown error";
        public bool Retryable { get; set; }
    }

    public sealed class AIWeatherFailoverConfigurationEnvelope
    {
        public int SchemaVersion { get; set; } = AIWeatherClusterProtocol.SchemaVersion;
        public string Product { get; set; } = AIWeatherClusterProtocol.Product;
        public string PrimaryNodeId { get; set; } = string.Empty;
        public string PrimarySessionId { get; set; } = string.Empty;
        public DateTime GeneratedUtc { get; set; }
        public string Revision { get; set; } = string.Empty;
        public string Salt { get; set; } = string.Empty;
        public string Nonce { get; set; } = string.Empty;
        public string Ciphertext { get; set; } = string.Empty;
        public string Tag { get; set; } = string.Empty;
    }

    public enum AIWeatherReplicaFailure
    {
        None,
        Waiting,
        Network,
        Authentication,
        Protocol,
        Stale
    }
}
