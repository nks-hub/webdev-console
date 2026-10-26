using System.Text.Json;
using CliWrap.Buffered;
using Dapper;
using NKS.WebDevConsole.Core.Interfaces;
using NKS.WebDevConsole.Daemon.Binaries;
using NKS.WebDevConsole.Daemon.Mcp;
using NKS.WebDevConsole.Daemon.Plugin;
using NKS.WebDevConsole.Daemon.Services;

namespace NKS.WebDevConsole.Daemon.Data;

/// <summary>
/// Route registrations for the database explorer surface: /api/databases/*
/// plus the MySQL and PostgreSQL plugin admin endpoints under /api/plugins/.
///
/// Lifted verbatim out of Program.cs. Identifier handling is the security-
/// sensitive part and is unchanged: IsValidDatabaseName allowlists
/// [a-zA-Z0-9_] before any name reaches a CLI arg or SQL string, and the v2
/// driver path quotes identifiers separately.
/// </summary>
internal static class DatabaseEndpoints
{
    // Limits for statements an AI client runs without an approved intent.
    internal const int McpReadOnlyMaxRows = 1000;
    internal const int McpReadOnlyTimeoutSeconds = 15;

    // Intent scopes of the database admin routes. wdc_request_confirmation in
    // services/mcp-server/src/tools/confirmation.ts mints intents with the same
    // (kind, domain, host) triple, or the approved intent is rejected.
    internal const string MySqlRootResetKind = "mysql_root_reset";
    internal const string MySqlUserDropKind = "mysql_user_drop";
    internal const string PostgresRootResetKind = "postgres_root_reset";
    internal const string MySqlIntentHost = "*mysql*";
    internal const string PostgresIntentHost = "*postgres*";
    internal const string MySqlRootIntentDomain = "root";
    internal const string PostgresRootIntentDomain = "postgres";

    internal const string MySqlUserPasswordKind = "mysql_user_password";
    internal const string MySqlUserAdminKind = "mysql_user_admin";

    /// <summary>
    /// Largest SQL dump /api/databases/{name}/import accepts (no settings key
    /// exists for it). The body is streamed to a temp file, never buffered.
    /// </summary>
    internal const long ImportMaxBytes = 512L * 1024 * 1024;

    // Room for multipart boundaries and part headers on top of ImportMaxBytes.
    private const long ImportMultipartOverheadBytes = 1024 * 1024;

    /// <summary>Intent domain of a MySQL account (drop, password reset, admin grant): <c>user@host</c>.</summary>
    internal static string MySqlUserIntentDomain(string userName, string host) => $"{userName}@{host}";

    /// <summary>
    /// Gate for the database administration parts of routes the MCP token may
    /// otherwise always call: MCP-token callers need the operator's
    /// <c>database_admin</c> capability and an approved intent; other callers
    /// pass unless they send X-Intent-Token (see <see cref="IntentGateAsync"/>).
    /// </summary>
    internal static async Task<IResult?> DatabaseAdminGateAsync(
        HttpContext ctx,
        Func<string, bool> isCapabilityEnabled,
        IDeployIntentValidator intentValidator,
        string kind,
        string domain,
        string host,
        string detail)
    {
        if (McpCallerContext.IsMcpToken
            && !isCapabilityEnabled(McpCapabilities.DatabaseAdmin))
        {
            var denial = McpTokenPolicy.CapabilityDisabled(
                McpCapabilities.Get(McpCapabilities.DatabaseAdmin), detail);
            return Results.Json(denial.Body, statusCode: denial.StatusCode);
        }
        return await IntentGateAsync(ctx, intentValidator, kind, domain, host);
    }

    /// <summary>
    /// H2 gate for creating a MySQL account or granting it a preset: an
    /// account for a non-local host, or the admin preset (ALL PRIVILEGES), is
    /// database administration. <paramref name="grantPreset"/> is the preset
    /// the endpoint will apply, or null when it grants nothing.
    /// </summary>
    internal static Task<IResult?> MySqlAccountGateAsync(
        HttpContext ctx,
        Func<string, bool> isCapabilityEnabled,
        IDeployIntentValidator intentValidator,
        string userName,
        string host,
        string? grantPreset)
    {
        if (!MySqlUserHelper.IsElevatedAccount(host, grantPreset))
            return Task.FromResult<IResult?>(null);
        return DatabaseAdminGateAsync(
            ctx, isCapabilityEnabled, intentValidator, MySqlUserAdminKind, MySqlUserIntentDomain(userName, host), MySqlIntentHost,
            $"MySQL account {userName}@{host}" + (grantPreset == "admin" ? " with the admin preset (ALL PRIVILEGES)" : " for a non-local host"));
    }

    /// <summary>
    /// M5: the v2 data browser splices its filter into SQL text. MCP-token
    /// callers get no filter; they filter through the read-only query route.
    /// </summary>
    internal static IResult? McpBrowseFilterRejection(string? where) =>
        McpCallerContext.IsMcpToken && !string.IsNullOrWhiteSpace(where)
            ? Results.Json(new
            {
                error = "mcp_filter_not_allowed",
                detail = "The table browser does not take a filter from the MCP token.",
                hint = "Filter with a SELECT through wdc_query (POST /api/databases/{name}/query), which runs read-only.",
            }, statusCode: StatusCodes.Status403Forbidden)
            : null;

    /// <summary>The upload was larger than the import limit.</summary>
    internal sealed class ImportTooLargeException(long maxBytes)
        : Exception($"The SQL dump is larger than the import limit of {maxBytes / (1024 * 1024)} MB.")
    {
        public long MaxBytes { get; } = maxBytes;
    }

    /// <summary>
    /// Streams an import upload (multipart file part, or the raw body) into
    /// <paramref name="destPath"/> without buffering it in memory. Returns an
    /// error message for a request without a file, or null.
    /// Throws <see cref="ImportTooLargeException"/> past <paramref name="maxBytes"/>.
    /// </summary>
    internal static async Task<string?> ReceiveImportAsync(HttpRequest request, string destPath, long maxBytes, CancellationToken ct)
    {
        if (!request.HasFormContentType)
        {
            await CopySqlToFileAsync(request.Body, destPath, maxBytes, ct);
            return null;
        }

        var mediaType = Microsoft.Net.Http.Headers.MediaTypeHeaderValue.Parse(request.ContentType);
        var boundary = Microsoft.Net.Http.Headers.HeaderUtilities.RemoveQuotes(mediaType.Boundary).Value;
        if (string.IsNullOrEmpty(boundary))
            return "No file uploaded";

        // The size check is CopySqlToFileAsync's; the reader's own per-part
        // limit (128 MB by default) would cut a legitimate dump short.
        var reader = new Microsoft.AspNetCore.WebUtilities.MultipartReader(boundary, request.Body) { BodyLengthLimit = null };
        while (await reader.ReadNextSectionAsync(ct) is { } section)
        {
            if (Microsoft.Net.Http.Headers.ContentDispositionHeaderValue.TryParse(section.ContentDisposition, out var disposition)
                && Microsoft.Net.Http.Headers.ContentDispositionHeaderValueIdentityExtensions.IsFileDisposition(disposition))
            {
                await CopySqlToFileAsync(section.Body, destPath, maxBytes, ct);
                return null;
            }
        }
        return "No file uploaded";
    }

    /// <summary>
    /// Copies an SQL dump to <paramref name="destPath"/> as UTF-8 for the mysql
    /// client, in chunks. A UTF-8 byte order mark is dropped and UTF-16/32 text
    /// (with a byte order mark) is transcoded, as the former StreamReader-based
    /// import did; any other bytes are copied unchanged.
    /// </summary>
    internal static async Task CopySqlToFileAsync(Stream source, string destPath, long maxBytes, CancellationToken ct)
    {
        long total = 0;
        async Task<int> ReadChunkAsync(byte[] buffer, int offset, int count)
        {
            var n = await source.ReadAsync(buffer.AsMemory(offset, count), ct);
            total += n;
            if (total > maxBytes) throw new ImportTooLargeException(maxBytes);
            return n;
        }

        await using var dest = new FileStream(destPath, FileMode.Create, FileAccess.Write, FileShare.None, 81920, useAsync: true);
        var buffer = new byte[81920];

        // Read up to four bytes to recognise a byte order mark.
        var head = 0;
        while (head < 4)
        {
            var n = await ReadChunkAsync(buffer, head, 4 - head);
            if (n == 0) break;
            head += n;
        }

        System.Text.Encoding? transcodeFrom = null;
        var bomLength = 0;
        ReadOnlySpan<byte> h = buffer.AsSpan(0, head);
        if (h.StartsWith(new byte[] { 0xFF, 0xFE, 0x00, 0x00 })) { transcodeFrom = new System.Text.UTF32Encoding(false, false); bomLength = 4; }
        else if (h.StartsWith(new byte[] { 0x00, 0x00, 0xFE, 0xFF })) { transcodeFrom = new System.Text.UTF32Encoding(true, false); bomLength = 4; }
        else if (h.StartsWith(new byte[] { 0xEF, 0xBB, 0xBF })) bomLength = 3;
        else if (h.StartsWith(new byte[] { 0xFF, 0xFE })) { transcodeFrom = System.Text.Encoding.Unicode; bomLength = 2; }
        else if (h.StartsWith(new byte[] { 0xFE, 0xFF })) { transcodeFrom = System.Text.Encoding.BigEndianUnicode; bomLength = 2; }

        if (transcodeFrom is null)
        {
            await dest.WriteAsync(buffer.AsMemory(bomLength, head - bomLength), ct);
            int n;
            while ((n = await ReadChunkAsync(buffer, 0, buffer.Length)) > 0)
                await dest.WriteAsync(buffer.AsMemory(0, n), ct);
            return;
        }

        var decoder = transcodeFrom.GetDecoder();
        await using var writer = new StreamWriter(dest, new System.Text.UTF8Encoding(false), 65536, leaveOpen: true);
        var chars = new char[transcodeFrom.GetMaxCharCount(buffer.Length)];
        var pending = head - bomLength;
        if (pending > 0) Array.Copy(buffer, bomLength, buffer, 0, pending);
        while (true)
        {
            var n = pending > 0 ? pending : await ReadChunkAsync(buffer, 0, buffer.Length);
            pending = 0;
            var charCount = decoder.GetChars(buffer, 0, n, chars, 0, flush: n == 0);
            await writer.WriteAsync(chars.AsMemory(0, charCount), ct);
            if (n == 0) break;
        }
        await writer.FlushAsync(ct);
    }

