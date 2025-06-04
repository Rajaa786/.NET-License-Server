using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using MyLanService.Utils;

namespace MyLanService
{
    public class LicenseInfo
    {
        // Logger field - marked as nullable to support the parameterless constructor for deserialization
        private ILogger? _logger;

        // Parameterized constructor for normal instantiation
        public LicenseInfo(ILogger logger)
        {
            _logger = logger;
        }

        // Parameterless constructor for JSON deserialization
        public LicenseInfo()
        {
            // Logger will be null until explicitly set
        }

        private double _currentTimestamp; // Standard backing field
        private double _expiryTimestamp; // Standard backing field
        private long _systemUpTime; // Standard backing field

        [JsonPropertyName("license_key")]
        public string LicenseKey { get; set; }

        // [JsonPropertyName("username")]
        // public string Username { get; set; }

        [JsonPropertyName("current_timestamp")]
        public double CurrentTimestamp
        {
            get { return _currentTimestamp; }
            set
            {
                if (!string.IsNullOrWhiteSpace(LicenseKey))
                {
                    _logger?.LogInformation(
                        $"[SetServerCurrentTime] Updating license current time from {_currentTimestamp} to {value}"
                    );
                    _currentTimestamp = value;
                }
                else
                {
                    _logger?.LogWarning(
                        "[SetServerCurrentTime] Cannot update current time, LicenseInfo is not initialized or invalid."
                    );
                }
            }
        }

        [JsonPropertyName("expiry_timestamp")]
        public double ExpiryTimestamp
        {
            get { return _expiryTimestamp; }
            set
            {
                if (!string.IsNullOrWhiteSpace(LicenseKey))
                {
                    _logger?.LogInformation(
                        $"[SetServerExpiryTime] Updating license expiry time from {_expiryTimestamp} to {value}"
                    );
                    _expiryTimestamp = value;
                }
                else
                {
                    _logger?.LogWarning(
                        "[SetServerExpiryTime] Cannot update expiry time, LicenseInfo is not initialized or invalid."
                    );
                }
            }
        }

        [JsonPropertyName("number_of_users")]
        public int NumberOfUsers { get; set; }

        [JsonPropertyName("number_of_statements")]
        public int NumberOfStatements { get; set; }

        [JsonPropertyName("role")]
        public string Role { get; set; }

        public int UsedStatements { get; set; }

        [JsonPropertyName("system_up_time")]
        public long SystemUpTime
        {
            get { return _systemUpTime; }
            set
            {
                if (!string.IsNullOrWhiteSpace(LicenseKey))
                {
                    _logger?.LogInformation(
                        $"[SetSystemUpTime] Updating system up-time from {_systemUpTime} to {value}"
                    );
                    _systemUpTime = value;
                }
                else
                {
                    _logger?.LogWarning(
                        "[SetSystemUpTime] Cannot update system up time, LicenseInfo is not initialized or invalid."
                    );
                }
            }
        }

        public bool IsValid()
        {
            return !string.IsNullOrWhiteSpace(LicenseKey)
                && CurrentTimestamp > 0
                && ExpiryTimestamp > CurrentTimestamp
                && NumberOfUsers > 0
                && NumberOfStatements != 0;
        }

        public override string ToString()
        {
            // Create a custom object with formatted dates for display only
            var displayInfo = new
            {
                LicenseKey = this.LicenseKey,
                // Convert Unix timestamp to IST (UTC+5:30)
                CurrentTimestampIST = ConvertUnixTimestampToIst(this.CurrentTimestamp),
                ExpiryTimestampIST = ConvertUnixTimestampToIst(this.ExpiryTimestamp),
                // Include raw timestamps for debugging
                CurrentTimestamp = this.CurrentTimestamp,
                ExpiryTimestamp = this.ExpiryTimestamp,
                NumberOfUsers = this.NumberOfUsers,
                NumberOfStatements = this.NumberOfStatements,
                Role = this.Role,
                UsedStatements = this.UsedStatements,
                // SystemUpTime = TimeSpan.FromMilliseconds(this.SystemUpTime).ToString(),
                SystemUpTime = this.SystemUpTime,
            };

            return JsonSerializer.Serialize(
                displayInfo,
                new JsonSerializerOptions { WriteIndented = true }
            );
        }

        // Helper method to convert Unix timestamp to IST formatted string
        private string ConvertUnixTimestampToIst(double unixTimestamp)
        {
            if (unixTimestamp <= 0)
                return "Not set";

            try
            {
                // Convert Unix timestamp to DateTime (UTC)
                DateTime utcDateTime = DateTimeOffset
                    .FromUnixTimeSeconds((long)unixTimestamp)
                    .UtcDateTime;

                // Create IST offset (UTC+5:30)
                // Using this approach instead of TimeZoneInfo.FindSystemTimeZoneById("Asia/Kolkata")
                // for better cross-platform compatibility
                TimeSpan istOffset = new TimeSpan(5, 30, 0);

                // Convert to IST by adding the offset
                DateTime istDateTime = utcDateTime.Add(istOffset);

                // Format with date and time
                return istDateTime.ToString("dd-MMM-yyyy hh:mm:ss tt") + " IST";
            }
            catch (Exception ex)
            {
                _logger?.LogWarning($"Error converting timestamp to IST: {ex.Message}");
                return $"Invalid timestamp: {unixTimestamp}";
            }
        }
    }

    public sealed class LicenseInfoProvider
    {
        private readonly ILogger<LicenseInfoProvider> _logger;

        private LicenseInfo _licenseInfo;
        private readonly LicenseHelper _licenseHelper;

