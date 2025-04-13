// EncryptionUtility.cs
using System.Security.Cryptography;
using System.Text;

namespace MyLanService.Utils
{
    public class EncryptionUtility
    {
        private readonly ILogger<EncryptionUtility> _logger;

        private EncryptionUtility(ILogger<EncryptionUtility> logger)
        {
            _logger = logger;
        }

        public static byte[] EncryptStringToBytes(
            string plainText,
            string fingerprint,
            string salt = "YourSuperSalt!@#"
        )
        {
            byte[] plainBytes = Encoding.UTF8.GetBytes(plainText);

            using var deriveBytes = new Rfc2898DeriveBytes(
                fingerprint,
                Encoding.UTF8.GetBytes(salt),
                100_000,
                HashAlgorithmName.SHA256
            );
            byte[] aesKey = deriveBytes.GetBytes(32);
            byte[] aesIV = deriveBytes.GetBytes(16);

            using var aes = Aes.Create();
            aes.Key = aesKey;
            aes.IV = aesIV;
            aes.Mode = CipherMode.CBC;
            aes.Padding = PaddingMode.PKCS7;

            using var encryptor = aes.CreateEncryptor();
            return encryptor.TransformFinalBlock(plainBytes, 0, plainBytes.Length);
        }

        public static string DecryptBytesToString(
            byte[] cipherBytes,
            string fingerprint,
            string salt = "YourSuperSalt!@#"
        )
        {
            using var deriveBytes = new Rfc2898DeriveBytes(
                fingerprint,
                Encoding.UTF8.GetBytes(salt),
                100_000,
                HashAlgorithmName.SHA256
            );
            byte[] aesKey = deriveBytes.GetBytes(32);
            byte[] aesIV = deriveBytes.GetBytes(16);

            using var aes = Aes.Create();
            aes.Key = aesKey;
            aes.IV = aesIV;
            aes.Mode = CipherMode.CBC;
            aes.Padding = PaddingMode.PKCS7;

            using var decryptor = aes.CreateDecryptor();
            byte[] plainBytes = decryptor.TransformFinalBlock(cipherBytes, 0, cipherBytes.Length);
            return Encoding.UTF8.GetString(plainBytes);
        }
    }
}
