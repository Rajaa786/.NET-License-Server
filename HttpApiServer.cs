using Microsoft.AspNetCore.Builder;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.AspNetCore.Http;
using System.Text.Json;
using System.Text;
using System.Security.Cryptography;
using System.IO;
using System.Collections.Concurrent;


namespace MyLanService
{
    public class HttpApiHost
    {
        private readonly int _port;
        private readonly ILogger _logger;
        private WebApplication _app;

        private readonly HttpClient _httpClient;

        public HttpApiHost(int port, ILogger logger)
        {
            _port = port;
            _logger = logger;
            _httpClient = new HttpClient(new HttpClientHandler
            {
                ServerCertificateCustomValidationCallback = (message, cert, chain, errors) => true // Ignore SSL errors for testing purposes
            });

        }
        public async Task StartAsync(CancellationToken stoppingToken)
        {
            var builder = WebApplication.CreateBuilder();

            builder.Services
                .AddControllers()
                .AddNewtonsoftJson();

            var app = builder.Build();

            // ✅ Load encrypted license info & init license manager
            var licenseInfo = LicenseInfoProvider.Instance.GetLicenseInfo();
            _logger.LogInformation("License Loaded: MaxUsers = {0}, Key = {1}", licenseInfo.NumberOfUsers, licenseInfo.LicenseKey);

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

            app.MapPost("/api/license/assign", async (HttpContext context) =>
            {
                return await HandleLicenseAssign(context);
            });

            app.MapPost("/api/license/release", async (HttpContext context) =>
            {
                return await HandleLicenseRelease(context);
            });

            app.MapPost("/api/license/activate-session", async (HttpContext context) =>
            {
                return await HandleActivateSession(context);
            });

            app.MapPost("/api/license/inactivate-session", async (HttpContext context) =>
            {
                return await HandleInactivateSession(context);
            });


            app.MapGet("/license/status/all", HandleAllLicenseStatus);

            // Endpoint to record the usage of one license statement
            app.MapPost("/api/license/use-statement", async (HttpContext context) =>
            {
                var success = LicenseStateManager.Instance.TryUseStatement(out var msg);
                var responseData = new
                {
                    success,
                    message = msg,
                    remaining = LicenseStateManager.Instance.RemainingStatements
                };

                return Results.Ok(responseData);
            });

            // Endpoint to check if the statement limit has been reached
            app.MapGet("/api/license/check-statement-limit", async (HttpContext context) =>
            {
                var limitReached = LicenseStateManager.Instance.IsStatementLimitReached();
                var responseData = new
                {
                    limitReached,
                    remaining = LicenseStateManager.Instance.RemainingStatements
                };

                return Results.Ok(responseData);

            });


            app.MapPost("/api/license/validate-session", async (HttpContext context) =>
            {
                var json = await context.Request.ReadFromJsonAsync<Dictionary<string, string>>();
                if (json == null ||
                    !json.TryGetValue("clientId", out var clientId) || string.IsNullOrWhiteSpace(clientId) ||
                    !json.TryGetValue("uuid", out var uuid) || string.IsNullOrWhiteSpace(uuid) ||
                    !json.TryGetValue("hostname", out var hostname) || string.IsNullOrWhiteSpace(hostname) ||
                    !json.TryGetValue("macAddress", out var macAddress) || string.IsNullOrWhiteSpace(macAddress))
                {
                    return Results.BadRequest(new { error = "Missing or invalid validation parameters." });
                }

                var licenseManager = LicenseStateManager.Instance;
                var message = "";

                if (licenseManager.IsSessionValid(clientId, uuid, macAddress, hostname))
                {
                    message = "Session is valid.";
                    return Results.Ok(new
                    {
                        success = true,
                        clientId,
                        message,
                        activeCount = licenseManager.ActiveCount
                    });
                }
                message = "Session is invalid or expired.";
                return Results.BadRequest(new { error = message });

            });


            app.MapPost("/api/validate-license", async (HttpContext context) =>
            {
                try
                {
                    var env = Environment.GetEnvironmentVariable("ASPNETCORE_ENVIRONMENT");
                    string appFolder = (!string.IsNullOrWhiteSpace(env) && env.Equals("Development", StringComparison.OrdinalIgnoreCase))
                        ? "CyphersolDev"    // Use a development-specific folder name
                        : "Cyphersol";  // Use the production folder name

                    string baseDir = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.CommonApplicationData), appFolder);


                    // Ensure the target directory exists in production
                    if (!Directory.Exists(baseDir))
                    {
                        Directory.CreateDirectory(baseDir);
                    }

                    string licenseFilePath = Path.Combine(baseDir, "license.enc");
                    if (!File.Exists(licenseFilePath))
                    {
                        return Results.Json(new
                        {
                            status = "ERROR",
                            message = "License file not found"
                        }, statusCode: StatusCodes.Status404NotFound);
                    }

                    byte[] encryptedBytes = await File.ReadAllBytesAsync(licenseFilePath);

                    string fingerprint = Environment.MachineName + Environment.UserName;
                    using var deriveBytes = new Rfc2898DeriveBytes(fingerprint, Encoding.UTF8.GetBytes("YourSuperSalt!@#"), 100_000, HashAlgorithmName.SHA256);
                    byte[] aesKey = deriveBytes.GetBytes(32);
                    byte[] aesIV = deriveBytes.GetBytes(16);

                    string decryptedJson;

                    using (var aes = Aes.Create())
                    {
                        aes.Key = aesKey;
                        aes.IV = aesIV;
                        aes.Mode = CipherMode.CBC;
                        aes.Padding = PaddingMode.PKCS7;

                        using var decryptor = aes.CreateDecryptor();
                        byte[] plainBytes = decryptor.TransformFinalBlock(encryptedBytes, 0, encryptedBytes.Length);
                        decryptedJson = Encoding.UTF8.GetString(plainBytes);
                    }

                    var jsonDoc = JsonDocument.Parse(decryptedJson);
                    var root = jsonDoc.RootElement;

                    var expiryElement = root.GetProperty("expiry_timestamp");
                    long expiryTimestamp = expiryElement.ValueKind switch
                    {
                        JsonValueKind.Number => (long)expiryElement.GetDouble(),
                        JsonValueKind.String when double.TryParse(expiryElement.GetString(), out var val) => (long)val,
                        _ => throw new FormatException("expiry_timestamp is not a valid number.")
                    };
                    long currentTimestamp = DateTimeOffset.UtcNow.ToUnixTimeSeconds();

                    if (expiryTimestamp < currentTimestamp)
                    {
                        return Results.Json(new
                        {
                            status = "EXPIRED",
                            message = "License has expired",
                            expiry_timestamp = expiryTimestamp,
                            current_timestamp = currentTimestamp
                        }, statusCode: StatusCodes.Status403Forbidden);
                    }

                    return Results.Json(new
                    {
                        status = "OK",
                        license_key = root.GetProperty("license_key").GetString(),
                        number_of_users = root.GetProperty("number_of_users").GetInt32(),
                        number_of_statements = root.GetProperty("number_of_statements").GetInt32(),
                        expiry_timestamp = expiryTimestamp,
                        current_timestamp = currentTimestamp
                    }, statusCode: StatusCodes.Status200OK);
                }
                catch (CryptographicException)
                {
                    return Results.Json(new
                    {
                        status = "ERROR",
                        message = "Decryption failed. Possibly invalid fingerprint or corrupted file."
                    }, statusCode: StatusCodes.Status401Unauthorized);
                }
                catch (Exception ex)
                {
                    _logger.LogError(ex, "Error validating license");
                    return Results.Json(new
                    {
                        status = "ERROR",
                        message = ex.Message
                    }, statusCode: StatusCodes.Status500InternalServerError);
                }
            });



