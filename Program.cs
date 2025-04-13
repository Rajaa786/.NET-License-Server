// using Microsoft.Extensions.DependencyInjection;
// using Microsoft.Extensions.Hosting;
// using MyLanService;

// IHost host = Host.CreateDefaultBuilder(args)
//     .UseWindowsService() // Enable Windows Service integration
//     .ConfigureServices((hostContext, services) =>
//     {
//         services.AddHostedService<Worker>();
//     })
//     .Build();

// await host.RunAsync();




using System.Runtime.InteropServices;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using MyLanService;
using MyLanService.Utils;

var builder = Host.CreateDefaultBuilder(args)
    .ConfigureServices(
        (hostContext, services) =>
        {
            services.AddSingleton<LicenseHelper>();
            services.AddSingleton<LicenseInfoProvider>();
            services.AddSingleton<LicenseStateManager>();
            services.AddSingleton<PathUtility>();
            services.AddSingleton<EncryptionUtility>();
            services.AddHostedService<Worker>();
        }
    );

if (RuntimeInformation.IsOSPlatform(OSPlatform.Windows))
{
    builder.UseWindowsService(); // Only add this on Windows
}

IHost host = builder.Build();
await host.RunAsync();