    /// <summary>
    /// MCP intent gate shared by the database admin routes. Callers holding the
    /// MCP token, or any caller that sends X-Intent-Token, must present an
    /// approved intent for (<paramref name="kind"/>, <paramref name="domain"/>,
    /// <paramref name="host"/>); a typed "YES" from the agent is not consent.
    /// GUI calls without the header pass unchanged. Returns the error response,
    /// or null when the request may proceed.
    /// </summary>
    internal static async Task<IResult?> IntentGateAsync(
        HttpContext ctx,
        IDeployIntentValidator intentValidator,
        string kind,
        string domain,
        string host)
    {
        var intentToken = ctx.Request.Headers["X-Intent-Token"].FirstOrDefault();
        if (string.IsNullOrEmpty(intentToken) && !NKS.WebDevConsole.Daemon.Mcp.McpCallerContext.IsMcpToken)
            return null;

        // The validator ignores X-Allow-Unconfirmed for the MCP token.
        var allowUnconfirmed = string.Equals(
            ctx.Request.Headers["X-Allow-Unconfirmed"].FirstOrDefault(), "true",
            StringComparison.OrdinalIgnoreCase);
        var verdict = await intentValidator.ValidateAndConsumeAsync(
            intentToken ?? "", kind, domain, host, allowUnconfirmed, ctx.RequestAborted);
        return verdict.Ok
            ? null
            : Results.Json(
                new
                {
                    error = "intent_rejected", reason = verdict.Reason, detail = verdict.Detail,
                    intentKind = kind, intentDomain = domain, intentHost = host,
                    hint = $"Ask the user to approve it: call wdc_request_confirmation for intent kind \"{kind}\" " +
                           $"on \"{domain}\", then send the approved intentToken as X-Intent-Token.",
                },
                statusCode: verdict.Reason == "pending_confirmation" ? 425 : 403);
    }

    public static void MapDatabaseEndpoints(
        this WebApplication app,
        PluginLoader pluginLoader,
        Database database,
        JsonSerializerOptions caseInsensitiveJson)
    {
        // F49c: port probe cache. A port that authenticated is cached for the
        // daemon's lifetime; the plugin-default fallback is cached only briefly
        // so a mysqld that comes up after the first probe (startup race,
        // manual start) is found on a later request instead of never.
        int? _cachedMysqlPort = null;
        int? _fallbackMysqlPort = null;
        DateTime _fallbackMysqlPortUntil = DateTime.MinValue;
        object _mysqlPortProbeLock = new();
        var fallbackPortTtl = TimeSpan.FromSeconds(30);

        // Pings one port with the WDC root password. Bounded by a hard 3 s
        // timeout — a non-MySQL listener on the port used to hang the probe
        // (and the request thread blocked on it) indefinitely.
        static async Task<bool> PingMysqlPortAsync(string mysqladmin, int port, string password)
        {
            using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(3));
            try
            {
                var args = new[] { "-h", "127.0.0.1", "-P", port.ToString(), "-u", "root", "--connect-timeout=2", "ping" };
                var env = new Dictionary<string, string?> { ["MYSQL_PWD"] = password };
                var result = await CliWrap.Cli.Wrap(mysqladmin)
                    .WithArguments(args)
                    .WithEnvironmentVariables(env)
                    .WithValidation(CliWrap.CommandResultValidation.None)
                    .ExecuteBufferedAsync(cts.Token)
                    .ConfigureAwait(false);
                // `mysqladmin ping` exits 0 and prints "mysqld is alive" even when
                // authentication fails, so an external mysqld (MAMP, XAMPP) would
                // otherwise win the probe. Reject the port when access was denied.
                return result.ExitCode == 0
                    && result.StandardOutput.Contains("mysqld is alive", StringComparison.OrdinalIgnoreCase)
                    && !result.StandardError.Contains("Access denied", StringComparison.OrdinalIgnoreCase);
            }
            catch
            {
                return false;
            }
        }

        // F49c: Resolve MySQL port with explicit-setting → live-prober → plugin-default
        // fallback chain. Plugin Port default (3306) collides with MAMP on user's machine,
        // so step 2 actively probes a small port sweep with the WDC password: the real
        // WDC mysqld is whichever port authenticates.
        int ResolveMysqlPortWithFallback(SettingsStore settings, IServiceProvider sp, PluginLoader loader, BinaryManager bm)
        {
            // Step 1: explicit user config wins always, no probing.
            if (settings.TryReadMysqlPort(out var configured) && configured > 0)
                return configured;

            // Step 2: probe cache — avoid repeating the probe on every endpoint hit.
            if (_cachedMysqlPort is int cached) return cached;
            if (_fallbackMysqlPort is int recent && DateTime.UtcNow < _fallbackMysqlPortUntil) return recent;

            lock (_mysqlPortProbeLock)
            {
                if (_cachedMysqlPort is int doubleChecked) return doubleChecked;
                if (_fallbackMysqlPort is int recent2 && DateTime.UtcNow < _fallbackMysqlPortUntil) return recent2;

                // Step 3: try port list with WDC root password via mysqladmin ping.
                // Ports are probed in parallel, so the worst case is one 3 s
                // timeout rather than four sequential ones.
                var password = NKS.WebDevConsole.Core.Services.MySqlRootPassword.TryRead();
                var mysql = bm.ListInstalled("mysql").FirstOrDefault();
                var mysqladmin = mysql?.Executable is null ? null : Path.Combine(Path.GetDirectoryName(mysql.Executable)!, OperatingSystem.IsWindows() ? "mysqladmin.exe" : "mysqladmin");
                if (!string.IsNullOrEmpty(password) && mysqladmin is not null && File.Exists(mysqladmin))
                {
                    var candidatePorts = new[] { 3306, 3307, 3308, 3309 };
                    var alive = Task.Run(() => Task.WhenAll(
                            candidatePorts.Select(port => PingMysqlPortAsync(mysqladmin, port, password))))
                        .GetAwaiter().GetResult();
                    for (var i = 0; i < candidatePorts.Length; i++)
                    {
                        if (alive[i])
                        {
                            _cachedMysqlPort = candidatePorts[i];
                            return candidatePorts[i];
                        }
                    }
                }

                // Step 4: fall back to plugin default (3306) — prober exhausted.
                var fallback = 3306;
                try
                {
                    var mysqlPlugin = loader.Plugins.FirstOrDefault(p => p.Instance.Id == "nks.wdc.mysql");
                    if (mysqlPlugin is not null)
                    {
                        var moduleType = mysqlPlugin.Assembly.GetType("NKS.WebDevConsole.Plugin.MySQL.MySqlModule");
                        if (moduleType is not null)
                        {
                            var module = sp.GetService(moduleType);
                            if (module is not null)
                            {
                                var portVal = moduleType.GetProperty("Port")?.GetValue(module);
                                if (portVal is int p && p > 0) fallback = p;
                            }
                        }
                    }
                }
                catch { }

                _fallbackMysqlPort = fallback;
                _fallbackMysqlPortUntil = DateTime.UtcNow + fallbackPortTtl;
                return fallback;
            }
        }

        int ResolvePostgreSqlPortWithFallback(SettingsStore settings, IServiceProvider sp, PluginLoader loader)
        {
            if (settings.TryReadPostgreSqlPort(out var configured) && configured > 0)
                return configured;

            try
            {
                var plugin = loader.Plugins.FirstOrDefault(p => p.Instance.Id == "nks.wdc.postgresql");
                var moduleType = plugin?.Assembly.GetType("NKS.WebDevConsole.Plugin.PostgreSQL.PostgreSqlModule");
                var module = moduleType is null ? null : sp.GetService(moduleType);
                var configField = moduleType?.GetField("_config", System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic);
                var config = module is null ? null : configField?.GetValue(module);
                var portVal = config?.GetType().GetProperty("Port")?.GetValue(config);
                if (portVal is int port && port > 0) return port;
            }
            catch { /* plugin absent or not initialized; use default */ }

            return 5432;
        }

        // Database identifier validation.
        // MySQL identifier rules allow unquoted names matching [a-zA-Z0-9_$] (and $ in some versions).
        // We use a strict subset to avoid shell escape, path traversal, and SQL injection when the
        // name is inlined into CLI args or (ab)used in SQL strings.
        static bool IsValidDatabaseName(string? name)
        {
            if (string.IsNullOrEmpty(name)) return false;
            if (name.Length > 64) return false; // MySQL hard limit
            return System.Text.RegularExpressions.Regex.IsMatch(name, @"^[a-zA-Z0-9_]+$");
        }

        // Build the base argv list for invoking mysql/mysqldump CLI. Reads the
        // daemon-managed root password from MySqlRootPassword (DPAPI on Windows,
        // 0600 plaintext on Unix). Returns a List<string> so callers use the
        // IEnumerable<string> overload of CliWrap.WithArguments() and avoid
        // shell-string parsing ambiguity entirely.
        //
        // Password is INTENTIONALLY NOT placed on the command line — `-p<pass>`
        // would leak the password to `ps aux` / Task Manager process listings.
        // Instead, callers pair this with MysqlEnvVars() which sets MYSQL_PWD,
        // the env var mysql.exe respects to bypass the interactive prompt
        // without exposing the password to other processes on the same host.
        //
        // BUG context: Before this helper, every mysql endpoint hard-coded
        // `-h 127.0.0.1 -P 3306 -u root` with NO password, which broke as soon
        // as the daemon's MySQL plugin set a root password via MySqlRootPassword
        // .EnsureExists() during initial mysqld --initialize-insecure flow. After
        // that point all GUI database operations would fail with "access denied
        // for user root@localhost" and the user had no way to recover via UI.
        static List<string> MysqlBaseArgs(int port = 3306)
        {
            return new List<string>
            {
                "-h", "127.0.0.1",
                "-P", port.ToString(System.Globalization.CultureInfo.InvariantCulture),
                "-u", "root",
            };
        }

        // Environment dictionary to pair with MysqlBaseArgs(). MYSQL_PWD is read
        // by mysql.exe / mysqldump.exe as the root password, bypassing the
        // interactive prompt. MySQL docs warn this is "extremely insecure" for
        // shared / multi-user systems because env vars CAN be inspected via
        // /proc/{pid}/environ, but for a single-user dev workstation it's
        // strictly better than -p<pass> on the command line which is visible
        // in ANY process listing.
        static IReadOnlyDictionary<string, string?> MysqlEnvVars()
        {
            var password = NKS.WebDevConsole.Core.Services.MySqlRootPassword.TryRead();
            return string.IsNullOrEmpty(password)
                ? new Dictionary<string, string?>()
                : new Dictionary<string, string?> { ["MYSQL_PWD"] = password };
        }

        static IReadOnlyDictionary<string, string?> MysqlEnvVarsForPassword(string? password) =>
            string.IsNullOrEmpty(password)
                ? new Dictionary<string, string?>()
                : new Dictionary<string, string?> { ["MYSQL_PWD"] = password };

        static void PersistMysqlRootPassword(string password)
        {
            if (string.IsNullOrEmpty(password))
                NKS.WebDevConsole.Core.Services.MySqlRootPassword.Clear();
            else
                NKS.WebDevConsole.Core.Services.MySqlRootPassword.SetPlaintext(password);
        }

