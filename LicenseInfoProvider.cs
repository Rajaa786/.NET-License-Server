using System.Text;
using System.Text.Json;
using System.Security.Cryptography;
using System.Text.Json.Serialization;

namespace MyLanService
{
    public class LicenseInfo
    {
        [JsonPropertyName("license_key")]
        public string LicenseKey { get; set; }

        [JsonPropertyName("username")]
        public string Username { get; set; }

        [JsonPropertyName("current_timestamp")]
        public double CurrentTimestamp { get; set; }

        [JsonPropertyName("expiry_timestamp")]
        public double ExpiryTimestamp { get; set; }

        [JsonPropertyName("number_of_users")]
        public int NumberOfUsers { get; set; }

        [JsonPropertyName("number_of_statements")]
        public int NumberOfStatements { get; set; }

        public override string ToString() => JsonSerializer.Serialize(this, new JsonSerializerOptions { WriteIndented = true });

    }

    public sealed class LicenseInfoProvider
    {
        private static readonly Lazy<LicenseInfoProvider> _instance = new(() => new LicenseInfoProvider());
        public static LicenseInfoProvider Instance => _instance.Value;

        private LicenseInfo _licenseInfo;

        private LicenseInfoProvider()
        {
            _licenseInfo = LoadLicenseInfo();
            Console.WriteLine($"LicenseInfoProvider initialized: {_licenseInfo}");
        }

        public LicenseInfo GetLicenseInfo() => _licenseInfo;

        private LicenseInfo LoadLicenseInfo()
        {
            try
            {
                var env = Environment.GetEnvironmentVariable("ASPNETCORE_ENVIRONMENT");
                string baseDir = (!string.IsNullOrWhiteSpace(env) && env.Equals("Development", StringComparison.OrdinalIgnoreCase))
                    ? Directory.GetCurrentDirectory()
                    : AppContext.BaseDirectory;
                string licenseFilePath = Path.Combine(baseDir, "license.enc");

                if (!File.Exists(licenseFilePath))
                {
                    Console.WriteLine($"License file not found at {licenseFilePath}");
                    return new LicenseInfo(); // fallback
                }
                Console.WriteLine($"License file found at {licenseFilePath}");

                byte[] encryptedBytes = File.ReadAllBytes(licenseFilePath);
                string fingerprint = Environment.MachineName + Environment.UserName;

                using var deriveBytes = new Rfc2898DeriveBytes(fingerprint, Encoding.UTF8.GetBytes("YourSuperSalt!@#"), 100_000, HashAlgorithmName.SHA256);
                byte[] aesKey = deriveBytes.GetBytes(32);
                byte[] aesIV = deriveBytes.GetBytes(16);

                using var aes = Aes.Create();
                aes.Key = aesKey;
                aes.IV = aesIV;
                aes.Mode = CipherMode.CBC;
                aes.Padding = PaddingMode.PKCS7;

                using var decryptor = aes.CreateDecryptor();
                byte[] decryptedBytes = decryptor.TransformFinalBlock(encryptedBytes, 0, encryptedBytes.Length);

                string json = Encoding.UTF8.GetString(decryptedBytes);

                return JsonSerializer.Deserialize<LicenseInfo>(json) ?? new LicenseInfo();
            }
            catch
            {
                return new LicenseInfo(); // fallback on error
            }
        }

    }
}
