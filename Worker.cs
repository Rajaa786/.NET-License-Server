using System;
using System.Collections.Generic;
using System.Net;
using System.Net.NetworkInformation;
using System.Net.Sockets;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Threading;
using System.Threading.Tasks;
using Makaretu.Dns;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using MyLanService.Database;
using MyLanService.Services;
using MyLanService.Utils;

namespace MyLanService
{
    public class Worker : BackgroundService
    {
        private readonly ILogger<Worker> _logger;

        // private TcpListener _tcpListener;
        private TcpApiServer _tcpApiServer;

        private const int TcpPort = 5000;
        private const int HttpPort = 7890;

        private readonly MdnsAdvertiser _mdnsAdvertiser;
        private readonly UdpDiscoveryService _udpDiscoveryService;
        private readonly LicenseHelper _licenseHelper;
        private readonly LicenseStateManager _licenseStateManager;
        private readonly LicenseInfoProvider _licenseInfoProvider;
        private readonly IConfiguration _configuration;

        private HttpApiHost _httpApiHost;
        private EmbeddedPostgresManager _postgresManager;
        private DatabaseManager _dbManager;

        public Worker(
            ILogger<Worker> logger,
            LicenseHelper licenseHelper,
            LicenseStateManager licenseStateManager,
            LicenseInfoProvider licenseInfoProvider,
            IConfiguration configuration,
            EmbeddedPostgresManager postgresManager,
            DatabaseManager dbManager,
            MdnsAdvertiser mdnsAdvertiser,
            UdpDiscoveryService udpDiscoveryService
        )
            : base()
        {
            _logger = logger;
            _licenseHelper = licenseHelper;
            _licenseStateManager = licenseStateManager;
            _licenseInfoProvider = licenseInfoProvider;

            _configuration = configuration;
            _postgresManager = postgresManager;
            _dbManager = dbManager;
            _mdnsAdvertiser = mdnsAdvertiser;
            _udpDiscoveryService = udpDiscoveryService;
            _logger.LogInformation("Worker initialized.");
        }

        protected override async Task ExecuteAsync(CancellationToken stoppingToken)
        {
            var env = Environment.GetEnvironmentVariable("DOTNET_ENVIRONMENT");
            _logger.LogInformation($"Environment DOTNET_ENVIRONMENT: {env}");
            // string baseDir = (!string.IsNullOrWhiteSpace(env) && env.Equals("Development", StringComparison.OrdinalIgnoreCase))
            //     ? Directory.GetCurrentDirectory()
            //     : AppContext.BaseDirectory;
            try
            {
                // Initialize mDNS components first
                await WaitForNetworkAsync(10, 3000, stoppingToken);
                var localIP = GetLocalIPAddress();

                // Initialize and start the mDNS advertiser
                _mdnsAdvertiser.Start();

                _httpApiHost = new HttpApiHost(
                    HttpPort,
                    _logger,
                    _licenseStateManager,
                    _licenseInfoProvider,
                    _licenseHelper,
                    _configuration,
                    _mdnsAdvertiser,
                    _udpDiscoveryService,
                    _postgresManager,
                    _dbManager
                );

                // Try to auto-start PostgreSQL server from saved configuration if available
                await _postgresManager.AutoStartFromConfigAsync();

                var httpTask = _httpApiHost.StartAsync(stoppingToken);
                var licensePollingTask = _httpApiHost.StartLicensePollingAsync(stoppingToken);
                _logger.LogInformation("HTTP API Server started on port 7890");

                // Start UDP discovery service
                _udpDiscoveryService.Start();
                _logger.LogInformation("UDP discovery service started.");

                // Get system hostname and local network IP address.
                string systemHostname = Dns.GetHostName();

                _logger.LogInformation($"IP address: {localIP}");

                // Advertise the license server via mDNS
                var serviceProfile = _mdnsAdvertiser.AdvertiseLicenseService(HttpPort);
                _mdnsAdvertiser.Start();

                _logger.LogInformation(
                    $"mDNS advertisement started using hostname: {systemHostname} and IP: {localIP}:{HttpPort}"
                );

                // Note: re-advertisement is now handled automatically by the MdnsAdvertiser class

                await Task.WhenAll(licensePollingTask, httpTask);
            }
            catch (Exception ex)
            {
                _logger.LogError($"Error: {ex.Message}");
            }
            finally
            {
                _tcpApiServer?.Stop();
                _mdnsAdvertiser.Stop();
            }
        }