        // Constructor with ILogger and LicenseInfo to inject logging and license data into LicenseInfoProvider
        public LicenseInfoProvider(ILogger<LicenseInfoProvider> logger, LicenseHelper licenseHelper)
        {
            _logger = logger;
            // _licenseInfo = licenseInfo ?? throw new ArgumentNullException(nameof(licenseInfo)); // Ensure LicenseInfo is not null
            _licenseHelper = licenseHelper;

            _licenseInfo = LoadLicenseInfo();
            _logger.LogInformation(
                $"LicenseInfoProvider initialized with LicenseKey: {_licenseInfo.ToString()}"
            );
            _logger.LogInformation("LicenseInfoProvider initialized.");
        }

        public LicenseInfo GetLicenseInfo() => _licenseInfo;

        public void SetLicenseInfo(LicenseInfo licenseInfo) => _licenseInfo = licenseInfo;

        public void SaveLicenseInfo(LicenseInfo licenseInfo)
        {
            try
            {
                var env = Environment.GetEnvironmentVariable("DOTNET_ENVIRONMENT");
                string appFolder =
                    (
                        !string.IsNullOrWhiteSpace(env)
                        && env.Equals("Development", StringComparison.OrdinalIgnoreCase)
                    )
                        ? "CyphersolDev" // Development folder
                        : "Cyphersol"; // Production folder

                var licenseFilePath = _licenseHelper.GetLicenseFilePath(appFolder);

                // First serialize to JSON
                string json = JsonSerializer.Serialize(licenseInfo);

                // Then encrypt and save
                byte[] encryptedBytes = _licenseHelper.GetEncryptedBytes(json);
                _licenseHelper.WriteBytesSync(licenseFilePath, encryptedBytes);

                _logger.LogInformation($"License info saved successfully to {licenseFilePath}");
            }
            catch (Exception ex)
            {
                _logger.LogError($"Error saving license info: {ex.Message}", ex);
            }
        }

        // public void SetExpiry(double expiryTimestamp)
        // {
        //     if (_licenseInfo != null && !string.IsNullOrWhiteSpace(_licenseInfo.LicenseKey))
        //     {
        //         _logger.LogInformation(
        //             $"[SetExpiry] Updating license expiry from {_licenseInfo.ExpiryTimestamp} to {expiryTimestamp}"
        //         );
        //         _licenseInfo.ExpiryTimestamp = expiryTimestamp;
        //     }
        //     else
        //     {
        //         _logger.LogWarning(
        //             "[SetExpiry] Cannot update expiry, LicenseInfo is not initialized or invalid."
        //         );
        //     }
        // }

        // public void SetServerCurrentTime(double currentTime)
        // {
        //     if (_licenseInfo != null && !string.IsNullOrWhiteSpace(_licenseInfo.LicenseKey))
        //     {
        //         _logger.LogInformation(
        //             $"[SetServerCurrentTime] Updating license current time from {_licenseInfo.CurrentTimestamp} to {currentTime}"
        //         );
        //         _licenseInfo.CurrentTimestamp = currentTime;
        //     }
        //     else
        //     {
        //         _logger.LogWarning(
        //             "[SetServerCurrentTime] Cannot update current time, LicenseInfo is not initialized or invalid."
        //         );
        //     }
        // }

        // public void SetSystemUpTime(long systemUpTime)
        // {
        //     if (_licenseInfo != null && !string.IsNullOrWhiteSpace(_licenseInfo.LicenseKey))
        //     {
        //         _logger.LogInformation(
        //             $"[SetSystemUpTime] Updating license system up time from {_licenseInfo.SystemUpTime} to {systemUpTime}"
        //         );
        //         _licenseInfo.SystemUpTime = systemUpTime;
        //     }
        //     else
        //     {
        //         _logger.LogWarning(
        //             "[SetSystemUpTime] Cannot update system up time, LicenseInfo is not initialized or invalid."
        //         );
        //     }
        // }

        /// <summary>
        /// Loads the license information from an encrypted file on disk.
        /// Determines the appropriate folder based on the current environment
        /// (Development or Production). Reads and decrypts the license file,
        /// then deserializes it into a LicenseInfo object. If any error occurs
        /// during the process, logs the error and returns a new instance of
        /// LicenseInfo as a fallback.
        /// </summary>

        private LicenseInfo LoadLicenseInfo()
        {
            try
            {
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
                    encryptedBytes = _licenseHelper.ReadBytesSync(licenseFilePath);
                    _logger.LogInformation(
                        $"Successfully read encrypted license from {licenseFilePath} {encryptedBytes.Length} bytes"
                    );
                }
                catch (Exception ex)
                {
                    _logger.LogError($"Error reading encrypted license file: {ex.Message}", ex);
                    throw new Exception();
                }

                // string? decryptedJson = _licenseHelper.GetDecryptedLicense(encryptedBytes);
                // return JsonSerializer.Deserialize<LicenseInfo>(decryptedJson) ?? new LicenseInfo(_logger);

                string? decryptedJson = _licenseHelper.GetDecryptedLicense(encryptedBytes);

                // Deserialize from JSON - will use the parameterless constructor
                var licenseInfo = JsonSerializer.Deserialize<LicenseInfo>(decryptedJson);

                if (licenseInfo == null)
                {
                    // Fallback if deserialization failed
                    return new LicenseInfo(_logger);
                }

                // Add reference to logger - needed since JSON doesn't include logger
                // SetLoggerForDeserializedLicense(licenseInfo);

                // Initialize system up‑time at application start
                licenseInfo.SystemUpTime = Environment.TickCount64;
                return licenseInfo;
            }
            catch (Exception ex)
            {
                _logger.LogError($"Error loading license info: {ex.Message}", ex);
                return new LicenseInfo(_logger); // fallback on error
            }
        }
    }
}
