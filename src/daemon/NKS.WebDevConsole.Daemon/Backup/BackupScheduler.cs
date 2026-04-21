using Microsoft.Extensions.Logging;
using NKS.WebDevConsole.Daemon.Services;

namespace NKS.WebDevConsole.Daemon.Backup;

/// <summary>
/// Runs automatic backups on a timer based on the
/// <c>backup.scheduleHours</c> setting from <see cref="SettingsStore"/>.
///
/// When the setting is 0 (default), the scheduler is dormant — no timer,
/// no CPU. When it's &gt; 0, a <see cref="Timer"/> fires every N hours
/// and calls <see cref="BackupManager"/> to produce a timestamped zip.
///
/// Re-reads the setting on every tick so the user can change the interval
/// from the Settings page without restarting the daemon. The scheduler
/// also prunes old backups, keeping the <see cref="DefaultRetainCount"/>
/// most recent by default — override via the <c>backup.retainCount</c>
/// setting (1…100, clamped).
/// </summary>
public sealed class BackupScheduler : IDisposable
{
    private readonly BackupManager _backupManager;
    private readonly SettingsStore _settings;
    private readonly ILogger<BackupScheduler> _logger;
    private Timer? _timer;
    private int _running;

    /// <summary>
    /// Default schedule: 24 hours (changed from 0/off so new installs get
    /// automatic daily backups without manual configuration).
    /// </summary>
    private const int DefaultScheduleHours = 24;
    private const int DefaultRetainCount = 10;
    private const int MinRetainCount = 1;
    private const int MaxRetainCount = 100;

    public BackupScheduler(
        BackupManager backupManager,
        SettingsStore settings,
        ILogger<BackupScheduler> logger)
    {
        _backupManager = backupManager;
        _settings = settings;
        _logger = logger;
    }

    public void Start()
    {
        // Check every 10 minutes whether the schedule changed or a backup
        // is due. This is intentionally coarse — backups are hours apart,
        // so 10-minute polling is negligible overhead while keeping the
        // implementation dead-simple (no FileSystemWatcher, no events).
        _timer = new Timer(OnTick, null, TimeSpan.FromMinutes(1), TimeSpan.FromMinutes(10));
        _logger.LogInformation("Backup scheduler started (checks every 10 min)");
    }

    private void OnTick(object? state)
    {
        if (Interlocked.Exchange(ref _running, 1) != 0) return;
        try
        {
            var hoursRaw = _settings.GetString("backup", "scheduleHours");
            var scheduleHours = DefaultScheduleHours;
            if (!string.IsNullOrWhiteSpace(hoursRaw) && int.TryParse(hoursRaw, out var parsedHours))
                scheduleHours = parsedHours;

            if (scheduleHours == 0)
                return; // disabled (explicit 0 means off)

            // Check if enough time has passed since the last backup
            var backups = _backupManager.ListBackups();
            if (backups.Count > 0)
            {
                var newest = backups[0]; // already sorted newest-first
                var age = DateTime.UtcNow - newest.Created;
                if (age.TotalHours < scheduleHours)
                    return; // not due yet
            }

            var flags = ReadContentFlags();
            _logger.LogInformation("Scheduled backup triggered (every {Hours}h, flags={Flags})", scheduleHours, flags);
            var result = _backupManager.CreateBackup(flags: flags);
            _logger.LogInformation("Scheduled backup created: {Path} ({Files} files, {Size} bytes, flags={Flags})",
                result.Path, result.FileCount, result.SizeBytes, result.Flags);

            // Prune: keep only the most recent N where N = backup.retainCount
            // (clamped to [MinRetainCount, MaxRetainCount]) or the default when
            // unset. Re-read per tick so operators can tune via Settings
            // without restarting the daemon.
            var retain = DefaultRetainCount;
            var retainRaw = _settings.GetString("backup", "retainCount");
            if (!string.IsNullOrWhiteSpace(retainRaw) && int.TryParse(retainRaw, out var r))
                retain = Math.Clamp(r, MinRetainCount, MaxRetainCount);

            var all = _backupManager.ListBackups();
            if (all.Count > retain)
            {
                foreach (var old in all.Skip(retain))
                {
                    try
                    {
                        File.Delete(old.Path);
                        _logger.LogDebug("Pruned old backup: {Path}", old.Path);
                    }
                    catch (Exception ex)
                    {
                        _logger.LogWarning("Failed to prune backup {Path}: {Error}", old.Path, ex.Message);
                    }
                }
            }
        }
        catch (Exception ex)
        {
            _logger.LogWarning("Backup scheduler tick failed: {Error}", ex.Message);
        }
        finally
        {
            Interlocked.Exchange(ref _running, 0);
        }
    }

    /// <summary>
    /// Reads per-category content flags from settings. Each flag maps to
    /// <c>backup.content.{name}</c> bool. Unset → default (see <see cref="BackupContentFlags.Default"/>).
    /// </summary>
    private BackupContentFlags ReadContentFlags()
    {
        var flags = BackupContentFlags.None;
        if (_settings.GetBool("backup", "content.vhosts", defaultValue: true))      flags |= BackupContentFlags.Vhosts;
        if (_settings.GetBool("backup", "content.pluginConfigs", defaultValue: true)) flags |= BackupContentFlags.PluginConfigs;
        if (_settings.GetBool("backup", "content.ssl", defaultValue: true))          flags |= BackupContentFlags.Ssl;
        if (_settings.GetBool("backup", "content.databases", defaultValue: false))   flags |= BackupContentFlags.Databases;
        if (_settings.GetBool("backup", "content.docroots", defaultValue: false))    flags |= BackupContentFlags.Docroots;
        return flags == BackupContentFlags.None ? BackupContentFlags.Default : flags;
    }

    public void Dispose()
    {
        _timer?.Dispose();
    }
}
