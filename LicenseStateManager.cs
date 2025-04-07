using System;
using System.Collections.Concurrent;

namespace MyLanService
{
    public sealed class LicenseStateManager
    {
        private static readonly Lazy<LicenseStateManager> _instance = new(() =>
        {
            // Fetch max users from loaded license
            var licenseInfo = LicenseInfoProvider.Instance.GetLicenseInfo();
            int maxUsers = licenseInfo?.NumberOfUsers > 0 ? licenseInfo.NumberOfUsers : 5;
            return new LicenseStateManager(maxUsers);
        });

        public static LicenseStateManager Instance => _instance.Value;

        private readonly int _maxLicenses;
        private readonly ConcurrentDictionary<string, DateTime> _activeLicenses;
        private readonly object _lock = new();

        private LicenseStateManager(int maxLicenses)
        {
            _maxLicenses = maxLicenses;
            _activeLicenses = new ConcurrentDictionary<string, DateTime>();
        }

        public bool TryUseLicense(string clientId, out string message)
        {
            lock (_lock)
            {
                if (_activeLicenses.ContainsKey(clientId))
                {
                    message = "License already assigned to this client.";
                    return true;
                }

                if (_activeLicenses.Count >= _maxLicenses)
                {
                    message = "No available licenses.";
                    return false;
                }

                _activeLicenses[clientId] = DateTime.UtcNow;
                message = "License successfully assigned.";
                return true;
            }
        }

        public bool ReleaseLicense(string clientId, out string message)
        {
            lock (_lock)
            {
                if (_activeLicenses.TryRemove(clientId, out _))
                {
                    message = "License successfully released.";
                    return true;
                }

                message = "No license assigned to this client.";
                return false;
            }
        }

        public int ActiveCount => _activeLicenses.Count;
        public string[] ActiveClients => _activeLicenses.Keys.ToArray();

        public void CleanupExpiredLicenses(TimeSpan expiration)
        {
            var now = DateTime.UtcNow;
            foreach (var kvp in _activeLicenses)
            {
                if (now - kvp.Value > expiration)
                {
                    _activeLicenses.TryRemove(kvp.Key, out _);
                }
            }
        }
    }
}
