using Microsoft.AspNetCore.Builder;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.AspNetCore.Http;
using System;

namespace MyLanService
{
    public class HttpApiHost
    {
        private readonly int _port;
        private readonly ILogger _logger;
        private WebApplication _app;

        public HttpApiHost(int port, ILogger logger)
        {
            _port = port;
            _logger = logger;
        }
        public async Task StartAsync(CancellationToken stoppingToken)
        {
            var builder = WebApplication.CreateBuilder();

            builder.Services
                .AddControllers()
                .AddNewtonsoftJson();

            var app = builder.Build();

            app.MapGet("/api/health", () =>
            {
                var html = """
                <!DOCTYPE html>
                <html>
                <head>
                    <title>Health Check</title>
                    <style>
                        body {
                            font-family: Arial, sans-serif;
                            background-color: #f4f4f4;
                            padding: 2rem;
                            color: #333;
                        }
                        .status {
                            padding: 1rem;
                            background-color: #d4edda;
                            border: 1px solid #c3e6cb;
                            border-radius: 5px;
                        }
                    </style>
                </head>
                <body>
                    <div class="status">
                        ✅ <strong>Status:</strong> OK<br />
                        💬 <strong>Message:</strong> HTTP Health Check Passed
                    </div>
                </body>
                </html>
            """;

                return Results.Content(html, "text/html");
            });

            app.MapPost("/api/check-license", async (HttpContext context) =>
            {
                var json = await context.Request.ReadFromJsonAsync<dynamic>();
                string licenseKey = json?.licenseKey ?? "";

                if (licenseKey == "ABC-123-XYZ")
                {
                    return Results.Ok(new
                    {
                        status = "ok",
                        message = "License valid",
                        data = new { expires = "2025-12-31", plan = "Pro" }
                    });
                }

                return Results.BadRequest(new
                {
                    status = "invalid",
                    message = "License key not recognized"
                });
            });

            await app.RunAsync($"http://0.0.0.0:{_port}"); ; // ✅ No URL here, only token
        }


        public async Task StopAsync(CancellationToken cancellationToken)
        {
            if (_app is not null)
            {
                _logger.LogInformation("Stopping HTTP API server...");
                await _app.StopAsync(cancellationToken);
                // No need to call _app.Dispose();
            }
        }

    }
}
