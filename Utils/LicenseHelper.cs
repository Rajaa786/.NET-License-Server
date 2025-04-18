using System.Management;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Security.Principal;
using System.Text;
using System.Text.Json;
using System.Net.NetworkInformation;

namespace MyLanService.Utils
{
    public class MacUtils
    {
        // P/Invoke for getting the UID on macOS
        [DllImport("libc")]
        private static extern uint getuid();

        public static string GetMacUserUid()
        {
            try
            {
                // Retrieve the UID
                uint uid = getuid();
                return uid.ToString();
            }
            catch
            {
                return "UnknownUID";
            }
        }
    }

    public class LicenseHelper
    {
        private readonly ILogger<LicenseHelper> _logger;
        private static string cachedSystemUUID = null;

        private string uuid;
        private string macAddress;
        private string hostname;
        private string windowsUserSID;
        private string username;

        // Constructor with DI for ILogger
        public LicenseHelper(ILogger<LicenseHelper> logger)
        {
            _logger = logger;
            InitializeDeviceInfo();
        }


        private void InitializeDeviceInfo()
        {
            try
            {
                // Get UUID
                uuid = GetSystemUUID();

                // Get MAC Address
                macAddress = GetMacAddress();

                // Get Hostname
                hostname = Environment.MachineName;

                // Get Windows User SID (if on Windows)
                if (RuntimeInformation.IsOSPlatform(OSPlatform.Windows))
                {
                    windowsUserSID = WindowsIdentity.GetCurrent()?.User?.Value ?? "UnknownSID";
                }
                else
                {
                    windowsUserSID = "NotApplicable";  // Not applicable for non-Windows systems
                }

                // Get Username
                username = Environment.UserName;

                _logger.LogInformation($"[DeviceInfo Init] UUID: {uuid}");
                _logger.LogInformation($"[DeviceInfo Init] MAC Address: {macAddress}");
                _logger.LogInformation($"[DeviceInfo Init] Hostname: {hostname}");
                _logger.LogInformation($"[DeviceInfo Init] Windows User SID: {windowsUserSID}");
                _logger.LogInformation($"[DeviceInfo Init] Username: {username}");
            }
            catch (Exception ex)
            {
                _logger.LogError($"Error initializing device info: {ex.Message}", ex);
            }
        }


        /// <summary>
        /// Saves the encrypted license JSON to a secure location on disk.
        /// </summary>
        public byte[] GetEncryptedBytes(string enrichedJson)
        {
            var fingerprint = this.GetFingerprint();
            byte[] encryptedBytes = EncryptionUtility.EncryptStringToBytes(
                enrichedJson,
                fingerprint
            );

            return encryptedBytes;

            // await File.WriteAllBytesAsync(licenseFilePath, encryptedBytes);
            // return licenseFilePath;
        }

        /// <summary>
        /// Loads and decrypts the license file from disk.
        /// Returns null if file does not exist or is unreadable.
        /// </summary>
        public string? GetDecryptedLicense(byte[] cipherBytes)
        {
            try
            {
                var fingerprint = this.GetFingerprint();
                return EncryptionUtility.DecryptBytesToString(cipherBytes, fingerprint);
            }
            catch (CryptographicException ex)
            {
                _logger.LogError(
                    $"Decryption failed. The license file may be corrupted or tampered with: {ex.Message}",
                    ex
                );
                throw new Exception();
            }
            catch (Exception ex)
            {
                _logger.LogError(
                    $"Unexpected error while reading or decrypting the license file: {ex.Message}",
                    ex
                );

                throw new Exception();
            }
        }

        public string GetSessionCacheFilePath()
        {
            var env = Environment.GetEnvironmentVariable("DOTNET_ENVIRONMENT");
            string appFolder = !string.IsNullOrWhiteSpace(env) &&
                               env.Equals("Development", StringComparison.OrdinalIgnoreCase)
                               ? "CyphersolDev"
                               : "Cyphersol";

            string baseDir = PathUtility.GetSharedAppDataPath(appFolder);
            PathUtility.EnsureDirectoryExists(baseDir);
            return Path.Combine(baseDir, "session-cache.enc");
        }



        public string GetLicenseFilePath(string appFolder)
        {
            string baseDir = PathUtility.GetSharedAppDataPath(appFolder);
            PathUtility.EnsureDirectoryExists(baseDir);
            string licenseFilePath = Path.Combine(baseDir, "license.enc");
            return licenseFilePath;
        }