        // Helper method to check if the network is ready.
        private async Task WaitForNetworkAsync(
            int maxRetries = 10,
            int delayMilliseconds = 3000,
            CancellationToken cancellationToken = default
        )
        {
            _logger.LogInformation("Waiting for network to be ready...");
            for (int i = 0; i < maxRetries; i++)
            {
                try
                {
                    var ip = GetLocalIPAddress(); // Your existing logic
                    _logger.LogInformation($"Network is ready with IP: {ip}");
                    return;
                }
                catch (Exception ex)
                {
                    _logger.LogWarning(
                        $"Network not ready yet (attempt {i + 1}/{maxRetries}): {ex.Message}"
                    );
                    await Task.Delay(delayMilliseconds * (int)Math.Pow(2, i), cancellationToken);
                }
            }

            throw new Exception("Network did not become ready in time.");
        }

        // Helper method to retrieve the local IPv4 address.
        private IPAddress GetLocalIPAddress()
        {
            foreach (var ni in NetworkInterface.GetAllNetworkInterfaces())
            {
                if (ni.OperationalStatus != OperationalStatus.Up)
                    continue;

                // Filter out unwanted interface types
                if (
                    ni.NetworkInterfaceType == NetworkInterfaceType.Loopback
                    || ni.NetworkInterfaceType == NetworkInterfaceType.Tunnel
                    || ni.Description.ToLower().Contains("virtual")
                    || ni.Description.ToLower().Contains("vpn")
                    || ni.Name.ToLower().Contains("virtual")
                    || ni.Name.ToLower().Contains("vpn")
                )
                {
                    continue;
                }

                var ipProps = ni.GetIPProperties();

                // Check if it has a default gateway — sign of active network
                if (ipProps.GatewayAddresses.Count == 0)
                    continue;

                foreach (var ua in ipProps.UnicastAddresses)
                {
                    if (
                        ua.Address.AddressFamily == AddressFamily.InterNetwork
                        && !IPAddress.IsLoopback(ua.Address)
                    )
                    {
                        _logger.LogInformation(
                            $"Found active network interface: {ni.Name} with IP: {ua.Address}"
                        );
                        return ua.Address;
                    }
                }
            }

            throw new Exception("No suitable active network interface with an IPv4 address found.");
        }

        // private async Task HandleClient(TcpClient client)
        // {
        //     using (NetworkStream stream = client.GetStream())
        //     {
        //         byte[] buffer = new byte[1024];
        //         int bytesRead = await stream.ReadAsync(buffer, 0, buffer.Length);
        //         string receivedData = Encoding.UTF8.GetString(buffer, 0, bytesRead);
        //         _logger.LogInformation($"Received: {receivedData}");

        //         byte[] response = Encoding.UTF8.GetBytes("Message received!");
        //         await stream.WriteAsync(response, 0, response.Length);
        //     }
        // }

        public override async Task StopAsync(CancellationToken cancellationToken)
        {
            _logger.LogInformation("Stopping Worker...");

            try
            {
                if (_httpApiHost is not null)
                {
                    await _httpApiHost.StopAsync(cancellationToken);
                }

                _tcpApiServer?.Stop();
                _mdnsAdvertiser.Stop();
                _udpDiscoveryService.Stop();
                _licenseStateManager?.Flush();
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Error during shutdown");
            }
            finally
            {
                await base.StopAsync(cancellationToken);
            }
        }
    }
}
