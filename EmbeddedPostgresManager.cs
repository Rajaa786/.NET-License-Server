using MysticMind.PostgresEmbed;
using Npgsql;

namespace MyLanService
{
    public class EmbeddedPostgresManager : IAsyncDisposable
    {
        readonly ILogger<EmbeddedPostgresManager> _logger;
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

        public EmbeddedPostgresManager(ILogger<EmbeddedPostgresManager> logger)
        {
            _logger = logger;
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
    }
}
