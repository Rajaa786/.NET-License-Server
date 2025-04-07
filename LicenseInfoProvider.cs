using System.Text;
using System.Text.Json;
using System.Security.Cryptography;

namespace MyLanService
{
    public class LicenseInfo
    {
        public string LicenseKey { get; set; }
        public string Username { get; set; }
        public double CurrentTimestamp { get; set; }
        public double ExpiryTimestamp { get; set; }
        public int NumberOfUsers { get; set; }
        public int NumberOfStatements { get; set; }
    }

    public sealed class LicenseInfoProvider
    {
        private static readonly Lazy<LicenseInfoProvider> _instance = new(() => new LicenseInfoProvider());
        public static LicenseInfoProvider Instance => _instance.Value;

        private LicenseInfo _licenseInfo;

        private LicenseInfoProvider()
        {
            _licenseInfo = LoadLicenseInfo();
        }

        public LicenseInfo GetLicenseInfo() => _licenseInfo;

        private LicenseInfo LoadLicenseInfo()
        {
            try
            {
                string baseDir = Directory.GetCurrentDirectory();
                string licenseFilePath = Path.Combine(baseDir, "license.enc");

                if (!File.Exists(licenseFilePath))
                    return new LicenseInfo(); // fallback

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
