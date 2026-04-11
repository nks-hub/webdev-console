using System.IO.Compression;
using Microsoft.Extensions.Logging;

namespace NKS.WebDevConsole.Daemon.Backup;

/// <summary>
/// Creates and restores zip archives of the user's NKS WebDev Console state.
///
/// What goes into a backup:
///   - <c>~/.wdc/sites/</c> — site TOML configs
///   - <c>~/.wdc/data/state.db</c> — SQLite runtime state
///   - <c>~/.wdc/ssl/sites/</c> — per-site mkcert certificates
///   - <c>~/.wdc/caddy/Caddyfile</c> — caddy config (if present)
///
/// What is intentionally excluded:
///   - <c>binaries/</c> — large, re-downloadable from catalog
///   - <c>generated/</c> — re-derivable from sites/
///   - <c>data/mysql/</c> — database files (huge, user can dump separately)
///   - <c>data/mysql-root.dpapi</c> — DPAPI-encrypted, only valid for the original
///     Windows user profile, including it in a backup creates a false sense of
///     portability and is a footgun on restore to a different machine.
///
/// Restore is atomic: extracts to a temp directory first, then renames over the
/// existing layout. Pre-restore the current state is itself backed up to
/// <c>~/.wdc/backups/auto-pre-restore-&lt;timestamp&gt;.zip</c> so a bad restore can
/// always be undone.
/// </summary>
public sealed class BackupManager
{
    private readonly ILogger<BackupManager> _logger;
    private readonly string _wdcRoot;
    private readonly string _backupRoot;

    private static readonly string[] IncludedSubdirs =
    {
        "sites",
        "ssl/sites",
        "caddy",
    };

    private static readonly string[] IncludedFiles =
    {
        "data/state.db",
    };

