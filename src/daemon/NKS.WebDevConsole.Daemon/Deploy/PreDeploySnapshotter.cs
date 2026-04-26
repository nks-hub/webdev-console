using System.Diagnostics;
using System.IO.Compression;
using System.Text;
using CliWrap;
using CliWrap.Buffered;
using Microsoft.Extensions.Logging;
using NKS.WebDevConsole.Core.Interfaces;
using NKS.WebDevConsole.Core.Services;

namespace NKS.WebDevConsole.Daemon.Deploy;

/// <summary>
/// Phase 6.2 — pre-deploy database snapshotter.
///
/// Outputs are gzipped files at <c>{WdcPaths.BackupsRoot}/pre-deploy/{deployId}.sql.gz</c>.
/// Caller (NksDeployBackend) writes Path + SizeBytes onto the deploy_runs
/// row via <see cref="IDeployRunsRepository.UpdatePreDeployBackupAsync"/>.
///
/// Implementation status by DB backend:
///   - SQLite (file copy): WORKING — single-file copy + gzip, fast, no
///     external tool dependency.
///   - MySQL: SCAFFOLD — emits a metadata header file with the parameters
///     needed for a real mysqldump invocation (host, port, db, user). Real
///     mysqldump integration is Phase 6.3 (needs credential resolution
///     from site config + linked DB plugin).
///   - PostgreSQL: SCAFFOLD — same as MySQL, emits metadata header.
///
/// Discovery is deliberately permissive: we look for the first DB hint we
/// recognise, fall back to the SCAFFOLD path that always succeeds. The goal
/// for Phase 6.2 is for the deploy_runs.pre_deploy_backup_path column to
/// always populate when Snapshot.Include=true so downstream UIs can stage
/// the "Restore" button — even if the actual restore is gated on the
/// backend producing a real dump.
/// </summary>
public sealed class PreDeploySnapshotter : IPreDeploySnapshotter
{
    private readonly ISiteRegistry _sites;
    private readonly ILogger<PreDeploySnapshotter> _logger;

    public PreDeploySnapshotter(ISiteRegistry sites, ILogger<PreDeploySnapshotter> logger)
    {
        _sites = sites;
        _logger = logger;
    }

