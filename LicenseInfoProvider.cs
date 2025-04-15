using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using MyLanService.Utils;

namespace MyLanService
{
    public class LicenseInfo
    {
        [JsonPropertyName("license_key")]
        public string LicenseKey { get; set; }

        // [JsonPropertyName("username")]
        // public string Username { get; set; }

        [JsonPropertyName("current_timestamp")]
        public double CurrentTimestamp { get; set; }

        [JsonPropertyName("expiry_timestamp")]
        public double ExpiryTimestamp { get; set; }

        [JsonPropertyName("number_of_users")]
        public int NumberOfUsers { get; set; }

        [JsonPropertyName("number_of_statements")]
        public int NumberOfStatements { get; set; }

        [JsonPropertyName("role")]
        public string Role { get; set; }

        public int UsedStatements { get; set; }

        public bool IsValid()
        {
            return !string.IsNullOrWhiteSpace(LicenseKey)
                && CurrentTimestamp > 0
                && ExpiryTimestamp > CurrentTimestamp
                && NumberOfUsers > 0
                && NumberOfStatements != 0;
        }

        public override string ToString() =>
            JsonSerializer.Serialize(this, new JsonSerializerOptions { WriteIndented = true });
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

                string? decryptedJson = _licenseHelper.GetDecryptedLicense(encryptedBytes);
                return JsonSerializer.Deserialize<LicenseInfo>(decryptedJson) ?? new LicenseInfo();
            }
            catch (Exception ex)
            {
                _logger.LogError($"Error loading license info: {ex.Message}", ex);
                return new LicenseInfo(); // fallback on error
            }
        }
    }
}