        // PostgreSQL plugin database tooling. The service plugin initializes local
        // clusters with trust auth for 127.0.0.1, so these operations never prompt.
        app.MapGet("/api/plugins/postgresql/databases", async (BinaryManager bm, SettingsStore settings, IServiceProvider sp) =>
        {
            var postgres = bm.ListInstalled("postgresql").FirstOrDefault();
            if (postgres?.Executable is null)
                return Results.Ok(new { error = "PostgreSQL not installed", databases = Array.Empty<string>() });

            var psql = PostgreSqlHelper.ResolveSiblingTool(postgres.Executable, "psql");
            if (psql is null)
                return Results.Ok(new { error = "psql not found next to postgres", databases = Array.Empty<string>() });

            var port = ResolvePostgreSqlPortWithFallback(settings, sp, pluginLoader);
            var args = new[]
            {
                "-h", "127.0.0.1",
                "-p", port.ToString(System.Globalization.CultureInfo.InvariantCulture),
                "-U", "postgres",
                "-d", "postgres",
                "-At",
                "-c", "SELECT datname FROM pg_database WHERE datistemplate = false ORDER BY datname"
            };

            try
            {
                var result = await CliWrap.Cli.Wrap(psql)
                    .WithArguments(args)
                    .WithValidation(CliWrap.CommandResultValidation.None)
                    .ExecuteBufferedAsync();

                if (result.ExitCode != 0)
                    return Results.Ok(new { error = result.StandardError.Trim(), attemptedPort = port, databases = Array.Empty<string>() });

                var dbs = result.StandardOutput
                    .Split('\n', StringSplitOptions.RemoveEmptyEntries)
                    .Select(d => d.Trim())
                    .Where(d => d.Length > 0)
                    .ToList();
                return Results.Ok(new { databases = dbs, attemptedPort = port });
            }
            catch (Exception ex)
            {
                return Results.Ok(new { error = ex.Message, attemptedPort = port, databases = Array.Empty<string>() });
            }
        });

        app.MapPost("/api/plugins/postgresql/reset-password", async (
            HttpContext ctx,
            BinaryManager bm,
            SettingsStore settings,
            IServiceProvider sp,
            ILoggerFactory lf,
            IDeployIntentValidator intentValidator) =>
        {
            if (await IntentGateAsync(ctx, intentValidator, PostgresRootResetKind, PostgresRootIntentDomain, PostgresIntentHost) is { } gate)
                return gate;

            var log = lf.CreateLogger("PostgreSqlResetPassword");
            var body = await ctx.Request.ReadFromJsonAsync<Dictionary<string, string>>(caseInsensitiveJson);
            var newPassword = PostgreSqlHelper.GetPayloadValue(body, "newPassword", "newPwd", "password");

            var validationError = PostgreSqlHelper.ValidatePassword(newPassword);
            if (validationError is not null)
                return Results.BadRequest(new { success = false, error = validationError });

            var postgres = bm.ListInstalled("postgresql").FirstOrDefault();
            if (postgres?.Executable is null)
                return Results.BadRequest(new { success = false, error = "PostgreSQL not installed" });

            var psql = PostgreSqlHelper.ResolveSiblingTool(postgres.Executable, "psql");
            if (psql is null)
                return Results.BadRequest(new { success = false, error = "psql not found next to postgres" });

            var port = ResolvePostgreSqlPortWithFallback(settings, sp, pluginLoader);
            var args = new[]
            {
                "-h", "127.0.0.1",
                "-p", port.ToString(System.Globalization.CultureInfo.InvariantCulture),
                "-U", "postgres",
                "-d", "postgres",
                "-v", "ON_ERROR_STOP=1",
                "-c", PostgreSqlHelper.BuildAlterUserSql(newPassword)
            };

            try
            {
                var result = await CliWrap.Cli.Wrap(psql)
                    .WithArguments(args)
                    .WithValidation(CliWrap.CommandResultValidation.None)
                    .ExecuteBufferedAsync();

                if (result.ExitCode != 0)
                    return Results.BadRequest(new { success = false, error = result.StandardError.Trim(), attemptedPort = port });

                log.LogInformation("PostgreSQL postgres user password reset on port {Port}", port);
                return Results.Ok(new { success = true, attemptedPort = port });
            }
            catch (Exception ex)
            {
                log.LogError(ex, "PostgreSQL password reset failed");
                return Results.Problem(title: "postgresql reset-password failed", detail: ex.Message, statusCode: 500);
            }
        });

        // Databases — list MySQL databases via mysql CLI
        app.MapGet("/api/databases", async (BinaryManager bm, SettingsStore settings, IServiceProvider sp) =>
        {
            var mysql = bm.ListInstalled("mysql").FirstOrDefault();
            if (mysql?.Executable is null)
                return Results.Ok(new { error = "MySQL not installed", databases = Array.Empty<string>() });

            // mysqld lives next to the `mysql` client; name differs by OS.
            var cliName = OperatingSystem.IsWindows() ? "mysql.exe" : "mysql";
            var mysqlCli = Path.Combine(Path.GetDirectoryName(mysql.Executable)!, cliName);
            if (!File.Exists(mysqlCli))
                return Results.Ok(new { error = $"{cliName} not found", databases = Array.Empty<string>() });

            try
            {
                var listArgs = MysqlBaseArgs(ResolveMysqlPortWithFallback(settings, sp, pluginLoader, bm));
                listArgs.Add("-N");
                listArgs.Add("-e");
                listArgs.Add("SHOW DATABASES");
                var result = await CliWrap.Cli.Wrap(mysqlCli)
                    .WithArguments(listArgs)
                    .WithEnvironmentVariables(MysqlEnvVars())
                    .WithValidation(CliWrap.CommandResultValidation.None)
                    .ExecuteBufferedAsync();

                if (result.ExitCode != 0)
                {
                    var stderr = result.StandardError.Trim();
                    var port = ResolveMysqlPortWithFallback(settings, sp, pluginLoader, bm);
                    var hint = "";
                    int? suggestedPort = null;
                    if (stderr.Contains("1045") || stderr.Contains("Access denied"))
                    {
                        hint = $"Port {port} has a mysqld process but WDC root password was rejected. Likely external MySQL (MAMP/XAMPP/Windows service) occupies this port.";
                        // Pick the first free TCP port above the current one — the
                        // frontend renders this as a one-click "Use port N" button
                        // that POSTs /api/databases/use-alt-port.
                        suggestedPort = FindFreeTcpPort(port + 1);
                    }
                    return Results.Ok(new { error = stderr, hint, attemptedPort = port, suggestedPort, databases = Array.Empty<string>() });
                }

                var dbs = result.StandardOutput
                    .Split('\n', StringSplitOptions.RemoveEmptyEntries)
                    .Select(d => d.Trim())
                    .Where(d => d != "information_schema" && d != "performance_schema" && d != "sys" && d != "mysql")
                    .ToList();

                return Results.Ok(new { databases = dbs });
            }
            catch (Exception ex)
            {
                return Results.Ok(new { error = ex.Message, databases = Array.Empty<string>() });
            }
        });

        /// <summary>
        /// Finds the first free TCP port ≥ <paramref name="startPort"/> by binding
        /// a TcpListener on each candidate until one succeeds. Caps the scan at
        /// 64 attempts so a fully-occupied port range doesn't hang the request.
        /// </summary>
        static int? FindFreeTcpPort(int startPort)
        {
            for (int p = Math.Max(1024, startPort); p < startPort + 64 && p <= 65535; p++)
            {
                try
                {
                    var listener = new System.Net.Sockets.TcpListener(System.Net.IPAddress.Loopback, p);
                    listener.Start();
                    listener.Stop();
                    return p;
                }
                catch { /* bound — try next */ }
            }
            return null;
        }

        static async Task StopResidualMySqlProcessesAsync(string mysqldPath, List<string> steps, ILogger log)
        {
            var expectedPath = Path.GetFullPath(mysqldPath);
            foreach (var processName in new[] { "mysqld", "mariadbd" })
            {
                foreach (var process in System.Diagnostics.Process.GetProcessesByName(processName))
                {
                    using (process)
                    {
                        try
                        {
                            if (process.HasExited) continue;
                            var modulePath = process.MainModule?.FileName;
                            if (!string.Equals(
                                    Path.GetFullPath(modulePath ?? ""),
                                    expectedPath,
                                    StringComparison.OrdinalIgnoreCase))
                                continue;

                            var pid = process.Id;
                            log.LogWarning("reset-password: killing residual {ProcessName} PID {Pid}", processName, pid);
                            process.Kill(entireProcessTree: true);
                            await process.WaitForExitAsync().WaitAsync(TimeSpan.FromSeconds(10));
                            steps.Add($"Killed residual {processName} PID={pid}");
                        }
                        catch (Exception ex)
                        {
                            log.LogWarning(ex, "reset-password: failed to inspect/stop residual {ProcessName} PID {Pid}", processName, process.Id);
                            steps.Add($"Warning: residual {processName} PID={process.Id} cleanup failed: {ex.Message}");
                        }
                    }
                }
            }
        }

        // MySQL root password management. GET reports whether a password is stored
        // (without ever returning it). POST accepts a new password + persists into
        // the DPAPI store. The caller is responsible for having run ALTER USER on
        // mysqld itself — this endpoint only syncs WDC's stored copy.
        app.MapGet("/api/databases/root-password", () =>
            Results.Ok(new
            {
                exists = NKS.WebDevConsole.Core.Services.MySqlRootPassword.Exists(),
                passwordless = !NKS.WebDevConsole.Core.Services.MySqlRootPassword.Exists()
            }));

        app.MapPost("/api/databases/root-password", async (HttpContext ctx) =>
        {
            var body = await ctx.Request.ReadFromJsonAsync<Dictionary<string, string>>();
            var password = body?.GetValueOrDefault("password") ?? "";
            try
            {
                PersistMysqlRootPassword(password);
                return Results.Ok(new { stored = !string.IsNullOrEmpty(password), passwordless = string.IsNullOrEmpty(password) });
            }
            catch (Exception ex)
            {
                return Results.Problem(title: "failed to persist password", detail: ex.Message, statusCode: 500);
            }
        });

