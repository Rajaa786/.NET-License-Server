using System;
using System.Net;
using System.Net.Sockets;
using System.Text;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Extensions.Logging;

namespace MyLanService.Services
{
    /// <summary>
    /// Service for handling UDP-based service discovery for license server and database
    /// </summary>
    public class UdpDiscoveryService : IDisposable
    {
        private readonly ILogger<UdpDiscoveryService> _logger;
        private UdpClient? _udpListener;
        private Task? _discoveryTask;
        private CancellationTokenSource? _cancellationTokenSource;
        private bool _isStarted = false;

        // Configuration values
        private readonly int _discoveryPort;
        private int _licenseServicePort;
        private readonly string _licenseServiceName;
        private readonly string _licenseDiscoveryQuery;

        // Database service values
        private string _dbDiscoveryQuery = "DISCOVER_POSTGRESQL_SERVER";
        private int _dbPort = 5432;
        private string _dbInstanceId = "";
        private string _dbVersion = "";
        private bool _dbServiceEnabled = false;

        /// <summary>
        /// Creates a new UDP discovery service
        /// </summary>
        /// <param name="logger">Logger instance</param>
        /// <param name="discoveryPort">UDP port to listen for discovery messages</param>
        /// <param name="servicePort">Port of the service being advertised</param>
        /// <param name="serviceName">Name of the service being advertised</param>
        /// <param name="discoveryQuery">Expected query string for discovery requests</param>
        public UdpDiscoveryService(
            ILogger<UdpDiscoveryService> logger,
            int discoveryPort = 41234,
            int licenseServicePort = 7890,
            string licenseServiceName = "LicenseServer",
            string licenseDiscoveryQuery = "DISCOVER_LICENSE_SERVER"
        )
        {
            _logger = logger;
            _discoveryPort = discoveryPort;
            _licenseServicePort = licenseServicePort;
            _licenseServiceName = licenseServiceName;
            _licenseDiscoveryQuery = licenseDiscoveryQuery;
            _cancellationTokenSource = new CancellationTokenSource();

            _logger.LogInformation(
                "UdpDiscoveryService initialized for license service {ServiceName} on port {ServicePort}, listening on UDP port {DiscoveryPort}",
                licenseServiceName,
                licenseServicePort,
                discoveryPort
            );
        }

        /// <summary>
        /// Starts the UDP discovery service
        /// </summary>
        /// <returns>Task representing the async operation</returns>
        public void Start()
        {
            if (_isStarted)
            {
                _logger.LogDebug("UdpDiscoveryService is already running");
                return;
            }

            try
            {
                _udpListener = new UdpClient(new IPEndPoint(IPAddress.Any, _discoveryPort));
                _cancellationTokenSource = new CancellationTokenSource();

                // Start the discovery task
                _discoveryTask = StartDiscoveryListenerAsync(_cancellationTokenSource.Token);
                _isStarted = true;

                _logger.LogInformation(
                    "UdpDiscoveryService started on port {Port}",
                    _discoveryPort
                );
            }
            catch (Exception ex)
            {
                _logger.LogError(
                    ex,
                    "Failed to start UdpDiscoveryService on port {Port}",
                    _discoveryPort
                );
                throw;
            }
        }

        /// <summary>
        /// Stops the UDP discovery service
        /// </summary>
        public void Stop()
        {
            if (!_isStarted)
            {
                return;
            }

            _logger.LogInformation("Stopping UdpDiscoveryService...");

            try
            {
                // Cancel the listener task
                _cancellationTokenSource?.Cancel();

                // Close the UDP listener
                _udpListener?.Close();
                _udpListener?.Dispose();
                _udpListener = null;

                _isStarted = false;
                _logger.LogInformation("UdpDiscoveryService stopped successfully");
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Error stopping UdpDiscoveryService");
            }
        }

        /// <summary>
        /// Main discovery listener task
        /// </summary>
        private async Task StartDiscoveryListenerAsync(CancellationToken cancellationToken)
        {
            _logger.LogInformation(
                "Starting UDP discovery listener on port {Port}...",
                _discoveryPort
            );

            try
            {
                while (!cancellationToken.IsCancellationRequested)
                {
                    try
                    {
                        // Wait for discovery messages
                        UdpReceiveResult result = await _udpListener.ReceiveAsync(
                            cancellationToken
                        );
                        string message = Encoding.UTF8.GetString(result.Buffer).Trim();

                        _logger.LogDebug(
                            "UDP message received from {Endpoint}: {Message}",
                            result.RemoteEndPoint,
                            message
                        );

                        // Check if it's a license server discovery message
                        if (message == _licenseDiscoveryQuery)
                        {
                            await RespondToLicenseDiscoveryRequestAsync(result.RemoteEndPoint);
                            continue;
                        }

                        // Check if it's a database discovery message
                        if (message == _dbDiscoveryQuery && _dbServiceEnabled)
                        {
                            await RespondToDatabaseDiscoveryRequestAsync(result.RemoteEndPoint);
                            continue;
                        }

                        _logger.LogDebug("Unrecognized UDP discovery query: {Message}", message);
                    }
                    catch (OperationCanceledException)
                    {
                        // Normal cancellation, just exit
                        break;
                    }
                    catch (ObjectDisposedException) when (cancellationToken.IsCancellationRequested)
                    {
                        // UDP client was disposed during shutdown, just exit
                        break;
                    }
                    catch (Exception ex)
                    {
                        _logger.LogWarning(ex, "Error processing UDP discovery message");

                        // Wait a moment before continuing to avoid tight error loops
                        try
                        {
                            await Task.Delay(1000, cancellationToken);
                        }
                        catch (OperationCanceledException)
                        {
                            break;
                        }
                    }
                }
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Fatal error in UDP discovery listener");
            }
            finally
            {
                _logger.LogInformation("UDP discovery listener stopped");
            }
        }

