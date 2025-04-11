using System;
using System.Collections.Concurrent;
using System.Security.Cryptography;
using System.Text;
using System.Linq;
using System.Text.Json;


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
        public bool Active { get; set; } = true;
    }

    public sealed class LicenseStateManager
    {
        private static readonly Lazy<LicenseStateManager> _instance = new(() =>
        {
            var licenseInfo = LicenseInfoProvider.Instance.GetLicenseInfo();
            int maxUsers = licenseInfo?.NumberOfUsers > 0 ? licenseInfo.NumberOfUsers : 5;
            return new LicenseStateManager(licenseInfo, maxUsers);
        });

        public static LicenseStateManager Instance => _instance.Value;

        private readonly int _maxLicenses;
        private readonly ConcurrentDictionary<string, LicenseSession> _activeLicenses;
        private readonly object _lock = new();

        // License usage tracking:
        // LicenseInfo.NumberOfStatements represents the allowed maximum.
        // _currentUsedStatements tracks the statements used (loaded from LicenseInfo.UsedStatements on startup).
        private readonly LicenseInfo _licenseInfo;
        private int _currentUsedStatements;
        // Flush disk writes every 10 seconds (adjust as needed)
        private DateTime _lastFlush = DateTime.UtcNow;
        private readonly TimeSpan _flushInterval = TimeSpan.FromSeconds(10);


        private LicenseStateManager(LicenseInfo licenseInfo, int maxLicenses)
        {
            _maxLicenses = maxLicenses;
            _currentUsedStatements = (licenseInfo?.UsedStatements ?? 0);

            _activeLicenses = new ConcurrentDictionary<string, LicenseSession>();
        }

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
            var rawData = $"{uuid?.Trim().ToLowerInvariant()}::{hostname?.Trim().ToLowerInvariant()}::{windowsUserSID?.Trim().ToLowerInvariant()}";

            // Generate SHA-256 hash and convert to a readable hex string
            var hashBytes = sha256.ComputeHash(Encoding.UTF8.GetBytes(rawData));
            var sessionKey = BitConverter.ToString(hashBytes).Replace("-", "").ToLowerInvariant();

            return sessionKey;
        }


        public bool TryUseLicense(string clientId, string uuid, string macAddress, string hostname, string username, out string message, out LicenseSession? session)
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
                    Active = true
                };

                _activeLicenses[sessionKey] = session;
                message = "License successfully assigned.";
                return true;
            }
        }

        public bool ReleaseLicense(string clientId, string uuid, string macAddress, string hostname, out string message)
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

        public bool ActivateSession(string clientId, string uuid, string macAddress, string hostname, out string message)
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

        public bool InactivateSession(string clientId, string uuid, string macAddress, string hostname, out string message)
        {
            var sessionKey = GenerateSessionKey(uuid, hostname, clientId);

            lock (_lock)
            {
                if (_activeLicenses.TryGetValue(sessionKey, out var session))
                {
                    session.Active = false;
                    message = "Session marked as inactive.";
                    return true;
                }

                message = "Session not found.";
                return false;
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
                if (_currentUsedStatements >= _licenseInfo.NumberOfStatements)
                {
                    message = "Statement limit reached.";
                    return false;
                }
                // Check against remaining statements (calculated on the fly)
                if ((_licenseInfo.NumberOfStatements - _currentUsedStatements) <= 0)
                {
                    message = "No remaining statements available.";
                    return false;
                }

                _currentUsedStatements++;
                message = "Statement used successfully.";

                if (DateTime.UtcNow - _lastFlush >= _flushInterval)
                {
                    FlushToDisk();
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
                    Console.WriteLine("[IsStatementLimitReached] License info is not available.");
                    return true; // Fail safe: assume limit is reached
                }
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
                if (_licenseInfo == null)
                {
                    Console.WriteLine("[FlushToDisk] License info is null. Skipping flush.");
                    return;
                }

                _licenseInfo.UsedStatements = _currentUsedStatements;

                string fingerprint = Environment.MachineName + Environment.UserName;
                using var deriveBytes = new Rfc2898DeriveBytes(fingerprint, Encoding.UTF8.GetBytes("YourSuperSalt!@#"), 100_000, HashAlgorithmName.SHA256);
                byte[] aesKey = deriveBytes.GetBytes(32);
                byte[] aesIV = deriveBytes.GetBytes(16);

                var json = JsonSerializer.Serialize(_licenseInfo);
                var plainBytes = Encoding.UTF8.GetBytes(json);

                using var aes = Aes.Create();
                aes.Key = aesKey;
                aes.IV = aesIV;
                aes.Mode = CipherMode.CBC;
                aes.Padding = PaddingMode.PKCS7;

                using var encryptor = aes.CreateEncryptor();
                var encryptedBytes = encryptor.TransformFinalBlock(plainBytes, 0, plainBytes.Length);

                var path = Path.Combine(AppContext.BaseDirectory, "license.enc");
                File.WriteAllBytes(path, encryptedBytes);
                _lastFlush = DateTime.UtcNow;
            }
            catch (Exception ex)
            {
                Console.WriteLine($"[FlushToDisk] Failed to write license file: {ex}");
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
