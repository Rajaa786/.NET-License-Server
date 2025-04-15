namespace MyLanService.Utils
{
    public class PathUtility
    {
        private readonly ILogger<PathUtility> _logger;

        public PathUtility(ILogger<PathUtility> logger)
        {
            _logger = logger;
        }

        public static string GetSharedAppDataPath(string appFolder)
        {
            if (OperatingSystem.IsMacOS())
            {
                return Path.Combine("/Library/Application Support", appFolder);
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

        public static void EnsureDirectoryExists(string path)
        {
            if (!Directory.Exists(path))
            {
                Directory.CreateDirectory(path);
            }
        }
    }
}
