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
/// Phase 6.4 — concrete <see cref="ISnapshotRestorer"/>. Symmetric to
/// <see cref="PreDeploySnapshotter"/>:
///
///   * SQLite: gunzip the archive back over the live DB file (after
///     a safety copy to <c>{path}.pre-restore.{ts}.bak</c>).
///   * MySQL/MariaDB: pipe gunzip → mysql client.
///   * PostgreSQL: pipe gunzip → psql client.
///   * SCAFFOLD archive: refuse (header carries "SCAFFOLD" marker).
///
/// Live-data overwrite, no rollback — operators must take responsibility.
/// We do however always create a safety copy of any SQLite file we're
/// about to overwrite, so a fat-finger restore against the wrong site
/// can be undone manually.
/// </summary>
public sealed class SnapshotRestorer : ISnapshotRestorer
{
    private readonly ISiteRegistry _sites;
    private readonly IDeployRunsRepository _runs;
    private readonly ILogger<SnapshotRestorer> _logger;

    public SnapshotRestorer(
        ISiteRegistry sites,
        IDeployRunsRepository runs,
        ILogger<SnapshotRestorer> logger)
    {
        _sites = sites;
        _runs = runs;
        _logger = logger;
    }

    public async Task<SnapshotRestoreResult> RestoreAsync(
        string domain,
        string deployId,
        CancellationToken ct)
    {
        var sw = Stopwatch.StartNew();

        var run = await _runs.GetByIdAsync(deployId, ct)
            ?? throw new KeyNotFoundException($"Unknown deploy id: {deployId}");
        if (string.IsNullOrEmpty(run.PreDeployBackupPath))
        {
            throw new InvalidOperationException(
                $"Deploy {deployId} has no pre-deploy snapshot recorded — nothing to restore.");
        }
        var archive = run.PreDeployBackupPath;
        if (!File.Exists(archive))
        {
            throw new FileNotFoundException(
                $"Snapshot archive not found at {archive} (was it deleted by IntentSweeperService or manual cleanup?).",
                archive);
        }
        if (!string.Equals(run.Domain, domain, StringComparison.OrdinalIgnoreCase))
        {
            throw new InvalidOperationException(
                $"Snapshot belongs to domain {run.Domain}, not {domain} — refusing cross-site restore.");
        }

        var site = _sites.GetSite(domain)
            ?? throw new InvalidOperationException(
                $"Site '{domain}' not found — cannot resolve restore target.");

        // Peek the gz header to decide which restore mode to use.
        var mode = await DetectArchiveModeAsync(archive, ct);
        switch (mode)
        {
            case "scaffold":
                throw new InvalidOperationException(
                    "Archive is a SCAFFOLD stub (no real dump was produced) — restore refused.");
            case "sqlite":
            {
                var sqlitePath = TryFindSqliteFileForSite(site.DocumentRoot)
                    ?? throw new InvalidOperationException(
                        $"No SQLite database file found under {site.DocumentRoot} — cannot match restore target.");
                var bytes = await RestoreSqliteAsync(archive, sqlitePath, ct);
                sw.Stop();
                _logger.LogInformation(
                    "Restored SQLite snapshot for {Domain} from {Archive} → {Live} ({Bytes} bytes, {Ms} ms)",
                    domain, archive, sqlitePath, bytes, sw.ElapsedMilliseconds);
                return new SnapshotRestoreResult("sqlite", bytes, sw.Elapsed);
            }
            case "sql":
            {
                var envConn = EnvFileDatabaseResolver.TryResolve(site.DocumentRoot)
                    ?? throw new InvalidOperationException(
                        ".env discovery failed — cannot resolve target DB credentials for restore.");
                if (envConn.Type is "mysql" or "mariadb")
                {
                    var mysql = TryFindMysqlClient()
                        ?? throw new InvalidOperationException(
                            "mysql client binary not found in WdcPaths.BinariesRoot or PATH.");
                    var bytes = await RestoreMysqlAsync(mysql, envConn, archive, ct);
                    sw.Stop();
                    _logger.LogInformation(
                        "Restored mysql snapshot for {Domain} into {Db}@{Host}:{Port} ({Bytes} bytes, {Ms} ms)",
                        domain, envConn.Database, envConn.Host, envConn.Port, bytes, sw.ElapsedMilliseconds);
                    return new SnapshotRestoreResult("mysql", bytes, sw.Elapsed);
                }
                if (envConn.Type is "pgsql")
                {
                    var psql = TryFindPsql()
                        ?? throw new InvalidOperationException(
                            "psql client binary not found in WdcPaths.BinariesRoot or PATH.");
                    var bytes = await RestorePgAsync(psql, envConn, archive, ct);
                    sw.Stop();
                    _logger.LogInformation(
                        "Restored pgsql snapshot for {Domain} into {Db}@{Host}:{Port} ({Bytes} bytes, {Ms} ms)",
                        domain, envConn.Database, envConn.Host, envConn.Port, bytes, sw.ElapsedMilliseconds);
                    return new SnapshotRestoreResult("pgsql", bytes, sw.Elapsed);
                }
                throw new InvalidOperationException(
                    $".env DB type '{envConn.Type}' is not yet supported for restore.");
            }
            default:
                throw new InvalidOperationException(
                    $"Unrecognised archive type '{mode}' — cannot restore.");
        }
    }