    public async Task<PreDeploySnapshotResult> CreateAsync(
        string domain,
        string deployId,
        CancellationToken ct)
    {
        var sw = Stopwatch.StartNew();
        var dir = Path.Combine(WdcPaths.BackupsRoot, "pre-deploy");
        Directory.CreateDirectory(dir);
        var outPath = Path.Combine(dir, $"{deployId}.sql.gz");

        // Resolve site to find its document root + linked DB. ISiteRegistry
        // is shared cross-ALC; the snapshot decision uses it read-only.
        var site = _sites.GetSite(domain);
        if (site is null)
        {
            throw new InvalidOperationException(
                $"Site '{domain}' not found — cannot snapshot DB for unknown site.");
        }
        await Task.Yield(); // keep async signature for future real impls

        // Detect SQLite first — by far the simplest case and common for
        // local Nette dev. Look for *.sqlite / *.db in common locations.
        var sqlitePath = TryFindSqliteFile(site.DocumentRoot);
        if (sqlitePath is not null)
        {
            _logger.LogInformation(
                "Pre-deploy snapshot: SQLite at {Src} → {Dst} (deploy {DeployId})",
                sqlitePath, outPath, deployId);
            await CopyAndGzipAsync(sqlitePath, outPath, ct);
            sw.Stop();
            var size = new FileInfo(outPath).Length;
            return new PreDeploySnapshotResult(outPath, size, sw.Elapsed);
        }

        // Phase 6.3 — no SQLite, try mysqldump via .env discovery. The
        // resolver scans common .env locations for DB_* keys (Laravel /
        // Symfony / Next.js convention).
        var envConn = EnvFileDatabaseResolver.TryResolve(site.DocumentRoot);
        if (envConn is not null && envConn.Type is "mysql" or "mariadb")
        {
            var mysqldump = TryFindMysqldump();
            if (mysqldump is not null)
            {
                // Fast TCP probe before spawning mysqldump — surfaces
                // "DB unreachable" with the actual connection details
                // instead of waiting for mysqldump's connect timeout +
                // generic "Can't connect to MySQL server" output.
                await ProbeTcpAsync(envConn.Host, envConn.Port, "mysql", ct);
                _logger.LogInformation(
                    "Pre-deploy snapshot: mysqldump {Db}@{Host}:{Port} (envFile={EnvFile}, deploy {DeployId})",
                    envConn.Database, envConn.Host, envConn.Port, envConn.DiscoveredAt, deployId);
                await DumpMysqlAsync(mysqldump, envConn, outPath, ct);
                sw.Stop();
                var size = new FileInfo(outPath).Length;
                return new PreDeploySnapshotResult(outPath, size, sw.Elapsed);
            }
            _logger.LogWarning(
                "Pre-deploy snapshot: .env identifies {Type} db {Db} for {Domain}, but mysqldump binary " +
                "not found in WdcPaths.BinariesRoot or PATH — falling back to scaffold stub.",
                envConn.Type, envConn.Database, domain);
        }

        // Phase 6.4 — pg_dump path. Same shape as mysqldump but uses the
        // PGPASSWORD env var (pg_dump's only safe non-argv password
        // channel — equivalent of mysqldump's defaults-extra-file).
        if (envConn is not null && envConn.Type is "pgsql")
        {
            var pgDump = TryFindPgDump();
            if (pgDump is not null)
            {
                // Fast TCP probe before spawning pg_dump (same rationale
                // as the mysqldump path above — clearer error surface).
                await ProbeTcpAsync(envConn.Host, envConn.Port, "pgsql", ct);
                _logger.LogInformation(
                    "Pre-deploy snapshot: pg_dump {Db}@{Host}:{Port} (envFile={EnvFile}, deploy {DeployId})",
                    envConn.Database, envConn.Host, envConn.Port, envConn.DiscoveredAt, deployId);
                await DumpPgAsync(pgDump, envConn, outPath, ct);
                sw.Stop();
                var size = new FileInfo(outPath).Length;
                return new PreDeploySnapshotResult(outPath, size, sw.Elapsed);
            }
            _logger.LogWarning(
                "Pre-deploy snapshot: .env identifies pgsql db {Db} for {Domain}, but pg_dump binary " +
                "not found in WdcPaths.BinariesRoot or PATH — falling back to scaffold stub.",
                envConn.Database, domain);
        }

        // Last resort — SCAFFOLD metadata file. Either no .env was found,
        // it had no DB_* keys, the type is unsupported, or the dump tool
        // is missing. Always succeeds so the deploy_runs row gets a
        // populated path; restore against this archive will surface a
        // clear error.
        await WriteScaffoldAsync(outPath, domain, deployId, envConn, ct);
        sw.Stop();
        var scaffoldSize = new FileInfo(outPath).Length;
        _logger.LogWarning(
            "Pre-deploy snapshot: wrote SCAFFOLD stub for {Domain} at {Path}. " +
            "(detected db type: {DbType})",
            domain, outPath, envConn?.Type ?? "none");
        return new PreDeploySnapshotResult(outPath, scaffoldSize, sw.Elapsed);
    }

