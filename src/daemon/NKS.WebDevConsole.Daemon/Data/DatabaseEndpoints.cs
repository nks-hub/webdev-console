using System.Text.Json;
using CliWrap.Buffered;
using Dapper;
using NKS.WebDevConsole.Core.Interfaces;
using NKS.WebDevConsole.Daemon.Binaries;
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
    public static void MapDatabaseEndpoints(
        this WebApplication app,
        PluginLoader pluginLoader,
        Database database,
        JsonSerializerOptions caseInsensitiveJson)
    {
        // F49c: port probe cache — set once per daemon boot by ResolveMysqlPortWithFallback.
        int? _cachedMysqlPort = null;
        object _mysqlPortProbeLock = new();

        // F49c: Resolve MySQL port with explicit-setting → live-prober → plugin-default
        // fallback chain. Plugin Port default (3306) collides with MAMP on user's machine,
        // so step 2 actively probes a small port sweep with the WDC password: the real
        // WDC mysqld is whichever port authenticates. Cached once per daemon boot.
        int ResolveMysqlPortWithFallback(SettingsStore settings, IServiceProvider sp, PluginLoader loader, BinaryManager bm)
        {
            // Step 1: explicit user config wins always, no probing.
            if (settings.TryReadMysqlPort(out var configured) && configured > 0)
                return configured;

            // Step 2: probe cache — avoid repeating the probe on every endpoint hit.
            if (_cachedMysqlPort is int cached) return cached;

            lock (_mysqlPortProbeLock)
            {
                if (_cachedMysqlPort is int doubleChecked) return doubleChecked;

                // Step 3: try port list with WDC root password via mysqladmin ping.
                var password = NKS.WebDevConsole.Core.Services.MySqlRootPassword.TryRead();
                var mysql = bm.ListInstalled("mysql").FirstOrDefault();
                var mysqladmin = mysql?.Executable is null ? null : Path.Combine(Path.GetDirectoryName(mysql.Executable)!, OperatingSystem.IsWindows() ? "mysqladmin.exe" : "mysqladmin");
                if (!string.IsNullOrEmpty(password) && mysqladmin is not null && File.Exists(mysqladmin))
                {
                    var candidatePorts = new[] { 3306, 3307, 3308, 3309 };
                    foreach (var port in candidatePorts)
                    {
                        try
                        {
                            var args = new[] { "-h", "127.0.0.1", "-P", port.ToString(), "-u", "root", "ping" };
                            var env = new Dictionary<string, string?> { ["MYSQL_PWD"] = password };
                            var result = CliWrap.Cli.Wrap(mysqladmin)
                                .WithArguments(args)
                                .WithEnvironmentVariables(env)
                                .WithValidation(CliWrap.CommandResultValidation.None)
                                .ExecuteBufferedAsync()
                                .ConfigureAwait(false)
                                .GetAwaiter().GetResult();
                            if (result.ExitCode == 0 && result.StandardOutput.Contains("mysqld is alive", StringComparison.OrdinalIgnoreCase))
                            {
                                _cachedMysqlPort = port;
                                return port;
                            }
                        }
                        catch { /* skip port, try next */ }
                    }
                }

                // Step 4: fall back to plugin default (3306) — prober exhausted.
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
                                if (portVal is int p && p > 0) { _cachedMysqlPort = p; return p; }
                            }
                        }
                    }
                }
                catch { }

                _cachedMysqlPort = 3306;
                return 3306;
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
            ILoggerFactory lf) =>
        {
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
        // for all root@* accounts, persists the new password, and verifies connectivity.
        app.MapPost("/api/plugins/mysql/change-password", async (
            HttpContext ctx,
            BinaryManager bm,
            SettingsStore settings,
            IServiceProvider sp,
            ILoggerFactory lf) =>
        {
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
            ILoggerFactory lf) =>
        {
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

        app.MapPost("/api/plugins/mysql/users", async (HttpContext ctx, BinaryManager bm, SettingsStore settings, IServiceProvider sp) =>
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

            var mysql = bm.ListInstalled("mysql").FirstOrDefault();
            if (mysql?.Executable is null)
                return Results.BadRequest(new { success = false, error = "MySQL not installed" });
            var mysqlCli = MySqlPasswordHelper.ResolveMysqlCli(mysql.Executable);
            if (mysqlCli is null)
                return Results.BadRequest(new { success = false, error = "mysql CLI not found next to mysqld" });

            try
            {
                var sql = MySqlUserHelper.BuildCreateUserSql(userName, host, password);
                if (!string.IsNullOrWhiteSpace(database))
                    sql += MySqlUserHelper.BuildGrantDatabaseSql(userName, host, database, string.IsNullOrWhiteSpace(privileges) ? "readWrite" : privileges);

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

        app.MapPost("/api/plugins/mysql/users/password", async (HttpContext ctx, BinaryManager bm, SettingsStore settings, IServiceProvider sp) =>
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

        app.MapPost("/api/plugins/mysql/users/grants", async (HttpContext ctx, BinaryManager bm, SettingsStore settings, IServiceProvider sp) =>
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

        app.MapPost("/api/plugins/mysql/users/drop", async (HttpContext ctx, BinaryManager bm, SettingsStore settings, IServiceProvider sp) =>
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
            if (!string.IsNullOrEmpty(dbIntentToken))
            {
                var dbAllowUnconfirmed = string.Equals(
                    ctx.Request.Headers["X-Allow-Unconfirmed"].FirstOrDefault(), "true",
                    StringComparison.OrdinalIgnoreCase);
                var dbVerdict = await intentValidator.ValidateAndConsumeAsync(
                    dbIntentToken, "database_drop", domain: name, host: "*db*", dbAllowUnconfirmed, ct);
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

            var dbqIntentToken = ctx.Request.Headers["X-Intent-Token"].FirstOrDefault();
            if (!string.IsNullOrEmpty(dbqIntentToken))
            {
                var dbqAllowUnconfirmed = string.Equals(
                    ctx.Request.Headers["X-Allow-Unconfirmed"].FirstOrDefault(), "true",
                    StringComparison.OrdinalIgnoreCase);
                var dbqVerdict = await intentValidator.ValidateAndConsumeAsync(
                    dbqIntentToken, "database_query", domain: name, host: "*db*", dbqAllowUnconfirmed, ct);
                if (!dbqVerdict.Ok)
                    return Results.Json(
                        new { error = "intent_rejected", reason = dbqVerdict.Reason, detail = dbqVerdict.Detail },
                        statusCode: dbqVerdict.Reason == "pending_confirmation" ? 425 : 403);
            }

            var body = await ctx.Request.ReadFromJsonAsync<Dictionary<string, string>>(ct);
            var sql = body?.GetValueOrDefault("sql") ?? "";
            if (string.IsNullOrWhiteSpace(sql))
                return Results.BadRequest(new { error = "sql required" });
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

        // Database export (mysqldump)
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
            if (!string.IsNullOrEmpty(impIntentToken))
            {
                var impAllowUnconfirmed = string.Equals(
                    ctx.Request.Headers["X-Allow-Unconfirmed"].FirstOrDefault(), "true",
                    StringComparison.OrdinalIgnoreCase);
                var impVerdict = await intentValidator.ValidateAndConsumeAsync(
                    impIntentToken, "database_import", domain: name, host: "*db*", impAllowUnconfirmed, ct);
                if (!impVerdict.Ok)
                    return Results.Json(
                        new { error = "intent_rejected", reason = impVerdict.Reason, detail = impVerdict.Detail },
                        statusCode: impVerdict.Reason == "pending_confirmation" ? 425 : 403);
            }

            var mysql = bm.ListInstalled("mysql").FirstOrDefault();
            if (mysql?.Executable is null)
                return Results.BadRequest(new { error = "MySQL not installed" });
            var mysqlCli = Path.Combine(Path.GetDirectoryName(mysql.Executable)!, OperatingSystem.IsWindows() ? "mysql.exe" : "mysql");

            // Read uploaded SQL file or raw body
            string sql;
            if (ctx.Request.HasFormContentType)
            {
                var form = await ctx.Request.ReadFormAsync();
                var file = form.Files.FirstOrDefault();
                if (file is null) return Results.BadRequest(new { error = "No file uploaded" });
                using var reader = new StreamReader(file.OpenReadStream());
                sql = await reader.ReadToEndAsync();
            }
            else
            {
                // `leaveOpen: true` because ASP.NET owns the request body stream;
                // StreamReader's default Dispose closes the underlying stream,
                // which would fight the hosting layer. The `using` still releases
                // the reader's ~1 KB read buffer immediately.
                using var reader = new StreamReader(ctx.Request.Body, System.Text.Encoding.UTF8, detectEncodingFromByteOrderMarks: true, bufferSize: -1, leaveOpen: true);
                sql = await reader.ReadToEndAsync();
            }

            var tmpFile = Path.GetTempFileName();
            await File.WriteAllTextAsync(tmpFile, sql);
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
            finally
            {
                try { File.Delete(tmpFile); } catch { /* best-effort */ }
            }
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
            var dbqIntentToken = ctx.Request.Headers["X-Intent-Token"].FirstOrDefault();
            if (!string.IsNullOrEmpty(dbqIntentToken))
            {
                var dbqAllowUnconfirmed = string.Equals(
                    ctx.Request.Headers["X-Allow-Unconfirmed"].FirstOrDefault(), "true",
                    StringComparison.OrdinalIgnoreCase);
                var dbqVerdict = await intentValidator.ValidateAndConsumeAsync(
                    dbqIntentToken, "database_query", domain: db, host: "*db*", dbqAllowUnconfirmed, ct);
                if (!dbqVerdict.Ok)
                    return Results.Json(
                        new { error = "intent_rejected", reason = dbqVerdict.Reason, detail = dbqVerdict.Detail },
                        statusCode: dbqVerdict.Reason == "pending_confirmation" ? 425 : 403);
            }

            var body = await ctx.Request.ReadFromJsonAsync<Dictionary<string, string>>(ct);
            var sql = body?.GetValueOrDefault("sql") ?? "";
            if (string.IsNullOrWhiteSpace(sql))
                return Results.BadRequest(new { error = "sql required" });

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
