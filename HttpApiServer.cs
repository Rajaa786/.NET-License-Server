using System.Collections.Concurrent;
using System.IO;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using MyLanService.Utils;
using Newtonsoft.Json.Linq;
using System.Text.Json.Serialization;
using MyLanService.Middlewares;


namespace MyLanService
{
    public class HttpApiHost
    {
        private readonly int _port;
        private readonly ILogger _logger;
        private WebApplication _app;
        private readonly LicenseStateManager _licenseStateManager;
        private readonly LicenseInfoProvider _licenseInfoProvider;

        private readonly LicenseHelper _licenseHelper;

        private readonly HttpClient _httpClient;
        private readonly string _djangoBaseUrl;


        public HttpApiHost(
            int port,
            ILogger logger,
            LicenseStateManager licenseStateManager,
            LicenseInfoProvider licenseInfoProvider,
            LicenseHelper licenseHelper,
            IConfiguration configuration
        )
        {
            _port = port;
            _logger = logger;
            _httpClient = new HttpClient(
                new HttpClientHandler
                {
                    ServerCertificateCustomValidationCallback = (message, cert, chain, errors) =>
                        true, // Ignore SSL errors for testing purposes
                }
            );
            _httpClient.DefaultRequestHeaders.UserAgent.ParseAdd(
                "CyphersolElectron/1.0 (Windows NT; Win64; x64)"
            );

            _licenseStateManager = licenseStateManager;
            _licenseInfoProvider = licenseInfoProvider;
            _licenseHelper = licenseHelper;
            // var envBaseUrl = Environment.GetEnvironmentVariable("DJANGO_BASEURL");
            // _djangoBaseUrl = string.IsNullOrWhiteSpace(envBaseUrl)
            //     ? "http://localhost:8000"
            //     : envBaseUrl.Trim();

            _djangoBaseUrl = configuration.GetValue<string>("Django:BaseUrl") ?? "http://localhost:8000";


            _logger.LogInformation(
                "Django base URL: {DjangoBaseUrl}",
                _djangoBaseUrl
            );
        }

        public async Task StartAsync(CancellationToken stoppingToken)
        {
            _licenseHelper.GetFingerprint();
            var builder = WebApplication.CreateBuilder();

            builder.Services.AddControllers().AddNewtonsoftJson();

            var app = builder.Build();

            // Register the middleware with its dependencies
            app.Use(async (context, next) =>
            {
                var middleware = new LicenseExpiryMiddleware(
                    next,
                    _logger,
                    _licenseInfoProvider,
                    async () => await TryResyncLicenseAsync(),
                    async () => await ReportClockTamperingAsync()
                );
                await middleware.InvokeAsync(context);
            });

            // ✅ Load encrypted license info & init license manager
            var licenseInfo = _licenseInfoProvider.GetLicenseInfo();
            _logger.LogInformation(
                "License Loaded: {LicenseInfo}",
                licenseInfo?.ToString() ?? "No license info available"
            );

            app.MapGet(
                "/api/health",
                () =>
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
                }
            );

            app.MapPost(
                "/api/license/assign",
                async (HttpContext context) =>
                {
                    return await HandleLicenseAssign(context);
                }
            );

            app.MapPost(
                "/api/license/release",
                async (HttpContext context) =>
                {
                    return await HandleLicenseRelease(context);
                }
            );

            app.MapPost(
                "/api/license/activate-session",
                async (HttpContext context) =>
                {
                    return await HandleActivateSession(context);
                }
            );

            app.MapPost(
                "/api/license/deactivate-session",
                async (HttpContext context) =>
                {
                    return await HandleDeactivateSession(context);
                }
            );

            app.MapGet("/license/status/all", HandleAllLicenseStatus);

            // Endpoint to record the usage of one license statement
            app.MapPost(
                "/api/license/use-statement",
                async (HttpContext context) =>
                {
                    var success = _licenseStateManager.TryUseStatement(out var msg);
                    if (!success)
                    {
                        return Results.BadRequest(
                            new
                            {
                                error = msg,
                                remaining = _licenseStateManager.RemainingStatements,
                                used = _licenseStateManager.CurrentUsedStatements,
                            }
                        );
                    }
                    var responseData = new
                    {
                        success,
                        message = msg,
                        remaining = _licenseStateManager.RemainingStatements,
                        used = _licenseStateManager.CurrentUsedStatements,
                    };

                    return Results.Ok(responseData);
                }
            );

            // Endpoint to check if the statement limit has been reached
            app.MapGet(
                "/api/license/check-statement-limit",
                async (HttpContext context) =>
                {
                    var limitReached = _licenseStateManager.IsStatementLimitReached();
                    var responseData = new
                    {
                        limitReached,
                        remaining = _licenseStateManager.RemainingStatements,
                    };

                    return Results.Ok(responseData);
                }
            );

