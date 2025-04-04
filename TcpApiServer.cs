using System;
using System.Net;
using System.Net.Sockets;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Extensions.Logging;

namespace MyLanService
{
    public class TcpApiServer
    {
        private readonly int _port;
        private readonly ILogger _logger;
        private TcpListener _listener;

        public TcpApiServer(int port, ILogger logger)
        {
            _port = port;
            _logger = logger;
        }

        public async Task StartAsync(CancellationToken cancellationToken)
        {
            _listener = new TcpListener(IPAddress.Any, _port);
            _listener.Server.SetSocketOption(SocketOptionLevel.Socket, SocketOptionName.ReuseAddress, true);
            _listener.Start();

            _logger.LogInformation($"TCP API Server started on port {_port}");

            while (!cancellationToken.IsCancellationRequested)
            {
                var client = await _listener.AcceptTcpClientAsync();
                _logger.LogInformation("Client connected!");
                _ = HandleClientAsync(client);
            }
        }

        public void Stop()
        {
            _listener?.Stop();
        }

        private async Task HandleClientAsync(TcpClient client)
        {
            using NetworkStream stream = client.GetStream();
            byte[] buffer = new byte[1024];
            int bytesRead = await stream.ReadAsync(buffer, 0, buffer.Length);
            string receivedData = Encoding.UTF8.GetString(buffer, 0, bytesRead);
            _logger.LogInformation($"Received: {receivedData}");

            string response = ProcessCommand(receivedData);
            byte[] responseBytes = Encoding.UTF8.GetBytes(response);
            await stream.WriteAsync(responseBytes, 0, responseBytes.Length);
        }

        private string ProcessCommand(string command)
        {
            switch (command.Trim().ToUpperInvariant())
            {
                case "PING": return "PONG";
                case "VERSION": return "MyLanService v1.0.0";
                case "STATUS": return "Service running";
                default: return "Unknown command";
            }
        }
    }
}
