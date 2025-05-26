namespace MyLanService.Utils
{
    public static class SystemUtils
    {
        public static void RunProcess(string fileName, List<string> arguments)
        {
            var startInfo = new System.Diagnostics.ProcessStartInfo
            {
                FileName = fileName,
                Arguments = string.Join(" ", arguments.Select(arg => $"\"{arg}\"")),
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                UseShellExecute = false,
                CreateNoWindow = true,
            };

            using var process = new System.Diagnostics.Process { StartInfo = startInfo };

            process.Start();
            string output = process.StandardOutput.ReadToEnd();
            string error = process.StandardError.ReadToEnd();
            process.WaitForExit();

            if (process.ExitCode != 0)
            {
                throw new Exception($"Process '{fileName}' failed: {error}");
            }
        }
    }
}