        /// <summary>
        /// Responds to a license server discovery request
        /// </summary>
        private async Task RespondToLicenseDiscoveryRequestAsync(IPEndPoint remoteEndpoint)
        {
            try
            {
                var ipAddress = (
                    _udpListener.Client.LocalEndPoint as IPEndPoint
                )?.Address.ToString();

                // Create response object with service details
                var responseObj = new
                {
                    name = _licenseServiceName,
                    host = Dns.GetHostName(),
                    ip = ipAddress,
                    port = _licenseServicePort,
                    type = "license-server",
                };

                // Serialize and send the response
                string responseJson = JsonSerializer.Serialize(responseObj);
                byte[] responseBytes = Encoding.UTF8.GetBytes(responseJson);

                await _udpListener.SendAsync(responseBytes, responseBytes.Length, remoteEndpoint);

                _logger.LogInformation(
                    "Responded to license server UDP discovery request from {Endpoint} with: {Response}",
                    remoteEndpoint,
                    responseJson
                );
            }
            catch (Exception ex)
            {
                _logger.LogError(
                    ex,
                    "Failed to respond to discovery request from {Endpoint}",
                    remoteEndpoint
                );
            }
        }

        /// <summary>
        /// Enable database discovery service
        /// </summary>
        /// <param name="instanceId">The database instance ID</param>
        /// <param name="port">The database port</param>
        /// <param name="version">The database version</param>
        public void EnableDatabaseDiscovery(
            string instanceId,
            int port = 5432,
            string version = "17.4.0"
        )
        {
            _dbInstanceId = instanceId;
            _dbPort = port;
            _dbVersion = version;
            _dbServiceEnabled = true;

            _logger.LogInformation(
                "Database UDP discovery enabled for instance {InstanceId} on port {Port}",
                instanceId,
                port
            );
        }

        /// <summary>
        /// Disable database discovery service
        /// </summary>
        public void DisableDatabaseDiscovery()
        {
            if (_dbServiceEnabled)
            {
                _dbServiceEnabled = false;
                _logger.LogInformation("Database UDP discovery disabled");
            }
        }

        /// <summary>
        /// Responds to a database discovery request
        /// </summary>
        private async Task RespondToDatabaseDiscoveryRequestAsync(IPEndPoint remoteEndpoint)
        {
            if (!_dbServiceEnabled)
            {
                _logger.LogDebug(
                    "Ignoring database discovery request because database discovery is not enabled"
                );
                return;
            }

            try
            {
                var ipAddress = (
                    _udpListener.Client.LocalEndPoint as IPEndPoint
                )?.Address.ToString();

                // Create response object with database details
                var responseObj = new
                {
                    name = $"{Dns.GetHostName()}-db",
                    host = Dns.GetHostName(),
                    ip = ipAddress,
                    port = _dbPort,
                    instanceId = _dbInstanceId,
                    version = _dbVersion,
                    type = "postgresql",
                };

                // Serialize and send the response
                string responseJson = JsonSerializer.Serialize(responseObj);
                byte[] responseBytes = Encoding.UTF8.GetBytes(responseJson);

                await _udpListener.SendAsync(responseBytes, responseBytes.Length, remoteEndpoint);

                _logger.LogInformation(
                    "Responded to database UDP discovery request from {Endpoint} with: {Response}",
                    remoteEndpoint,
                    responseJson
                );
            }
            catch (Exception ex)
            {
                _logger.LogError(
                    ex,
                    "Failed to respond to database discovery request from {Endpoint}",
                    remoteEndpoint
                );
            }
        }

        /// <summary>
        /// Modify license service port after initialization
        /// </summary>
        public void UpdateLicenseServicePort(int newPort)
        {
            _logger.LogInformation(
                "Updating advertised license service port from {OldPort} to {NewPort}",
                _licenseServicePort,
                newPort
            );
            _licenseServicePort = newPort;
        }

        /// <summary>
        /// Modify database port after initialization
        /// </summary>
        public void UpdateDatabasePort(int newPort)
        {
            _logger.LogInformation(
                "Updating advertised database port from {OldPort} to {NewPort}",
                _dbPort,
                newPort
            );
            _dbPort = newPort;
        }

        /// <summary>
        /// Disposes resources
        /// </summary>
        public void Dispose()
        {
            _logger.LogInformation("Disposing UdpDiscoveryService");

            // Stop the service
            Stop();

            // Dispose the cancellation token source
            _cancellationTokenSource?.Dispose();

            _logger.LogDebug("UdpDiscoveryService disposed successfully");
        }
    }
}
