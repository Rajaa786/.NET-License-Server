using System;
using System.Collections.Concurrent;
using System.Linq;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using MyLanService.Utils;

namespace MyLanService
{
    public class LicenseSession
    {
        public string ClientId { get; set; }
        public string UUID { get; set; }
        public string Hostname { get; set; }
        public string Username { get; set; }
        public string MACAddress { get; set; }
        public DateTime AssignedAt { get; set; }
        public DateTime? LastHeartbeat { get; set; }
        public bool Active { get; set; } = false;
    }

    public class LicenseStateManager
    {
        public int _maxLicenses;
        private readonly ConcurrentDictionary<string, LicenseSession> _activeLicenses;
        private readonly object _lock = new();
        private readonly ILogger<LicenseStateManager> _logger;

        // License usage tracking:
        // LicenseInfo.NumberOfStatements represents the allowed maximum.
        // _currentUsedStatements tracks the statements used (loaded from LicenseInfo.UsedStatements on startup).
        public LicenseInfo _licenseInfo;
        public int _currentUsedStatements;

        // Flush disk writes every 10 seconds (adjust as needed)
        private DateTime _lastFlush = DateTime.UtcNow;
        private readonly TimeSpan _flushInterval = TimeSpan.FromSeconds(10);
        private readonly LicenseHelper _licenseHelper;

        public LicenseStateManager(
            LicenseInfoProvider licenseInfoProvider,
            ILogger<LicenseStateManager> logger,
            LicenseHelper licenseHelper
        )
        {
            _logger = logger;
            _licenseHelper = licenseHelper;
            _licenseInfo = licenseInfoProvider.GetLicenseInfo();
            _logger.LogInformation(
                $"[LicenseStateManager] License info loaded: {_licenseInfo.ToString()}"
            );
            _maxLicenses = _licenseInfo?.NumberOfUsers > 0 ? _licenseInfo.NumberOfUsers : 5;
            _currentUsedStatements = _licenseInfo?.UsedStatements ?? 0;
            _logger.LogInformation(
                $"[LicenseStateManager] Max licenses: {_maxLicenses}, Current used statements: {_currentUsedStatements}"
            );

            _activeLicenses = new ConcurrentDictionary<string, LicenseSession>();
        }

        private bool IsUnlimitedStatements => _licenseInfo?.NumberOfStatements == -1;

        /// <summary>
        /// Calculates the remaining statements allowed.
        /// </summary>
        public int RemainingStatements
        {
            get
            {
                lock (_lock)
                {
                    if (_licenseInfo == null)
                    {
                        return 0;
                    }
                    if (IsUnlimitedStatements)
                    {
                        return int.MaxValue; // Unlimited
                    }
                    return _licenseInfo.NumberOfStatements - _currentUsedStatements;
                }
            }
        }

        public int CurrentUsedStatements
        {
            get
            {
                lock (_lock)
                {
                    return _currentUsedStatements;
                }
            }
        }

        private string GenerateSessionKey(string uuid, string hostname, string windowsUserSID)
        {
            using var sha256 = SHA256.Create();

            // Normalize and combine input components
            var rawData =
                $"{uuid?.Trim().ToLowerInvariant()}::{hostname?.Trim().ToLowerInvariant()}::{windowsUserSID?.Trim().ToLowerInvariant()}";

            // Generate SHA-256 hash and convert to a readable hex string
            var hashBytes = sha256.ComputeHash(Encoding.UTF8.GetBytes(rawData));
            var sessionKey = BitConverter.ToString(hashBytes).Replace("-", "").ToLowerInvariant();

            return sessionKey;
        }

        public bool TryUseLicense(
            string clientId,
            string uuid,
            string macAddress,
            string hostname,
            string username,
            out string message,
            out LicenseSession? session
        )
        {
            var sessionKey = GenerateSessionKey(uuid, hostname, clientId);

            lock (_lock)
            {
                if (_activeLicenses.TryGetValue(sessionKey, out var existingSession))
                {
                    message = "License already assigned to this client.";
                    session = existingSession;
                    return true;
                }

                // ❌ No available licenses
                if (_activeLicenses.Count >= _maxLicenses)
                {
                    message = "No available licenses.";
                    session = null; // ✅ Avoid leaking previous reference
                    return false;
                }

                session = new LicenseSession
                {
                    ClientId = clientId,
                    UUID = uuid,
                    MACAddress = macAddress,
                    Hostname = hostname,
                    Username = username,
                    AssignedAt = DateTime.UtcNow,
                    LastHeartbeat = DateTime.UtcNow,
                    Active = false,
                };

                _activeLicenses[sessionKey] = session;
                message = "License successfully assigned.";
                return true;
            }
        }

