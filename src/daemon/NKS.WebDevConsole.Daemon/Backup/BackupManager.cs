using System.Globalization;
using System.IO.Compression;
using System.Text.Json;
using Microsoft.Data.Sqlite;
using Microsoft.Extensions.Logging;
using NKS.WebDevConsole.Core.Interfaces;
using NKS.WebDevConsole.Core.Services;
using NKS.WebDevConsole.Daemon.Data;

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
    private readonly ISiteRegistry? _sites;
    private readonly IMysqlBackupClient? _mysql;
    private readonly ConfigHistoryRepository? _history;

    public BackupManager(ILogger<BackupManager> logger, ISiteRegistry sites, IMysqlBackupClient mysql,
        ConfigHistoryRepository? history = null)
        : this(logger, WdcPaths.Root, sites, mysql, history)
    {
    }

    /// <summary>
    /// Test constructor: lets tests pin the wdc root to a temp directory so
    /// they don't touch the developer's real ~/.wdc/. Without a site registry
    /// or dumper the Docroots and Databases categories are reported as skipped.
    /// </summary>
    public BackupManager(ILogger<BackupManager> logger, string wdcRoot,
        ISiteRegistry? sites = null, IMysqlBackupClient? mysql = null, ConfigHistoryRepository? history = null)
    {
        _logger = logger;
        _wdcRoot = wdcRoot;
        _backupRoot = Path.Combine(_wdcRoot, "backups");
        _sites = sites;
        _mysql = mysql;
        _history = history;
        Directory.CreateDirectory(_backupRoot);
    }

    /// <summary>Folder that holds the backup archives (<c>{wdcRoot}/backups</c>).</summary>
    public string BackupRoot => _backupRoot;

    public string DefaultBackupPath() => UniquePath("wdc-backup");

    private string UniquePath(string prefix)
    {
        // Local time, the clock the Backups page shows: a UTC name read an hour
        // or two off next to "Last backup 22:10".
        var timestamp = DateTime.Now.ToString("yyyyMMdd-HHmmss", System.Globalization.CultureInfo.InvariantCulture);
        var path = Path.Combine(_backupRoot, $"{prefix}-{timestamp}.zip");
        for (var i = 2; File.Exists(path); i++)
            path = Path.Combine(_backupRoot, $"{prefix}-{timestamp}-{i}.zip");
        return path;
    }

    /// <summary>
    /// File-name prefix of backups written by <see cref="BackupScheduler"/>.
    /// Manual and pre-uninstall backups use <c>wdc-backup-</c> and restore
    /// safety copies use <c>auto-pre-restore-</c>; only this prefix is ever
    /// pruned by the scheduler.
    /// </summary>
    public const string ScheduledBackupPrefix = "wdc-scheduled-";

    private static readonly System.Text.RegularExpressions.Regex ScheduledBackupName =
        new(@"^wdc-scheduled-\d{8}-\d{6}(-\d+)?\.zip$", System.Text.RegularExpressions.RegexOptions.CultureInvariant);

    /// <summary>Path for a new scheduled backup: <c>wdc-scheduled-yyyyMMdd-HHmmss.zip</c> in the backup root (<c>-2</c>, <c>-3</c>… when that name is taken).</summary>
    public string ScheduledBackupPath() => UniquePath("wdc-scheduled");

    /// <summary>True when <paramref name="path"/> names a scheduler-created backup.</summary>
    public static bool IsScheduledBackup(string path) =>
        ScheduledBackupName.IsMatch(Path.GetFileName(path));

    /// <summary>
    /// Deletes scheduler-created backups in the backup root beyond the
    /// <paramref name="retain"/> newest (ordered by the timestamp in the file
    /// name). Manual backups, restore safety backups and any other zip are
    /// never touched. Returns the deleted paths.
    /// </summary>
    public IReadOnlyList<string> PruneScheduledBackups(int retain)
    {
        if (!Directory.Exists(_backupRoot)) return Array.Empty<string>();
        var deleted = new List<string>();
        var stale = Directory.GetFiles(_backupRoot, ScheduledBackupPrefix + "*.zip")
            .Where(IsScheduledBackup)
            .OrderByDescending(p => Path.GetFileName(p), StringComparer.Ordinal)
            .Skip(Math.Max(retain, 0));
        foreach (var path in stale)
        {
            try
            {
                File.Delete(path);
                deleted.Add(path);
                _logger.LogDebug("Pruned old scheduled backup: {Path}", path);
            }
            catch (Exception ex)
            {
                _logger.LogWarning("Failed to prune backup {Path}: {Error}", path, ex.Message);
            }
        }
        return deleted;
    }

    /// <summary>
    /// Resolves where a backup is written. Without <paramref name="outputPath"/>
    /// it is a new timestamped file in the backup folder. A caller-chosen path
    /// must name a <c>.zip</c> file directly in the backup folder (a bare file
    /// name is resolved there): the archive holds state.db and TLS private
    /// keys, so it must never land, or overwrite a file, anywhere else.
    /// </summary>
    /// <exception cref="ArgumentException">The path is outside the backup folder or not a .zip file.</exception>
    public string ResolveOutputPath(string? outputPath)
    {
        if (string.IsNullOrWhiteSpace(outputPath)) return DefaultBackupPath();

        var root = Path.GetFullPath(_backupRoot);
        string full;
        try { full = Path.GetFullPath(outputPath, root); }
        catch (Exception ex) when (ex is ArgumentException or NotSupportedException or PathTooLongException)
        {
            throw new ArgumentException($"Invalid backup path: {ex.Message}", nameof(outputPath), ex);
        }

        var comparison = OperatingSystem.IsWindows() || OperatingSystem.IsMacOS()
            ? StringComparison.OrdinalIgnoreCase
            : StringComparison.Ordinal;
        var dir = Path.GetDirectoryName(full);
        if (!NKS.WebDevConsole.Daemon.Config.PathContainment.IsUnder(root, full)
            || dir is null
            || !string.Equals(Path.TrimEndingDirectorySeparator(dir), Path.TrimEndingDirectorySeparator(root), comparison))
            throw new ArgumentException($"Backups are written to {root}; pass a file name in that folder.", nameof(outputPath));
        if (!full.EndsWith(".zip", StringComparison.OrdinalIgnoreCase))
            throw new ArgumentException("Backup file name must end in .zip.", nameof(outputPath));
        return full;
    }

    /// <summary>
    /// Creates a zip backup with the given content flags, at
    /// <see cref="ResolveOutputPath"/>. Never overwrites an existing file, and
    /// on Unix the archive is created owner-only (0600) from the start.
    /// Returns the absolute path, file count, archive size, the categories the
    /// archive really holds, and what was left out (MySQL not running, a site
    /// folder missing). The manifest records the same categories as the result.
    /// The backup is listed in the activity feed unless <paramref name="recordActivity"/>
    /// is false (the safety copy a restore makes first is not news).
    /// </summary>
    /// <exception cref="ArgumentException">The path is outside the backup folder or not a .zip file.</exception>
    /// <exception cref="BackupTargetExistsException">A file already exists at the path.</exception>
    public (string Path, int FileCount, long SizeBytes, BackupContentFlags Flags, IReadOnlyList<BackupSkip> Skipped) CreateBackup(
        string? outputPath = null,
        BackupContentFlags flags = BackupContentFlags.Default,
        bool recordActivity = true)
    {
        var target = ResolveOutputPath(outputPath);
        var targetDir = Path.GetDirectoryName(target)!;

        _logger.LogInformation("Backup starting → {Path} (flags={Flags}, wdcRoot={Root})", target, flags, _wdcRoot);

        if (!Directory.Exists(targetDir))
        {
            _logger.LogDebug("Creating output directory: {Dir}", targetDir);
            Directory.CreateDirectory(targetDir);
        }

        if (!Directory.Exists(_wdcRoot))
            _logger.LogWarning("WDC root does not exist: {Root} — backup may be empty", _wdcRoot);

        if (File.Exists(target) || Directory.Exists(target))
            throw new BackupTargetExistsException(target);

        int count = 0;
        var skipped = new List<BackupSkip>();
        // Recorded in the manifest so restore can list them and knows where
        // each site's files go back.
        var dumped = new List<string>();
        var siteRoots = new SortedDictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        try
        {
            using var fs = OpenNewArchive(target);
            _logger.LogDebug("Archive file stream opened: {Path}", target);

            using var zip = new ZipArchive(fs, ZipArchiveMode.Create, leaveOpen: false);

            if (flags.HasFlag(BackupContentFlags.Vhosts))
                count += PackVhosts(zip);

            if (flags.HasFlag(BackupContentFlags.Ssl))
                count += PackSsl(zip);

            if (flags.HasFlag(BackupContentFlags.PluginConfigs))
                count += PackPluginConfigs(zip);

            // null = the whole category could not be packed; it leaves the flags.
            if (flags.HasFlag(BackupContentFlags.Databases))
            {
                if (PackDatabases(zip, skipped, dumped) is int dumpCount) count += dumpCount;
                else flags &= ~BackupContentFlags.Databases;
            }

            if (flags.HasFlag(BackupContentFlags.Docroots))
            {
                if (PackDocroots(zip, skipped, siteRoots) is int siteFiles) count += siteFiles;
                else flags &= ~BackupContentFlags.Docroots;
            }

            // Always pack state.db — small and critical
            count += PackStateDb(zip);

            // Manifest last, so its flags name only what was actually packed.
            var manifest = JsonSerializer.Serialize(new
            {
                created = DateTime.UtcNow.ToString("O", CultureInfo.InvariantCulture),
                source = Environment.MachineName,
                version = "2",
                flags = flags.ToString(),
                databases = dumped,
                docroots = siteRoots,
            });
            var manifestEntry = zip.CreateEntry("manifest.json", CompressionLevel.Optimal);
            using (var writer = new StreamWriter(manifestEntry.Open()))
                writer.Write(manifest);
            count++;
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

        foreach (var skip in skipped)
            _logger.LogWarning("Backup {Path} left out {Content}: {Detail}", target, skip.Content, skip.Detail);
        _logger.LogInformation("Backup complete: {Path} ({Files} files, {Size} bytes, flags={Flags})", target, count, size, flags);
        if (recordActivity) _history?.Record("backup", Path.GetFileName(target), "INSERT");
        return (target, count, size, flags, skipped);
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

    /// <summary>
    /// One <c>databases/{name}.sql</c> per user database (mysqldump with
    /// CREATE DATABASE, so <c>mysql &lt; file</c> restores it). Returns null
    /// when no database could be dumped: MySQL missing, stopped or refusing root.
    /// Names of the packed databases go to <paramref name="dumped"/>.
    /// </summary>
    private int? PackDatabases(ZipArchive zip, List<BackupSkip> skipped, List<string> dumped)
    {
        if (_mysql is null)
        {
            skipped.Add(new(BackupContentFlags.Databases, "MySQL dumps are not available in this process"));
            return null;
        }
        IReadOnlyList<string> databases;
        try
        {
            databases = _mysql.ListDatabases();
        }
        catch (Exception ex)
        {
            skipped.Add(new(BackupContentFlags.Databases, ex.Message));
            return null;
        }

        int count = 0;
        foreach (var database in databases)
        {
            if (!MysqlBackupClient.IsSafeDatabaseName(database))
            {
                skipped.Add(new(BackupContentFlags.Databases, $"{database}: the name has characters backups do not handle"));
                continue;
            }
            // Dump to a temp file first: a dump that fails halfway must not
            // leave a truncated .sql in the archive (zip entries can't be removed).
            var tempPath = Path.Combine(_backupRoot, $".dump-{Guid.NewGuid():N}.sql");
            try
            {
                using var dump = OpenTempFile(tempPath);
                _mysql.Dump(database, dump);
                dump.Position = 0;
                var entry = zip.CreateEntry($"databases/{database}.sql", CompressionLevel.Optimal);
                using var entryStream = entry.Open();
                dump.CopyTo(entryStream);
                dumped.Add(database);
                count++;
            }
            catch (Exception ex)
            {
                skipped.Add(new(BackupContentFlags.Databases, $"{database}: {ex.Message}"));
            }
        }
        return databases.Count > 0 && count == 0 ? null : count;
    }

    /// <summary>
    /// Each site's document root under <c>docroots/{domain}/</c>. Linked
    /// folders (junctions, symlinks) are not followed, so a link cannot pull in
    /// a tree outside the site or loop; the backups folder itself is never packed.
    /// Returns null when there is no site registry to read the folders from.
    /// Each packed domain and its folder go to <paramref name="roots"/>.
    /// </summary>
    private int? PackDocroots(ZipArchive zip, List<BackupSkip> skipped, IDictionary<string, string> roots)
    {
        if (_sites is null)
        {
            skipped.Add(new(BackupContentFlags.Docroots, "The site list is not available in this process"));
            return null;
        }

        var options = new EnumerationOptions { IgnoreInaccessible = true, AttributesToSkip = 0 };
        var packedRoots = new HashSet<string>(OperatingSystem.IsLinux() ? StringComparer.Ordinal : StringComparer.OrdinalIgnoreCase);
        int count = 0;
        foreach (var (domain, site) in _sites.Sites.OrderBy(s => s.Key, StringComparer.OrdinalIgnoreCase))
        {
            if (string.IsNullOrWhiteSpace(site.DocumentRoot) || !Directory.Exists(site.DocumentRoot))
            {
                skipped.Add(new(BackupContentFlags.Docroots, $"{domain}: folder {site.DocumentRoot} not found"));
                continue;
            }
            var root = Path.GetFullPath(site.DocumentRoot);
            if (!packedRoots.Add(root)) continue;
            roots[domain] = root;

            var pending = new Stack<string>();
            pending.Push(root);
            while (pending.TryPop(out var dir))
            {
                try
                {
                    foreach (var file in Directory.EnumerateFiles(dir, "*", options))
                    {
                        if (NKS.WebDevConsole.Daemon.Config.PathContainment.IsUnder(_backupRoot, file)) continue;
                        var rel = Path.GetRelativePath(root, file).Replace(Path.DirectorySeparatorChar, '/');
                        count += AddFileToZipSafe(zip, file, $"docroots/{domain}/{rel}");
                    }
                    foreach (var sub in new DirectoryInfo(dir).EnumerateDirectories("*", options))
                    {
                        if (!sub.Attributes.HasFlag(FileAttributes.ReparsePoint)) pending.Push(sub.FullName);
                    }
                }
                catch (IOException ex)
                {
                    // A folder removed while the backup walks the tree.
                    skipped.Add(new(BackupContentFlags.Docroots, $"{domain}: {ex.Message}"));
                }
            }
        }
        return count;
    }

    /// <summary>Scratch file next to the archives, owner-only on Unix, deleted on close.</summary>
    private static FileStream OpenTempFile(string path)
    {
        var options = new FileStreamOptions
        {
            Mode = FileMode.CreateNew,
            Access = FileAccess.ReadWrite,
            Share = FileShare.None,
            Options = FileOptions.DeleteOnClose,
        };
        if (!OperatingSystem.IsWindows())
            options.UnixCreateMode = UnixFileMode.UserRead | UnixFileMode.UserWrite;
        return new FileStream(path, options);
    }

    private int PackStateDb(ZipArchive zip)
    {
        var src = Path.Combine(_wdcRoot, "data", "state.db");
        if (!File.Exists(src))
        {
            _logger.LogDebug("state.db not found, skipping: {Path}", src);
            return 0;
        }
        // state.db runs in WAL mode, so a raw file copy misses every commit that
        // still sits in state.db-wal. Take a consistent snapshot through SQLite's
        // online backup API (next to the live file, not in the shared temp dir)
        // and pack that; fall back to the raw copy if SQLite cannot read it.
        var snapshot = Path.Combine(Path.GetDirectoryName(src)!, $".state-backup-{Guid.NewGuid():N}.db");
        try
        {
            return TrySqliteCopy(src, snapshot)
                ? AddFileToZipSafe(zip, snapshot, "data/state.db")
                : AddFileToZipSafe(zip, src, "data/state.db");
        }
        finally
        {
            TryDeleteSqliteFiles(snapshot);
        }
    }

    /// <summary>
    /// Copies <paramref name="sourceDb"/> into <paramref name="destinationDb"/> with
    /// SQLite's online backup API, which takes the proper locks and includes pages
    /// still in the WAL. Returns false (and leaves the caller to fall back) when
    /// either file is not a usable SQLite database.
    /// </summary>
    private bool TrySqliteCopy(string sourceDb, string destinationDb)
    {
        try
        {
            using var source = new SqliteConnection(new SqliteConnectionStringBuilder
            {
                DataSource = sourceDb, Mode = SqliteOpenMode.ReadOnly, Pooling = false,
            }.ToString());
            using var destination = new SqliteConnection(new SqliteConnectionStringBuilder
            {
                DataSource = destinationDb, Mode = SqliteOpenMode.ReadWriteCreate, Pooling = false,
            }.ToString());
            source.Open();
            destination.Open();
            source.BackupDatabase(destination);
            return true;
        }
        catch (SqliteException ex)
        {
            _logger.LogWarning(ex, "SQLite online backup {Source} -> {Destination} failed; falling back to a file copy",
                sourceDb, destinationDb);
            return false;
        }
    }

    private static void TryDeleteSqliteFiles(string dbPath)
    {
        foreach (var suffix in new[] { "", "-wal", "-shm", "-journal" })
        {
            try { File.Delete(dbPath + suffix); } catch { /* best-effort cleanup */ }
        }
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
    ///
    /// MySQL dumps and site files are restored only when
    /// <paramref name="options"/> asks for them; the safety backup then also
    /// holds the current databases and site files they overwrite. What could
    /// not be restored (MySQL not running, a path outside the site folder) is
    /// reported in <c>Skipped</c> and does not stop the rest of the restore.
    /// </summary>
    public (int RestoredFiles, string SafetyBackupPath, IReadOnlyList<BackupRestored> Items, IReadOnlyList<BackupSkip> Skipped) RestoreBackup(
        string archivePath, BackupRestoreOptions? options = null)
    {
        if (!File.Exists(archivePath))
            throw new FileNotFoundException("Backup archive not found", archivePath);
        options ??= new BackupRestoreOptions();

        BackupManifest manifest;
        using (var peekFs = File.OpenRead(archivePath))
        using (var peekZip = new ZipArchive(peekFs, ZipArchiveMode.Read))
            manifest = ReadManifest(peekZip);
        var restoreDatabases = options.Databases && manifest.Databases.Count > 0;
        var restoreSiteFiles = options.SiteFiles && manifest.Docroots.Count > 0;

        var safetyBackup = UniquePath("auto-pre-restore");
        try
        {
            var safetyFlags = BackupContentFlags.Default
                | (restoreDatabases ? BackupContentFlags.Databases : BackupContentFlags.None)
                | (restoreSiteFiles ? BackupContentFlags.Docroots : BackupContentFlags.None);
            CreateBackup(safetyBackup, safetyFlags, recordActivity: false);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Failed to create pre-restore safety backup — aborting restore");
            throw;
        }

        _logger.LogInformation("Restoring {Archive} (flags={Flags}, databases={Databases}, siteFiles={SiteFiles})",
            archivePath, manifest.Flags, restoreDatabases, restoreSiteFiles);
        var items = new List<BackupRestored>();
        var skipped = new List<BackupSkip>();

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
                // Database dumps and site files are read straight from the
                // archive below, only when asked; staging would copy gigabytes.
                if (entry.FullName.StartsWith("databases/", StringComparison.Ordinal)
                    || entry.FullName.StartsWith("docroots/", StringComparison.Ordinal)) continue;
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
                // The daemon holds pooled WAL connections to the live state.db.
                // File.Copy over it left a stale state.db-wal next to the new file
                // (corruption, or a restore that silently did not take effect).
                // Write through SQLite so the restore uses the same locks and WAL.
                if (!(File.Exists(dstDb) && TrySqliteCopy(srcDb, dstDb)))
                {
                    // Raw-copy fallback: drop the old WAL and shared-memory files
                    // too, or SQLite replays their frames onto the restored file.
                    SqliteConnection.ClearAllPools();
                    foreach (var suffix in new[] { "-wal", "-shm" })
                    {
                        try { File.Delete(dstDb + suffix); } catch { /* best-effort */ }
                    }
                    File.Copy(srcDb, dstDb, overwrite: true);
                }
                promoted++;
            }

            if (restoreDatabases) RestoreDatabases(zip, items, skipped);
            if (restoreSiteFiles) RestoreSiteFiles(zip, manifest.Docroots, items, skipped);

            foreach (var skip in skipped)
                _logger.LogWarning("Restore of {Archive} left out {Content}: {Detail}", archivePath, skip.Content, skip.Detail);
            _logger.LogInformation("Restored {Count} files from {Archive}", promoted, archivePath);
            // After the state.db swap above, so the row lands in the restored database.
            _history?.Record("backup", Path.GetFileName(archivePath), "RESTORE");
            return (promoted, safetyBackup, items, skipped);
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

    /// <summary>
    /// Imports each <c>databases/{name}.sql</c> (CREATE DATABASE when missing).
    /// MySQL not answering skips the whole category with one reason.
    /// </summary>
    private void RestoreDatabases(ZipArchive zip, List<BackupRestored> items, List<BackupSkip> skipped)
    {
        if (_mysql is null)
        {
            skipped.Add(new(BackupContentFlags.Databases, "MySQL is not available in this process"));
            return;
        }
        try
        {
            _mysql.ListDatabases();
        }
        catch (Exception ex)
        {
            skipped.Add(new(BackupContentFlags.Databases, ex.Message));
            return;
        }

        foreach (var entry in zip.Entries.Where(IsDatabaseDump))
        {
            var database = Path.GetFileNameWithoutExtension(entry.Name);
            if (!MysqlBackupClient.IsSafeDatabaseName(database))
            {
                skipped.Add(new(BackupContentFlags.Databases, $"{database}: not a database name restore accepts"));
                continue;
            }
            try
            {
                using var sql = entry.Open();
                _mysql.Import(database, sql);
                items.Add(new(BackupContentFlags.Databases, database, 1));
            }
            catch (Exception ex)
            {
                skipped.Add(new(BackupContentFlags.Databases, $"{database}: {ex.Message}"));
            }
        }
    }

    private static bool IsDatabaseDump(ZipArchiveEntry entry) =>
        entry.FullName.StartsWith("databases/", StringComparison.Ordinal)
        && entry.FullName.EndsWith(".sql", StringComparison.OrdinalIgnoreCase)
        && entry.FullName.IndexOf('/', "databases/".Length) < 0;

    /// <summary>
    /// Writes <c>docroots/{domain}/…</c> back into the folder the manifest
    /// recorded for that domain, overwriting files of the same name and
    /// leaving other files alone. Nothing is written outside that folder: an
    /// entry whose path leaves it (<c>..</c>, an absolute path) or runs
    /// through a linked folder is skipped, as is a domain with no usable folder.
    /// </summary>
    private void RestoreSiteFiles(ZipArchive zip, IReadOnlyDictionary<string, string> roots,
        List<BackupRestored> items, List<BackupSkip> skipped)
    {
        var byDomain = zip.Entries
            .Where(e => e.FullName.StartsWith("docroots/", StringComparison.Ordinal) && !string.IsNullOrEmpty(e.Name))
            .GroupBy(e => e.FullName.Split('/')[1], StringComparer.OrdinalIgnoreCase);
        foreach (var group in byDomain)
        {
            var domain = group.Key;
            var problem = roots.TryGetValue(domain, out var recorded)
                ? SiteRootProblem(recorded)
                : "the backup does not record the site folder";
            if (problem is not null)
            {
                skipped.Add(new(BackupContentFlags.Docroots, $"{domain}: {problem}"));
                continue;
            }
            var root = Path.GetFullPath(recorded!);
            var prefix = $"docroots/{domain}/";
            int files = 0;
            foreach (var entry in group)
            {
                var rel = entry.FullName.Substring(prefix.Length);
                if (SiteFileTarget(root, rel) is not { } target)
                {
                    skipped.Add(new(BackupContentFlags.Docroots, $"{domain}: {rel} would land outside {root}"));
                    continue;
                }
                try
                {
                    Directory.CreateDirectory(Path.GetDirectoryName(target)!);
                    entry.ExtractToFile(target, overwrite: true);
                    files++;
                }
                catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
                {
                    skipped.Add(new(BackupContentFlags.Docroots, $"{domain}: {rel}: {ex.Message}"));
                }
            }
            items.Add(new(BackupContentFlags.Docroots, domain, files));
        }
    }

    /// <summary>Why a recorded site folder cannot take files back, or null when it can.</summary>
    internal static string? SiteRootProblem(string? recorded)
    {
        if (string.IsNullOrWhiteSpace(recorded) || !Path.IsPathFullyQualified(recorded))
            return $"the recorded folder '{recorded}' is not an absolute path";
        var full = Path.TrimEndingDirectorySeparator(Path.GetFullPath(recorded));
        // "C:\" or "/" as a site folder would let a foreign archive write anywhere on the disk.
        if (string.Equals(full, Path.TrimEndingDirectorySeparator(Path.GetPathRoot(full) ?? ""), StringComparison.OrdinalIgnoreCase))
            return $"the recorded folder '{recorded}' is a drive root";
        return null;
    }

    /// <summary>
    /// Where <paramref name="relativePath"/> from the archive goes under
    /// <paramref name="root"/>, or null when it would leave the folder (zip
    /// slip) or pass through a junction or symlink that could point elsewhere.
    /// </summary>
    internal static string? SiteFileTarget(string root, string relativePath)
    {
        if (string.IsNullOrEmpty(relativePath) || Path.IsPathRooted(relativePath)) return null;
        var target = Path.GetFullPath(Path.Combine(root, relativePath.Replace('/', Path.DirectorySeparatorChar)));
        if (!NKS.WebDevConsole.Daemon.Config.PathContainment.IsUnder(root, target)) return null;
        // The target is inside root, so every longer ancestor is a folder in the site.
        var rootLength = Path.TrimEndingDirectorySeparator(root).Length;
        for (var dir = Path.GetDirectoryName(target); dir is not null && dir.Length > rootLength; dir = Path.GetDirectoryName(dir))
        {
            if (Directory.Exists(dir) && new DirectoryInfo(dir).Attributes.HasFlag(FileAttributes.ReparsePoint)) return null;
        }
        if (File.Exists(target) && File.GetAttributes(target).HasFlag(FileAttributes.ReparsePoint)) return null;
        return target;
    }

    /// <summary>
    /// Opens a new archive file: CreateNew (an existing file or link at the
    /// path fails instead of being followed or truncated) and, on Unix, mode
    /// 0600 at creation so the archive is never readable by other users.
    /// </summary>
    internal static FileStream OpenNewArchive(string path)
    {
        var options = new FileStreamOptions
        {
            Mode = FileMode.CreateNew,
            Access = FileAccess.Write,
            Share = FileShare.None,
        };
        if (!OperatingSystem.IsWindows())
            options.UnixCreateMode = UnixFileMode.UserRead | UnixFileMode.UserWrite;
        try
        {
            return new FileStream(path, options);
        }
        catch (IOException) when (File.Exists(path) || Directory.Exists(path))
        {
            throw new BackupTargetExistsException(path);
        }
    }

    public IReadOnlyList<BackupEntry> ListBackups()
    {
        if (!Directory.Exists(_backupRoot)) return Array.Empty<BackupEntry>();
        return Directory.GetFiles(_backupRoot, "*.zip")
            .Select(p =>
            {
                var manifest = ReadManifest(p);
                return new BackupEntry(p, new FileInfo(p).Length, File.GetCreationTimeUtc(p),
                    manifest.Flags, manifest.Databases, manifest.Docroots);
            })
            .OrderByDescending(x => x.Created)
            .ToList();
    }

    private static BackupManifest ReadManifest(string zipPath)
    {
        try
        {
            using var fs = File.OpenRead(zipPath);
            using var zip = new ZipArchive(fs, ZipArchiveMode.Read);
            return ReadManifest(zip);
        }
        catch (Exception ex) when (ex is IOException or InvalidDataException or UnauthorizedAccessException)
        {
            return BackupManifest.Fallback; // corrupt zip or not our format
        }
    }

    /// <summary>
    /// What manifest.json says the archive holds. Archives without one (or
    /// with an unreadable one) count as the default content set.
    /// </summary>
    private static BackupManifest ReadManifest(ZipArchive zip)
    {
        var entry = zip.GetEntry("manifest.json");
        if (entry is null) return BackupManifest.Fallback;
        try
        {
            using var stream = entry.Open();
            using var doc = JsonDocument.Parse(stream);
            var root = doc.RootElement;
            var flags = root.TryGetProperty("flags", out var f) && f.ValueKind == JsonValueKind.String
                && Enum.TryParse<BackupContentFlags>(f.GetString(), out var parsed) ? parsed : BackupContentFlags.Default;
            var databases = root.TryGetProperty("databases", out var d) && d.ValueKind == JsonValueKind.Array
                ? d.EnumerateArray().Where(x => x.ValueKind == JsonValueKind.String).Select(x => x.GetString()!).ToList()
                : new List<string>();
            var docroots = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
            if (root.TryGetProperty("docroots", out var r) && r.ValueKind == JsonValueKind.Object)
                foreach (var site in r.EnumerateObject().Where(p => p.Value.ValueKind == JsonValueKind.String))
                    docroots[site.Name] = site.Value.GetString()!;
            return new BackupManifest(flags, databases, docroots);
        }
        catch (Exception ex) when (ex is JsonException or IOException or InvalidDataException)
        {
            return BackupManifest.Fallback;
        }
    }

    private sealed record BackupManifest(
        BackupContentFlags Flags, IReadOnlyList<string> Databases, IReadOnlyDictionary<string, string> Docroots)
    {
        public static readonly BackupManifest Fallback =
            new(BackupContentFlags.Default, Array.Empty<string>(), new Dictionary<string, string>());
    }

    /// <summary>
    /// A single backup archive in the listing. <paramref name="Databases"/>
    /// and <paramref name="Sites"/> (domain → recorded folder) are what a
    /// restore can bring back on request.
    /// </summary>
    public record BackupEntry(string Path, long Size, DateTime Created, BackupContentFlags ContentFlags,
        IReadOnlyList<string> Databases, IReadOnlyDictionary<string, string> Sites);
}

/// <summary>Something a backup was asked to include but left out, and why.</summary>
public sealed record BackupSkip(BackupContentFlags Content, string Detail);

/// <summary>Opt-in parts of a restore; without them only the configuration comes back.</summary>
public sealed record BackupRestoreOptions(bool Databases = false, bool SiteFiles = false);

/// <summary>One database or one site's files a restore brought back.</summary>
public sealed record BackupRestored(BackupContentFlags Content, string Name, int Files);

/// <summary>A backup was asked to write to a path that already exists.</summary>
public sealed class BackupTargetExistsException(string path)
    : IOException($"Backup file already exists: {path}")
{
    public string Path { get; } = path;
}