            app.MapPost("/api/activate-license", async (HttpContext context) =>
            {
                try
                {
                    var json = await context.Request.ReadFromJsonAsync<Dictionary<string, object>>();

                    // Validate incoming Electron data
                    if (!json.ContainsKey("licenseKey") || !json.ContainsKey("uuid_hash") || !json.ContainsKey("role"))
                        return Results.BadRequest(new { error = "Missing required fields" });

                    // Construct Django request payload
                    var djangoPayload = new
                    {
                        license_key = json["licenseKey"],
                        uuid_hash = json["uuid_hash"],
                        timestamp = DateTimeOffset.UtcNow.ToUnixTimeSeconds(),
                        is_activated = true // or false based on context
                    };

                    var requestBody = new StringContent(
                        JsonSerializer.Serialize(djangoPayload),
                        Encoding.UTF8,
                        "application/json"
                    );

                    var djangoRequest = new HttpRequestMessage
                    {
                        Method = HttpMethod.Post,
                        RequestUri = new Uri("http://localhost:8000/api/activate-offline-license/"), // Your Django URL
                        Content = requestBody
                    };

                    // 🔐 Add API key header just for this request
                    djangoRequest.Headers.Add("X-API-Key", "L4#gP93NEuzyXQFYAGk_KhY2SDHzJJ-O0fqFMlxJ46HZkNLtpdBI.CAgICAgICAk=");

                    var response = await _httpClient.SendAsync(djangoRequest);
                    var resultContent = await response.Content.ReadAsStringAsync();

                    if (response.IsSuccessStatusCode)
                    {

                        var role = json["role"]?.ToString();

                        // ✅ Parse and enrich Django response with role
                        var parsedResult = JsonSerializer.Deserialize<Dictionary<string, object>>(resultContent);
                        parsedResult["role"] = role;

                        // Serialize enriched response
                        var enrichedJson = JsonSerializer.Serialize(parsedResult);

                        // ✅ Save encrypted license securely
                        // ✅ Determine the base directory based on the environment
                        var env = Environment.GetEnvironmentVariable("ASPNETCORE_ENVIRONMENT");
                        string appFolder = (!string.IsNullOrWhiteSpace(env) && env.Equals("Development", StringComparison.OrdinalIgnoreCase))
                            ? "CyphersolDev"    // Use a development-specific folder name
                            : "Cyphersol";  // Use the production folder name

                        string baseDir = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.CommonApplicationData), appFolder);


                        // Ensure the target directory exists in production
                        if (!Directory.Exists(baseDir))
                        {
                            Directory.CreateDirectory(baseDir);
                        }

                        string licenseFilePath = Path.Combine(baseDir, "license.enc");
                        byte[] plainBytes = Encoding.UTF8.GetBytes(enrichedJson);

                        // 🔐 Generate encryption key from machine-locked fingerprint (not stored anywhere)
                        string fingerprint = Environment.MachineName + Environment.UserName;
                        using var deriveBytes = new Rfc2898DeriveBytes(fingerprint, Encoding.UTF8.GetBytes("YourSuperSalt!@#"), 100_000, HashAlgorithmName.SHA256);
                        byte[] aesKey = deriveBytes.GetBytes(32); // AES-256
                        byte[] aesIV = deriveBytes.GetBytes(16);  // 128-bit IV

                        byte[] encryptedBytes;

                        using (var aes = Aes.Create())
                        {
                            aes.Key = aesKey;
                            aes.IV = aesIV;
                            aes.Mode = CipherMode.CBC;
                            aes.Padding = PaddingMode.PKCS7;

                            using var encryptor = aes.CreateEncryptor();
                            encryptedBytes = encryptor.TransformFinalBlock(plainBytes, 0, plainBytes.Length);
                        }

                        await File.WriteAllBytesAsync(licenseFilePath, encryptedBytes);
                        _logger.LogInformation("License information securely saved at {0}", licenseFilePath);

                        return Results.Ok(JsonDocument.Parse(enrichedJson).RootElement);
                    }
                    _logger.LogError("Django API returned error: {0}", resultContent);

                    return Results.BadRequest(JsonDocument.Parse(resultContent).RootElement);
                }
                catch (Exception ex)
                {
                    _logger.LogError("Error calling Django validation API: {0}", ex.Message);
                    return Results.Problem("Internal Server Error during license validation.");
                }
            });

            await app.RunAsync($"http://0.0.0.0:{_port}"); ; // ✅ No URL here, only token
        }

        private async Task<IResult> HandleLicenseAssign(HttpContext context)
        {

            var json = await context.Request.ReadFromJsonAsync<Dictionary<string, string>>();

            _logger.LogInformation("License assign request: {Json}", json);
            if (json == null ||
                !json.TryGetValue("clientId", out var clientId) || string.IsNullOrWhiteSpace(clientId) ||
                !json.TryGetValue("uuid", out var uuid) || string.IsNullOrWhiteSpace(uuid) ||
                !json.TryGetValue("macAddress", out var macAddress) || string.IsNullOrWhiteSpace(macAddress) ||
                !json.TryGetValue("hostname", out var hostname) || string.IsNullOrWhiteSpace(hostname) ||
                !json.TryGetValue("username", out var username) || string.IsNullOrWhiteSpace(username))
            {
                return Results.BadRequest(new { error = "Missing or invalid license client information." });
            }

            _logger.LogInformation("License assign request: {ClientId}, {UUID}, {MAC}, {Hostname}, {Username}",
                clientId, uuid, macAddress, hostname, username);

            var licenseInfo = LicenseInfoProvider.Instance.GetLicenseInfo();

            _logger.LogInformation("License info: {LicenseInfo}", licenseInfo);

            if (licenseInfo == null ||
                string.IsNullOrWhiteSpace(licenseInfo.LicenseKey) ||
                licenseInfo.ExpiryTimestamp <= 0 ||
                licenseInfo.NumberOfUsers <= 0)
            {
                return Results.Json(
                    new { error = "License not loaded, corrupted, or invalid." },
                    statusCode: StatusCodes.Status500InternalServerError
                );
            }

            var licenseManager = LicenseStateManager.Instance;

            _logger.LogInformation("License manager active count: {ActiveCount} / {MaxUsers}",
                licenseManager.ActiveCount, licenseInfo.NumberOfUsers);

            if (licenseManager.TryUseLicense(clientId, uuid, macAddress, hostname, username, out var message, out var session))
            {
                return Results.Ok(new
                {
                    success = true,
                    clientId,
                    message,
                    licenseExpiry = licenseInfo.ExpiryTimestamp,
                    assignedAt = session!.AssignedAt,
                    lastHeartbeat = session!.LastHeartbeat,
                    activeCount = licenseManager.ActiveCount,
                    maxUsers = licenseInfo.NumberOfUsers
                });
            }
            else
            {
                // If all licenses are full and no new license could be assigned,
                // return the list of inactive licenses (Active == false)
                var inactiveLicenses = licenseManager.GetInactiveLicensesWithKey();
                return Results.Json(new
                {
                    success = false,
                    error = message,
                    inactiveLicenses,
                    activeCount = licenseManager.ActiveCount,
                    maxUsers = licenseInfo.NumberOfUsers
                }, statusCode: StatusCodes.Status429TooManyRequests);
            }
        }


        private async Task<IResult> HandleLicenseRelease(HttpContext context)
        {
            var json = await context.Request.ReadFromJsonAsync<Dictionary<string, string>>();
            if (json == null ||
                !json.TryGetValue("clientId", out var clientId) || string.IsNullOrWhiteSpace(clientId) ||
                !json.TryGetValue("uuid", out var uuid) || string.IsNullOrWhiteSpace(uuid) ||
                !json.TryGetValue("hostname", out var hostname) || string.IsNullOrWhiteSpace(hostname) ||
                !json.TryGetValue("macAddress", out var macAddress) || string.IsNullOrWhiteSpace(macAddress))

            {
                return Results.BadRequest(new { error = "Missing or invalid release parameters." });
            }

            var licenseManager = LicenseStateManager.Instance;

            if (licenseManager.ReleaseLicense(clientId, uuid, macAddress, hostname, out var message))
            {
                return Results.Ok(new
                {
                    success = true,
                    clientId,
                    message,
                    activeCount = licenseManager.ActiveCount
                });
            }

            return Results.BadRequest(new { error = message });
        }


        private async Task<IResult> HandleActivateSession(HttpContext context)
        {
            var json = await context.Request.ReadFromJsonAsync<Dictionary<string, string>>();
            if (json == null ||
                !json.TryGetValue("clientId", out var clientId) || string.IsNullOrWhiteSpace(clientId) ||
                !json.TryGetValue("uuid", out var uuid) || string.IsNullOrWhiteSpace(uuid) ||
                !json.TryGetValue("macAddress", out var macAddress) || string.IsNullOrWhiteSpace(macAddress) ||
                !json.TryGetValue("hostname", out var hostname) || string.IsNullOrWhiteSpace(hostname))
            {
                return Results.BadRequest(new { error = "Missing or invalid activation parameters." });
            }

            var licenseManager = LicenseStateManager.Instance;

            if (licenseManager.ActivateSession(clientId, uuid, macAddress, hostname, out var message))
            {
                return Results.Ok(new
                {
                    success = true,
                    clientId,
                    message,
                    activeCount = licenseManager.ActiveCount
                });
            }

            return Results.BadRequest(new { error = message });
        }


        private async Task<IResult> HandleInactivateSession(HttpContext context)
        {
            var json = await context.Request.ReadFromJsonAsync<Dictionary<string, string>>();
            if (json == null ||
                !json.TryGetValue("clientId", out var clientId) || string.IsNullOrWhiteSpace(clientId) ||
                !json.TryGetValue("uuid", out var uuid) || string.IsNullOrWhiteSpace(uuid) ||
                !json.TryGetValue("macAddress", out var macAddress) || string.IsNullOrWhiteSpace(macAddress) ||
                !json.TryGetValue("hostname", out var hostname) || string.IsNullOrWhiteSpace(hostname))
            {
                return Results.BadRequest(new { error = "Missing or invalid inactivation parameters." });
            }

            var licenseManager = LicenseStateManager.Instance;

            if (licenseManager.InactivateSession(clientId, uuid, macAddress, hostname, out var message))
            {
                return Results.Ok(new
                {
                    success = true,
                    clientId,
                    message,
                    activeCount = licenseManager.ActiveCount
                });
            }

            return Results.BadRequest(new { error = message });
        }

        private async Task<IResult> HandleAllLicenseStatus()
        {
            var licenseManager = LicenseStateManager.Instance;

            var sessionsField = typeof(LicenseStateManager)
                .GetField("_activeLicenses", System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance)!;

            var activeLicenses = sessionsField.GetValue(licenseManager) as ConcurrentDictionary<string, LicenseSession>;

            var allSessions = activeLicenses!.Values.Select(session => new
            {
                session.ClientId,
                session.UUID,
                session.Hostname,
                session.Username,
                session.MACAddress,
                AssignedAt = session.AssignedAt.ToString("yyyy-MM-dd HH:mm:ss"),
                LastHeartbeat = session.LastHeartbeat?.ToString("yyyy-MM-dd HH:mm:ss") ?? "Never",
                session.Active
            }).ToList();

            return Results.Ok(new
            {
                success = true,
                count = allSessions.Count,
                sessions = allSessions
            });
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
