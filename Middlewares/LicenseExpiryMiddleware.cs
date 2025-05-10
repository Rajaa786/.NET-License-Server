using System;
using System.Collections.Generic;
using System.Threading.Tasks;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Logging;
using MyLanService;

namespace MyLanService.Middlewares
{
    public class LicenseExpiryMiddleware
    {
        private readonly RequestDelegate _next;
        private readonly ILogger _logger;
        private readonly LicenseInfoProvider _licenseInfoProvider;
        private readonly Func<Task<bool>> _resyncCallback;

        private readonly Func<Task<bool>> _reportClockTampering;

        public LicenseExpiryMiddleware(
            RequestDelegate next,
            ILogger logger,
            LicenseInfoProvider licenseInfoProvider,
            Func<Task<bool>> resyncCallback,
            Func<Task<bool>> reportClockTampering
        )
        {
            _next = next;
            _logger = logger;
            _licenseInfoProvider = licenseInfoProvider;
            _resyncCallback = resyncCallback;
            _reportClockTampering = reportClockTampering;
        }

        public async Task InvokeAsync(HttpContext context)
        {
            try
            {
                var path = context.Request.Path.Value;

                // 🛡️ List of endpoints to exclude from license checks
                var excludedPaths = new List<string>
                {
                    "/api/activate-license",
                    "/api/health",
                    "/license/status/all/",
                    "/db/test/firewall",
                    "/db/test/network",
                };

                // Skip license check for excluded endpoints
                if (excludedPaths.Any(p => path.StartsWith(p, StringComparison.OrdinalIgnoreCase)))
                {
                    await _next(context);
                    return;
                }

                // Retrieve the license info from LicenseInfoProvider
                var licenseInfo = _licenseInfoProvider.GetLicenseInfo();
                _logger.LogInformation("Middleware LicenseInfo: {0}", licenseInfo);

                // Check if the license is valid
                if (licenseInfo == null || !licenseInfo.IsValid())
                {
                    context.Response.StatusCode = 403; // Forbidden
                    await context.Response.WriteAsync("License is invalid or not found.");
                    return;
                }

                // ✅ 1. Check if more than 2 hours since last server sync using Environment.TickCount64
                var tickNow = Environment.TickCount64;
                var ticksSinceLastSync = tickNow - licenseInfo.SystemUpTime;

                if (ticksSinceLastSync > TimeSpan.FromMinutes(1).TotalMilliseconds)
                {
                    _logger.LogWarning(
                        "⏳ More than 2 hours since last sync. Attempting to re-sync..."
                    );

                    var recheckSuccess = await _resyncCallback();
                    if (!recheckSuccess)
                    {
                        context.Response.StatusCode = 403;
                        await context.Response.WriteAsync(
                            "License sync failed. Please connect to the network."
                        );
                        return;
                    }

                    // Refresh license info after sync
                    licenseInfo = _licenseInfoProvider.GetLicenseInfo();
                }

                var systemCurrentTimestamp = DateTimeOffset.UtcNow.ToUnixTimeSeconds();
                var licenseGeneratedTimestamp = licenseInfo.CurrentTimestamp;
                var licenseExpiryTimestamp = licenseInfo.ExpiryTimestamp;

                // 🛡️ Clock tampering check: system time is behind license time
                if (Math.Abs(systemCurrentTimestamp - licenseGeneratedTimestamp) >= 600)
                {
                    _logger.LogWarning(
                        "⏱️ Potential clock tampering detected. System timestamp: {System}, License timestamp: {License}",
                        systemCurrentTimestamp,
                        licenseGeneratedTimestamp
                    );

                    // Run the tampering report in a background fire-and-forget task
                    _ = Task.Run(async () =>
                    {
                        try
                        {
                            var result = await _reportClockTampering();
                            _logger?.LogInformation(
                                "System clock tampering report sent. Success: {Result}",
                                result
                            );
                        }
                        catch (Exception ex)
                        {
                            _logger?.LogError(ex, "Failed to report system clock tampering.");
                        }
                    });

                    context.Response.StatusCode = 403;
                    await context.Response.WriteAsync("License is invalid or not found."); // Silent error
                    return;
                }

                // 🧭 License expiry check
                if (licenseExpiryTimestamp < systemCurrentTimestamp)
                {
                    context.Response.StatusCode = 403;
                    await context.Response.WriteAsync("License has expired.");
                    return;
                }

                // If the license is valid and not expired, continue processing the request
                await _next(context);
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Error during license expiry check.");
                context.Response.StatusCode = 500; // Internal Server Error
                await context.Response.WriteAsync("An error occurred during license validation.");
            }
        }
    }
}