        public bool ReleaseLicense(
            string clientId,
            string uuid,
            string macAddress,
            string hostname,
            out string message
        )
        {
            var sessionKey = GenerateSessionKey(uuid, hostname, clientId);

            lock (_lock)
            {
                if (_activeLicenses.TryRemove(sessionKey, out _))
                {
                    message = "License successfully released.";
                    return true;
                }

                message = "No license assigned to this client.";
                return false;
            }
        }

        public bool IsSessionValid(string clientId, string uuid, string macAddress, string hostname)
        {
            var sessionKey = GenerateSessionKey(uuid, hostname, clientId);

            lock (_lock)
            {
                if (_activeLicenses.ContainsKey(sessionKey))
                {
                    return true;
                }

                return false;
            }
        }

        public bool ActivateSession(
            string clientId,
            string uuid,
            string macAddress,
            string hostname,
            out string message
        )
        {
            var sessionKey = GenerateSessionKey(uuid, hostname, clientId);

            lock (_lock)
            {
                if (_activeLicenses.TryGetValue(sessionKey, out var session))
                {
                    session.Active = true;
                    session.LastHeartbeat = DateTime.UtcNow;
                    message = "Session activated.";
                    return true;
                }

                message = "Session not found.";
                return false;
            }
        }

        public bool InactivateSession(
            string clientId,
            string uuid,
            string macAddress,
            string hostname,
            out string message
        )
        {
            var sessionKey = GenerateSessionKey(uuid, hostname, clientId);

            lock (_lock)
            {
                if (_activeLicenses.TryGetValue(sessionKey, out var session))
                {
                    _logger.LogInformation(
                        $"[InactivateSession] Found session for clientId: {clientId}, uuid: {uuid}, macAddress: {macAddress}, hostname: {hostname}"
                    );
                    session.Active = false;
                    message = "Session marked as inactive.";
                    return true;
                }
                _logger.LogInformation(
                    $"[InactivateSession] Session not found for clientId: {clientId}, uuid: {uuid}, macAddress: {macAddress}, hostname: {hostname}"
                );

                message = "Session not found.";
                return false;
            }
        }

        public bool RevokeInactiveSession(string sessionKey, out string message)
        {
            // Generate the session key using your existing method.
            // var sessionKey = GenerateSessionKey(uuid, hostname, clientId);

            lock (_lock)
            {
                // Check if the session exists.
                if (_activeLicenses.TryGetValue(sessionKey, out var session))
                {
                    // Only allow revoking if the session is inactive.
                    if (!session.Active)
                    {
                        _activeLicenses.TryRemove(sessionKey, out _);
                        message = "Inactive session revoked successfully.";
                        return true;
                    }
                    else
                    {
                        message = "Session is active and cannot be revoked.";
                        return false;
                    }
                }
                else
                {
                    message = "Session not found.";
                    return false;
                }
            }
        }

        public IEnumerable<object> GetInactiveLicensesWithKey()
        {
            lock (_lock)
            {
                // Return an anonymous object containing the session key and the license session data
                return _activeLicenses
                    .Where(kvp => !kvp.Value.Active)
                    .Select(kvp => new { sessionKey = kvp.Key, sessionDetails = kvp.Value })
                    .ToList();
            }
        }

        public int ActiveCount => _activeLicenses.Count;
        public string[] ActiveClients => _activeLicenses.Keys.ToArray();

        public void CleanupExpiredLicenses(TimeSpan expiration)
        {
            var now = DateTime.UtcNow;
            foreach (var kvp in _activeLicenses)
            {
                if (now - kvp.Value.AssignedAt > expiration)
                {
                    _activeLicenses.TryRemove(kvp.Key, out _);
                }
            }
        }

