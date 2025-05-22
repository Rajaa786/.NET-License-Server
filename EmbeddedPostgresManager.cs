using MyLanService.Utils;
using MysticMind.PostgresEmbed;
using Npgsql;

namespace MyLanService
{
    public class EmbeddedPostgresManager : IAsyncDisposable
    {
        readonly ILogger<EmbeddedPostgresManager> _logger;
        readonly LicenseHelper _licenseHelper;
        PgServer _server;

        public bool IsRunning()
        {
            if (_server != null)
            {
                _logger.LogInformation("Postgres is running... {server}", _server);
                return true;
            }
            else
            {
                _logger.LogInformation("Postgres is not running");
                return false;
            }
        }

        public EmbeddedPostgresManager(
            ILogger<EmbeddedPostgresManager> logger,
            LicenseHelper licenseHelper
        )
        {
            _logger = logger;
            _licenseHelper = licenseHelper;
        }

        public async Task StartAsync(string version, string dbDir, int port, Guid InstanceId)
        {
            if (_server != null)
                return;

            _logger.LogInformation("Starting embedded Postgres…");

            _server = new PgServer(version, dbDir: dbDir, port: port, instanceId: InstanceId);
            await _server.StartAsync();

            _logger.LogInformation("Embedded Postgres is now running on port {Port}", port);
        }

        public void Stop()
        {
            _logger.LogInformation("Stopping embedded Postgres…");
            _server?.Stop();
            _logger.LogInformation("Embedded Postgres has been stopped.");
            _server = null;
        }

        public string GetConnectionString(
            string database = "postgres",
            string username = "postgres",
            string password = "password",
            string host = "localhost",
            int port = 5432
        )
        {
            // build a connection string to the running server
            return new NpgsqlConnectionStringBuilder
            {
                Host = host,
                Port = port,
                Username = username,
                Password = password,
                Database = database,
            }.ToString();
        }

        public async ValueTask DisposeAsync()
        {
            Stop();
        }

        /// <summary>
        /// Attempts to start PostgreSQL server from saved configuration.
        /// Assumes the server is not running when the app starts.
        /// </summary>
        /// <returns>True if the server was started successfully, false otherwise</returns>
        public async Task<bool> AutoStartFromConfigAsync()
        {
            try
            {
                // Try to load database configuration
                var config = _licenseHelper.LoadDatabaseConfig();
                if (config == null)
                {
                    _logger.LogInformation("No database configuration found, skipping auto-start");
                    return false;
                }

                _logger.LogInformation(
                    "Found database configuration, attempting to start PostgreSQL server {config}",
                    config
                );

                // Start the server with the saved configuration
                await StartAsync(
                    config.PostgresVersion,
                    config.DataDirectory,
                    config.Port,
                    config.InstanceId
                );

                _logger.LogInformation(
                    "PostgreSQL server auto-started successfully from saved configuration"
                );
                return true;
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Failed to auto-start PostgreSQL server from configuration");
                return false;
            }
        }
    }
}