        // POST /api/plugins/mysql/change-password
        // Verifies currentPwd matches the stored root password, then executes ALTER USER
        // for root@localhost and root@127.0.0.1, persists the new password, and verifies connectivity.
        // With no stored password an empty currentPwd matches, so for the MCP
        // token this is a root reset: database_admin plus a mysql_root_reset intent.
        app.MapPost("/api/plugins/mysql/change-password", async (
            HttpContext ctx,
            BinaryManager bm,
            SettingsStore settings,
            IServiceProvider sp,
            ILoggerFactory lf,
            IDeployIntentValidator intentValidator) =>
        {
            if (await IntentGateAsync(ctx, intentValidator, MySqlRootResetKind, MySqlRootIntentDomain, MySqlIntentHost) is { } gate)
                return gate;

            var log = lf.CreateLogger("MySqlChangePassword");
            var body = await ctx.Request.ReadFromJsonAsync<Dictionary<string, string>>(caseInsensitiveJson);
            var currentPwd = MySqlPasswordHelper.GetPayloadValue(body, "currentPwd", "currentPassword");
            var newPwd = MySqlPasswordHelper.GetPayloadValue(body, "newPwd", "newPassword");

            var validationError = MySqlPasswordHelper.ValidatePassword(newPwd);
            if (validationError is not null)
                return Results.BadRequest(new { success = false, error = validationError });

            // Verify currentPwd matches stored password.
            var stored = NKS.WebDevConsole.Core.Services.MySqlRootPassword.TryRead() ?? "";
            if (currentPwd != stored)
                return Results.BadRequest(new { success = false, error = "current password does not match the stored root password." });

            var mysql = bm.ListInstalled("mysql").FirstOrDefault();
            if (mysql?.Executable is null)
                return Results.BadRequest(new { success = false, error = "MySQL not installed" });

            var mysqlCli = MySqlPasswordHelper.ResolveMysqlCli(mysql.Executable);
            if (mysqlCli is null)
                return Results.BadRequest(new { success = false, error = "mysql CLI not found next to mysqld" });

            var port = ResolveMysqlPortWithFallback(settings, sp, pluginLoader, bm);
            var initFile = "";
            try
            {
                log.LogInformation("change-password: writing ALTER USER init-file for port {Port}", port);
                var sql = MySqlPasswordHelper.BuildAlterUserSql(newPwd);
                initFile = MySqlPasswordHelper.WriteTempInitFile(sql);

                var changeArgs = new List<string>
                {
                    "-h", "127.0.0.1",
                    "-P", port.ToString(System.Globalization.CultureInfo.InvariantCulture),
                    "-u", "root",
                    "--init-command", $"source {initFile}"
                };

                log.LogInformation("change-password: executing ALTER USER via mysql CLI");
                var changeResult = await CliWrap.Cli.Wrap(mysqlCli)
                    .WithArguments(changeArgs)
                    .WithEnvironmentVariables(MysqlEnvVarsForPassword(currentPwd))
                    .WithValidation(CliWrap.CommandResultValidation.None)
                    .ExecuteBufferedAsync();

                if (changeResult.ExitCode != 0)
                {
                    log.LogWarning("change-password: ALTER USER failed (exit {Code}): {Err}",
                        changeResult.ExitCode, changeResult.StandardError.Trim());
                    return Results.BadRequest(new
                    {
                        success = false,
                        error = $"ALTER USER failed: {changeResult.StandardError.Trim()}"
                    });
                }

                log.LogInformation("change-password: ALTER USER succeeded, persisting new password");
                PersistMysqlRootPassword(newPwd);

                // Verify connectivity with new password.
                log.LogInformation("change-password: verifying new password via SELECT 1");
                var verifyArgs = new List<string>
                {
                    "-h", "127.0.0.1",
                    "-P", port.ToString(System.Globalization.CultureInfo.InvariantCulture),
                    "-u", "root",
                    "-N", "-e", "SELECT 1"
                };
                var verifyResult = await CliWrap.Cli.Wrap(mysqlCli)
                    .WithArguments(verifyArgs)
                    .WithEnvironmentVariables(MysqlEnvVarsForPassword(newPwd))
                    .WithValidation(CliWrap.CommandResultValidation.None)
                    .ExecuteBufferedAsync();

                var verified = verifyResult.ExitCode == 0;
                log.LogInformation("change-password: verification {Result}", verified ? "OK" : "FAILED");
                return Results.Ok(new { success = true, verified });
            }
            catch (Exception ex)
            {
                log.LogError(ex, "change-password: unexpected error");
                return Results.Problem(title: "change-password failed", detail: ex.Message, statusCode: 500);
            }
            finally
            {
                if (!string.IsNullOrEmpty(initFile))
                    try { File.Delete(initFile); } catch { /* best effort */ }
            }
        });

        // POST /api/plugins/mysql/reset-password
        // DANGER: resets root password without knowing the current one.
        // Stops mysqld, spawns a skip-grant-tables instance, runs ALTER USER, then
        // restarts the normal mysqld.
        app.MapPost("/api/plugins/mysql/reset-password", async (
            HttpContext ctx,
            BinaryManager bm,
            SettingsStore settings,
            IServiceProvider sp,
            ILoggerFactory lf,
            IDeployIntentValidator intentValidator) =>
        {
            if (await IntentGateAsync(ctx, intentValidator, MySqlRootResetKind, MySqlRootIntentDomain, MySqlIntentHost) is { } gate)
                return gate;

            var log = lf.CreateLogger("MySqlResetPassword");
            var body = await ctx.Request.ReadFromJsonAsync<Dictionary<string, string>>(caseInsensitiveJson);
            var newPwd = MySqlPasswordHelper.GetPayloadValue(body, "newPwd", "newPassword");

            var validationError = MySqlPasswordHelper.ValidatePassword(newPwd);
            if (validationError is not null)
                return Results.BadRequest(new { success = false, error = validationError });

            var mysql = bm.ListInstalled("mysql").FirstOrDefault();
            if (mysql?.Executable is null)
                return Results.BadRequest(new { success = false, error = "MySQL not installed" });

            var mysqldPath = mysql.Executable;
            var mysqlCli = MySqlPasswordHelper.ResolveMysqlCli(mysqldPath);
            var mysqladmin = MySqlPasswordHelper.ResolveMysqladmin(mysqldPath);
            if (mysqlCli is null)
                return Results.BadRequest(new { success = false, error = "mysql CLI not found next to mysqld" });

            var port = ResolveMysqlPortWithFallback(settings, sp, pluginLoader, bm);
            var steps = new List<string>();
            var initFile = "";
            System.Diagnostics.Process? safeProcess = null;

            // Find the MySQL IServiceModule so we can stop/start the managed service.
            var mysqlModule = sp.GetServices<IServiceModule>()
                .FirstOrDefault(m => m.ServiceId.Equals("mysql", StringComparison.OrdinalIgnoreCase));

            try
            {
                // Step 1: stop the normal mysqld.
                steps.Add("Stopping managed mysqld");
                log.LogInformation("reset-password: stopping managed mysqld");
                if (mysqlModule is not null)
                {
                    try { await mysqlModule.StopAsync(CancellationToken.None); }
                    catch (Exception ex) { log.LogWarning(ex, "reset-password: StopAsync threw (continuing)"); }
                }
                await StopResidualMySqlProcessesAsync(mysqldPath, steps, log);
                steps.Add("mysqld stopped");

                // Step 2: write an init-file. MySQL 8.4 disables TCP when
                // --skip-grant-tables is used on Windows, so reset through the normal
                // server bootstrap path instead of a skip-grant TCP session.
                steps.Add("Writing reset init-file");
                log.LogInformation("reset-password: writing reset init-file");
                var sql = MySqlPasswordHelper.BuildAlterUserSql(newPwd);
                initFile = MySqlPasswordHelper.WriteTempInitFile(sql);
                var dataDir = Path.Combine(NKS.WebDevConsole.Core.Services.WdcPaths.DataRoot, "mysql");

                // Step 3: start mysqld normally with --init-file. The init-file is
                // executed during startup before external clients authenticate.
                steps.Add("Starting init-file mysqld");
                log.LogInformation("reset-password: starting init-file mysqld on port {Port}", port);
                var safeArgs = $"--port={port} --datadir=\"{dataDir}\" --init-file=\"{initFile}\" --console";

                var safePsi = new System.Diagnostics.ProcessStartInfo
                {
                    FileName = mysqldPath,
                    Arguments = safeArgs,
                    UseShellExecute = false,
                    CreateNoWindow = true
                };
                safeProcess = System.Diagnostics.Process.Start(safePsi)
                    ?? throw new InvalidOperationException("Failed to start init-file mysqld");

                steps.Add($"init-file mysqld PID={safeProcess.Id}");

                steps.Add($"Waiting for init-file mysqld on port {port}");
                var ready = await MySqlPasswordHelper.WaitForTcpPortAsync(port, TimeSpan.FromSeconds(60), CancellationToken.None);
                if (!ready)
                    throw new TimeoutException($"init-file mysqld did not bind port {port} within 60s");
                steps.Add("init-file mysqld ready");

                // Step 4: verify the init-file changed the password, then shut this
                // bootstrap instance down before starting the managed module normally.
                steps.Add("Verifying init-file password");
                var initVerifyArgs = new List<string>
                {
                    "-h", "127.0.0.1",
                    "-P", port.ToString(System.Globalization.CultureInfo.InvariantCulture),
                    "-u", "root",
                    "-N", "-e", "SELECT 1"
                };
                var initVerifyResult = await CliWrap.Cli.Wrap(mysqlCli)
                    .WithArguments(initVerifyArgs)
                    .WithEnvironmentVariables(MysqlEnvVarsForPassword(newPwd))
                    .WithValidation(CliWrap.CommandResultValidation.None)
                    .ExecuteBufferedAsync();
                if (initVerifyResult.ExitCode != 0)
                    throw new InvalidOperationException($"init-file password verification failed: {initVerifyResult.StandardError.Trim()}");

                steps.Add("Shutting down init-file mysqld");
                log.LogInformation("reset-password: shutting down init-file mysqld");
                if (mysqladmin is not null && File.Exists(mysqladmin))
                {
                    try
                    {
                        await CliWrap.Cli.Wrap(mysqladmin)
                            .WithArguments(new[] { "-h", "127.0.0.1", "-P", port.ToString(), "-u", "root", "shutdown" })
                            .WithEnvironmentVariables(MysqlEnvVarsForPassword(newPwd))
                            .WithValidation(CliWrap.CommandResultValidation.None)
                            .ExecuteAsync();
                    }
                    catch (Exception ex)
                    {
                        log.LogWarning(ex, "reset-password: init-file mysqladmin shutdown failed, killing process");
                    }
                }
                if (safeProcess is not null && !safeProcess.HasExited)
                {
                    safeProcess.Kill(entireProcessTree: true);
                    await safeProcess.WaitForExitAsync();
                }
                steps.Add("init-file mysqld stopped");

                // Step 6: persist new password.
                PersistMysqlRootPassword(newPwd);
                steps.Add(string.IsNullOrEmpty(newPwd) ? "Password store cleared for passwordless root" : "Password persisted to DPAPI store");

                // Step 7: start normal mysqld.
                steps.Add("Starting normal mysqld");
                log.LogInformation("reset-password: starting normal mysqld");
                if (mysqlModule is not null)
                {
                    try { await mysqlModule.StartAsync(CancellationToken.None); }
                    catch (Exception ex)
                    {
                        log.LogWarning(ex, "reset-password: StartAsync threw");
                        steps.Add($"Warning: normal mysqld start error: {ex.Message}");
                    }
                }
                steps.Add("Normal mysqld started");

                // Step 8: verify new password.
                steps.Add("Verifying new password");
                log.LogInformation("reset-password: verifying new password on port {Port}", port);
                var verifyArgs = new List<string>
                {
                    "-h", "127.0.0.1",
                    "-P", port.ToString(System.Globalization.CultureInfo.InvariantCulture),
                    "-u", "root",
                    "-N", "-e", "SELECT 1"
                };
                var verifyResult = await CliWrap.Cli.Wrap(mysqlCli)
                    .WithArguments(verifyArgs)
                    .WithEnvironmentVariables(MysqlEnvVarsForPassword(newPwd))
                    .WithValidation(CliWrap.CommandResultValidation.None)
                    .ExecuteBufferedAsync();

                var verified = verifyResult.ExitCode == 0;
                steps.Add(verified ? "Verification OK" : $"Verification FAILED: {verifyResult.StandardError.Trim()}");
                log.LogInformation("reset-password: verification {Result}", verified ? "OK" : "FAILED");

                return Results.Ok(new { success = true, verified, steps });
            }
            catch (Exception ex)
            {
                log.LogError(ex, "reset-password: failed at step: {LastStep}", steps.LastOrDefault() ?? "unknown");
                steps.Add($"ERROR: {ex.Message}");

                // Always try to restart normal mysqld on failure.
                if (mysqlModule is not null)
                {
                    try
                    {
                        log.LogInformation("reset-password: attempting normal mysqld restart after error");
                        await mysqlModule.StartAsync(CancellationToken.None);
                        steps.Add("Normal mysqld restarted after error");
                    }
                    catch (Exception restartEx)
                    {
                        log.LogWarning(restartEx, "reset-password: restart after error also failed");
                        steps.Add($"Restart after error failed: {restartEx.Message}");
                    }
                }

                return Results.Problem(
                    title: "reset-password failed",
                    detail: ex.Message,
                    statusCode: 500,
                    extensions: new Dictionary<string, object?> { ["steps"] = steps });
            }
            finally
            {
                if (!string.IsNullOrEmpty(initFile))
                    try { File.Delete(initFile); } catch { /* best effort */ }
                if (safeProcess is not null && !safeProcess.HasExited)
                    try { safeProcess.Kill(entireProcessTree: true); } catch { /* best effort */ }
                safeProcess?.Dispose();
            }
        });

