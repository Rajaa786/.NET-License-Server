using System.IO; // Add this for Path operations
using System.Runtime.InteropServices;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using MyLanService;
using MyLanService.Database;
using MyLanService.Middlewares;
using MyLanService.Services;
using MyLanService.Utils;
using Serilog;
using Serilog.Settings.Configuration;
using Serilog.Sinks.File;

// <CETCompat>false</CETCompat>

// Determine environment and set appropriate log directory
var environment = Environment.GetEnvironmentVariable("DOTNET_ENVIRONMENT") ?? "Production";
string logBaseDir = environment.Equals("Development", StringComparison.OrdinalIgnoreCase)
    ? Directory.GetCurrentDirectory() // Use project directory in development
    : AppContext.BaseDirectory; // Use executable directory in production

var logPath = Path.Combine(logBaseDir, "logs", "gateway", "gateway_.txt");
var logDir = Path.GetDirectoryName(logPath);

if (!environment.Equals("Development", StringComparison.OrdinalIgnoreCase))
{
    // Ensure directory exists
    if (!Directory.Exists(logDir))
    {
        Directory.CreateDirectory(logDir);
    }

    // Load configuration
    var configuration = new ConfigurationBuilder()
        .AddJsonFile("appsettings.json", optional: false, reloadOnChange: true)
        .Build();

    // Configure Serilog - use config for settings except file path
    var options = new ConfigurationReaderOptions(
        typeof(FileLoggerConfigurationExtensions).Assembly
    );

    Log.Logger = new LoggerConfiguration()
        .ReadFrom.Configuration(configuration, options)
        .WriteTo.File(
            path: logPath,
            rollingInterval: RollingInterval.Day,
            fileSizeLimitBytes: 104857600,
            rollOnFileSizeLimit: true,
            retainedFileCountLimit: 31
        )
        .Enrich.FromLogContext()
        .CreateLogger();
}
else
{
    Log.Logger = new LoggerConfiguration().WriteTo.Console().CreateLogger();
}

try
{
    Log.Information("Starting service in {Environment}...", environment);
    Log.Information("Actual log path: {LogPath}", Path.GetFullPath(logPath));

    var builder = Host.CreateDefaultBuilder(args)
        .UseSerilog()
        .ConfigureServices(
            (hostContext, services) =>
            {
                Log.Information(
                    ">>> ENVIRONMENT: {Env}",
                    hostContext.HostingEnvironment.EnvironmentName
                );

                services.AddSingleton<LicenseHelper>();
                services.AddSingleton<LicenseInfoProvider>();
                services.AddSingleton<LicenseStateManager>();
                services.AddSingleton<PathUtility>();
                services.AddSingleton<EncryptionUtility>();
                services.AddSingleton<EmbeddedPostgresManager>();
                services.AddSingleton<DatabaseManager>();
                // Register network discovery services
                services.AddSingleton<MdnsAdvertiser>();
                services.AddSingleton<UdpDiscoveryService>();
                services.AddDbContext<ApplicationDbContext>(
                    (sp, options) =>
                    {
                        var pgManager = sp.GetRequiredService<EmbeddedPostgresManager>();
                        var conn = pgManager.GetConnectionString(
                            database: "postgres",
                            username: "postgres",
                            password: "password",
                            host: "localhost",
                            port: 5432
                        );
                        options.UseNpgsql(
                            conn,
                            npgsqlOpts =>
                            {
                                // ensure EF knows where your migrations live
                                npgsqlOpts.MigrationsAssembly(
                                    typeof(ApplicationDbContext).Assembly.FullName
                                );
                            }
                        );
                    }
                );
                services.AddHostedService<Worker>();
            }
        );

    if (RuntimeInformation.IsOSPlatform(OSPlatform.Windows))
    {
        builder.UseWindowsService();
    }

    await builder.Build().RunAsync();
}
catch (Exception ex)
{
    Log.Fatal(ex, "Service terminated unexpectedly");
}
finally
{
    Log.CloseAndFlush();
}