    public BackupManager(ILogger<BackupManager> logger)
    {
        _logger = logger;
        _wdcRoot = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.UserProfile),
            ".wdc");
        _backupRoot = Path.Combine(_wdcRoot, "backups");
        Directory.CreateDirectory(_backupRoot);
    }

    public string DefaultBackupPath()
    {
        var timestamp = DateTime.UtcNow.ToString("yyyyMMdd-HHmmss");
        return Path.Combine(_backupRoot, $"wdc-backup-{timestamp}.zip");
    }

    /// <summary>
    /// Creates a zip backup at the requested path. Returns the absolute path of
    /// the resulting archive and the number of files included.
    /// </summary>
    public (string Path, int FileCount, long SizeBytes) CreateBackup(string? outputPath = null)
    {
        var target = string.IsNullOrWhiteSpace(outputPath) ? DefaultBackupPath() : Path.GetFullPath(outputPath);
        Directory.CreateDirectory(Path.GetDirectoryName(target)!);

        if (File.Exists(target)) File.Delete(target);

        int count = 0;
        using (var fs = File.Create(target))
        using (var zip = new ZipArchive(fs, ZipArchiveMode.Create))
        {
            // Add manifest first so future restores can validate compatibility
            var manifest = $"{{\"created\":\"{DateTime.UtcNow:O}\",\"source\":\"{Environment.MachineName}\",\"version\":\"1\"}}";
            var manifestEntry = zip.CreateEntry("manifest.json", CompressionLevel.Optimal);
            using (var writer = new StreamWriter(manifestEntry.Open()))
                writer.Write(manifest);
            count++;

            foreach (var sub in IncludedSubdirs)
            {
                var src = Path.Combine(_wdcRoot, sub.Replace('/', Path.DirectorySeparatorChar));
                if (!Directory.Exists(src)) continue;
                foreach (var file in Directory.EnumerateFiles(src, "*", SearchOption.AllDirectories))
                {
                    var rel = Path.GetRelativePath(_wdcRoot, file).Replace(Path.DirectorySeparatorChar, '/');
                    AddFileToZip(zip, file, rel);
                    count++;
                }
            }

            foreach (var rel in IncludedFiles)
            {
                var src = Path.Combine(_wdcRoot, rel.Replace('/', Path.DirectorySeparatorChar));
                if (!File.Exists(src)) continue;
                AddFileToZip(zip, src, rel);
                count++;
            }
        }

        var size = new FileInfo(target).Length;
        _logger.LogInformation("Backup created: {Path} ({Files} files, {Size} bytes)", target, count, size);
        return (target, count, size);
    }

    private static void AddFileToZip(ZipArchive zip, string sourcePath, string entryName)
    {
        var entry = zip.CreateEntry(entryName, CompressionLevel.Optimal);
        using var entryStream = entry.Open();
        // FileShare.ReadWrite | Delete is required because state.db is held open by
        // the running daemon (SQLite WAL). Plain File.OpenRead would throw IOException.
        // For consistency we read the SQLite snapshot the OS gives us; for a strictly
        // consistent point-in-time copy a future version can wire SqliteConnection.BackupDatabase.
        using var fs = new FileStream(sourcePath, FileMode.Open, FileAccess.Read,
            FileShare.ReadWrite | FileShare.Delete);
        fs.CopyTo(entryStream);
    }

    /// <summary>
    /// Restores a backup zip into the user's NKS WebDev Console state. Before
    /// touching anything an automatic safety backup of the current state is
    /// written, so the operation can be reversed manually if needed.
    /// </summary>
    public (int RestoredFiles, string SafetyBackupPath) RestoreBackup(string archivePath)
    {
        if (!File.Exists(archivePath))
            throw new FileNotFoundException("Backup archive not found", archivePath);

        var safetyBackup = Path.Combine(_backupRoot, $"auto-pre-restore-{DateTime.UtcNow:yyyyMMdd-HHmmss}.zip");
        try
        {
            CreateBackup(safetyBackup);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Failed to create pre-restore safety backup — aborting restore");
            throw;
        }

        // Extract to a temp directory first so a corrupt zip can't half-replace
        var tempExtract = Path.Combine(_backupRoot, $"restore-staging-{Guid.NewGuid():N}");
        Directory.CreateDirectory(tempExtract);
        try
        {
            using (var fs = File.OpenRead(archivePath))
            using (var zip = new ZipArchive(fs, ZipArchiveMode.Read))
            {
                int count = 0;
                foreach (var entry in zip.Entries)
                {
                    if (string.IsNullOrEmpty(entry.Name)) continue; // directory entry
                    if (entry.FullName.Contains("..")) continue;     // zip-slip defense
                    var dest = Path.Combine(tempExtract, entry.FullName.Replace('/', Path.DirectorySeparatorChar));
                    var destFull = Path.GetFullPath(dest);
                    if (!destFull.StartsWith(Path.GetFullPath(tempExtract), StringComparison.OrdinalIgnoreCase))
                        continue;
                    Directory.CreateDirectory(Path.GetDirectoryName(destFull)!);
                    using var entryStream = entry.Open();
                    using var outFs = File.Create(destFull);
                    entryStream.CopyTo(outFs);
                    count++;
                }

                // Promote staged files into the live ~/.wdc/ tree
                var promoted = 0;
                foreach (var sub in IncludedSubdirs)
                {
                    var src = Path.Combine(tempExtract, sub.Replace('/', Path.DirectorySeparatorChar));
                    var dst = Path.Combine(_wdcRoot, sub.Replace('/', Path.DirectorySeparatorChar));
                    if (!Directory.Exists(src)) continue;
                    if (Directory.Exists(dst)) Directory.Delete(dst, recursive: true);
                    Directory.CreateDirectory(Path.GetDirectoryName(dst)!);
                    Directory.Move(src, dst);
                    promoted += Directory.EnumerateFiles(dst, "*", SearchOption.AllDirectories).Count();
                }

                foreach (var rel in IncludedFiles)
                {
                    var src = Path.Combine(tempExtract, rel.Replace('/', Path.DirectorySeparatorChar));
                    var dst = Path.Combine(_wdcRoot, rel.Replace('/', Path.DirectorySeparatorChar));
                    if (!File.Exists(src)) continue;
                    Directory.CreateDirectory(Path.GetDirectoryName(dst)!);
                    File.Copy(src, dst, overwrite: true);
                    promoted++;
                }

                _logger.LogInformation("Restored {Count} files from {Archive}", promoted, archivePath);
                return (promoted, safetyBackup);
            }
        }
        finally
        {
            try { if (Directory.Exists(tempExtract)) Directory.Delete(tempExtract, recursive: true); } catch { }
        }
    }

    public IReadOnlyList<(string Path, long Size, DateTime Created)> ListBackups()
    {
        if (!Directory.Exists(_backupRoot)) return Array.Empty<(string, long, DateTime)>();
        return Directory.GetFiles(_backupRoot, "*.zip")
            .Select(p => (Path: p, Size: new FileInfo(p).Length, Created: File.GetCreationTimeUtc(p)))
            .OrderByDescending(x => x.Created)
            .ToList();
    }
}
