using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Linq;
using System.Net;
using System.Threading;
using System.Threading.Tasks;
using Makaretu.Dns;
using Microsoft.Extensions.Logging;

namespace MyLanService.Services
{
    /// <summary>
    /// Service for advertising network services via mDNS
    /// </summary>
    public class MdnsAdvertiser : IDisposable
    {
        private readonly ILogger<MdnsAdvertiser> _logger;
        private readonly MulticastService _mdns;
        private readonly ServiceDiscovery _serviceDiscovery;
        private bool _isStarted = false;

        // Track all advertised services for re-advertisement
        private readonly Dictionary<string, ServiceProfile> _advertisedServices =
            new Dictionary<string, ServiceProfile>();
        private Task _reAdvertiseTask;
        private CancellationTokenSource _cancellationTokenSource;

        // Default re-advertisement interval
        private TimeSpan _reAdvertiseInterval = TimeSpan.FromMinutes(1);

        public MdnsAdvertiser(ILogger<MdnsAdvertiser> logger)
        {
            _logger = logger;
            _logger.LogInformation("Initializing MdnsAdvertiser...");
            _mdns = new MulticastService();
            _serviceDiscovery = new ServiceDiscovery(_mdns);
            _cancellationTokenSource = new CancellationTokenSource();
            _logger.LogInformation("MdnsAdvertiser initialized successfully");
        }

        /// <summary>
        /// Starts the mDNS service and re-advertisement task
        /// </summary>
        public void Start()
        {
            if (!_isStarted)
            {
                // Register event handlers for monitoring mDNS traffic
                RegisterEventHandlers();

                _mdns.Start();
                _isStarted = true;
                _logger.LogInformation("mDNS service started");

                // Start re-advertisement task
                StartReAdvertisementTask();
                _logger.LogInformation(
                    "mDNS advertisement task started with interval: {0} seconds",
                    _reAdvertiseInterval.TotalSeconds
                );
            }
        }

        /// <summary>
        /// Registers event handlers to monitor mDNS traffic
        /// </summary>
        private void RegisterEventHandlers()
        {
            // Monitor queries for our services
            _mdns.QueryReceived += (s, e) =>
            {
                var relevantQueries = e
                    .Message.Questions.Where(q =>
                        (
                            q.Name == "_license-server._tcp.local"
                            || q.Name == "_postgresql._tcp.local"
                        )
                        && q.Type == DnsType.PTR
                    )
                    .Select(q => new { Name = q.Name, Type = q.Type });

                foreach (var query in relevantQueries)
                {
                    _logger.LogInformation(
                        "Received mDNS query for service: {ServiceName} of type {QueryType}",
                        query.Name,
                        query.Type
                    );
                }
            };

            // Monitor answers for our services
            _mdns.AnswerReceived += (s, e) =>
            {
                var relevantAnswers = e
                    .Message.Answers.Where(ans =>
                        (
                            ans.Name == "_license-server._tcp.local"
                            || ans.Name == "_postgresql._tcp.local"
                        )
                        && ans.Type == DnsType.PTR
                    )
                    .Select(ans => new { Name = ans.Name, Type = ans.Type })
                    .Distinct();

                foreach (var answer in relevantAnswers)
                {
                    _logger.LogInformation(
                        "Received mDNS answer for service: {ServiceName} of type {AnswerType}",
                        answer.Name,
                        answer.Type
                    );
                }
            };

            // Monitor network interface discovery
            _mdns.NetworkInterfaceDiscovered += (s, e) =>
            {
                foreach (var nic in e.NetworkInterfaces)
                {
                    _logger.LogInformation(
                        "Discovered network interface: {InterfaceName} ({InterfaceId})",
                        nic.Name,
                        nic.Id
                    );
                }
            };

            _logger.LogInformation("Registered mDNS event handlers for traffic monitoring");
        }

        /// <summary>
        /// Starts the background task for periodically re-advertising services
        /// </summary>
        private void StartReAdvertisementTask()
        {
            if (_reAdvertiseTask != null)
            {
                _logger.LogInformation("Re-advertisement task already running, skipping creation");
                return; // Task already running
            }

            _reAdvertiseTask = Task.Run(
                async () =>
                {
                    while (!_cancellationTokenSource.Token.IsCancellationRequested)
                    {
                        try
                        {
                            // Wait first so we don't re-advertise immediately after startup
                            await Task.Delay(_reAdvertiseInterval, _cancellationTokenSource.Token);

                            if (_advertisedServices.Count > 0)
                            {
                                _logger.LogInformation(
                                    "Re-advertising {Count} mDNS services...",
                                    _advertisedServices.Count
                                );

                                // Take a snapshot of services to avoid potential concurrent modification issues
                                var services = _advertisedServices.Values.ToList();
                                foreach (var service in services)
                                {
                                    try
                                    {
                                        _serviceDiscovery.Advertise(service);
                                    }
                                    catch (Exception ex)
                                    {
                                        _logger.LogWarning(
                                            ex,
                                            "Failed to re-advertise service: {ServiceName} ({ServiceType})",
                                            service.InstanceName,
                                            service.ServiceName
                                        );
                                    }
                                }
                            }
                        }
                        catch (OperationCanceledException)
                        {
                            // Normal cancellation, just exit
                            break;
                        }
                        catch (Exception ex)
                        {
                            _logger.LogWarning(
                                ex,
                                "Error in mDNS re-advertisement task - will retry in 10 seconds"
                            );

                            // Wait a bit before retrying after an error
                            try
                            {
                                await Task.Delay(
                                    TimeSpan.FromSeconds(10),
                                    _cancellationTokenSource.Token
                                );
                            }
                            catch (OperationCanceledException)
                            {
                                break;
                            }
                        }
                    }
                },
                _cancellationTokenSource.Token
            );
        }