        // MySQL user management. These endpoints intentionally use narrow
        // privilege presets and validated account/database names; arbitrary GRANT
        // SQL belongs in the database query console, not the user-management UI.
        app.MapGet("/api/plugins/mysql/users", async (BinaryManager bm, SettingsStore settings, IServiceProvider sp) =>
        {
            var mysql = bm.ListInstalled("mysql").FirstOrDefault();
            if (mysql?.Executable is null)
                return Results.Ok(new { error = "MySQL not installed", users = Array.Empty<object>() });

            var mysqlCli = MySqlPasswordHelper.ResolveMysqlCli(mysql.Executable);
            if (mysqlCli is null)
                return Results.Ok(new { error = "mysql CLI not found next to mysqld", users = Array.Empty<object>() });

            var port = ResolveMysqlPortWithFallback(settings, sp, pluginLoader, bm);
            var args = MysqlBaseArgs(port);
            args.Add("-B");
            args.Add("-N");
            args.Add("-e");
            args.Add(MySqlUserHelper.BuildListUsersSql());

            try
            {
                var result = await CliWrap.Cli.Wrap(mysqlCli)
                    .WithArguments(args)
                    .WithEnvironmentVariables(MysqlEnvVars())
                    .WithValidation(CliWrap.CommandResultValidation.None)
                    .ExecuteBufferedAsync();

                if (result.ExitCode != 0)
                    return Results.Ok(new { error = result.StandardError.Trim(), attemptedPort = port, users = Array.Empty<object>() });

                var users = result.StandardOutput
                    .Split('\n', StringSplitOptions.RemoveEmptyEntries)
                    .Select(line => line.TrimEnd('\r'))
                    .Where(line => line.Length > 0)
                    .Select(line =>
                    {
                        var parts = line.Split('\t');
                        return new
                        {
                            userName = parts.ElementAtOrDefault(0) ?? "",
                            host = parts.ElementAtOrDefault(1) ?? "",
                            plugin = parts.ElementAtOrDefault(2) ?? "",
                            accountLocked = string.Equals(parts.ElementAtOrDefault(3), "Y", StringComparison.OrdinalIgnoreCase),
                            passwordExpired = string.Equals(parts.ElementAtOrDefault(4), "Y", StringComparison.OrdinalIgnoreCase)
                        };
                    })
                    .ToList();

                return Results.Ok(new { users, attemptedPort = port });
            }
            catch (Exception ex)
            {
                return Results.Ok(new { error = ex.Message, attemptedPort = port, users = Array.Empty<object>() });
            }
        });

        app.MapPost("/api/plugins/mysql/users", async (HttpContext ctx, BinaryManager bm, SettingsStore settings, IServiceProvider sp, IDeployIntentValidator intentValidator) =>
        {
            var body = await ctx.Request.ReadFromJsonAsync<Dictionary<string, string>>(caseInsensitiveJson);
            var userName = MySqlPasswordHelper.GetPayloadValue(body, "userName", "user");
            var host = MySqlPasswordHelper.GetPayloadValue(body, "host");
            var password = MySqlPasswordHelper.GetPayloadValue(body, "password", "newPassword", "newPwd");
            var database = MySqlPasswordHelper.GetPayloadValue(body, "database", "databaseName");
            var privileges = MySqlPasswordHelper.GetPayloadValue(body, "privileges", "preset");

            if (MySqlUserHelper.ValidateUserName(userName) is { } userError)
                return Results.BadRequest(new { success = false, error = userError });
            if (MySqlUserHelper.ValidateHost(host) is { } hostError)
                return Results.BadRequest(new { success = false, error = hostError });
            if (MySqlPasswordHelper.ValidatePassword(password) is { } passwordError)
                return Results.BadRequest(new { success = false, error = passwordError });
            if (!string.IsNullOrWhiteSpace(database) && MySqlUserHelper.ValidateDatabaseName(database) is { } dbError)
                return Results.BadRequest(new { success = false, error = dbError });
            var grantPreset = string.IsNullOrWhiteSpace(database) ? null
                : string.IsNullOrWhiteSpace(privileges) ? "readWrite" : privileges;

            if (await MySqlAccountGateAsync(ctx, id => McpCapabilities.IsEnabled(settings, id), intentValidator, userName, host, grantPreset) is { } gate)
                return gate;

            var mysql = bm.ListInstalled("mysql").FirstOrDefault();
            if (mysql?.Executable is null)
                return Results.BadRequest(new { success = false, error = "MySQL not installed" });
            var mysqlCli = MySqlPasswordHelper.ResolveMysqlCli(mysql.Executable);
            if (mysqlCli is null)
                return Results.BadRequest(new { success = false, error = "mysql CLI not found next to mysqld" });

            try
            {
                var sql = MySqlUserHelper.BuildCreateUserSql(userName, host, password);
                if (grantPreset is not null)
                    sql += MySqlUserHelper.BuildGrantDatabaseSql(userName, host, database, grantPreset);

                var args = MysqlBaseArgs(ResolveMysqlPortWithFallback(settings, sp, pluginLoader, bm));
                args.Add("-e");
                args.Add(sql);
                var result = await CliWrap.Cli.Wrap(mysqlCli)
                    .WithArguments(args)
                    .WithEnvironmentVariables(MysqlEnvVars())
                    .WithValidation(CliWrap.CommandResultValidation.None)
                    .ExecuteBufferedAsync();

                return result.ExitCode == 0
                    ? Results.Created("/api/plugins/mysql/users", new { success = true, userName, host })
                    : Results.BadRequest(new { success = false, error = result.StandardError.Trim() });
            }
            catch (Exception ex)
            {
                return Results.BadRequest(new { success = false, error = ex.Message });
            }
        });

        app.MapPost("/api/plugins/mysql/users/password", async (HttpContext ctx, BinaryManager bm, SettingsStore settings, IServiceProvider sp, IDeployIntentValidator intentValidator) =>
        {
            var body = await ctx.Request.ReadFromJsonAsync<Dictionary<string, string>>(caseInsensitiveJson);
            var userName = MySqlPasswordHelper.GetPayloadValue(body, "userName", "user");
            var host = MySqlPasswordHelper.GetPayloadValue(body, "host");
            var password = MySqlPasswordHelper.GetPayloadValue(body, "password", "newPassword", "newPwd");

            if (string.Equals(userName, "root", StringComparison.OrdinalIgnoreCase))
                return Results.BadRequest(new { success = false, error = "Use the root password tab for root accounts." });
            if (MySqlUserHelper.ValidateUserName(userName) is { } userError)
                return Results.BadRequest(new { success = false, error = userError });
            if (MySqlUserHelper.ValidateHost(host) is { } hostError)
                return Results.BadRequest(new { success = false, error = hostError });
            if (MySqlPasswordHelper.ValidatePassword(password) is { } passwordError)
                return Results.BadRequest(new { success = false, error = passwordError });

            // Setting another account's password is an account takeover: the
            // MCP token needs an approved intent for that account.
            if (await IntentGateAsync(ctx, intentValidator, MySqlUserPasswordKind, MySqlUserIntentDomain(userName, host), MySqlIntentHost) is { } gate)
                return gate;

            var mysql = bm.ListInstalled("mysql").FirstOrDefault();
            if (mysql?.Executable is null)
                return Results.BadRequest(new { success = false, error = "MySQL not installed" });
            var mysqlCli = MySqlPasswordHelper.ResolveMysqlCli(mysql.Executable);
            if (mysqlCli is null)
                return Results.BadRequest(new { success = false, error = "mysql CLI not found next to mysqld" });

            var args = MysqlBaseArgs(ResolveMysqlPortWithFallback(settings, sp, pluginLoader, bm));
            args.Add("-e");
            args.Add(MySqlUserHelper.BuildAlterPasswordSql(userName, host, password));
            var result = await CliWrap.Cli.Wrap(mysqlCli)
                .WithArguments(args)
                .WithEnvironmentVariables(MysqlEnvVars())
                .WithValidation(CliWrap.CommandResultValidation.None)
                .ExecuteBufferedAsync();

            return result.ExitCode == 0
                ? Results.Ok(new { success = true, userName, host })
                : Results.BadRequest(new { success = false, error = result.StandardError.Trim() });
        });

        app.MapPost("/api/plugins/mysql/users/grants", async (HttpContext ctx, BinaryManager bm, SettingsStore settings, IServiceProvider sp, IDeployIntentValidator intentValidator) =>
        {
            var body = await ctx.Request.ReadFromJsonAsync<Dictionary<string, string>>(caseInsensitiveJson);
            var userName = MySqlPasswordHelper.GetPayloadValue(body, "userName", "user");
            var host = MySqlPasswordHelper.GetPayloadValue(body, "host");
            var database = MySqlPasswordHelper.GetPayloadValue(body, "database", "databaseName");
            var privileges = MySqlPasswordHelper.GetPayloadValue(body, "privileges", "preset");

            if (MySqlUserHelper.ValidateUserName(userName) is { } userError)
                return Results.BadRequest(new { success = false, error = userError });
            if (MySqlUserHelper.ValidateHost(host) is { } hostError)
                return Results.BadRequest(new { success = false, error = hostError });
            if (MySqlUserHelper.ValidateDatabaseName(database) is { } dbError)
                return Results.BadRequest(new { success = false, error = dbError });
            if (string.IsNullOrWhiteSpace(privileges))
                privileges = "readWrite";

            if (await MySqlAccountGateAsync(ctx, id => McpCapabilities.IsEnabled(settings, id), intentValidator, userName, host, privileges) is { } gate)
                return gate;

            var mysql = bm.ListInstalled("mysql").FirstOrDefault();
            if (mysql?.Executable is null)
                return Results.BadRequest(new { success = false, error = "MySQL not installed" });
            var mysqlCli = MySqlPasswordHelper.ResolveMysqlCli(mysql.Executable);
            if (mysqlCli is null)
                return Results.BadRequest(new { success = false, error = "mysql CLI not found next to mysqld" });

            try
            {
                var args = MysqlBaseArgs(ResolveMysqlPortWithFallback(settings, sp, pluginLoader, bm));
                args.Add("-e");
                args.Add(MySqlUserHelper.BuildGrantDatabaseSql(userName, host, database, privileges));
                var result = await CliWrap.Cli.Wrap(mysqlCli)
                    .WithArguments(args)
                    .WithEnvironmentVariables(MysqlEnvVars())
                    .WithValidation(CliWrap.CommandResultValidation.None)
                    .ExecuteBufferedAsync();

                return result.ExitCode == 0
                    ? Results.Ok(new { success = true, userName, host, database, privileges })
                    : Results.BadRequest(new { success = false, error = result.StandardError.Trim() });
            }
            catch (Exception ex)
            {
                return Results.BadRequest(new { success = false, error = ex.Message });
            }
        });