            app.MapPost(
                "/api/license/validate-session",
                async (HttpContext context) =>
                {
                    var json = await context.Request.ReadFromJsonAsync<
                        Dictionary<string, string>
                    >();
                    if (
                        json == null
                        || !json.TryGetValue("clientId", out var clientId)
                        || string.IsNullOrWhiteSpace(clientId)
                        || !json.TryGetValue("uuid", out var uuid)
                        || string.IsNullOrWhiteSpace(uuid)
                        || !json.TryGetValue("hostname", out var hostname)
                        || string.IsNullOrWhiteSpace(hostname)
                        || !json.TryGetValue("macAddress", out var macAddress)
                        || string.IsNullOrWhiteSpace(macAddress)
                    )
                    {
                        return Results.BadRequest(
                            new { error = "Missing or invalid validation parameters." }
                        );
                    }

                    // var licenseManager = LicenseStateManager.Instance;
                    var message = "";

                    if (_licenseStateManager.IsSessionValid(clientId, uuid, macAddress, hostname))
                    {
                        message = "Session is valid.";
                        return Results.Ok(
                            new
                            {
                                success = true,
                                clientId,
                                message,
                                activeCount = _licenseStateManager.ActiveCount,
                            }
                        );
                    }
                    message = "Session is invalid or expired.";
                    return Results.BadRequest(new { error = message });
                }
            );

            app.MapPost(
                "/api/license/revoke-session",
                async (HttpContext context) =>
                {
                    // Deserialize the incoming JSON to our request DTO.
                    var json = await context.Request.ReadFromJsonAsync<
                        Dictionary<string, string>
                    >();

                    if (
                        json == null
                        || !json.TryGetValue("sessionKey", out var sessionKey)
                        || string.IsNullOrWhiteSpace(sessionKey)
                    )
                    {
                        return Results.BadRequest(
                            new { success = false, error = "Missing or invalid session key." }
                        );
                    }

                    _logger.LogInformation("Revoke session request: {SessionKey}", sessionKey);

                    // Attempt to revoke the inactive session.
                    bool revoked = _licenseStateManager.RevokeInactiveSession(
                        sessionKey,
                        out string message
                    );

                    if (revoked)
                    {
                        return Results.Ok(new { success = true, message });
                    }
                    else
                    {
                        return Results.BadRequest(new { success = false, message });
                    }
                }
            );

            app.MapPost(
                "/api/validate-license",
                async (HttpContext context) =>
                {
                    try
                    {
                        var env = Environment.GetEnvironmentVariable("DOTNET_ENVIRONMENT");
                        string appFolder =
                            (
                                !string.IsNullOrWhiteSpace(env)
                                && env.Equals("Development", StringComparison.OrdinalIgnoreCase)
                            )
                                ? "CyphersolDev" // Use a development-specific folder name
                                : "Cyphersol"; // Use the production folder name

                        var licenseFilePath = _licenseHelper.GetLicenseFilePath(appFolder);

                        byte[] encryptedBytes = null;
                        try
                        {
                            encryptedBytes = await _licenseHelper.ReadBytesAsync(licenseFilePath);
                            _logger.LogInformation(
                                $"Successfully read encrypted license from {licenseFilePath}"
                            );
                        }
                        catch (Exception ex)
                        {
                            _logger.LogError(
                                $"Error reading encrypted license file: {ex.Message}",
                                ex
                            );
                            throw new Exception();
                        }

                        string? decryptedJson = _licenseHelper.GetDecryptedLicense(encryptedBytes);

                        var jsonDoc = JsonDocument.Parse(decryptedJson);
                        var root = jsonDoc.RootElement;

                        var expiryElement = root.GetProperty("expiry_timestamp");
                        long expiryTimestamp = expiryElement.ValueKind switch
                        {
                            JsonValueKind.Number => (long)expiryElement.GetDouble(),
                            JsonValueKind.String
                                when double.TryParse(expiryElement.GetString(), out var val) =>
                                (long)val,
                            _ => throw new FormatException(
                                "expiry_timestamp is not a valid number."
                            ),
                        };
                        long currentTimestamp = DateTimeOffset.UtcNow.ToUnixTimeSeconds();

                        if (expiryTimestamp < currentTimestamp)
                        {
                            return Results.Json(
                                new
                                {
                                    status = "EXPIRED",
                                    message = "License has expired",
                                    expiry_timestamp = expiryTimestamp,
                                    current_timestamp = currentTimestamp,
                                },
                                statusCode: StatusCodes.Status403Forbidden
                            );
                        }

                        return Results.Json(
                            new
                            {
                                status = "OK",
                                license_key = root.GetProperty("license_key").GetString(),
                                number_of_users = root.GetProperty("number_of_users").GetInt32(),
                                number_of_statements = root.GetProperty("number_of_statements")
                                    .GetInt32(),
                                expiry_timestamp = expiryTimestamp,
                                current_timestamp = currentTimestamp,
                            },
                            statusCode: StatusCodes.Status200OK
                        );
                    }
                    catch (FileNotFoundException)
                    {
                        return Results.Json(
                            new
                            {
                                status = "ERROR",
                                message = "License file not found. Please activate the license.",
                            },
                            statusCode: StatusCodes.Status404NotFound
                        );
                    }
                    catch (Exception ex)
                    {
                        _logger.LogError(ex, "Error validating license");
                        return Results.Json(
                            new { status = "ERROR", message = ex.Message },
                            statusCode: StatusCodes.Status500InternalServerError
                        );
                    }
                }
            );