        /// <summary>
        /// Stops the mDNS service and re-advertisement task
        /// </summary>
        public void Stop()
        {
            if (_isStarted)
            {
                // Note: We don't unregister event handlers because we can't easily remove
                // specific delegates from MulticastService events in .NET
                _logger.LogInformation(
                    "mDNS event handlers will be disposed with MulticastService"
                );

                // Cancel and wait for re-advertisement task to complete
                if (
                    _cancellationTokenSource != null
                    && !_cancellationTokenSource.IsCancellationRequested
                )
                {
                    _logger.LogInformation("Canceling mDNS re-advertisement task");
                    _cancellationTokenSource.Cancel();
                    try
                    {
                        _reAdvertiseTask?.Wait(TimeSpan.FromSeconds(2));
                        _logger.LogInformation("mDNS re-advertisement task stopped gracefully");
                    }
                    catch (Exception ex)
                    {
                        _logger.LogInformation(
                            ex,
                            "Expected exception during re-advertisement task cancellation"
                        );
                        // Ignore exceptions from task cancellation
                    }
                }

                _mdns.Stop();
                _isStarted = false;
                _logger.LogInformation("mDNS service stopped");
            }
        }

        /// <summary>
        /// Gets a list of all currently advertised services
        /// </summary>
        /// <returns>Dictionary of service keys and their profiles</returns>
        public IReadOnlyDictionary<string, ServiceProfile> GetAdvertisedServices()
        {
            return new ReadOnlyDictionary<string, ServiceProfile>(_advertisedServices);
        }

        /// <summary>
        /// Advertises the License Server service via mDNS
        /// </summary>
        /// <param name="port">The license server HTTP port</param>
        /// <returns>The service profile that was advertised</returns>
        public ServiceProfile AdvertiseLicenseService(int port)
        {
            string systemHostname = Dns.GetHostName();
            string localIP = GetLocalIPAddress();
            string serviceKey = $"license-server:{port}";

            // Convert port to ushort as required by ServiceProfile
            ushort servicePort = (ushort)port;

            // Check if we already have this service registered
            if (_advertisedServices.TryGetValue(serviceKey, out var existingProfile))
            {
                _logger.LogInformation(
                    "Re-using existing license server service profile for port {Port}",
                    port
                );
                // Re-advertise existing profile
                _serviceDiscovery.Advertise(existingProfile);
                _logger.LogInformation(
                    "Re-advertised existing license server service on port {Port}",
                    port
                );
                return existingProfile;
            }

            // Create new service profile
            var serviceProfile = new ServiceProfile(
                instanceName: systemHostname,
                serviceName: "_license-server._tcp",
                port: servicePort
            );

            serviceProfile.AddProperty("description", "License Server Service");
            serviceProfile.AddProperty("ttl", "300");

            // Advertise and register the service
            _serviceDiscovery.Advertise(serviceProfile);
            _advertisedServices[serviceKey] = serviceProfile;

            _logger.LogInformation(
                "mDNS advertisement started for license server (key: {ServiceKey}) using hostname: {Hostname} and IP: {IP}:{Port}",
                serviceKey,
                systemHostname,
                localIP,
                port
            );

            return serviceProfile;
        }

        /// <summary>
        /// Advertises the PostgreSQL database service via mDNS
        /// </summary>
        /// <param name="instanceId">The PostgreSQL instance ID</param>
        /// <param name="port">The PostgreSQL port number</param>
        /// <param name="version">The PostgreSQL version</param>
        /// <returns>The service profile that was advertised</returns>
        public ServiceProfile AdvertiseDatabaseService(
            Guid instanceId,
            int port = 5432,
            string version = "17.4.0"
        )
        {
            string systemHostname = Dns.GetHostName();
            string serviceKey = $"postgresql:{instanceId}:{port}";

            // Check if we already have this database service registered
            if (_advertisedServices.TryGetValue(serviceKey, out var existingProfile))
            {
                _logger.LogInformation(
                    "Re-using existing PostgreSQL database service profile for instance {InstanceId} on port {Port}",
                    instanceId,
                    port
                );
                // Re-advertise existing profile
                _serviceDiscovery.Advertise(existingProfile);
                _logger.LogInformation(
                    "Re-advertised PostgreSQL database service for instance {InstanceId} on port {Port}",
                    instanceId,
                    port
                );
                return existingProfile;
            }

            // Convert port to ushort as required by ServiceProfile
            ushort servicePort = (ushort)port;

            // Create new database service profile
            var dbServiceProfile = new ServiceProfile(
                instanceName: $"{systemHostname}-db",
                serviceName: "_postgresql._tcp", // Standard service name for PostgreSQL
                port: servicePort
            );

            dbServiceProfile.AddProperty("description", "Embedded PostgreSQL Database");
            dbServiceProfile.AddProperty("ttl", "300");
            dbServiceProfile.AddProperty("version", version);
            dbServiceProfile.AddProperty("instance", instanceId.ToString());

            // Advertise and register the service
            _serviceDiscovery.Advertise(dbServiceProfile);
            _advertisedServices[serviceKey] = dbServiceProfile;

            _logger.LogInformation(
                "mDNS advertisement started for PostgreSQL database (key: {ServiceKey}, instance: {InstanceId}) using hostname: {Hostname}-db and IP: {IP}:{Port}",
                serviceKey,
                instanceId,
                systemHostname,
                IPAddress.Loopback,
                port
            );

            return dbServiceProfile;
        }

