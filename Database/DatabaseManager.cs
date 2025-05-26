// DatabaseManager.cs
using System;
using System.Threading.Tasks;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;

namespace MyLanService.Database
{
    public class DatabaseManager
    {
        private readonly EmbeddedPostgresManager _pgManager;
        private readonly IServiceScopeFactory _scopeFactory;
        private readonly ILogger<DatabaseManager> _logger;

        public DatabaseManager(
            EmbeddedPostgresManager pgManager,
            IServiceScopeFactory scopeFactory,
            ILogger<DatabaseManager> logger
        )
        {
            _pgManager = pgManager;
            _scopeFactory = scopeFactory;
            _logger = logger;
        }

        public async Task MigrateAsync()
        {
            if (!_pgManager.IsRunning())
            {
                // _logger.LogInformation("[DB] Starting embedded Postgres before migration…");
                // await _pgManager.StartAsync("15.3.0", "./pg_data", 5432, Guid.NewGuid());
            }

            _logger.LogInformation("[DB] Applying EF Core migrations…");

            try
            {
                // Create a scope to get a scoped ApplicationDbContext
                using var scope = _scopeFactory.CreateScope();
                var db = scope.ServiceProvider.GetRequiredService<ApplicationDbContext>();

                var pendingMigrations = await db.Database.GetPendingMigrationsAsync();
                if (!pendingMigrations.Any())
                {
                    _logger.LogError(
                        "[DB] No pending migrations were found. Database is already up to date."
                    );
                    // throw new InvalidOperationException("No migrations to apply.");
                }

                _logger.LogInformation(
                    "[DB] Applying {Count} pending migration(s)…",
                    pendingMigrations.Count()
                );

                await db.Database.MigrateAsync();
                var migrations = db.Database.GetMigrations();
                _logger.LogInformation(
                    "[DB] Migrations: {Migrations}, Count: {Count}",
                    string.Join(", ", migrations),
                    migrations.Count()
                );
                _logger.LogInformation("[DB] Database is up to date.");
            }
            catch (Exception ex)
            {
                _logger.LogError("[DB] Failed to apply migrations: {Message}", ex.Message);
            }
        }
    }
}