    /// <summary>
    /// Sniff archive content to decide route. SQLite files start with the
    /// 16-byte string "SQLite format 3\0"; SCAFFOLD stubs start with the
    /// "-- NKS WDC pre-deploy snapshot SCAFFOLD" comment; everything else
    /// is treated as a SQL text dump.
    /// </summary>
    private static async Task<string> DetectArchiveModeAsync(string archivePath, CancellationToken ct)
    {
        await using var fs = File.OpenRead(archivePath);
        await using var gz = new GZipStream(fs, CompressionMode.Decompress);
        var buffer = new byte[64];
        var read = await gz.ReadAsync(buffer.AsMemory(), ct);
        if (read == 0) return "empty";
        var asText = Encoding.UTF8.GetString(buffer, 0, read);
        if (asText.StartsWith("SQLite format 3", StringComparison.Ordinal)) return "sqlite";
        if (asText.Contains("SCAFFOLD", StringComparison.Ordinal)) return "scaffold";
        return "sql";
    }

    private static async Task<long> RestoreSqliteAsync(string archive, string livePath, CancellationToken ct)
    {
        // Safety copy first — overwrite is irreversible. Timestamp-suffix
        // so consecutive restores don't clobber each other's safeties.
        var safety = livePath + $".pre-restore.{DateTime.UtcNow:yyyyMMdd-HHmmss}.bak";
        File.Copy(livePath, safety, overwrite: false);

        // Inline using-blocks (NOT method-scoped await using) so dst is
        // flushed + closed BEFORE we measure FileInfo.Length. Otherwise
        // the buffered writes are still pending and the size comes back
        // as 0 / partial, and the caller sees a sharing violation on
        // Windows when it tries to read the live file.
        await using (var src = File.OpenRead(archive))
        await using (var gz = new GZipStream(src, CompressionMode.Decompress))
        await using (var dst = File.Create(livePath))
        {
            await gz.CopyToAsync(dst, ct);
        }
        return new FileInfo(livePath).Length;
    }

    private static string? TryFindSqliteFileForSite(string documentRoot)
    {
        // Same scan paths as PreDeploySnapshotter — keeps the asymmetric
        // restore consistent with what the snapshotter would have picked
        // when the archive was created.
        var siteRoot = Directory.GetParent(documentRoot)?.FullName ?? documentRoot;
        string[] dirs = { siteRoot, Path.Combine(siteRoot, "app"),
            Path.Combine(siteRoot, "var"), Path.Combine(siteRoot, "data"),
            Path.Combine(siteRoot, "db"), Path.Combine(siteRoot, "database"),
            documentRoot };
        string[] exts = { "*.sqlite", "*.sqlite3", "*.db" };
        foreach (var dir in dirs)
        {
            if (!Directory.Exists(dir)) continue;
            foreach (var ext in exts)
            {
                try
                {
                    var hit = Directory.EnumerateFiles(dir, ext).FirstOrDefault();
                    if (hit is not null) return hit;
                }
                catch (UnauthorizedAccessException) { /* skip */ }
            }
        }
        return null;
    }

