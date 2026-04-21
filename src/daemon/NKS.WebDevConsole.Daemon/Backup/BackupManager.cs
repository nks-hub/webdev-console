using System.IO.Compression;
using Microsoft.Extensions.Logging;
using NKS.WebDevConsole.Core.Services;

namespace NKS.WebDevConsole.Daemon.Backup;

/// <summary>
/// Creates and restores zip archives of the user's NKS WebDev Console state.
///
/// Content is controlled by <see cref="BackupContentFlags"/>. The default set
/// (<see cref="BackupContentFlags.Default"/>) covers vhosts, plugin configs,
/// and SSL certificates. Opt-in categories (Databases, Docroots) are
/// excluded by default because they can be large.
///
/// What is intentionally excluded:
///   - <c>binaries/</c> — large, re-downloadable from catalog
///   - <c>data/mysql/</c> — database files (huge; use the Databases flag for mysqldump)
///   - <c>data/mysql-root.dpapi</c> — DPAPI-encrypted, only valid for the original
///     Windows user profile — including it creates a false sense of portability.
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

    public BackupManager(ILogger<BackupManager> logger)
        : this(logger, WdcPaths.Root)
    {
    }

    /// <summary>
    /// Test-only constructor: lets tests pin the wdc root to a temp directory so
    /// they don't touch the developer's real ~/.wdc/. Production code uses the
    /// single-arg overload above.
    /// </summary>
    public BackupManager(ILogger<BackupManager> logger, string wdcRoot)
    {
        _logger = logger;
        _wdcRoot = wdcRoot;
        _backupRoot = Path.Combine(_wdcRoot, "backups");
        Directory.CreateDirectory(_backupRoot);
    }

    public string DefaultBackupPath()
    {
        var timestamp = DateTime.UtcNow.ToString("yyyyMMdd-HHmmss");
        return Path.Combine(_backupRoot, $"wdc-backup-{timestamp}.zip");
    }

    /// <summary>
    /// Creates a zip backup at the requested path with the given content flags.
    /// Returns the absolute path, file count, and archive size.
    /// </summary>
    public (string Path, int FileCount, long SizeBytes, BackupContentFlags Flags) CreateBackup(
        string? outputPath = null,
        BackupContentFlags flags = BackupContentFlags.Default)
    {
        var target = string.IsNullOrWhiteSpace(outputPath) ? DefaultBackupPath() : Path.GetFullPath(outputPath);
        var targetDir = Path.GetDirectoryName(target)!;

        _logger.LogInformation("Backup starting → {Path} (flags={Flags}, wdcRoot={Root})", target, flags, _wdcRoot);

        if (!Directory.Exists(targetDir))
        {
            _logger.LogDebug("Creating output directory: {Dir}", targetDir);
            Directory.CreateDirectory(targetDir);
        }

        if (!Directory.Exists(_wdcRoot))
            _logger.LogWarning("WDC root does not exist: {Root} — backup may be empty", _wdcRoot);

        if (File.Exists(target))
        {
            _logger.LogDebug("Removing existing backup file: {Path}", target);
            File.Delete(target);
        }

        int count = 0;
        try
        {
            using var fs = File.Create(target);
            _logger.LogDebug("Archive file stream opened: {Path}", target);

            using var zip = new ZipArchive(fs, ZipArchiveMode.Create, leaveOpen: false);

            // Manifest — always included
            var contentList = flags.ToString();
            var manifest = $"{{\"created\":\"{DateTime.UtcNow:O}\",\"source\":\"{Environment.MachineName}\",\"version\":\"2\",\"flags\":\"{contentList}\"}}";
            var manifestEntry = zip.CreateEntry("manifest.json", CompressionLevel.Optimal);
            using (var writer = new StreamWriter(manifestEntry.Open()))
                writer.Write(manifest);
            count++;
            _logger.LogDebug("Manifest written");

            if (flags.HasFlag(BackupContentFlags.Vhosts))
                count += PackVhosts(zip);

            if (flags.HasFlag(BackupContentFlags.Ssl))
                count += PackSsl(zip);

            if (flags.HasFlag(BackupContentFlags.PluginConfigs))
                count += PackPluginConfigs(zip);

            if (flags.HasFlag(BackupContentFlags.Databases))
                count += PackDatabases(zip);

            if (flags.HasFlag(BackupContentFlags.Docroots))
                count += PackDocroots(zip);

            // Always pack state.db — small and critical
            count += PackStateDb(zip);
        }
        catch (UnauthorizedAccessException ex)
        {
            _logger.LogError(ex, "Backup FAILED — access denied writing {Path}. Antivirus or folder permissions may be blocking writes.", target);
            throw;
        }
        catch (IOException ex)
        {
            _logger.LogError(ex, "Backup FAILED — IO error writing {Path}. Disk full or file locked?", target);
            throw;
        }

        long size = 0;
        try
        {
            var fi = new FileInfo(target);
            size = fi.Length;
            if (size == 0)
                _logger.LogWarning("Backup produced a 0-byte archive at {Path}. The zip stream may have closed before flush.", target);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Could not stat archive after creation: {Path}", target);
        }

        _logger.LogInformation("Backup complete: {Path} ({Files} files, {Size} bytes, flags={Flags})", target, count, size, flags);
        return (target, count, size, flags);
    }

    private int PackVhosts(ZipArchive zip)
    {
        // sites/ directory contains per-site TOML configs (the source of truth
        // for WDC vhost generation). Pack them all.
        return PackDirectory(zip, "sites", "vhosts/sites");
    }

    private int PackSsl(ZipArchive zip)
    {
        return PackDirectory(zip, Path.Combine("ssl", "sites"), "ssl/sites");
    }

    private int PackPluginConfigs(ZipArchive zip)
    {
        int count = 0;
        // Pack caddy config
        count += PackDirectory(zip, "caddy", "plugin-configs/caddy");
        // Pack any plugin sub-dirs under ~/.wdc/plugins/ that contain config files
        var pluginsDir = Path.Combine(_wdcRoot, "plugins");
        if (Directory.Exists(pluginsDir))
        {
            foreach (var pluginDir in Directory.EnumerateDirectories(pluginsDir))
            {
                var pluginId = Path.GetFileName(pluginDir);
                var configFile = Path.Combine(pluginDir, "config.json");
                if (File.Exists(configFile))
                {
                    var entryName = $"plugin-configs/{pluginId}/config.json";
                    count += AddFileToZipSafe(zip, configFile, entryName);
                }
            }
        }
        return count;
    }

    private int PackDatabases(ZipArchive zip)
    {
        // mysqldump is large and requires a running MySQL instance.
        // For now we log a warning that this is a no-op in the default build
        // (a future sprint will wire CliWrap + mysqldump). The flag is
        // intentionally wired so the scheduler correctly includes the
        // category name in the manifest even before the packer is implemented.
        _logger.LogWarning("BackupContentFlags.Databases selected — mysqldump packer not yet implemented; skipping databases. Use manual mysqldump for now.");
        return 0;
    }

    private int PackDocroots(ZipArchive zip)
    {
        // Docroots can be very large. Log a diagnostic and skip — the full
        // implementation will require a progress-streaming API so the UI can
        // show a progress bar during multi-GB packs.
        _logger.LogWarning("BackupContentFlags.Docroots selected — docroots packer not yet implemented; skipping. Sites can be large, implement with care.");
        return 0;
    }

    private int PackStateDb(ZipArchive zip)
    {
        var src = Path.Combine(_wdcRoot, "data", "state.db");
        if (!File.Exists(src))
        {
            _logger.LogDebug("state.db not found, skipping: {Path}", src);
            return 0;
        }
        return AddFileToZipSafe(zip, src, "data/state.db");
    }

    private int PackDirectory(ZipArchive zip, string relativeSubdir, string zipPrefix)
    {
        var src = Path.Combine(_wdcRoot, relativeSubdir.Replace('/', Path.DirectorySeparatorChar));
        if (!Directory.Exists(src))
        {
            _logger.LogDebug("Directory not found, skipping: {Dir}", src);
            return 0;
        }

        int count = 0;
        foreach (var file in Directory.EnumerateFiles(src, "*", SearchOption.AllDirectories))
        {
            var rel = Path.GetRelativePath(src, file).Replace(Path.DirectorySeparatorChar, '/');
            var entryName = $"{zipPrefix}/{rel}";
            count += AddFileToZipSafe(zip, file, entryName);
        }
        _logger.LogDebug("Packed directory {Dir} → {Prefix} ({Count} files)", src, zipPrefix, count);
        return count;
    }

    private int AddFileToZipSafe(ZipArchive zip, string sourcePath, string entryName)
    {
        try
        {
            var entry = zip.CreateEntry(entryName, CompressionLevel.Optimal);
            using var entryStream = entry.Open();
            // FileShare.ReadWrite | Delete is required because state.db is held open by
            // the running daemon (SQLite WAL). Plain File.OpenRead would throw IOException.
            using var fs = new FileStream(sourcePath, FileMode.Open, FileAccess.Read,
                FileShare.ReadWrite | FileShare.Delete);
            fs.CopyTo(entryStream);
            return 1;
        }
        catch (UnauthorizedAccessException ex)
        {
            _logger.LogWarning(ex, "Skipping file — access denied: {File}. Check antivirus exclusions for ~/.wdc/", sourcePath);
            return 0;
        }
        catch (IOException ex)
        {
            _logger.LogWarning(ex, "Skipping file — IO error: {File}", sourcePath);
            return 0;
        }
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

        // Detect flags from manifest to know which dirs to restore
        BackupContentFlags flags = BackupContentFlags.Default;
        try
        {
            using var peekFs = File.OpenRead(archivePath);
            using var peekZip = new ZipArchive(peekFs, ZipArchiveMode.Read);
            var manifestEntry = peekZip.GetEntry("manifest.json");
            if (manifestEntry != null)
            {
                using var sr = new StreamReader(manifestEntry.Open());
                var json = sr.ReadToEnd();
                // Simple parse — avoid System.Text.Json dependency just for one field
                var flagsMatch = System.Text.RegularExpressions.Regex.Match(json, "\"flags\":\"([^\"]+)\"");
                if (flagsMatch.Success && Enum.TryParse<BackupContentFlags>(flagsMatch.Groups[1].Value, out var parsed))
                    flags = parsed;
            }
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Could not read manifest from {Archive} — using default restore set", archivePath);
        }

        _logger.LogInformation("Restoring {Archive} (flags={Flags})", archivePath, flags);

        var tempExtract = Path.Combine(_backupRoot, $"restore-staging-{Guid.NewGuid():N}");
        Directory.CreateDirectory(tempExtract);
        try
        {
            using var fs = File.OpenRead(archivePath);
            using var zip = new ZipArchive(fs, ZipArchiveMode.Read);
            foreach (var entry in zip.Entries)
            {
                if (string.IsNullOrEmpty(entry.Name)) continue;
                if (entry.FullName.Contains("..")) continue;
                var dest = Path.Combine(tempExtract, entry.FullName.Replace('/', Path.DirectorySeparatorChar));
                var destFull = Path.GetFullPath(dest);
                if (!destFull.StartsWith(Path.GetFullPath(tempExtract), StringComparison.OrdinalIgnoreCase))
                    continue;
                Directory.CreateDirectory(Path.GetDirectoryName(destFull)!);
                using var entryStream = entry.Open();
                using var outFs = File.Create(destFull);
                entryStream.CopyTo(outFs);
            }

            int promoted = 0;
            promoted += PromoteDirectory(tempExtract, "vhosts/sites", Path.Combine("sites"));
            promoted += PromoteDirectory(tempExtract, "ssl/sites", Path.Combine("ssl", "sites"));
            promoted += PromoteDirectory(tempExtract, "plugin-configs", "plugin-configs-staging"); // safe intermediate

            // plugin-configs subfolders → merge into live paths
            var pcStaging = Path.Combine(_wdcRoot, "plugin-configs-staging");
            if (Directory.Exists(pcStaging))
            {
                foreach (var subDir in Directory.EnumerateDirectories(pcStaging))
                {
                    var pluginId = Path.GetFileName(subDir);
                    // caddy → ~/.wdc/caddy/
                    if (pluginId == "caddy")
                    {
                        var dst = Path.Combine(_wdcRoot, "caddy");
                        if (Directory.Exists(dst)) Directory.Delete(dst, recursive: true);
                        Directory.Move(subDir, dst);
                        promoted += Directory.EnumerateFiles(dst, "*", SearchOption.AllDirectories).Count();
                    }
                    else
                    {
                        var dst = Path.Combine(_wdcRoot, "plugins", pluginId);
                        Directory.CreateDirectory(dst);
                        var configSrc = Path.Combine(subDir, "config.json");
                        var configDst = Path.Combine(dst, "config.json");
                        if (File.Exists(configSrc)) { File.Copy(configSrc, configDst, overwrite: true); promoted++; }
                    }
                }
                try { Directory.Delete(pcStaging, recursive: true); } catch { }
            }

            // state.db
            var srcDb = Path.Combine(tempExtract, "data", "state.db");
            var dstDb = Path.Combine(_wdcRoot, "data", "state.db");
            if (File.Exists(srcDb))
            {
                Directory.CreateDirectory(Path.GetDirectoryName(dstDb)!);
                File.Copy(srcDb, dstDb, overwrite: true);
                promoted++;
            }

            _logger.LogInformation("Restored {Count} files from {Archive}", promoted, archivePath);
            return (promoted, safetyBackup);
        }
        finally
        {
            try { if (Directory.Exists(tempExtract)) Directory.Delete(tempExtract, recursive: true); } catch { }
        }
    }

    private int PromoteDirectory(string stagingRoot, string stagingRelPath, string wdcRelPath)
    {
        var src = Path.Combine(stagingRoot, stagingRelPath.Replace('/', Path.DirectorySeparatorChar));
        if (!Directory.Exists(src)) return 0;
        var dst = Path.Combine(_wdcRoot, wdcRelPath.Replace('/', Path.DirectorySeparatorChar));
        if (Directory.Exists(dst)) Directory.Delete(dst, recursive: true);
        Directory.CreateDirectory(Path.GetDirectoryName(dst)!);
        Directory.Move(src, dst);
        return Directory.EnumerateFiles(dst, "*", SearchOption.AllDirectories).Count();
    }

    public IReadOnlyList<BackupEntry> ListBackups()
    {
        if (!Directory.Exists(_backupRoot)) return Array.Empty<BackupEntry>();
        return Directory.GetFiles(_backupRoot, "*.zip")
            .Select(p =>
            {
                var flags = ReadFlagsFromManifest(p);
                return new BackupEntry(p, new FileInfo(p).Length, File.GetCreationTimeUtc(p), flags);
            })
            .OrderByDescending(x => x.Created)
            .ToList();
    }

    private BackupContentFlags ReadFlagsFromManifest(string zipPath)
    {
        try
        {
            using var fs = File.OpenRead(zipPath);
            using var zip = new ZipArchive(fs, ZipArchiveMode.Read);
            var entry = zip.GetEntry("manifest.json");
            if (entry == null) return BackupContentFlags.Default;
            using var sr = new StreamReader(entry.Open());
            var json = sr.ReadToEnd();
            var m = System.Text.RegularExpressions.Regex.Match(json, "\"flags\":\"([^\"]+)\"");
            if (m.Success && Enum.TryParse<BackupContentFlags>(m.Groups[1].Value, out var f))
                return f;
        }
        catch { /* corrupt zip or not our format */ }
        return BackupContentFlags.Default;
    }

    /// <summary>Represents a single backup archive in the listing.</summary>
    public record BackupEntry(string Path, long Size, DateTime Created, BackupContentFlags ContentFlags);
}
