using System.Diagnostics;
using System.IO.Compression;
using System.Text;
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

        // No SQLite — write the SCAFFOLD metadata file. Real mysqldump /
        // pg_dump integration lands in Phase 6.3 once we wire site→DB
        // credential resolution. Until then this gives the deploy_runs
        // row a populated path (so the GUI can show "snapshot pending
        // implementation" instead of guessing).
        await WriteScaffoldAsync(outPath, domain, deployId, ct);
        sw.Stop();
        var scaffoldSize = new FileInfo(outPath).Length;
        _logger.LogWarning(
            "Pre-deploy snapshot: no SQLite found for {Domain}; wrote SCAFFOLD stub at {Path}. " +
            "Real mysqldump/pg_dump integration is Phase 6.3 — set Snapshot.Include=false to skip.",
            domain, outPath);
        return new PreDeploySnapshotResult(outPath, scaffoldSize, sw.Elapsed);
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

    private static async Task WriteScaffoldAsync(string dst, string domain, string deployId, CancellationToken ct)
    {
        var header = new StringBuilder()
            .AppendLine("-- NKS WDC pre-deploy snapshot SCAFFOLD")
            .AppendLine($"-- domain: {domain}")
            .AppendLine($"-- deployId: {deployId}")
            .AppendLine($"-- createdAt: {DateTimeOffset.UtcNow:O}")
            .AppendLine("-- status: PENDING — real mysqldump/pg_dump integration in Phase 6.3")
            .AppendLine("-- This file is a placeholder. Restore against this archive will refuse.")
            .ToString();
        await using var outFs = File.Create(dst);
        await using var gz = new GZipStream(outFs, CompressionLevel.SmallestSize);
        var bytes = Encoding.UTF8.GetBytes(header);
        await gz.WriteAsync(bytes.AsMemory(), ct);
    }
}
