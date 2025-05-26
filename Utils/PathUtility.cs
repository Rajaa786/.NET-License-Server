namespace MyLanService.Utils
{
    public class PathUtility
    {
        private readonly ILogger<PathUtility> _logger;

        public PathUtility(ILogger<PathUtility> logger)
        {
            _logger = logger;
        }

        private void SetPermissionsAndRemoveQuarantine(string path)
        {
            _logger.LogInformation("Setting permissions and removing quarantine from {path}", path);
            try
            {
                SystemUtils.RunProcess("chmod", new List<string> { "-R", "755", $"{path}" });
                SystemUtils.RunProcess(
                    "xattr",
                    new List<string> { "-dr", "com.apple.quarantine", $"{path}" }
                );

                _logger.LogInformation("Set permissions and removed quarantine from {path}", path);
            }
            catch (Exception ex)
            {
                _logger.LogWarning(
                    ex,
                    "Could not set permissions or remove quarantine from {path}",
                    path
                );
            }
        }

        public string GetSharedAppDataPath(string appFolder)
        {
            if (OperatingSystem.IsMacOS())
            {
                // var baseDir = "/Users/rajaa/Desktop/PostgreData";
                // var baseDir = Path.Combine("/Library/Application Support", appFolder);
                // var baseDir = Path.Combine("/usr/local/bin/", appFolder);
                // var baseDir = Path.Combine("/Library", appFolder);
                // var localAppData = Environment.GetFolderPath(
                //     Environment.SpecialFolder.LocalApplicationData
                // );
                // var parentDir = Directory.GetParent(localAppData)?.FullName;
                // var baseDir = Path.Combine(parentDir ?? localAppData, appFolder);

                var baseDir = Path.Combine("/Users/Shared", appFolder);

                // SetPermissionsAndRemoveQuarantine(baseDir);

                return baseDir;
            }
            else if (OperatingSystem.IsWindows())
            {
                return Path.Combine(
                    Environment.GetFolderPath(Environment.SpecialFolder.CommonApplicationData),
                    appFolder
                );
            }
            else
            {
                return Path.Combine("/var/lib", appFolder);
            }
        }

        public void EnsureDirectoryExists(string path)
        {
            if (!Directory.Exists(path))
            {
                Directory.CreateDirectory(path);
            }
        }
    }
}