        app.MapPost("/api/plugins/mysql/users/drop", async (HttpContext ctx, BinaryManager bm, SettingsStore settings, IServiceProvider sp, IDeployIntentValidator intentValidator) =>
        {
            var body = await ctx.Request.ReadFromJsonAsync<Dictionary<string, string>>(caseInsensitiveJson);
            var userName = MySqlPasswordHelper.GetPayloadValue(body, "userName", "user");
            var host = MySqlPasswordHelper.GetPayloadValue(body, "host");

            if (string.Equals(userName, "root", StringComparison.OrdinalIgnoreCase))
                return Results.BadRequest(new { success = false, error = "Root accounts cannot be deleted from user management." });
            if (MySqlUserHelper.ValidateUserName(userName) is { } userError)
                return Results.BadRequest(new { success = false, error = userError });
            if (MySqlUserHelper.ValidateHost(host) is { } hostError)
                return Results.BadRequest(new { success = false, error = hostError });

            if (await IntentGateAsync(ctx, intentValidator, MySqlUserDropKind, MySqlUserIntentDomain(userName, host), MySqlIntentHost) is { } gate)
                return gate;

            var mysql = bm.ListInstalled("mysql").FirstOrDefault();
            if (mysql?.Executable is null)
                return Results.BadRequest(new { success = false, error = "MySQL not installed" });
            var mysqlCli = MySqlPasswordHelper.ResolveMysqlCli(mysql.Executable);
            if (mysqlCli is null)
                return Results.BadRequest(new { success = false, error = "mysql CLI not found next to mysqld" });

            var args = MysqlBaseArgs(ResolveMysqlPortWithFallback(settings, sp, pluginLoader, bm));
            args.Add("-e");
            args.Add(MySqlUserHelper.BuildDropUserSql(userName, host));
            var result = await CliWrap.Cli.Wrap(mysqlCli)
                .WithArguments(args)
                .WithEnvironmentVariables(MysqlEnvVars())
                .WithValidation(CliWrap.CommandResultValidation.None)
                .ExecuteBufferedAsync();

            return result.ExitCode == 0
                ? Results.Ok(new { success = true, userName, host })
                : Results.BadRequest(new { success = false, error = result.StandardError.Trim() });
        });

        // Auto-heal flow: when /api/databases returns a 1045 + suggestedPort, the
        // frontend can POST here to flip ports.mysql to the suggested free port and
        // restart the WDC mysqld so the user doesn't have to dig through Settings.
        app.MapPost("/api/databases/use-alt-port", async (
            HttpContext ctx,
            SettingsStore settings,
            PluginLoader loader,
            CancellationToken ct) =>
        {
            var body = await ctx.Request.ReadFromJsonAsync<Dictionary<string, int>>();
            var newPort = body?.GetValueOrDefault("port") ?? 0;
            if (newPort < 1024 || newPort > 65535)
                return Results.BadRequest(new { error = "port out of range" });

            settings.Set("ports", "mysql", newPort.ToString(System.Globalization.CultureInfo.InvariantCulture));

            // Kick the MySQL / MariaDB plugin to restart on the new port. Lookup is
            // lenient — some installs only carry one of the two.
            var mysql = loader.Plugins.FirstOrDefault(p => p.Instance.Id == "nks.wdc.mysql" || p.Instance.Id == "nks.wdc.mariadb");
            if (mysql?.Instance is IServiceModule svc)
            {
                try { await svc.StopAsync(ct); } catch { /* already stopped is fine */ }
                try { await svc.StartAsync(ct); } catch { /* surface via next /api/databases probe */ }
            }
            return Results.Ok(new { port = newPort, restarted = mysql is not null });
        });

        // Create database
        app.MapPost("/api/databases", async (HttpContext ctx, BinaryManager bm, SettingsStore settings, IServiceProvider sp) =>
        {
            var body = await ctx.Request.ReadFromJsonAsync<Dictionary<string, string>>();
            var dbName = body?.GetValueOrDefault("name") ?? "";
            if (!IsValidDatabaseName(dbName))
                return Results.BadRequest(new { error = "Invalid database name — allowed chars: letters, digits, underscore (max 64 chars)" });

            var mysql = bm.ListInstalled("mysql").FirstOrDefault();
            if (mysql?.Executable is null)
                return Results.BadRequest(new { error = "MySQL not installed" });

            var mysqlCli = Path.Combine(Path.GetDirectoryName(mysql.Executable)!, OperatingSystem.IsWindows() ? "mysql.exe" : "mysql");
            var args = MysqlBaseArgs(ResolveMysqlPortWithFallback(settings, sp, pluginLoader, bm));
            args.Add("-e");
            args.Add($"CREATE DATABASE IF NOT EXISTS `{dbName}`");
            try
            {
                var result = await CliWrap.Cli.Wrap(mysqlCli)
                    .WithArguments(args)
                    .WithEnvironmentVariables(MysqlEnvVars())
                    .WithValidation(CliWrap.CommandResultValidation.None)
                    .ExecuteBufferedAsync();
                return result.ExitCode == 0
                    ? Results.Created($"/api/databases/{dbName}", new { name = dbName })
                    : Results.BadRequest(new { error = result.StandardError.Trim() });
            }
            catch (Exception ex)
            {
                return Results.BadRequest(new { error = $"Failed to invoke mysql: {ex.Message}" });
            }
        });

        // Drop database — MCP intent-gated under kind=database_drop. An AI
        // with a wildcard session grant could otherwise chain DROP DATABASE
        // against every site as a single irreversible action; the gate forces
        // the same intent + always-confirm pipeline that protects deploy
        // kinds. Header-driven (X-Intent-Token) so the GUI delete button
        // (which doesn't set the header) keeps working unchanged — only AI
        // callers that DON'T already hold a GUI session pay the gate cost.
        app.MapDelete("/api/databases/{name}", async (
            string name,
            BinaryManager bm,
            SettingsStore settings,
            IServiceProvider sp,
            NKS.WebDevConsole.Core.Interfaces.IDeployIntentValidator intentValidator,
            HttpContext ctx,
            CancellationToken ct) =>
        {
            if (!IsValidDatabaseName(name))
                return Results.BadRequest(new { error = "Invalid database name" });

            // MCP intent gate. Mirrors the test_hook / settings_write gates in
            // the deploy plugin: only validates if a token is present, so direct
            // GUI calls flow through unchanged. Bound to the synthetic host
            // marker "*db*" since database operations aren't tied to a deploy
            // host — keeps a deploy intent from being reused as a database
            // intent and vice versa.
            var dbIntentToken = ctx.Request.Headers["X-Intent-Token"].FirstOrDefault();
            if (!string.IsNullOrEmpty(dbIntentToken) || NKS.WebDevConsole.Daemon.Mcp.McpCallerContext.IsMcpToken)
            {
                var dbAllowUnconfirmed = string.Equals(
                    ctx.Request.Headers["X-Allow-Unconfirmed"].FirstOrDefault(), "true",
                    StringComparison.OrdinalIgnoreCase);
                var dbVerdict = await intentValidator.ValidateAndConsumeAsync(
                    dbIntentToken ?? "", "database_drop", domain: name, host: "*db*", dbAllowUnconfirmed, ct);
                if (!dbVerdict.Ok)
                    return Results.Json(
                        new { error = "intent_rejected", reason = dbVerdict.Reason, detail = dbVerdict.Detail },
                        statusCode: dbVerdict.Reason == "pending_confirmation" ? 425 : 403);
            }

            var mysql = bm.ListInstalled("mysql").FirstOrDefault();
            if (mysql?.Executable is null)
                return Results.BadRequest(new { error = "MySQL not installed" });

            var mysqlCli = Path.Combine(Path.GetDirectoryName(mysql.Executable)!, OperatingSystem.IsWindows() ? "mysql.exe" : "mysql");
            var args = MysqlBaseArgs(ResolveMysqlPortWithFallback(settings, sp, pluginLoader, bm));
            args.Add("-e");
            args.Add($"DROP DATABASE IF EXISTS `{name}`");
            try
            {
                var result = await CliWrap.Cli.Wrap(mysqlCli)
                    .WithArguments(args)
                    .WithEnvironmentVariables(MysqlEnvVars())
                    .WithValidation(CliWrap.CommandResultValidation.None)
                    .ExecuteBufferedAsync(ct);
                return result.ExitCode == 0
                    ? Results.NoContent()
                    : Results.BadRequest(new { error = result.StandardError.Trim() });
            }
            catch (Exception ex)
            {
                return Results.BadRequest(new { error = $"Failed to invoke mysql: {ex.Message}" });
            }
        });

        // Database tables
        app.MapGet("/api/databases/{name}/tables", async (string name, BinaryManager bm, SettingsStore settings, IServiceProvider sp) =>
        {
            if (!IsValidDatabaseName(name))
                return Results.BadRequest(new { error = "Invalid database name" });
            var mysql = bm.ListInstalled("mysql").FirstOrDefault();
            if (mysql?.Executable is null)
                return Results.BadRequest(new { error = "MySQL not installed" });
            var mysqlCli = Path.Combine(Path.GetDirectoryName(mysql.Executable)!, OperatingSystem.IsWindows() ? "mysql.exe" : "mysql");
            var args = MysqlBaseArgs(ResolveMysqlPortWithFallback(settings, sp, pluginLoader, bm));
            args.Add("-N");
            args.Add("-e");
            args.Add($"SELECT TABLE_NAME, TABLE_ROWS, ROUND(((DATA_LENGTH + INDEX_LENGTH) / 1024 / 1024), 2) AS size_mb FROM information_schema.TABLES WHERE TABLE_SCHEMA = '{name}' ORDER BY TABLE_NAME");
            CliWrap.Buffered.BufferedCommandResult result;
            try
            {
                result = await CliWrap.Cli.Wrap(mysqlCli)
                    .WithArguments(args)
                    .WithEnvironmentVariables(MysqlEnvVars())
                    .WithValidation(CliWrap.CommandResultValidation.None)
                    .ExecuteBufferedAsync();
            }
            catch (Exception ex)
            {
                return Results.BadRequest(new { error = $"Failed to invoke mysql: {ex.Message}" });
            }
            if (result.ExitCode != 0)
                return Results.BadRequest(new { error = result.StandardError.Trim() });
            var tables = result.StandardOutput.Trim()
                .Split('\n', StringSplitOptions.RemoveEmptyEntries)
                .Select(line =>
                {
                    var parts = line.Split('\t');
                    return new { name = parts[0], rows = parts.Length > 1 ? parts[1] : "0", size = parts.Length > 2 ? parts[2] + " MB" : "0 MB" };
                }).ToList();
            return Results.Ok(new { tables });
        });