    /// <summary>
    /// Locate the mysqldump binary. Prefers a wdc-managed install at
    /// <c>{BinariesRoot}/mysql/{any-version}/bin/mysqldump</c>, falls back
    /// to PATH so a system mysql/mariadb client also works on dev machines.
    /// </summary>
    private static string? TryFindMysqldump()
    {
        var ext = OperatingSystem.IsWindows() ? ".exe" : "";
        // Managed install — pick the first version directory that has
        // mysqldump (we don't care which version for snapshotting).
        var managedRoot = Path.Combine(WdcPaths.BinariesRoot, "mysql");
        if (Directory.Exists(managedRoot))
        {
            foreach (var versionDir in Directory.EnumerateDirectories(managedRoot))
            {
                var candidate = Path.Combine(versionDir, "bin", "mysqldump" + ext);
                if (File.Exists(candidate)) return candidate;
            }
        }
        // Same probe for mariadb-managed installs (mariadb-dump alias).
        var mariaRoot = Path.Combine(WdcPaths.BinariesRoot, "mariadb");
        if (Directory.Exists(mariaRoot))
        {
            foreach (var versionDir in Directory.EnumerateDirectories(mariaRoot))
            {
                foreach (var name in new[] { "mysqldump", "mariadb-dump" })
                {
                    var candidate = Path.Combine(versionDir, "bin", name + ext);
                    if (File.Exists(candidate)) return candidate;
                }
            }
        }
        // Fall back to PATH — CliWrap handles the resolution if we just
        // pass the bare name. Verify presence by checking PATH dirs.
        var pathEnv = Environment.GetEnvironmentVariable("PATH") ?? "";
        var pathSep = OperatingSystem.IsWindows() ? ';' : ':';
        foreach (var dir in pathEnv.Split(pathSep, StringSplitOptions.RemoveEmptyEntries))
        {
            var candidate = Path.Combine(dir.Trim(), "mysqldump" + ext);
            if (File.Exists(candidate)) return candidate;
        }
        return null;
    }

    /// <summary>
    /// Run mysqldump against the resolved connection, gzipping the stream
    /// as it arrives. Password (if any) is passed via a temp
    /// <c>--defaults-extra-file</c> so it never appears in the process
    /// argv (avoids leaking via <c>ps</c> and the daemon's own subprocess
    /// logs). The temp file is deleted in finally regardless of outcome.
    /// </summary>
    private async Task DumpMysqlAsync(
        string mysqldump,
        EnvFileDatabaseResolver.DatabaseConnection conn,
        string outPath,
        CancellationToken ct)
    {
        string? defaultsFile = null;
        try
        {
            // mysqldump uses [client] section credentials when --defaults-extra-file
            // is supplied. Username goes here too so the final argv is just
            // the dump options + the database name.
            var defaultsContent =
                "[client]\n" +
                $"user={conn.User}\n" +
                (string.IsNullOrEmpty(conn.Password) ? "" : $"password={conn.Password}\n") +
                $"host={conn.Host}\n" +
                $"port={conn.Port}\n";
            defaultsFile = Path.Combine(Path.GetTempPath(),
                $"wdc-mysqldump-{Guid.NewGuid():N}.cnf");
            await File.WriteAllTextAsync(defaultsFile, defaultsContent, ct);
            if (!OperatingSystem.IsWindows())
            {
                try
                {
                    new FileInfo(defaultsFile).UnixFileMode =
                        UnixFileMode.UserRead | UnixFileMode.UserWrite;
                }
                catch { /* best effort */ }
            }

            // Stream stdout straight into a GZipStream — avoids buffering
            // the entire dump in memory for big DBs.
            await using var outFs = File.Create(outPath);
            await using var gz = new GZipStream(outFs, CompressionLevel.SmallestSize);
            // CliWrap PipeTarget.ToStream wires stdout → our gzip sink.
            // --single-transaction gives a consistent snapshot on InnoDB
            // without taking table-level locks; --quick streams row-by-row
            // so big tables don't pin RAM.
            var result = await Cli.Wrap(mysqldump)
                .WithArguments(new[]
                {
                    $"--defaults-extra-file={defaultsFile}",
                    "--single-transaction",
                    "--quick",
                    "--routines",
                    "--triggers",
                    "--events",
                    "--skip-lock-tables",
                    conn.Database,
                })
                .WithStandardOutputPipe(PipeTarget.ToStream(gz))
                .WithValidation(CommandResultValidation.None)
                .ExecuteBufferedAsync(ct);
            if (result.ExitCode != 0)
            {
                throw new InvalidOperationException(
                    $"mysqldump exit {result.ExitCode}: {result.StandardError.Trim()}");
            }
        }
        finally
        {
            if (defaultsFile is not null)
            {
                try { File.Delete(defaultsFile); } catch { /* best effort */ }
            }
        }
    }

