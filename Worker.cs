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

        private const int UdpPort = 41234;
        private UdpClient _udpListener;
        private Task _udpListeningTask;

        private MulticastService _mdns;
        private ServiceDiscovery _serviceDiscovery;
        private readonly LicenseHelper _licenseHelper;
        private readonly LicenseStateManager _licenseStateManager;
        private readonly LicenseInfoProvider _licenseInfoProvider;
        private readonly IConfiguration _configuration;

        private HttpApiHost _httpApiHost;

        public Worker(
            ILogger<Worker> logger,
            LicenseHelper licenseHelper,
            LicenseStateManager licenseStateManager,
            LicenseInfoProvider licenseInfoProvider,
            IConfiguration configuration
        )
            : base()
        {
            _logger = logger;
            _licenseHelper = licenseHelper;
            _licenseStateManager = licenseStateManager;
            _licenseInfoProvider = licenseInfoProvider;
            _udpListener = new UdpClient(UdpPort);
            _configuration = configuration;
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

                // Use the constructor with a filter to select only the matching NIC
                _mdns = new MulticastService();
                _serviceDiscovery = new ServiceDiscovery(_mdns);

                _httpApiHost = new HttpApiHost(
                    HttpPort,
                    _logger,
                    _licenseStateManager,
                    _licenseInfoProvider,
                    _licenseHelper,
                    _configuration,
                    _serviceDiscovery
                );

                var httpTask = _httpApiHost.StartAsync(stoppingToken);
                var licensePollingTask = _httpApiHost.StartLicensePollingAsync(stoppingToken);
                _logger.LogInformation("HTTP API Server started on port 7890");

                _udpListeningTask = Task.Run(
                    () => ListenForUdpBroadcastsAsync(stoppingToken),
                    stoppingToken
                );
                _logger.LogInformation($"UDP broadcast listener started on port {UdpPort}.");

                // _mdns.QueryReceived += (s, e) =>
                // {
                //     var names = e.Message.Questions.Select(q => q.Name + " " + q.Type);
                //     Console.WriteLine($"got a query for {String.Join(", ", names)}");
                // };

                // _mdns.AnswerReceived += (s, e) =>
                // {
                //     var names = e.Message.Answers
                //         .Select(q => q.Name + " " + q.Type)
                //         .Distinct();
                //     Console.WriteLine($"got answer for {String.Join(", ", names)}");
                // };

                _mdns.QueryReceived += (s, e) =>
                {
                    var relevantQueries = e
                        .Message.Questions.Where(q =>
                            q.Name == "_license-server._tcp.local" && q.Type == DnsType.PTR
                        )
                        .Select(q => $"{q.Name} {q.Type}");

                    foreach (var query in relevantQueries)
                    {
                        _logger.LogInformation($"got query for {query}");
                    }
                };

                _mdns.AnswerReceived += (s, e) =>
                {
                    var relevantAnswers = e
                        .Message.Answers.Where(ans =>
                            ans.Name == "_license-server._tcp.local" && ans.Type == DnsType.PTR
                        )
                        .Select(ans => $"{ans.Name} {ans.Type}")
                        .Distinct();

                    foreach (var answer in relevantAnswers)
                    {
                        _logger.LogInformation($"got answer for {answer}");
                    }
                };

                // _mdns.NetworkInterfaceDiscovered += (s, e) =>
                // {
                //     foreach (var nic in e.NetworkInterfaces)
                //     {
                //         Console.WriteLine($"discovered NIC '{nic.Name}'");
                //     }
                // };


                // Get system hostname and local network IP address.
                string systemHostname = Dns.GetHostName();

                _logger.LogInformation($"IP address: {localIP}");

                // Create a service profile with your hostname and port.
                var serviceProfile = new ServiceProfile(
                    instanceName: systemHostname,
                    serviceName: "_license-server._tcp",
                    port: HttpPort
                );

                serviceProfile.AddProperty("description", "My TCP Server Service");
                serviceProfile.AddProperty("ttl", "300");

                // Advertise the service via mDNS.
                _serviceDiscovery.Advertise(serviceProfile);
                _mdns.Start();
                _logger.LogInformation(
                    $"mDNS advertisement started using hostname: {systemHostname} and IP: {localIP}:{HttpPort}"
                );

                _ = Task.Run(
                    async () =>
                    {
                        while (!stoppingToken.IsCancellationRequested)
                        {
                            try
                            {
                                _logger.LogInformation("Re-advertising mDNS service...");
                                _serviceDiscovery.Advertise(serviceProfile);
                            }
                            catch (Exception ex)
                            {
                                _logger.LogWarning(
                                    $"Failed to re-advertise mDNS service: {ex.Message}"
                                );
                            }

                            await Task.Delay(TimeSpan.FromMinutes(1), stoppingToken); // re-advertise every 60 seconds
                        }
                    },
                    stoppingToken
                );

                await Task.WhenAll(licensePollingTask, httpTask);
            }
            catch (Exception ex)
            {
                _logger.LogError($"Error: {ex.Message}");
            }
            finally
            {
                _tcpApiServer?.Stop();
                _serviceDiscovery?.Dispose();
                _mdns?.Stop();
                _mdns?.Dispose();
            }
        }

        private async Task ListenForUdpBroadcastsAsync(CancellationToken stoppingToken)
        {
            IPEndPoint remoteEndpoint = new IPEndPoint(IPAddress.Any, 0);

            try
            {
                while (!stoppingToken.IsCancellationRequested)
                {
                    UdpReceiveResult received = await _udpListener.ReceiveAsync();
                    string message = Encoding.UTF8.GetString(received.Buffer);

                    Console.WriteLine(
                        $"UDP broadcast received from {received.RemoteEndPoint}: {message}"
                    );

                    // Example check for a discovery query
                    if (message.Trim() == "DISCOVER_LICENSE_SERVER")
                    {
                        var ipAddress = (
                            _udpListener.Client.LocalEndPoint as IPEndPoint
                        )?.Address.ToString();

                        var responseObj = new
                        {
                            name = "LicenseServer",
                            host = Dns.GetHostName(),
                            ip = ipAddress,
                            port = HttpPort,
                        };

                        string response = JsonSerializer.Serialize(responseObj);
                        byte[] responseBytes = Encoding.UTF8.GetBytes(response);
                        await _udpListener.SendAsync(
                            responseBytes,
                            responseBytes.Length,
                            received.RemoteEndPoint
                        );

                        Console.WriteLine($"Responded to UDP discovery with: {response}");
                    }
                }
            }
            catch (ObjectDisposedException) when (stoppingToken.IsCancellationRequested)
            {
                // Listener was closed as part of shutdown, ignore.
            }
            catch (Exception ex)
            {
                Console.WriteLine("Error in UDP listener");
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
                _serviceDiscovery?.Dispose();

                if (_mdns is not null)
                {
                    _logger.LogInformation("Stopping mDNS service...");
                    _mdns.Stop();
                    _logger.LogInformation("mDNS service stopped.");
                    _mdns.Dispose();
                }
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