        // Database size
        app.MapGet("/api/databases/{name}/size", async (string name, BinaryManager bm, SettingsStore settings, IServiceProvider sp) =>
        {
            if (!IsValidDatabaseName(name))
                return Results.BadRequest(new { error = "Invalid database name" });
            var mysql = bm.ListInstalled("mysql").FirstOrDefault();
            if (mysql?.Executable is null)
                return Results.BadRequest(new { error = "MySQL not installed" });
            var mysqlCli = Path.Combine(Path.GetDirectoryName(mysql.Executable)!, OperatingSystem.IsWindows() ? "mysql.exe" : "mysql");
            var args = MysqlBaseArgs(ResolveMysqlPortWithFallback(settings, sp, pluginLoader, bm));
            args.Add("-N");
            args.Add("-e");
            args.Add($"SELECT ROUND(SUM(DATA_LENGTH + INDEX_LENGTH) / 1024 / 1024, 2) FROM information_schema.TABLES WHERE TABLE_SCHEMA = '{name}'");
            try
            {
                var result = await CliWrap.Cli.Wrap(mysqlCli)
                    .WithArguments(args)
                    .WithEnvironmentVariables(MysqlEnvVars())
                    .WithValidation(CliWrap.CommandResultValidation.None)
                    .ExecuteBufferedAsync();
                if (result.ExitCode != 0)
                    return Results.BadRequest(new { error = result.StandardError.Trim() });
                return Results.Ok(new { size = result.StandardOutput.Trim() + " MB" });
            }
            catch (Exception ex)
            {
                return Results.BadRequest(new { error = $"Failed to invoke mysql: {ex.Message}" });
            }
        });

        // Database query execution
        // MCP intent-gated under kind=database_query. The endpoint accepts any
        // SQL — SELECT for the GUI database explorer is the common case, but
        // nothing prevents DROP/DELETE/TRUNCATE from being submitted. An AI
        // with a wildcard grant + this endpoint ungated could chain destructive
        // SQL as a single action; the gate forces the same intent + always-
        // confirm pipeline. Header-driven so the GUI explorer stays untouched.
        app.MapPost("/api/databases/{name}/query", async (
            string name,
            HttpContext ctx,
            BinaryManager bm,
            SettingsStore settings,
            IServiceProvider sp,
            NKS.WebDevConsole.Core.Interfaces.IDeployIntentValidator intentValidator,
            CancellationToken ct) =>
        {
            if (!IsValidDatabaseName(name))
                return Results.BadRequest(new { error = "Invalid database name" });

            var body = await ctx.Request.ReadFromJsonAsync<Dictionary<string, string>>(ct);
            var sql = body?.GetValueOrDefault("sql") ?? "";
            if (string.IsNullOrWhiteSpace(sql))
                return Results.BadRequest(new { error = "sql required" });

            // An MCP-token caller (AI agent) runs read-only statements freely;
            // anything the guard cannot prove read-only needs a confirmed intent.
            var dbqIntentToken = ctx.Request.Headers["X-Intent-Token"].FirstOrDefault();
            if (!string.IsNullOrEmpty(dbqIntentToken)
                || (NKS.WebDevConsole.Daemon.Mcp.McpCallerContext.IsMcpToken && !NKS.WebDevConsole.Daemon.Mcp.SqlReadOnlyGuard.IsReadOnly(sql)))
            {
                var dbqAllowUnconfirmed = string.Equals(
                    ctx.Request.Headers["X-Allow-Unconfirmed"].FirstOrDefault(), "true",
                    StringComparison.OrdinalIgnoreCase);
                var dbqVerdict = await intentValidator.ValidateAndConsumeAsync(
                    dbqIntentToken ?? "", "database_query", domain: name, host: "*db*", dbqAllowUnconfirmed, ct);
                if (!dbqVerdict.Ok)
                    return Results.Json(
                        new { error = "intent_rejected", reason = dbqVerdict.Reason, detail = dbqVerdict.Detail },
                        statusCode: dbqVerdict.Reason == "pending_confirmation" ? 425 : 403);
            }

            // AI client (MCP token): never through the mysql CLI, which also
            // runs its own client commands (system, source, ...) that are not
            // SQL. Without an approved intent the guard above let the statement
            // through as read-only; run it so that the server enforces that
            // too (read-only transaction, single statement, time limit, row
            // cap). With an approved intent it runs through the driver as is.
            if (NKS.WebDevConsole.Daemon.Mcp.McpCallerContext.IsMcpToken)
            {
                static object Shape(QueryResultSet set, bool truncated)
                {
                    var headers = set.Columns.Select(c => c.Name).ToArray();
                    var shaped = set.Rows.Select(r =>
                    {
                        var row = new Dictionary<string, string?>();
                        for (int i = 0; i < headers.Length && i < r.Count; i++)
                            row[headers[i]] = r[i]?.ToString();
                        return row;
                    }).ToList();
                    return new { columns = headers, rows = shaped, rowCount = shaped.Count, truncated };
                }

                try
                {
                    if (string.IsNullOrEmpty(dbqIntentToken))
                    {
                        var ro = await MakeMySqlDriverV2(settings, sp, bm).ExecuteReadOnlyQueryAsync(
                            name, sql, McpReadOnlyMaxRows, McpReadOnlyTimeoutSeconds, ct);
                        return Results.Ok(Shape(ro.Set, ro.Truncated));
                    }

                    var full = await MakeMySqlDriverV2(settings, sp, bm).ExecuteQueryAsync(name, sql, ct);
                    var withRows = full.Results.FirstOrDefault(r => r.Columns.Count > 0);
                    return withRows is not null
                        ? Results.Ok(Shape(withRows, truncated: false))
                        : Results.Ok(new
                        {
                            rows = Array.Empty<object>(),
                            rowsAffected = full.Results.Sum(r => r.RowsAffected),
                            message = "Query executed successfully (no output)",
                        });
                }
                catch (MySqlConnector.MySqlException mex)
                {
                    return Results.BadRequest(new { error = mex.Message, code = mex.Number, sqlState = mex.SqlState });
                }
                catch (Exception ex)
                {
                    return Results.BadRequest(new { error = ex.Message });
                }
            }
            var mysql = bm.ListInstalled("mysql").FirstOrDefault();
            if (mysql?.Executable is null)
                return Results.BadRequest(new { error = "MySQL not installed" });
            var mysqlCli = Path.Combine(Path.GetDirectoryName(mysql.Executable)!, OperatingSystem.IsWindows() ? "mysql.exe" : "mysql");
            var args = MysqlBaseArgs(ResolveMysqlPortWithFallback(settings, sp, pluginLoader, bm));
            args.Add(name);
            args.Add("-e");
            args.Add(sql);
            CliWrap.Buffered.BufferedCommandResult result;
            try
            {
                result = await CliWrap.Cli.Wrap(mysqlCli)
                    .WithArguments(args)
                    .WithEnvironmentVariables(MysqlEnvVars())
                    .WithValidation(CliWrap.CommandResultValidation.None)
                    .ExecuteBufferedAsync();
            }
            catch (Exception ex)
            {
                return Results.BadRequest(new { error = $"Failed to invoke mysql: {ex.Message}" });
            }
            if (result.ExitCode != 0)
                return Results.BadRequest(new { error = result.StandardError.Trim() });
            // Parse tab-separated output to JSON
            var lines = result.StandardOutput.Trim().Split('\n', StringSplitOptions.RemoveEmptyEntries);
            if (lines.Length == 0)
                return Results.Ok(new { rows = Array.Empty<object>(), message = "Query executed successfully (no output)" });
            var headers = lines[0].Split('\t').Select(h => h.Trim()).ToArray();
            var rows = lines.Skip(1).Select(l =>
            {
                var vals = l.Split('\t').Select(v => v.Trim()).ToArray();
                var row = new Dictionary<string, string>();
                for (int i = 0; i < headers.Length && i < vals.Length; i++)
                    row[headers[i]] = vals[i];
                return row;
            }).ToList();
            return Results.Ok(new { columns = headers, rows, rowCount = rows.Count });
        });

        // Database export (mysqldump). The dump is buffered in memory before it
        // is sent; fine for development databases, but a very large database
        // would need the mysqldump output streamed to the response instead.
        app.MapGet("/api/databases/{name}/export", async (string name, BinaryManager bm, SettingsStore settings, IServiceProvider sp) =>
        {
            if (!IsValidDatabaseName(name))
                return Results.BadRequest(new { error = "Invalid database name" });
            var mysql = bm.ListInstalled("mysql").FirstOrDefault();
            if (mysql?.Executable is null)
                return Results.BadRequest(new { error = "MySQL not installed" });
            var mysqldump = Path.Combine(Path.GetDirectoryName(mysql.Executable)!, OperatingSystem.IsWindows() ? "mysqldump.exe" : "mysqldump");
            if (!File.Exists(mysqldump))
                return Results.BadRequest(new { error = "mysqldump.exe not found" });
            var args = MysqlBaseArgs(ResolveMysqlPortWithFallback(settings, sp, pluginLoader, bm));
            args.Add(name);
            try
            {
                var result = await CliWrap.Cli.Wrap(mysqldump)
                    .WithArguments(args)
                    .WithEnvironmentVariables(MysqlEnvVars())
                    .WithValidation(CliWrap.CommandResultValidation.None)
                    .ExecuteBufferedAsync();
                if (result.ExitCode != 0)
                    return Results.BadRequest(new { error = result.StandardError.Trim() });
                return Results.Text(result.StandardOutput, "application/sql");
            }
            catch (Exception ex)
            {
                return Results.BadRequest(new { error = $"Failed to invoke mysqldump: {ex.Message}" });
            }
        });