    private static string? TryFindSqliteFile(string documentRoot)
    {
        // Nette / Symfony / etc. commonly drop SQLite files in app/, var/,
        // data/, db/, or directly next to composer.json. Scan one level
        // deep — enough for the common layouts without descending into
        // node_modules / vendor.
        var siteRoot = Directory.GetParent(documentRoot)?.FullName ?? documentRoot;
        var candidateDirs = new[]
        {
            siteRoot,
            Path.Combine(siteRoot, "app"),
            Path.Combine(siteRoot, "var"),
            Path.Combine(siteRoot, "data"),
            Path.Combine(siteRoot, "db"),
            Path.Combine(siteRoot, "database"),
            documentRoot,
        };
        var extensions = new[] { "*.sqlite", "*.sqlite3", "*.db" };
        foreach (var dir in candidateDirs)
        {
            if (!Directory.Exists(dir)) continue;
            foreach (var ext in extensions)
            {
                try
                {
                    var hit = Directory.EnumerateFiles(dir, ext).FirstOrDefault();
                    if (hit is not null) return hit;
                }
                catch (UnauthorizedAccessException) { /* continue */ }
            }
        }
        return null;
    }

    private static async Task CopyAndGzipAsync(string src, string dst, CancellationToken ct)
    {
        await using var input = File.OpenRead(src);
        await using var outFs = File.Create(dst);
        await using var gz = new GZipStream(outFs, CompressionLevel.SmallestSize);
        await input.CopyToAsync(gz, ct);
    }

    /// <summary>
    /// Fast TCP probe with a 3-second budget. Throws a clear
    /// <see cref="InvalidOperationException"/> when the DB host/port
    /// can't be reached, so the failure surface in deploy_runs's
    /// error_message is "DB unreachable at host:port" rather than the
    /// dump tool's generic exit code.
    ///
    /// 3 s is intentionally short — a healthy local DB connects in ~1ms;
    /// a healthy LAN DB in ~10ms. Anything past that is a misconfig and
    /// failing fast beats hanging the deploy waiting for a TCP timeout.
    /// </summary>
    internal static async Task ProbeTcpAsync(string host, int port, string dbType, CancellationToken ct)
    {
        using var probe = new System.Net.Sockets.TcpClient();
        using var cts = CancellationTokenSource.CreateLinkedTokenSource(ct);
        cts.CancelAfter(TimeSpan.FromSeconds(3));
        try
        {
            await probe.ConnectAsync(host, port, cts.Token);
        }
        catch (OperationCanceledException) when (cts.IsCancellationRequested && !ct.IsCancellationRequested)
        {
            throw new InvalidOperationException(
                $"{dbType} DB unreachable at {host}:{port} (TCP probe timed out after 3s) — " +
                $"check the .env DB_HOST/DB_PORT values and that the DB server is running.");
        }
        catch (System.Net.Sockets.SocketException ex)
        {
            throw new InvalidOperationException(
                $"{dbType} DB unreachable at {host}:{port}: {ex.Message}", ex);
        }
    }

