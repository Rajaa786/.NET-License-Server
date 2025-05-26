namespace MyLanService.Utils
{
    public enum LogLevel
    {
        Info,
        Warning,
        Error,
    }

    public class LogEntry
    {
        private readonly string _timestamp;
        private readonly string _fullMessage;
        public string Message { get; }
        public LogLevel Level { get; }

        public LogEntry(string message, LogLevel level)
        {
            Message = message;
            Level = level;
            _timestamp = DateTime.Now.ToString("HH:mm:ss");
            string levelPrefix = Level switch
            {
                LogLevel.Info => "[INFO]   ",
                LogLevel.Warning => "[WARNING]",
                LogLevel.Error => "[ERROR]  ",
                _ => "[INFO]   ",
            };

            _fullMessage = $"[{_timestamp}] {levelPrefix} {Message}";
        }

        public override string ToString()
        {
            return _fullMessage;
        }
    }

    public class ProvisionStatus
    {
        public string Status { get; set; } = "in-progress"; // "completed", "error"
        public List<LogEntry> Logs { get; set; } = new();
        public int? Progress { get; set; }
        public string? Error { get; set; }
    }

    public class ProvisionStatusStore
    {
        private readonly object _lock = new();
        private ProvisionStatus _status = new();

        public void SetStatus(string status, string? error = null, int? progress = null)
        {
            lock (_lock)
            {
                _status.Status = status;
                _status.Error = error;
                _status.Progress = progress;
            }
        }

        public void AddLog(string message, LogLevel level = LogLevel.Info)
        {
            lock (_lock)
            {
                _status.Logs.Add(new LogEntry(message, level));
            }
        }

        public void AddErrorLog(string message)
        {
            AddLog(message, LogLevel.Error);
        }

        public void AddWarningLog(string message)
        {
            AddLog(message, LogLevel.Warning);
        }

        public void AddInfoLog(string message)
        {
            AddLog(message, LogLevel.Info);
        }

        public ProvisionStatus GetStatus()
        {
            lock (_lock)
            {
                return new ProvisionStatus
                {
                    Status = _status.Status,
                    Error = _status.Error,
                    Logs = new List<LogEntry>(_status.Logs),
                    Progress = _status.Progress,
                };
            }
        }

        public void Reset()
        {
            lock (_lock)
            {
                _status.Status = "idle";
                _status.Error = null;
                _status.Logs.Clear();
                _status.Progress = null;
            }
        }

        public void UpdateStatus(string status, string? error = null)
        {
            lock (_lock)
            {
                _status.Status = status;
                _status.Error = error;
            }
        }

        public void SetProgress(int progress)
        {
            lock (_lock)
            {
                _status.Progress = progress;
            }
        }
    }
}
