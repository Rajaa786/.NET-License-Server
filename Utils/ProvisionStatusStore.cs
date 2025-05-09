namespace MyLanService
{
    public class ProvisionStatus
    {
        public string Status { get; set; } = "in-progress"; // "completed", "error"
        public List<string> Logs { get; set; } = new();
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

        public void AddLog(string log)
        {
            lock (_lock)
            {
                _status.Logs.Add($"[{DateTime.Now:HH:mm:ss}] {log}");
            }
        }

        public ProvisionStatus GetStatus()
        {
            lock (_lock)
            {
                return new ProvisionStatus
                {
                    Status = _status.Status,
                    Error = _status.Error,
                    Logs = new List<string>(_status.Logs),
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