        // Database import (mysql < file). MCP intent-gated under
        // kind=database_import. Imports overwrite the destination database
        // — strictly destructive when the operator runs against an existing
        // schema. Header-driven gate so the GUI import wizard stays untouched.
        app.MapPost("/api/databases/{name}/import", async (
            string name,
            HttpContext ctx,
            BinaryManager bm,
            SettingsStore settings,
            IServiceProvider sp,
            NKS.WebDevConsole.Core.Interfaces.IDeployIntentValidator intentValidator,
            CancellationToken ct) =>
        {
            if (!IsValidDatabaseName(name))
                return Results.BadRequest(new { error = "Invalid database name" });

            var impIntentToken = ctx.Request.Headers["X-Intent-Token"].FirstOrDefault();
            if (!string.IsNullOrEmpty(impIntentToken) || NKS.WebDevConsole.Daemon.Mcp.McpCallerContext.IsMcpToken)
            {
                var impAllowUnconfirmed = string.Equals(
                    ctx.Request.Headers["X-Allow-Unconfirmed"].FirstOrDefault(), "true",
                    StringComparison.OrdinalIgnoreCase);
                var impVerdict = await intentValidator.ValidateAndConsumeAsync(
                    impIntentToken ?? "", "database_import", domain: name, host: "*db*", impAllowUnconfirmed, ct);
                if (!impVerdict.Ok)
                    return Results.Json(
                        new { error = "intent_rejected", reason = impVerdict.Reason, detail = impVerdict.Detail },
                        statusCode: impVerdict.Reason == "pending_confirmation" ? 425 : 403);
            }

            var mysql = bm.ListInstalled("mysql").FirstOrDefault();
            if (mysql?.Executable is null)
                return Results.BadRequest(new { error = "MySQL not installed" });
            var mysqlCli = Path.Combine(Path.GetDirectoryName(mysql.Executable)!, OperatingSystem.IsWindows() ? "mysql.exe" : "mysql");

            // Stream the uploaded SQL file (or raw body) to a temp file; a dump
            // can be hundreds of MB, so it is never held in memory. Kestrel's
            // default 30 MB body limit is raised to the import limit here.
            var sizeFeature = ctx.Features.Get<Microsoft.AspNetCore.Http.Features.IHttpMaxRequestBodySizeFeature>();
            if (sizeFeature is { IsReadOnly: false })
                sizeFeature.MaxRequestBodySize = ImportMaxBytes + ImportMultipartOverheadBytes;
            if (ctx.Request.ContentLength > ImportMaxBytes + ImportMultipartOverheadBytes)
                return ImportTooLarge();

            // Note: the mysql client also runs its own client commands (source,
            // system, ...) found in the dump. Turning them off needs --commands=OFF
            // (recent MySQL) or --binary-mode, which could not be confirmed for
            // every client version in the binary catalog, so the import runs
            // without them; MCP-token imports need an approved intent.
            var tmpFile = Path.GetTempFileName();
            try
            {
                try
                {
                    if (await ReceiveImportAsync(ctx.Request, tmpFile, ImportMaxBytes, ct) is { } uploadError)
                        return Results.BadRequest(new { error = uploadError });
                }
                catch (ImportTooLargeException)
                {
                    return ImportTooLarge();
                }
                catch (Microsoft.AspNetCore.Http.BadHttpRequestException bre) when (bre.StatusCode == StatusCodes.Status413PayloadTooLarge)
                {
                    return ImportTooLarge();
                }

                var args = MysqlBaseArgs(ResolveMysqlPortWithFallback(settings, sp, pluginLoader, bm));
                args.Add(name);
                try
                {
                    var result = await CliWrap.Cli.Wrap(mysqlCli)
                        .WithArguments(args)
                        .WithEnvironmentVariables(MysqlEnvVars())
                        .WithStandardInputPipe(CliWrap.PipeSource.FromFile(tmpFile))
                        .WithValidation(CliWrap.CommandResultValidation.None)
                        .ExecuteBufferedAsync();
                    return result.ExitCode == 0
                        ? Results.Ok(new { ok = true, message = "Import completed" })
                        : Results.BadRequest(new { error = result.StandardError.Trim() });
                }
                catch (Exception ex)
                {
                    return Results.BadRequest(new { error = $"Failed to invoke mysql: {ex.Message}" });
                }
            }
            finally
            {
                try { File.Delete(tmpFile); } catch { /* best-effort */ }
            }

            static IResult ImportTooLarge() => Results.Json(
                new { error = "import_too_large", maxBytes = ImportMaxBytes, detail = $"The SQL dump exceeds the import limit of {ImportMaxBytes / (1024 * 1024)} MB." },
                statusCode: StatusCodes.Status413PayloadTooLarge);
        });

        // ── Database explorer v2 (MySqlConnector-backed) ──────────────────────────
        // Engine-agnostic surface for the new Databases page: rich table metadata,
        // paged data browse, structure (columns + indexes), structured multi-result
        // SQL execution. The legacy /api/databases/* shell-out endpoints above are
        // preserved for back-compat with MCP and the prior UI.

        NKS.WebDevConsole.Daemon.Data.MySqlDriver MakeMySqlDriverV2(
            SettingsStore settings, IServiceProvider sp, BinaryManager bm) =>
                NKS.WebDevConsole.Daemon.Data.DbDriverFactory.CreateMySql(
                    ResolveMysqlPortWithFallback(settings, sp, pluginLoader, bm));

        app.MapGet("/api/databases/v2", async (
            BinaryManager bm, SettingsStore settings, IServiceProvider sp, CancellationToken ct) =>
        {
            var mysql = bm.ListInstalled("mysql").FirstOrDefault();
            if (mysql?.Executable is null)
                return Results.Ok(new { error = "MySQL not installed", databases = Array.Empty<object>() });
            try
            {
                var driver = MakeMySqlDriverV2(settings, sp, bm);
                var dbs = await driver.ListDatabasesAsync(ct);
                return Results.Ok(new { engine = driver.Engine, databases = dbs });
            }
            catch (MySqlConnector.MySqlException mex)
            {
                var port = ResolveMysqlPortWithFallback(settings, sp, pluginLoader, bm);
                var hint = mex.Number == 1045
                    ? $"Port {port} accepted the connection but rejected the WDC root password. Likely an external mysqld (MAMP/XAMPP/system service) on this port."
                    : null;
                int? suggestedPort = mex.Number == 1045 ? FindFreeTcpPort(port + 1) : null;
                return Results.Ok(new { error = mex.Message, code = mex.Number, hint, suggestedPort, databases = Array.Empty<object>() });
            }
            catch (Exception ex)
            {
                return Results.Ok(new { error = ex.Message, databases = Array.Empty<object>() });
            }
        });

        app.MapGet("/api/databases/v2/{db}/tables", async (
            string db, BinaryManager bm, SettingsStore settings, IServiceProvider sp, CancellationToken ct) =>
        {
            if (!IsValidDatabaseName(db))
                return Results.BadRequest(new { error = "Invalid database name" });
            try
            {
                var tables = await MakeMySqlDriverV2(settings, sp, bm).ListTablesAsync(db, ct);
                return Results.Ok(new { tables });
            }
            catch (Exception ex) { return Results.BadRequest(new { error = ex.Message }); }
        });

        app.MapGet("/api/databases/v2/{db}/tables/{table}/columns", async (
            string db, string table, BinaryManager bm, SettingsStore settings, IServiceProvider sp, CancellationToken ct) =>
        {
            if (!IsValidDatabaseName(db) || !IsValidDatabaseName(table))
                return Results.BadRequest(new { error = "Invalid identifier" });
            try
            {
                var cols = await MakeMySqlDriverV2(settings, sp, bm).ListColumnsAsync(db, table, ct);
                return Results.Ok(new { columns = cols });
            }
            catch (Exception ex) { return Results.BadRequest(new { error = ex.Message }); }
        });

        app.MapGet("/api/databases/v2/{db}/tables/{table}/indexes", async (
            string db, string table, BinaryManager bm, SettingsStore settings, IServiceProvider sp, CancellationToken ct) =>
        {
            if (!IsValidDatabaseName(db) || !IsValidDatabaseName(table))
                return Results.BadRequest(new { error = "Invalid identifier" });
            try
            {
                var idx = await MakeMySqlDriverV2(settings, sp, bm).ListIndexesAsync(db, table, ct);
                return Results.Ok(new { indexes = idx });
            }
            catch (Exception ex) { return Results.BadRequest(new { error = ex.Message }); }
        });

        app.MapGet("/api/databases/v2/{db}/tables/{table}/data", async (
            string db, string table, HttpContext ctx, BinaryManager bm, SettingsStore settings, IServiceProvider sp,
            int? page, int? pageSize, string? orderBy, string? orderDir, string? where, CancellationToken ct) =>
        {
            if (!IsValidDatabaseName(db) || !IsValidDatabaseName(table))
                return Results.BadRequest(new { error = "Invalid identifier" });
            if (McpBrowseFilterRejection(where) is { } filterRejected)
                return filterRejected;
            try
            {
                var opts = new NKS.WebDevConsole.Daemon.Data.BrowseOptions
                {
                    Page = page ?? 1,
                    PageSize = pageSize ?? 50,
                    OrderBy = orderBy,
                    OrderDir = orderDir ?? "asc",
                    WhereClause = where,
                };
                var result = await MakeMySqlDriverV2(settings, sp, bm).BrowseTableAsync(db, table, opts, ct);
                return Results.Ok(result);
            }
            catch (InvalidOperationException ex) { return Results.BadRequest(new { error = ex.Message }); }
            catch (ArgumentException ex) { return Results.BadRequest(new { error = ex.Message }); }
            catch (Exception ex) { return Results.BadRequest(new { error = ex.Message }); }
        });

        app.MapPost("/api/databases/v2/{db}/query", async (
            string db, HttpContext ctx, BinaryManager bm, SettingsStore settings, IServiceProvider sp,
            NKS.WebDevConsole.Core.Interfaces.IDeployIntentValidator intentValidator, CancellationToken ct) =>
        {
            if (!IsValidDatabaseName(db))
                return Results.BadRequest(new { error = "Invalid database name" });

            // MCP intent gate parity with /api/databases/{name}/query (kind=database_query).
            var body = await ctx.Request.ReadFromJsonAsync<Dictionary<string, string>>(ct);
            var sql = body?.GetValueOrDefault("sql") ?? "";
            if (string.IsNullOrWhiteSpace(sql))
                return Results.BadRequest(new { error = "sql required" });

            // An MCP-token caller (AI agent) runs read-only statements freely;
            // anything the guard cannot prove read-only needs a confirmed intent.
            var dbqIntentToken = ctx.Request.Headers["X-Intent-Token"].FirstOrDefault();
            if (!string.IsNullOrEmpty(dbqIntentToken)
                || (NKS.WebDevConsole.Daemon.Mcp.McpCallerContext.IsMcpToken && !NKS.WebDevConsole.Daemon.Mcp.SqlReadOnlyGuard.IsReadOnly(sql)))
            {
                var dbqAllowUnconfirmed = string.Equals(
                    ctx.Request.Headers["X-Allow-Unconfirmed"].FirstOrDefault(), "true",
                    StringComparison.OrdinalIgnoreCase);
                var dbqVerdict = await intentValidator.ValidateAndConsumeAsync(
                    dbqIntentToken ?? "", "database_query", domain: db, host: "*db*", dbqAllowUnconfirmed, ct);
                if (!dbqVerdict.Ok)
                    return Results.Json(
                        new { error = "intent_rejected", reason = dbqVerdict.Reason, detail = dbqVerdict.Detail },
                        statusCode: dbqVerdict.Reason == "pending_confirmation" ? 425 : 403);
            }

            if (NKS.WebDevConsole.Daemon.Mcp.McpCallerContext.IsMcpToken && string.IsNullOrEmpty(dbqIntentToken))
            {
                try
                {
                    var ro = await MakeMySqlDriverV2(settings, sp, bm).ExecuteReadOnlyQueryAsync(
                        db, sql, McpReadOnlyMaxRows, McpReadOnlyTimeoutSeconds, ct);
                    return Results.Ok(new QueryExecutionResult
                    {
                        Results = new[] { ro.Set },
                        ExecutionTimeMs = ro.Set.ExecutionTimeMs,
                        Warnings = ro.Truncated
                            ? new[] { new QueryWarning("Note", 0, $"Result truncated to {McpReadOnlyMaxRows} rows") }
                            : null,
                    });
                }
                catch (MySqlConnector.MySqlException mex)
                {
                    return Results.BadRequest(new { error = mex.Message, code = mex.Number, sqlState = mex.SqlState });
                }
                catch (Exception ex)
                {
                    return Results.BadRequest(new { error = ex.Message });
                }
            }

            try
            {
                var result = await MakeMySqlDriverV2(settings, sp, bm).ExecuteQueryAsync(db, sql, ct);
                return Results.Ok(result);
            }
            catch (MySqlConnector.MySqlException mex)
            {
                return Results.BadRequest(new { error = mex.Message, code = mex.Number, sqlState = mex.SqlState });
            }
            catch (Exception ex)
            {
                return Results.BadRequest(new { error = ex.Message });
            }
        });
    }
}