    /// <summary>
    /// Locate pg_dump. Same probe order as mysqldump — managed install at
    /// <c>{BinariesRoot}/postgres/{any-version}/bin/pg_dump</c>, then PATH.
    /// </summary>
    private static string? TryFindPgDump()
    {
        var ext = OperatingSystem.IsWindows() ? ".exe" : "";
        // Managed install — pick the first version directory that has pg_dump.
        foreach (var rootName in new[] { "postgres", "postgresql" })
        {
            var managedRoot = Path.Combine(WdcPaths.BinariesRoot, rootName);
            if (!Directory.Exists(managedRoot)) continue;
            foreach (var versionDir in Directory.EnumerateDirectories(managedRoot))
            {
                var candidate = Path.Combine(versionDir, "bin", "pg_dump" + ext);
                if (File.Exists(candidate)) return candidate;
            }
        }
        // PATH fallback.
        var pathEnv = Environment.GetEnvironmentVariable("PATH") ?? "";
        var pathSep = OperatingSystem.IsWindows() ? ';' : ':';
        foreach (var dir in pathEnv.Split(pathSep, StringSplitOptions.RemoveEmptyEntries))
        {
            var candidate = Path.Combine(dir.Trim(), "pg_dump" + ext);
            if (File.Exists(candidate)) return candidate;
        }
        return null;
    }

    /// <summary>
    /// Run pg_dump against the resolved connection, gzipping stdout.
    /// Password is passed through PGPASSWORD env var rather than -W (which
    /// is interactive) or argv (which leaks via ps). pg_dump's
    /// <c>--no-owner --no-privileges</c> keep the dump portable across
    /// hosts that have different role names.
    /// </summary>
    private async Task DumpPgAsync(
        string pgDump,
        EnvFileDatabaseResolver.DatabaseConnection conn,
        string outPath,
        CancellationToken ct)
    {
        await using var outFs = File.Create(outPath);
        await using var gz = new GZipStream(outFs, CompressionLevel.SmallestSize);
        var cmd = Cli.Wrap(pgDump)
            .WithArguments(new[]
            {
                $"--host={conn.Host}",
                $"--port={conn.Port}",
                $"--username={conn.User}",
                "--no-password",       // never prompt; rely on PGPASSWORD
                "--no-owner",
                "--no-privileges",
                "--clean",             // emit DROP before CREATE for restore
                "--if-exists",         // idempotent restore against existing schema
                conn.Database,
            })
            .WithStandardOutputPipe(PipeTarget.ToStream(gz))
            .WithValidation(CommandResultValidation.None);
        if (!string.IsNullOrEmpty(conn.Password))
        {
            cmd = cmd.WithEnvironmentVariables(env => env.Set("PGPASSWORD", conn.Password));
        }
        var result = await cmd.ExecuteBufferedAsync(ct);
        if (result.ExitCode != 0)
        {
            throw new InvalidOperationException(
                $"pg_dump exit {result.ExitCode}: {result.StandardError.Trim()}");
        }
    }

    private static async Task WriteScaffoldAsync(
        string dst,
        string domain,
        string deployId,
        EnvFileDatabaseResolver.DatabaseConnection? envConn,
        CancellationToken ct)
    {
        var sb = new StringBuilder()
            .AppendLine("-- NKS WDC pre-deploy snapshot SCAFFOLD")
            .AppendLine($"-- domain: {domain}")
            .AppendLine($"-- deployId: {deployId}")
            .AppendLine($"-- createdAt: {DateTimeOffset.UtcNow:O}")
            .AppendLine("-- status: PENDING — could not produce a real DB dump.")
            .AppendLine("-- Reason chain: SQLite scan negative; mysqldump path either")
            .AppendLine("--   (a) no .env-discovered DB credentials, or")
            .AppendLine("--   (b) DB type unsupported (postgres ships in Phase 6.4), or")
            .AppendLine("--   (c) mysqldump binary missing from BinariesRoot + PATH.");
        if (envConn is not null)
        {
            sb.AppendLine("-- Discovered .env conn (for operator triage):")
              .AppendLine($"--   type={envConn.Type} host={envConn.Host} port={envConn.Port} db={envConn.Database} user={envConn.User} envFile={envConn.DiscoveredAt}");
        }
        sb.AppendLine("-- Restore against this archive will refuse.");
        await using var outFs = File.Create(dst);
        await using var gz = new GZipStream(outFs, CompressionLevel.SmallestSize);
        var bytes = Encoding.UTF8.GetBytes(sb.ToString());
        await gz.WriteAsync(bytes.AsMemory(), ct);
    }
}