        /// <summary>
        /// Attempts to record the usage of one statement.
        /// First checks if the current usage is less than the allowed limit by verifying RemainingStatements.
        /// </summary>
        /// <param name="message">Returns an informational message</param>
        /// <returns>True if a statement was successfully used; false if the limit has been reached.</returns>
        public bool TryUseStatement(out string message)
        {
            lock (_lock)
            {
                if (
                    !IsUnlimitedStatements
                    && _currentUsedStatements >= _licenseInfo.NumberOfStatements
                )
                {
                    message = "Statement limit reached.";
                    return false;
                }

                _currentUsedStatements++;
                message = "Statement used successfully.";

                if (DateTime.UtcNow - _lastFlush >= _flushInterval)
                {
                    _logger.LogInformation(
                        $"[TryUseStatement] Flushing {_currentUsedStatements} used statements to disk., last flush was at {_lastFlush}, difference: {DateTime.UtcNow - _lastFlush}"
                    );
                    FlushToDisk();
                }
                else
                {
                    _logger.LogInformation(
                        $"[TryUseStatement] Not flushing yet. Last flush was at {_lastFlush}, difference: {DateTime.UtcNow - _lastFlush}"
                    );
                }
                return true;
            }
        }

        /// <summary>
        /// Returns true if the statement usage limit has been reached.
        /// </summary>
        public bool IsStatementLimitReached()
        {
            lock (_lock)
            {
                if (_licenseInfo == null)
                {
                    _logger.LogInformation("[IsStatementLimitReached] License info is not available.");
                    return true; // Fail safe: assume limit is reached
                }

                if (IsUnlimitedStatements)
                {
                    return false; // Never limited
                }
                _logger.LogInformation(_licenseInfo.ToString());
                _logger.LogInformation(
                    $"[IsStatementLimitReached] Current used statements: {_currentUsedStatements}, License limit: {_licenseInfo.NumberOfStatements}"
                );

                return _currentUsedStatements >= _licenseInfo.NumberOfStatements;
            }
        }

        /// <summary>
        /// Flushes the current used statements back to disk.
        /// This method updates the in-memory LicenseInfo (UsedStatements field)
        /// and writes the encrypted version to file. Ensure you call Flush() on app close.
        /// </summary>
        private void FlushToDisk()
        {
            try
            {
                if (_licenseInfo == null || !_licenseInfo.IsValid())
                {
                    _logger.LogWarning("[FlushToDisk] License info is null. Skipping flush.");
                    return;
                }

                _logger.LogInformation(
                    $"[FlushToDisk] Flushing {_currentUsedStatements} used statements to disk."
                );

                _licenseInfo.UsedStatements = _currentUsedStatements;

                var enrichedJson = JsonSerializer.Serialize(_licenseInfo);

                _logger.LogInformation($"[FlushToDisk] Enriched JSON: {enrichedJson}");

                var env = Environment.GetEnvironmentVariable("DOTNET_ENVIRONMENT");
                string appFolder =
                    (
                        !string.IsNullOrWhiteSpace(env)
                        && env.Equals("Development", StringComparison.OrdinalIgnoreCase)
                    )
                        ? "CyphersolDev" // Use a development-specific folder name
                        : "Cyphersol"; // Use the production folder name

                var licenseFilePath = _licenseHelper.GetLicenseFilePath(appFolder);

                byte[] encryptedBytes = null;

                try
                {
                    encryptedBytes = _licenseHelper.GetEncryptedBytes(enrichedJson);
                }
                catch (Exception ex)
                {
                    _logger.LogError(
                        $"Unexpected error while getting encrypted bytes: {ex.Message}",
                        ex
                    );

                    throw new Exception("Something went wrong");
                }

                // Save the encrypted bytes to a file
                try
                {
                    _licenseHelper.WriteBytesSync(licenseFilePath, encryptedBytes);
                }
                catch (Exception ex)
                {
                    _logger.LogError($"Error saving encrypted license file: {ex.Message}", ex);
                    throw new Exception("Something went wrong");
                }
                _logger.LogInformation(
                    "License information securely saved at {0}",
                    licenseFilePath
                );

                _lastFlush = DateTime.UtcNow;
            }
            catch (Exception ex)
            {
                _logger.LogError($"[FlushToDisk] Failed to write license file: {ex}");
            }
        }

        /// <summary>
        /// Public method to flush the current license state to disk.
        /// This should be called on application close to ensure that the latest statement usage is persisted.
        /// </summary>
        public void Flush()
        {
            lock (_lock)
            {
                FlushToDisk();
            }
        }
    }
}