        public bool EnsureLicenseFileExists(string path)
        {
            if (!File.Exists(path))
            {
                throw new FileNotFoundException($"License file not found at {path}");
            }

            return true;
        }

        public void WriteBytesSync(string filePath, byte[] bytes)
        {
            try
            {
                _logger.LogInformation($"Writing license file to {filePath}");
                File.WriteAllBytes(filePath, bytes);
            }
            catch (Exception ex)
            {
                _logger.LogError($"Error writing file: {ex.Message}", ex);
                throw new Exception();
            }
        }

        public async Task WriteBytesAsync(string filePath, byte[] bytes)
        {
            try
            {
                _logger.LogInformation($"Writing async license file to {filePath}");
                await File.WriteAllBytesAsync(filePath, bytes);
            }
            catch (Exception ex)
            {
                _logger.LogError($"Error writing file: {ex.Message}", ex);
                throw new Exception();
            }
        }

        public byte[] ReadBytesSync(string filePath)
        {
            this.EnsureLicenseFileExists(filePath);
            try
            {
                _logger.LogInformation($"Reading license file from {filePath}");
                return File.ReadAllBytes(filePath);
            }
            catch (Exception ex)
            {
                _logger.LogError($"Error reading file: {ex.Message}", ex);
                throw new Exception();
            }
        }

        public async Task<byte[]> ReadBytesAsync(string filePath)
        {
            this.EnsureLicenseFileExists(filePath);
            try
            {
                _logger.LogInformation($"Reading async license file from {filePath}");
                return await File.ReadAllBytesAsync(filePath);
            }
            catch (Exception ex)
            {
                _logger.LogError($"Error reading file: {ex.Message}", ex);
                throw new Exception();
            }
        }

        public string GetFingerprint()
        {
            try
            {
                var sb = new StringBuilder();

                // Machine Name
                string machineName = Environment.MachineName;
                sb.Append(machineName);

                // Username
                string userName = Environment.UserName;
                sb.Append(userName);

                if (RuntimeInformation.IsOSPlatform(OSPlatform.Windows))
                {
                    // Windows User SID
                    string userSid = WindowsIdentity.GetCurrent()?.User?.Value ?? "UnknownSID";
                    sb.Append(userSid);
                }
                else if (RuntimeInformation.IsOSPlatform(OSPlatform.OSX))
                {
                    // macOS UID
                    string macUid = MacUtils.GetMacUserUid();
                    sb.Append(macUid);
                }

                // System UUID from SMBIOS
                if (RuntimeInformation.IsOSPlatform(OSPlatform.Windows))
                {
                    if (cachedSystemUUID == null)
                    {
                        cachedSystemUUID = GetSystemUUID();
                    }
                    sb.Append(cachedSystemUUID);
                }

                string fingerprint = sb.ToString();
                _logger.LogInformation($"Fingerprint: {fingerprint}");

                return fingerprint;
            }
            catch (Exception ex)
            {
                _logger.LogError($"Error generating fingerprint: {ex.Message}", ex);
                return Environment.MachineName + Environment.UserName;
            }
        }

        private string GetSystemUUID()
        {
            if (RuntimeInformation.IsOSPlatform(OSPlatform.Windows))
            {
                try
                {
                    using var searcher = new ManagementObjectSearcher(
                        "SELECT UUID FROM Win32_ComputerSystemProduct"
                    );
                    foreach (ManagementObject obj in searcher.Get())
                    {
                        return obj["UUID"]?.ToString() ?? "UnknownUUID";
                    }
                }
                catch (Exception ex)
                {
                    _logger.LogWarning($"Could not retrieve system UUID: {ex.Message}");
                }
            }

            return "UnknownUUID";
        }


        private string GetMacAddress()
        {
            try
            {
                foreach (NetworkInterface nic in NetworkInterface.GetAllNetworkInterfaces())
                {
                    if (nic.OperationalStatus == OperationalStatus.Up)
                    {
                        return string.Join(":", nic.GetPhysicalAddress().GetAddressBytes().Select(b => b.ToString("X2")));
                    }
                }
            }
            catch (Exception ex)
            {
                _logger.LogWarning($"Could not retrieve MAC address: {ex.Message}");
            }

            return "UnknownMAC";
        }


        public string GetDeviceInfo()
        {
            var deviceInfo = new
            {
                uuid = uuid,
                macAddress = macAddress,
                hostname = hostname,
                windowsUserSID = windowsUserSID,
                username = username
            };

            return JsonSerializer.Serialize(deviceInfo);
        }
    }
}