        /// <summary>
        /// Gets the local IP address of the machine
        /// </summary>
        private string GetLocalIPAddress()
        {
            try
            {
                string hostName = Dns.GetHostName();
                IPHostEntry hostEntry = Dns.GetHostEntry(hostName);

                foreach (IPAddress address in hostEntry.AddressList)
                {
                    // Return IPv4 address
                    if (address.AddressFamily == System.Net.Sockets.AddressFamily.InterNetwork)
                    {
                        return address.ToString();
                    }
                }

                return "127.0.0.1"; // Default to localhost if no suitable address found
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "Error getting local IP address");
                return "127.0.0.1";
            }
        }

        /// <summary>
        /// Sets the interval for automatic re-advertisement of services
        /// </summary>
        /// <param name="interval">Time interval between re-advertisements</param>
        public void SetReAdvertisementInterval(TimeSpan interval)
        {
            if (interval < TimeSpan.FromSeconds(10))
            {
                throw new ArgumentException(
                    "Re-advertisement interval cannot be less than 10 seconds",
                    nameof(interval)
                );
            }

            var oldInterval = _reAdvertiseInterval;
            _reAdvertiseInterval = interval;
            _logger.LogInformation(
                "mDNS re-advertisement interval changed from {OldInterval} seconds to {NewInterval} seconds",
                oldInterval.TotalSeconds,
                interval.TotalSeconds
            );
        }

        /// <summary>
        /// Manually re-advertise all registered services
        /// </summary>
        public void ReAdvertiseAllServices()
        {
            if (_advertisedServices.Count == 0)
            {
                return;
            }

            _logger.LogInformation(
                "Manually re-advertising {Count} mDNS services: {Services}",
                _advertisedServices.Count,
                string.Join(", ", _advertisedServices.Keys)
            );

            foreach (var service in _advertisedServices.Values)
            {
                try
                {
                    _serviceDiscovery.Advertise(service);
                }
                catch (Exception ex)
                {
                    _logger.LogWarning(
                        ex,
                        "Failed to re-advertise service: {ServiceName}",
                        service.InstanceName
                    );
                }
            }
        }

        /// <summary>
        /// Unregister a previously advertised service
        /// </summary>
        /// <param name="serviceKey">The key of the service to unregister</param>
        /// <returns>True if the service was found and unregistered, false otherwise</returns>
        public bool UnregisterService(string serviceKey)
        {
            if (_advertisedServices.TryGetValue(serviceKey, out _))
            {
                var removedService = _advertisedServices[serviceKey];
                _advertisedServices.Remove(serviceKey);
                _logger.LogInformation(
                    "Unregistered mDNS service: {ServiceKey}, name: {ServiceName}, type: {ServiceType}",
                    serviceKey,
                    removedService.InstanceName,
                    removedService.ServiceName
                );
                return true;
            }

            return false;
        }

        /// <summary>
        /// Disposes of resources
        /// </summary>
        public void Dispose()
        {
            // Cancel re-advertisement task
            if (
                _cancellationTokenSource != null
                && !_cancellationTokenSource.IsCancellationRequested
            )
            {
                _logger.LogInformation("Canceling mDNS re-advertisement task during dispose");
                _cancellationTokenSource.Cancel();
                try
                {
                    _reAdvertiseTask?.Wait(TimeSpan.FromSeconds(2));
                    _logger.LogInformation(
                        "mDNS re-advertisement task stopped gracefully during dispose"
                    );
                }
                catch (Exception ex)
                {
                    _logger.LogInformation(
                        ex,
                        "Expected exception during re-advertisement task cancellation in dispose"
                    );
                    // Ignore exceptions from task cancellation
                }
                _cancellationTokenSource.Dispose();
            }

            Stop();

            _logger.LogInformation("Disposing mDNS components");
            _mdns?.Dispose();
            _serviceDiscovery?.Dispose();

            // Clear service registry
            var serviceCount = _advertisedServices.Count;
            _advertisedServices.Clear();
            _logger.LogInformation(
                "Cleared {Count} mDNS service registrations during dispose",
                serviceCount
            );
        }
    }
}