    private async Task<long> RestoreMysqlAsync(
        string mysqlClient,
        EnvFileDatabaseResolver.DatabaseConnection conn,
        string archive,
        CancellationToken ct)
    {
        string? defaultsFile = null;
        try
        {
            var defaultsContent =
                "[client]\n" +
                $"user={conn.User}\n" +
                (string.IsNullOrEmpty(conn.Password) ? "" : $"password={conn.Password}\n") +
                $"host={conn.Host}\n" +
                $"port={conn.Port}\n";
            defaultsFile = Path.Combine(Path.GetTempPath(),
                $"wdc-mysqlrestore-{Guid.NewGuid():N}.cnf");
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

            // Stream gunzip → mysql client stdin.
            await using var src = File.OpenRead(archive);
            await using var gz = new GZipStream(src, CompressionMode.Decompress);
            var result = await Cli.Wrap(mysqlClient)
                .WithArguments(new[]
                {
                    $"--defaults-extra-file={defaultsFile}",
                    conn.Database,
                })
                .WithStandardInputPipe(PipeSource.FromStream(gz))
                .WithValidation(CommandResultValidation.None)
                .ExecuteBufferedAsync(ct);
            if (result.ExitCode != 0)
            {
                throw new InvalidOperationException(
                    $"mysql restore exit {result.ExitCode}: {result.StandardError.Trim()}");
            }
            return new FileInfo(archive).Length;
        }
        finally
        {
            if (defaultsFile is not null)
            {
                try { File.Delete(defaultsFile); } catch { /* best effort */ }
            }
        }
    }

    private async Task<long> RestorePgAsync(
        string psql,
        EnvFileDatabaseResolver.DatabaseConnection conn,
        string archive,
        CancellationToken ct)
    {
        await using var src = File.OpenRead(archive);
        await using var gz = new GZipStream(src, CompressionMode.Decompress);
        var cmd = Cli.Wrap(psql)
            .WithArguments(new[]
            {
                $"--host={conn.Host}",
                $"--port={conn.Port}",
                $"--username={conn.User}",
                $"--dbname={conn.Database}",
                "--no-password",
                "--single-transaction",
                "--set", "ON_ERROR_STOP=1",
            })
            .WithStandardInputPipe(PipeSource.FromStream(gz))
            .WithValidation(CommandResultValidation.None);
        if (!string.IsNullOrEmpty(conn.Password))
        {
            cmd = cmd.WithEnvironmentVariables(env => env.Set("PGPASSWORD", conn.Password));
        }
        var result = await cmd.ExecuteBufferedAsync(ct);
        if (result.ExitCode != 0)
        {
            throw new InvalidOperationException(
                $"psql restore exit {result.ExitCode}: {result.StandardError.Trim()}");
        }
        return new FileInfo(archive).Length;
    }

    // Same probe pattern as PreDeploySnapshotter — duplication is fine here
    // (both files in the same folder, easy to keep in sync).
    private static string? TryFindMysqlClient()
    {
        var ext = OperatingSystem.IsWindows() ? ".exe" : "";
        foreach (var rootName in new[] { "mysql", "mariadb" })
        {
            var managedRoot = Path.Combine(WdcPaths.BinariesRoot, rootName);
            if (!Directory.Exists(managedRoot)) continue;
            foreach (var versionDir in Directory.EnumerateDirectories(managedRoot))
            {
                var candidate = Path.Combine(versionDir, "bin", "mysql" + ext);
                if (File.Exists(candidate)) return candidate;
            }
        }
        return ProbePath("mysql" + ext);
    }

    private static string? TryFindPsql()
    {
        var ext = OperatingSystem.IsWindows() ? ".exe" : "";
        foreach (var rootName in new[] { "postgres", "postgresql" })
        {
            var managedRoot = Path.Combine(WdcPaths.BinariesRoot, rootName);
            if (!Directory.Exists(managedRoot)) continue;
            foreach (var versionDir in Directory.EnumerateDirectories(managedRoot))
            {
                var candidate = Path.Combine(versionDir, "bin", "psql" + ext);
                if (File.Exists(candidate)) return candidate;
            }
        }
        return ProbePath("psql" + ext);
    }

    private static string? ProbePath(string fileName)
    {
        var pathEnv = Environment.GetEnvironmentVariable("PATH") ?? "";
        var pathSep = OperatingSystem.IsWindows() ? ';' : ':';
        foreach (var dir in pathEnv.Split(pathSep, StringSplitOptions.RemoveEmptyEntries))
        {
            var candidate = Path.Combine(dir.Trim(), fileName);
            if (File.Exists(candidate)) return candidate;
        }
        return null;
    }
}
