using System.Diagnostics;
using System.Net;
using System.Net.NetworkInformation;
using System.Net.Sockets;
using MyLanService.Services;
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
        private readonly MdnsAdvertiser _mdnsAdvertiser;
        int _port;
        Guid _instanceId;

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
            LicenseHelper licenseHelper,
            MdnsAdvertiser mdnsAdvertiser
        )
        {
            _logger = logger;
            _licenseHelper = licenseHelper;
            _mdnsAdvertiser = mdnsAdvertiser;
        }

        public async Task StartAsync(string version, int port, Guid InstanceId, string dbDir = "")
        {
            if (_server != null)
                return;

            _logger.LogInformation("Starting embedded Postgres…");

            var serverParams = new Dictionary<string, string>();

            // // set generic query optimizer to off
            // serverParams.Add("geqo", "off");

            // // set timezone as UTC
            // serverParams.Add("timezone", "UTC");

            // // switch off synchronous commit
            // serverParams.Add("synchronous_commit", "off");

            // set max connections
            serverParams.Add("max_connections", "500");
            serverParams.Add("listen_addresses", "*");

            _server = new PgServer(
                version,
                dbDir: dbDir,
                port: port,
                instanceId: InstanceId,
                // addLocalUserAccessPermission: true,
                pgServerParams: serverParams
            );
            await _server.StartAsync();

            // Save port information
            this._port = port;
            this._instanceId = InstanceId;

            _logger.LogInformation("Embedded Postgres is now running on port {Port}", port);
        }

        /// <summary>
        /// Configures PostgreSQL's pg_hba.conf to allow connections from local LAN networks
        /// and reloads the configuration
        /// </summary>
        public async Task ConfigurePgAccessControlAsync(string dbDir)
        {
            if (_server == null)
            {
                _logger.LogWarning(
                    "Cannot configure pg_hba.conf: PostgreSQL server is not running"
                );
                return;
            }

            // string dataDir = _server.DataDir;
            string InstanceDataDir = Path.Combine(dbDir, "pg_embed", this._instanceId.ToString());

            string dataDir = Path.Combine(InstanceDataDir, "data");
            string pgHbaPath = Path.Combine(dataDir, "pg_hba.conf");

            try
            {
                if (!File.Exists(pgHbaPath))
                {
                    _logger.LogWarning(
                        "pg_hba.conf not found at expected location: {Path}",
                        pgHbaPath
                    );
                    return;
                }

                _logger.LogInformation(
                    "Configuring pg_hba.conf at {Path} to allow LAN access",
                    pgHbaPath
                );

                // Create a backup of the original file
                File.Copy(pgHbaPath, pgHbaPath + ".bak", overwrite: true);

                // Read existing content
                string[] existingLines = await File.ReadAllLinesAsync(pgHbaPath);

                // Check if our custom rules already exist
                bool customRulesExist = existingLines.Any(line =>
                    line.Contains("Custom rules for LAN access")
                    || (line.Contains("host") && line.Contains("192.168."))
                );

                if (customRulesExist)
                {
                    _logger.LogInformation("Custom LAN access rules already exist in pg_hba.conf");
                    return; // No need to modify the file again
                }

                // Keep all original content
                var newContent = new List<string>(existingLines);

                // Add a blank line if the file doesn't end with one
                if (
                    existingLines.Length > 0
                    && !string.IsNullOrWhiteSpace(existingLines[existingLines.Length - 1])
                )
                {
                    newContent.Add("");
                }

                // Add our custom rules
                newContent.Add("# Custom rules for LAN access added by License Server");
                // newContent.Add("host    all             all             127.0.0.1/32            md5");

                // Get the current local network subnets
                var localSubnets = GetLocalNetworkSubnets();
                foreach (string subnet in localSubnets)
                {
                    newContent.Add(
                        $"host    all             all             {subnet}           trust"
                    ); // Current local subnet
                    _logger.LogInformation("Added access rule for local subnet: {Subnet}", subnet);
                }

                // Add common private network ranges as fallback
                newContent.Add(
                    "host    all             all             10.0.0.0/8               md5"
                ); // Corporate networks
                newContent.Add(
                    "host    all             all             172.16.0.0/12            md5"
                ); // Alternative private range

                // Write updated content
                await File.WriteAllLinesAsync(pgHbaPath, newContent);

                _logger.LogInformation("Successfully updated pg_hba.conf to allow LAN access");

                // Reload PostgreSQL configuration
                await ReloadPostgresConfigurationAsync(dataDir);
            }
            catch (Exception ex)
            {
                _logger.LogError(
                    ex,
                    "Error configuring PostgreSQL access control: {Message}",
                    ex.Message
                );
            }
        }

        /// <summary>
        /// Reloads PostgreSQL configuration
        /// </summary>
        private async Task ReloadPostgresConfigurationAsync(string dataDir)
        {
            if (_server == null)
            {
                return;
            }

            try
            {
                _logger.LogInformation("Reloading PostgreSQL configuration...");

                // Method 1: Use SQL command (preferred method)
                try
                {
                    _logger.LogInformation("Reloading PostgreSQL configuration via SQL command...");

                    string connectionString = GetConnectionString();
                    using (var connection = new NpgsqlConnection(connectionString))
                    {
                        await connection.OpenAsync();
                        using (var cmd = new NpgsqlCommand("SELECT pg_reload_conf()", connection))
                        {
                            bool result = (bool)await cmd.ExecuteScalarAsync();
                            if (result)
                            {
                                _logger.LogInformation(
                                    "PostgreSQL configuration reloaded successfully via SQL"
                                );
                                return; // Success, no need to try other methods
                            }
                            else
                            {
                                _logger.LogWarning(
                                    "SQL reload function returned false, trying alternate method"
                                );
                            }
                        }
                    }
                }
                catch (Exception sqlEx)
                {
                    _logger.LogWarning(
                        sqlEx,
                        "Error reloading PostgreSQL configuration via SQL, falling back to pg_ctl"
                    );
                }

                // Method 2: Fall back to pg_ctl if SQL method fails
                _logger.LogInformation("Falling back to pg_ctl for configuration reload...");



                // string BinDir = Path.Combine(dataDir, "bin");
                string dataParentDir = Directory.GetParent(dataDir).FullName;
                _logger.LogInformation("Data parent directory: {DataParentDir}", dataParentDir);
                string BinDir = Path.Combine(dataParentDir, "bin");
                string pgCtlPath = Path.Combine(BinDir, "pg_ctl");

                var process = new System.Diagnostics.Process
                {
                    StartInfo = new System.Diagnostics.ProcessStartInfo
                    {
                        FileName = pgCtlPath,
                        Arguments = $"reload -D \"{dataDir}\"",
                        WorkingDirectory = dataParentDir,
                        RedirectStandardOutput = true,
                        RedirectStandardError = true,
                        UseShellExecute = false,
                        CreateNoWindow = true,
                    },
                };

                process.Start();
                string output = await process.StandardOutput.ReadToEndAsync();
                string error = await process.StandardError.ReadToEndAsync();
                await process.WaitForExitAsync();

                if (process.ExitCode == 0)
                {
                    _logger.LogInformation(
                        "PostgreSQL configuration reloaded successfully using pg_ctl"
                    );
                }
                else
                {
                    _logger.LogWarning(
                        "Failed to reload PostgreSQL configuration via pg_ctl: {Error}",
                        error
                    );
                    throw new Exception($"Failed to reload PostgreSQL configuration: {error}");
                }
            }
            catch (Exception ex)
            {
                _logger.LogError(
                    ex,
                    "Error reloading PostgreSQL configuration: {Message}",
                    ex.Message
                );
            }
        }

        /// <summary>
        /// Gets a list of local network subnets in CIDR notation
        /// </summary>
        private List<string> GetLocalNetworkSubnets()
        {
            var result = new List<string>();
            try
            {
                // Get all network interfaces
                NetworkInterface[] interfaces = NetworkInterface.GetAllNetworkInterfaces();

                foreach (NetworkInterface adapter in interfaces)
                {
                    // Only consider up and running adapters that are not loopback or tunnel interfaces
                    if (
                        adapter.OperationalStatus == OperationalStatus.Up
                        && adapter.NetworkInterfaceType != NetworkInterfaceType.Loopback
                        && !adapter.Description.Contains("VPN", StringComparison.OrdinalIgnoreCase)
                        && !adapter.Description.Contains(
                            "Virtual",
                            StringComparison.OrdinalIgnoreCase
                        )
                    )
                    {
                        // Get IP properties for this adapter
                        IPInterfaceProperties properties = adapter.GetIPProperties();

                        foreach (UnicastIPAddressInformation ip in properties.UnicastAddresses)
                        {
                            // Only consider IPv4 addresses
                            if (ip.Address.AddressFamily == AddressFamily.InterNetwork)
                            {
                                // Convert subnet mask to prefix length (CIDR notation)
                                int prefixLength = CountSetBits(ip.IPv4Mask.GetAddressBytes());

                                // Calculate network address
                                byte[] ipBytes = ip.Address.GetAddressBytes();
                                byte[] maskBytes = ip.IPv4Mask.GetAddressBytes();
                                byte[] networkBytes = new byte[4];

                                for (int i = 0; i < 4; i++)
                                {
                                    networkBytes[i] = (byte)(ipBytes[i] & maskBytes[i]);
                                }

                                IPAddress networkAddress = new IPAddress(networkBytes);
                                string subnet = $"{networkAddress}/{prefixLength}";

                                _logger.LogInformation(
                                    "Detected local network: {Interface} - {IP}/{Prefix}",
                                    adapter.Name,
                                    networkAddress,
                                    prefixLength
                                );

                                result.Add(subnet);
                            }
                        }
                    }
                }
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Error detecting local network subnets");
                // Add a default subnet as fallback
                result.Add("192.168.0.0/16");
            }

            // If no subnets were found, add default private network ranges
            if (result.Count == 0)
            {
                _logger.LogWarning(
                    "No local network subnets detected, using default 192.168.0.0/16"
                );
                result.Add("192.168.0.0/16");
            }

            return result;
        }

        /// <summary>
        /// Counts the number of set bits in a subnet mask
        /// </summary>
        private int CountSetBits(byte[] bytes)
        {
            int count = 0;
            foreach (byte b in bytes)
            {
                for (int i = 0; i < 8; i++)
                {
                    if ((b & (1 << (7 - i))) != 0)
                    {
                        count++;
                    }
                }
            }
            return count;
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
            string password = null,
            string host = "127.0.0.1",
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
                    "Found database configuration, attempting to start PostgreSQL server {version} {dataDirectory} {port} {instanceId}",
                    config.PostgresVersion,
                    config.Port,
                    config.InstanceId,
                    config.DataDirectory
                );

                // Start the server with the saved configuration
                await StartAsync(
                    config.PostgresVersion,
                    config.Port,
                    config.InstanceId,
                    config.DataDirectory
                );

                // Advertise the PostgreSQL database service via mDNS
                try
                {
                    _mdnsAdvertiser.AdvertiseDatabaseService(
                        config.InstanceId,
                        config.Port,
                        config.PostgresVersion
                    );
                    _logger.LogInformation(
                        "PostgreSQL database service advertised via mDNS on port {Port} with instance ID {InstanceId}",
                        config.Port,
                        config.InstanceId
                    );
                }
                catch (Exception ex)
                {
                    _logger.LogWarning(
                        ex,
                        "Failed to advertise PostgreSQL database service via mDNS: {Message}",
                        ex.Message
                    );
                    // Continue even if mDNS advertisement fails
                }

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
