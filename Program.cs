using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using MyLanService;

IHost host = Host.CreateDefaultBuilder(args)
    .UseWindowsService() // Enable Windows Service integration
    .ConfigureServices((hostContext, services) =>
    {
        services.AddHostedService<Worker>();
    })
    .Build();

await host.RunAsync();