            app.MapPost(
                "/api/activate-license",
                async (HttpContext context) =>
                {
                    try
                    {
                        var json = await context.Request.ReadFromJsonAsync<
                            Dictionary<string, object>
                        >();

                        // Validate incoming Electron data
                        if (
                            !json.ContainsKey("licenseKey")
                            || !json.ContainsKey("role")
                            || !json.ContainsKey("deviceInfo")
                        )
                            return Results.BadRequest(new { error = "Missing required fields" });

                        // Construct Django request payload
                        var djangoPayload = new
                        {
                            license_key = json["licenseKey"],
                            device_info = json["deviceInfo"],
                            timestamp = DateTimeOffset.UtcNow.ToUnixTimeSeconds(),
                            is_activated = true, // or false based on context
                        };

                        var requestBody = new StringContent(
                            JsonSerializer.Serialize(djangoPayload),
                            Encoding.UTF8,
                            "application/json"
                        );

                        var djangoRequest = new HttpRequestMessage
                        {
                            Method = HttpMethod.Post,
                            RequestUri = new Uri(
                                $"{_djangoBaseUrl}/api/activate-offline-license/"
                            ), // Your Django URL
                            Content = requestBody,
                        };

                        // 🔐 Add API key header just for this request
                        djangoRequest.Headers.Add(
                            "X-API-Key",
                            "L4#gP93NEuzyXQFYAGk_KhY2SDHzJJ-O0fqFMlxJ46HZkNLtpdBI.CAgICAgICAk="
                        );

                        var response = await _httpClient.SendAsync(djangoRequest);
                        var resultContent = await response.Content.ReadAsStringAsync();

                        _logger.LogInformation("Django API response: {0}", resultContent);

                        if (response.IsSuccessStatusCode)
                        {
                            var role = json["role"]?.ToString();

                            // ✅ Parse and enrich Django response with role
                            var parsedResult = JsonSerializer.Deserialize<
                                Dictionary<string, object>
                            >(resultContent);
                            parsedResult["role"] = role;

                            // var deviceInfo = json["deviceInfo"] as JObject;

                            var currentTimestamp = parsedResult.ContainsKey("current_timestamp") && parsedResult["current_timestamp"] is JsonElement timestampElement
                                ? timestampElement.GetDouble() // Get the value as a double
                                    : 0.0; // Default value if not present or invalid

                            var expiryTimestamp = parsedResult.ContainsKey("expiry_timestamp") && parsedResult["expiry_timestamp"] is JsonElement expiryElement
                                ? expiryElement.GetDouble() // Get the value as a double
                                : 0.0; // Default value if not present or invalid

                            var numberOfUsers = parsedResult.ContainsKey("number_of_users") && parsedResult["number_of_users"] is JsonElement usersElement
                                ? usersElement.GetInt32() // Get the value as an integer
                                : 0; // Default value if not present or invalid

                            // var licenseInfo = new LicenseInfo
                            // {
                            //     LicenseKey = parsedResult["license_key"]?.ToString(),
                            //     // Username = deviceInfo["username"]?.ToString(),
                            //     CurrentTimestamp = Convert.ToDouble(
                            //         parsedResult["current_timestamp"]
                            //     ),
                            //     ExpiryTimestamp = Convert.ToDouble(
                            //         parsedResult["expiry_timestamp"]
                            //     ),
                            //     NumberOfUsers = Convert.ToInt32(parsedResult["number_of_users"]),
                            //     NumberOfStatements = Convert.ToInt32(
                            //         parsedResult["number_of_statements"]
                            //     ),
                            //     Role = parsedResult["role"]?.ToString(),
                            //     UsedStatements = 0, // Set used statements as needed
                            // };

                            var licenseInfo = new LicenseInfo
                            {
                                LicenseKey = parsedResult["license_key"]?.ToString(),
                                CurrentTimestamp = currentTimestamp,
                                ExpiryTimestamp = expiryTimestamp,
                                NumberOfUsers = numberOfUsers,
                                NumberOfStatements = parsedResult.ContainsKey("number_of_statements") && parsedResult["number_of_statements"] is JsonElement statementsElement
                                ? statementsElement.GetInt32()
                                : 0, // Default value if not present or invalid
                                Role = parsedResult["role"]?.ToString(),
                                UsedStatements = 0, // Set used statements as needed
                                SystemUpTime = Environment.TickCount64
                            };

                            // Serialize enriched response
                            var enrichedJson = JsonSerializer.Serialize(parsedResult);

                            _logger.LogInformation(
                                "Final JSON before encryption: {Json}",
                                enrichedJson
                            );

                            // ✅ Save encrypted license securely
                            // ✅ Determine the base directory based on the environment
                            var env = Environment.GetEnvironmentVariable("DOTNET_ENVIRONMENT");
                            string appFolder =
                                (
                                    !string.IsNullOrWhiteSpace(env)
                                    && env.Equals("Development", StringComparison.OrdinalIgnoreCase)
                                )
                                    ? "CyphersolDev" // Use a development-specific folder name
                                    : "Cyphersol"; // Use the production folder name

                            var licenseFilePath = _licenseHelper.GetLicenseFilePath(appFolder);

                            byte[] encryptedBytes = null;

                            try
                            {
                                encryptedBytes = _licenseHelper.GetEncryptedBytes(enrichedJson);

                                _logger.LogInformation(
                                    $"Successfully encrypted license JSON: {encryptedBytes.Length} bytes"
                                );
                            }
                            catch (Exception ex)
                            {
                                _logger.LogError(
                                    $"Unexpected error while getting encrypted bytes: {ex.Message}",
                                    ex
                                );

                                throw new Exception("Something went wrong");
                            }

                            // Save the encrypted bytes to a file
                            try
                            {
                                await _licenseHelper.WriteBytesAsync(
                                    licenseFilePath,
                                    encryptedBytes
                                );

                                var nencryptedBytes = _licenseHelper.ReadBytesSync(licenseFilePath);

                                var decryptedJson = _licenseHelper.GetDecryptedLicense(
                                    nencryptedBytes
                                );
                                _logger.LogInformation(
                                    "Decrypted JSON for validation: {Json}",
                                    nencryptedBytes.Length,
                                    decryptedJson
                                );
                            }
                            catch (Exception ex)
                            {
                                _logger.LogError(
                                    $"Error saving encrypted license file: {ex.Message}",
                                    ex
                                );
                                throw new Exception("Something went wrong");
                            }

                            _logger.LogInformation(
                                "License information securely saved at {0}",
                                licenseFilePath
                            );

                            _licenseInfoProvider.SetLicenseInfo(licenseInfo);
                            _licenseStateManager._maxLicenses = licenseInfo.NumberOfUsers;
                            _licenseStateManager._currentUsedStatements = licenseInfo.UsedStatements;
                            _licenseStateManager._licenseInfo = _licenseInfoProvider.GetLicenseInfo();

                            _logger.LogInformation("While Activation License info : {0}", _licenseInfoProvider.GetLicenseInfo());


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
                }
            );

            await app.RunAsync($"http://0.0.0.0:{_port}");
            ; // ✅ No URL here, only token
        }

        private async Task<IResult> HandleLicenseAssign(HttpContext context)
        {
            var json = await context.Request.ReadFromJsonAsync<Dictionary<string, string>>();

            _logger.LogInformation("License assign request: {Json}", json);
            if (
                json == null
                || !json.TryGetValue("clientId", out var clientId)
                || string.IsNullOrWhiteSpace(clientId)
                || !json.TryGetValue("uuid", out var uuid)
                || string.IsNullOrWhiteSpace(uuid)
                || !json.TryGetValue("macAddress", out var macAddress)
                || string.IsNullOrWhiteSpace(macAddress)
                || !json.TryGetValue("hostname", out var hostname)
                || string.IsNullOrWhiteSpace(hostname)
                || !json.TryGetValue("username", out var username)
                || string.IsNullOrWhiteSpace(username)
            )
            {
                return Results.BadRequest(
                    new { error = "Missing or invalid license client information.", errorCode = "invalid-parameters" }
                );
            }

            _logger.LogInformation(
                "License assign request: {ClientId}, {UUID}, {MAC}, {Hostname}, {Username}",
                clientId,
                uuid,
                macAddress,
                hostname,
                username
            );

            var licenseInfo = _licenseInfoProvider.GetLicenseInfo();

            _logger.LogInformation("License info: {LicenseInfo}", licenseInfo);

            if (
                licenseInfo == null
                || string.IsNullOrWhiteSpace(licenseInfo.LicenseKey)
                || licenseInfo.ExpiryTimestamp <= 0
                || licenseInfo.NumberOfUsers <= 0
            )
            {
                return Results.Json(
                    new { error = "License not loaded, corrupted, or invalid.", errorCode = "license-not-loaded" },
                    statusCode: StatusCodes.Status500InternalServerError
                );
            }

            // var licenseManager = LicenseStateManager.Instance;

            _logger.LogInformation(
                "License manager active count: {ActiveCount} / {MaxUsers}",
                _licenseStateManager.ActiveCount,
                licenseInfo.NumberOfUsers
            );

            if (
                _licenseStateManager.TryUseLicense(
                    clientId,
                    uuid,
                    macAddress,
                    hostname,
                    username,
                    out var message,
                    out var session
                )
            )
            {
                return Results.Ok(
                    new
                    {
                        success = true,
                        clientId,
                        message,
                        licenseKey = licenseInfo.LicenseKey,
                        licenseExpiry = licenseInfo.ExpiryTimestamp,
                        role = licenseInfo.Role,
                        assignedAt = session!.AssignedAt,
                        lastHeartbeat = session!.LastHeartbeat,
                        activeCount = _licenseStateManager.ActiveCount,
                        maxUsers = licenseInfo.NumberOfUsers,
                    }
                );
            }
            else
            {
                // If all licenses are full and no new license could be assigned,
                // return the list of inactive licenses (Active == false)
                IEnumerable<object> inactiveLicenses = _licenseStateManager.GetInactiveLicensesWithKey();

                if (inactiveLicenses.Any())
                {
                    return Results.Json(
                        new
                        {
                            success = false,
                            error = message,
                            licenseCount = inactiveLicenses.Count(),
                            inactiveLicenses,
                            activeCount = _licenseStateManager.ActiveCount,
                            maxUsers = licenseInfo.NumberOfUsers,
                        },
                        statusCode: StatusCodes.Status429TooManyRequests
                    );
                }
                else
                {
                    // If no inactive licenses, send the list of active sessions
                    var activeLicenses = _licenseStateManager.GetActiveLicensesWithKey();
                    var licenseCount = _licenseStateManager._maxLicenses;

                    return Results.Json(
                        new
                        {
                            success = false,
                            error = "No license available.",
                            licenseCount,
                            activeLicenses,
                            activeCount = _licenseStateManager.ActiveCount,
                            maxUsers = licenseInfo.NumberOfUsers,
                        },
                        statusCode: StatusCodes.Status429TooManyRequests
                    );
                }
            }
        }

        private async Task<IResult> HandleLicenseRelease(HttpContext context)
        {
            var json = await context.Request.ReadFromJsonAsync<Dictionary<string, string>>();
            if (
                json == null
                || !json.TryGetValue("clientId", out var clientId)
                || string.IsNullOrWhiteSpace(clientId)
                || !json.TryGetValue("uuid", out var uuid)
                || string.IsNullOrWhiteSpace(uuid)
                || !json.TryGetValue("hostname", out var hostname)
                || string.IsNullOrWhiteSpace(hostname)
                || !json.TryGetValue("macAddress", out var macAddress)
                || string.IsNullOrWhiteSpace(macAddress)
            )
            {
                return Results.BadRequest(new { error = "Missing or invalid release parameters." });
            }

            // var licenseManager = LicenseStateManager.Instance;

            if (
                _licenseStateManager.ReleaseLicense(
                    clientId,
                    uuid,
                    macAddress,
                    hostname,
                    out var message
                )
            )
            {
                return Results.Ok(
                    new
                    {
                        success = true,
                        clientId,
                        message,
                        activeCount = _licenseStateManager.ActiveCount,
                    }
                );
            }

            return Results.BadRequest(new { error = message });
        }

        private async Task<IResult> HandleActivateSession(HttpContext context)
        {
            var json = await context.Request.ReadFromJsonAsync<Dictionary<string, string>>();
            if (
                json == null
                || !json.TryGetValue("clientId", out var clientId)
                || string.IsNullOrWhiteSpace(clientId)
                || !json.TryGetValue("uuid", out var uuid)
                || string.IsNullOrWhiteSpace(uuid)
                || !json.TryGetValue("macAddress", out var macAddress)
                || string.IsNullOrWhiteSpace(macAddress)
                || !json.TryGetValue("hostname", out var hostname)
                || string.IsNullOrWhiteSpace(hostname)
                || !json.TryGetValue("username", out var username)
                || string.IsNullOrWhiteSpace(username)
            )
            {
                return Results.BadRequest(
                    new
                    {
                        error = "Missing or invalid activation parameters.",
                        errorCode = "invalid-parameters"
                    }
                );
            }

            // var licenseManager = LicenseStateManager.Instance;

            if (
                _licenseStateManager.ActivateSession(
                    clientId,
                    uuid,
                    macAddress,
                    hostname,
                    out var message
                )
            )
            {
                _ = Task.Run(async () =>
                {
                    try
                    {
                        var deviceInfo = new
                        {
                            uuid,
                            macAddress,
                            hostname,
                            clientId,
                            username,
                        };

                        var payload = new
                        {
                            license_key = _licenseInfoProvider.GetLicenseInfo().LicenseKey,
                            device_info = deviceInfo,
                            timestamp = DateTimeOffset.UtcNow.ToUnixTimeSeconds(),
                            event_type = "session-activation",
                        };

                        var requestBody = new StringContent(
                            JsonSerializer.Serialize(payload),
                            Encoding.UTF8,
                            "application/json"
                        );

                        var djangoRequest = new HttpRequestMessage
                        {
                            Method = HttpMethod.Post,
                            RequestUri = new Uri(
                                $"{_djangoBaseUrl}/api/activate-license-session/"
                            ),
                            Content = requestBody,
                        };

                        djangoRequest.Headers.Add(
                            "X-API-Key",
                            "L4#gP93NEuzyXQFYAGk_KhY2SDHzJJ-O0fqFMlxJ46HZkNLtpdBI.CAgICAgICAk="
                        );

                        var response = await _httpClient.SendAsync(djangoRequest);
                        var content = await response.Content.ReadAsStringAsync();

                        if (!response.IsSuccessStatusCode)
                        {
                            _logger.LogWarning("Failed to log session activation: {0}", content);
                        }
                        else
                        {
                            _logger.LogInformation("Session activation logged to Django.");
                        }
                    }
                    catch (Exception ex)
                    {
                        _logger.LogError("Error logging session activation: {0}", ex.Message);
                    }
                });


                var licenseInfo = _licenseInfoProvider.GetLicenseInfo();
                var remainingSeconds = (long)Math.Floor(_licenseHelper.GetRemainingLicenseSeconds(licenseInfo, ReportClockTamperingAsync));

                return Results.Ok(
                    new
                    {
                        success = true,
                        clientId,
                        message,
                        remainingSeconds,
                        activeCount = _licenseStateManager.ActiveCount,
                    }
                );
            }

            return Results.BadRequest(new { error = message, errorCode = "session-not-available" });
        }

        private async Task<IResult> HandleDeactivateSession(HttpContext context)
        {
            var json = await context.Request.ReadFromJsonAsync<Dictionary<string, string>>();
            if (
                json == null
                || !json.TryGetValue("clientId", out var clientId)
                || string.IsNullOrWhiteSpace(clientId)
                || !json.TryGetValue("uuid", out var uuid)
                || string.IsNullOrWhiteSpace(uuid)
                || !json.TryGetValue("macAddress", out var macAddress)
                || string.IsNullOrWhiteSpace(macAddress)
                || !json.TryGetValue("hostname", out var hostname)
                || string.IsNullOrWhiteSpace(hostname)
                || !json.TryGetValue("username", out var username)
                || string.IsNullOrWhiteSpace(username)
            )
            {
                _logger.LogError("Missing or invalid deactivation parameters: {Json}", json);
                return Results.BadRequest(
                    new { error = "Missing or invalid inactivation parameters." }
                );
            }

            // var licenseManager = LicenseStateManager.Instance;

            if (
                _licenseStateManager.InactivateSession(
                    clientId,
                    uuid,
                    macAddress,
                    hostname,
                    out var message
                )
            )
            {
                _ = Task.Run(async () =>
                {
                    try
                    {
                        var deviceInfo = new
                        {
                            uuid,
                            macAddress,
                            hostname,
                            clientId,
                            username,
                        };

                        var payload = new
                        {
                            license_key = _licenseInfoProvider.GetLicenseInfo().LicenseKey,
                            device_info = deviceInfo,
                            timestamp = DateTimeOffset.UtcNow.ToUnixTimeSeconds(),
                            event_type = "session-deactivation",
                        };

                        var requestBody = new StringContent(
                            JsonSerializer.Serialize(payload),
                            Encoding.UTF8,
                            "application/json"
                        );

                        var djangoRequest = new HttpRequestMessage
                        {
                            Method = HttpMethod.Post,
                            RequestUri = new Uri(
                                $"{_djangoBaseUrl}/api/deactivate-license-session/"
                            ),
                            Content = requestBody,
                        };

                        djangoRequest.Headers.Add(
                            "X-API-Key",
                            "L4#gP93NEuzyXQFYAGk_KhY2SDHzJJ-O0fqFMlxJ46HZkNLtpdBI.CAgICAgICAk="
                        );

                        var response = await _httpClient.SendAsync(djangoRequest);
                        var content = await response.Content.ReadAsStringAsync();

                        if (!response.IsSuccessStatusCode)
                        {
                            _logger.LogWarning("Failed to log session deactivation: {0}", content);
                        }
                        else
                        {
                            _logger.LogInformation("Session deactivation logged to Django.");
                        }
                    }
                    catch (Exception ex)
                    {
                        _logger.LogError("Error logging session deactivation: {0}", ex.Message);
                    }
                });

                return Results.Ok(
                    new
                    {
                        success = true,
                        clientId,
                        message,
                        activeCount = _licenseStateManager.ActiveCount,
                    }
                );
            }

            return Results.BadRequest(new { error = message });
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


        public async Task StartLicensePollingAsync(CancellationToken stoppingToken)
        {
            _logger.LogInformation("Starting license polling...");
            await PollLicenseStatusAsync(stoppingToken);
        }

        private async Task PollLicenseStatusAsync(CancellationToken stoppingToken)
        {
            var checkInterval = TimeSpan.FromSeconds(30);

            while (!stoppingToken.IsCancellationRequested)
            {
                try
                {
                    if (await TryResyncLicenseAsync(stoppingToken))
                    {
                        _logger.LogInformation("[License Polling] License resynced successfully.");
                    }
                    else
                    {
                        _logger.LogWarning("[License Polling] Failed to resync license.");
                    }
                }
                catch (Exception ex)
                {
                    _logger.LogError("[License Polling] Error while resyncing license: {0}", ex.Message);
                }


                await Task.Delay(checkInterval, stoppingToken);
            }
        }


        private async Task<bool> ReportClockTamperingAsync()
        {
            try
            {
                var licenseKey = _licenseInfoProvider.GetLicenseInfo().LicenseKey.Trim();
                var deviceInfo = _licenseHelper.GetDeviceInfo();
                var payload = new
                {
                    license_key = licenseKey,
                    device_info = deviceInfo
                };

                string jsonPayload = JsonSerializer.Serialize(payload);
                var content = new StringContent(jsonPayload, Encoding.UTF8, "application/json");

                var apiUrl = $"{_djangoBaseUrl}/api/report-clock-tampering/";

                HttpResponseMessage response = await _httpClient.PostAsync(apiUrl, content);

                if (response.IsSuccessStatusCode)
                {
                    _logger.LogInformation("Clock tampering reported successfully.");
                    return true;
                }
                else
                {
                    string errorResponse = await response.Content.ReadAsStringAsync();
                    _logger.LogError($"Failed to report clock tampering. Status: {response.StatusCode}, Response: {errorResponse}");
                    return false;
                }
            }
            catch (Exception ex)
            {
                _logger.LogError($"Exception while reporting clock tampering: {ex.Message}");
                return false;
            }
        }


        private async Task<bool> TryResyncLicenseAsync(CancellationToken stoppingToken = default)
        {
            try
            {
                // Check if license info is present before making a request
                var licenseInfo = _licenseInfoProvider.GetLicenseInfo();

                if (licenseInfo == null || !licenseInfo.IsValid())
                {
                    _logger.LogWarning("[License Polling] License info is null. Skipping poll.");
                    return false;
                }

                var licenseKey = licenseInfo.LicenseKey;

                if (string.IsNullOrWhiteSpace(licenseKey))
                {
                    _logger.LogWarning("[License Polling] No license key found. Skipping poll.");
                    return false;
                }

                licenseKey = licenseKey.Trim();


                // var deviceInfo = _licenseHelper.GetDeviceInfo();

                var requestData = new
                {
                    license_key = licenseKey,
                    // device_info = deviceInfo
                };

                var json = JsonSerializer.Serialize(requestData);
                var content = new StringContent(json, Encoding.UTF8, "application/json");

                var apiUrl = $"{_djangoBaseUrl}/api/check-license-status/";
                var response = await _httpClient.PostAsync(apiUrl, content, stoppingToken);

                if (response.IsSuccessStatusCode)
                {
                    var responseContent = await response.Content.ReadAsStringAsync();
                    var result = JsonSerializer.Deserialize<LicenseStatusResponse>(responseContent);

                    _logger.LogInformation($"[License Polling] Expiry: {result.ExpiryTimestamp}");

                    _licenseInfoProvider.SetExpiry(result.ExpiryTimestamp);
                    _licenseInfoProvider.SetServerCurrentTime(result.CurrentTimestamp);
                    _licenseInfoProvider.SetSystemUpTime(Environment.TickCount64);

                    _licenseStateManager._licenseInfo = _licenseInfoProvider.GetLicenseInfo();

                    _logger.LogInformation("[License Polling] LicenseInfo: {0}", _licenseStateManager._licenseInfo.ToString());
                    _logger.LogInformation("[License Polling] LicenseInfo: {0}", _licenseInfoProvider.GetLicenseInfo().ToString());

                    return true;
                }
                else
                {
                    _logger.LogWarning($"[License Polling] Status: {response.StatusCode}");
                    return false;
                }

            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "[License Polling] Exception occurred");
                return false;
            }
        }


        private class LicenseStatusResponse
        {
            [JsonPropertyName("license_key")]
            public string LicenseKey { get; set; }

            [JsonPropertyName("expiry_timestamp")]
            public double ExpiryTimestamp { get; set; }

            [JsonPropertyName("current_timestamp")]
            public double CurrentTimestamp { get; set; }
        }


        private async Task<IResult> HandleAllLicenseStatus()
        {
            var sessionsField = typeof(LicenseStateManager).GetField(
                "_activeLicenses",
                System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance
            )!;

            var activeLicenses =
                sessionsField.GetValue(_licenseStateManager)
                as ConcurrentDictionary<string, LicenseSession>;

            var allSessions = activeLicenses!
                .Select(kv => new
                {
                    SessionKey = kv.Key,
                    SessionDetails = new
                    {
                        kv.Value.ClientId,
                        kv.Value.UUID,
                        kv.Value.Hostname,
                        kv.Value.Username,
                        kv.Value.MACAddress,
                        AssignedAt = kv.Value.AssignedAt.ToString("yyyy-MM-dd HH:mm:ss"),
                        LastHeartbeat = kv.Value.LastHeartbeat?.ToString("yyyy-MM-dd HH:mm:ss")
                            ?? "Never",
                        kv.Value.Active,
                    },
                })
                .ToList();

            // If no active sessions, add a sample session for visualization purposes
            // if (allSessions.Count == 0)
            // {
            //     allSessions.Add(
            //         new
            //         {
            //             SessionKey = "sample-session-key-123456",
            //             SessionDetails = new
            //             {
            //                 ClientId = "SAMPLE-CLIENT-001",
            //                 UUID = "550e8400-e29b-41d4-a716-446655440000",
            //                 Hostname = "WORKSTATION-TEST",
            //                 Username = "john.doe",
            //                 MACAddress = "00:1A:2B:3C:4D:5E",
            //                 AssignedAt = DateTime.Now.AddHours(-3).ToString("yyyy-MM-dd HH:mm:ss"),
            //                 LastHeartbeat = DateTime
            //                     .Now.AddMinutes(-5)
            //                     .ToString("yyyy-MM-dd HH:mm:ss"),
            //                 Active = true,
            //             },
            //         }
            //     );
            // }

            // Render HTML with responsive design and modern styling
            var htmlContent =
                $@"
<!DOCTYPE html>
<html lang='en'>
<head>
    <meta charset='UTF-8'>
    <meta name='viewport' content='width=device-width, initial-scale=1.0'>
    <title>License Status Dashboard</title>
    <link href='https://cdnjs.cloudflare.com/ajax/libs/font-awesome/6.0.0/css/all.min.css' rel='stylesheet'>
    <style>
        :root {{
            --primary: #3498db;
            --success: #2ecc71;
            --danger: #e74c3c;
            --warning: #f39c12;
            --dark: #2c3e50;
            --light: #ecf0f1;
        }}
        
        * {{
            margin: 0;
            padding: 0;
            box-sizing: border-box;
            font-family: 'Segoe UI', Tahoma, Geneva, Verdana, sans-serif;
        }}
        
        body {{
            background-color: #f8f9fa;
            color: #333;
            padding: 20px;
        }}
        
        .container {{
            max-width: 1200px;
            margin: 0 auto;
            background-color: white;
            border-radius: 8px;
            box-shadow: 0 0 20px rgba(0, 0, 0, 0.1);
            overflow: hidden;
        }}
        
        header {{
            background-color: var(--primary);
            color: white;
            padding: 20px;
            display: flex;
            justify-content: space-between;
            align-items: center;
        }}
        
        header h1 {{
            font-size: 1.8rem;
            font-weight: 600;
        }}
        
        .stats-container {{
            display: grid;
            grid-template-columns: repeat(auto-fit, minmax(200px, 1fr));
            gap: 20px;
            padding: 20px;
            background-color: #f8f9fa;
        }}
        
        .stat-card {{
            background-color: white;
            padding: 15px;
            border-radius: 8px;
            box-shadow: 0 2px 5px rgba(0, 0, 0, 0.05);
            text-align: center;
        }}
        
        .stat-card h3 {{
            color: var(--dark);
            font-size: 0.9rem;
            margin-bottom: 10px;
        }}
        
        .stat-value {{
            font-size: 1.8rem;
            font-weight: bold;
            color: var(--primary);
        }}
        
        .license-table {{
            width: 100%;
            border-collapse: collapse;
            margin-top: 10px;
        }}
        
        .license-table th, .license-table td {{
            padding: 12px 15px;
            text-align: left;
            border-bottom: 1px solid #e1e1e1;
        }}
        
        .license-table th {{
            background-color: #f8f9fa;
            color: var(--dark);
            font-weight: 600;
            position: sticky;
            top: 0;
        }}
        
        .license-table tbody tr:hover {{
            background-color: #f8f9fa;
        }}
        
        .table-container {{
            padding: 20px;
            overflow-x: auto;
        }}
        
        .status-active {{
            color: var(--success);
            display: inline-flex;
            align-items: center;
            font-weight: 600;
        }}
        
        .status-inactive {{
            color: var(--danger);
            display: inline-flex;
            align-items: center;
            font-weight: 600;
        }}
        
        .status-icon {{
            margin-right: 5px;
        }}
        
        .search-bar {{
            padding: 10px 20px;
            background-color: #f8f9fa;
            border-bottom: 1px solid #e1e1e1;
        }}
        
        .search-input {{
            width: 100%;
            padding: 10px;
            border: 1px solid #ddd;
            border-radius: 4px;
            font-size: 1rem;
        }}
        
        .timestamp {{
            color: #666;
            font-size: 0.85rem;
        }}
        
        .last-update {{
            font-size: 0.8rem;
            color: #666;
            text-align: right;
            padding: 10px 20px;
            border-top: 1px solid #e1e1e1;
        }}
        
        @media (max-width: 768px) {{
            .stats-container {{
                grid-template-columns: 1fr;
            }}
            
            .license-table th, .license-table td {{
                padding: 8px 10px;
            }}
            
            header {{
                flex-direction: column;
                text-align: center;
            }}
            
            header h1 {{
                margin-bottom: 10px;
            }}
        }}
    </style>
</head>
<body>
    <div class='container'>
        <header>
            <h1><i class='fas fa-key'></i> License Status Dashboard</h1>
            <div class='header-controls'>
                <button onclick='window.location.reload()' style='background: white; color: var(--primary); border: none; padding: 8px 15px; border-radius: 4px; cursor: pointer;'>
                    <i class='fas fa-sync-alt'></i> Refresh
                </button>
            </div>
        </header>
        
        <div class='stats-container'>
            <div class='stat-card'>
                <h3>Active Licenses</h3>
                <div class='stat-value'>{allSessions.Count(s => s.SessionDetails.Active)}</div>
            </div>
            <div class='stat-card'>
                <h3>Total Licenses</h3>
                <div class='stat-value'>{allSessions.Count}</div>
            </div>
            <div class='stat-card'>
                <h3>Inactive Licenses</h3>
                <div class='stat-value'>{allSessions.Count(s => !s.SessionDetails.Active)}</div>
            </div>
        </div>
        
        <div class='search-bar'>
            <input type='text' id='searchInput' class='search-input' placeholder='Search by Hostname, Username, Client ID...' onkeyup='searchTable()'>
        </div>
        
        <div class='table-container'>
            <table class='license-table' id='licenseTable'>
                <thead>
                    <tr>
                        <th>Status</th>
                        <th>Hostname</th>
                        <th>Username</th>
                        <th>Client ID</th>
                        <th>UUID</th>
                        <th>MAC Address</th>
                        <th>Assigned At</th>
                        <th>Last Heartbeat</th>
                    </tr>
                </thead>
                <tbody>
                    {string.Join("", allSessions.Select(session => $@"
                    <tr>
                        <td>
                            {(session.SessionDetails.Active
                                ? "<span class='status-active'><i class='fas fa-circle status-icon'></i> Active</span>"
                                : "<span class='status-inactive'><i class='fas fa-circle status-icon'></i> Inactive</span>")}
                        </td>
                        <td>{session.SessionDetails.Hostname}</td>
                        <td>{session.SessionDetails.Username}</td>
                        <td>{session.SessionDetails.ClientId}</td>
                        <td>{session.SessionDetails.UUID}</td>
                        <td>{session.SessionDetails.MACAddress}</td>
                        <td class='timestamp'>{session.SessionDetails.AssignedAt}</td>
                        <td class='timestamp'>{session.SessionDetails.LastHeartbeat}</td>
                    </tr>
                    "))}
                </tbody>
            </table>
        </div>
        
        <div class='last-update'>
            Last updated: {DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss")}
        </div>
    </div>
    
    <script>
        function searchTable() {{
            const input = document.getElementById('searchInput');
            const filter = input.value.toUpperCase();
            const table = document.getElementById('licenseTable');
            const rows = table.getElementsByTagName('tr');
            
            for (let i = 1; i < rows.length; i++) {{
                let found = false;
                const cells = rows[i].getElementsByTagName('td');
                
                for (let j = 0; j < cells.length; j++) {{
                    const cell = cells[j];
                    if (cell) {{
                        const textValue = cell.textContent || cell.innerText;
                        if (textValue.toUpperCase().indexOf(filter) > -1) {{
                            found = true;
                            break;
                        }}
                    }}
                }}
                
                rows[i].style.display = found ? '' : 'none';
            }}
        }}
        
        // Initial page load animations
        document.addEventListener('DOMContentLoaded', function() {{
            const statValues = document.querySelectorAll('.stat-value');
            statValues.forEach(value => {{
                const finalValue = value.innerText;
                value.innerText = '0';
                
                let current = 0;
                const target = parseInt(finalValue);
                const increment = Math.max(1, Math.floor(target / 20));
                
                const timer = setInterval(() => {{
                    current += increment;
                    if (current >= target) {{
                        clearInterval(timer);
                        value.innerText = finalValue;
                    }} else {{
                        value.innerText = current;
                    }}
                }}, 30);
            }});
        }});
    </script>
</body>
</html>
";

            return Results.Content(htmlContent, "text/html");
        }
    }
}
