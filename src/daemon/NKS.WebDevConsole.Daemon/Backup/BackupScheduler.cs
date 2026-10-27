using Microsoft.Extensions.Logging;
using NKS.WebDevConsole.Daemon.Services;

namespace NKS.WebDevConsole.Daemon.Backup;

/// <summary>
/// Runs automatic backups on a timer based on the
/// <c>backup.scheduleHours</c> setting from <see cref="SettingsStore"/>.
///
/// When the setting is 0 or negative, the scheduler is dormant. When it's
/// &gt; 0 (default 24), a check every 10 minutes calls
/// <see cref="BackupManager"/> to produce a timestamped
/// <c>wdc-scheduled-*.zip</c> once N hours have passed since the newest backup.
///
/// Re-reads the setting on every tick so the user can change the interval
/// from the Settings page without restarting the daemon. The scheduler
/// also prunes its own old backups, keeping the <see cref="DefaultRetainCount"/>
/// most recent by default — override via the <c>backup.retainCount</c>
/// setting (1…100, clamped). Manual backups and restore safety backups are
/// never pruned.
/// </summary>
public sealed class BackupScheduler : IDisposable
{
    private readonly BackupManager _backupManager;
    private readonly SettingsStore _settings;
    private readonly SseService _sse;
    private readonly ILogger<BackupScheduler> _logger;
    private Timer? _timer;
    private int _running;

    /// <summary>
    /// Default schedule: 24 hours (changed from 0/off so new installs get
    /// automatic daily backups without manual configuration).
    /// </summary>
    internal const int DefaultScheduleHours = 24;
    internal const int DefaultRetainCount = 10;
    private const int MinRetainCount = 1;
    private const int MaxRetainCount = 100;

    public BackupScheduler(
        BackupManager backupManager,
        SettingsStore settings,
        SseService sse,
        ILogger<BackupScheduler> logger)
    {
        _backupManager = backupManager;
        _settings = settings;
        _sse = sse;
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

    private void OnTick(object? state) => RunTick();

    /// <summary>One scheduler check: create a backup when due, then prune.</summary>
    internal void RunTick()
    {
        if (Interlocked.Exchange(ref _running, 1) != 0) return;
        try
        {
            var hoursRaw = _settings.GetString("backup", "scheduleHours");
            var scheduleHours = DefaultScheduleHours;
            if (!string.IsNullOrWhiteSpace(hoursRaw) && int.TryParse(hoursRaw, out var parsedHours))
                scheduleHours = parsedHours;

            if (scheduleHours <= 0)
                return; // disabled: 0 means off, and a negative value must not mean "every tick"

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
            (string Path, int FileCount, long SizeBytes, BackupContentFlags Flags) result;
            try
            {
                result = _backupManager.CreateBackup(_backupManager.ScheduledBackupPath(), flags);
            }
            catch (Exception ex)
            {
                _logger.LogWarning("Scheduled backup failed: {Error}", ex.Message);
                Publish("backup:failed", new { trigger = "scheduled", error = ex.Message });
                return;
            }
            _logger.LogInformation("Scheduled backup created: {Path} ({Files} files, {Size} bytes, flags={Flags})",
                result.Path, result.FileCount, result.SizeBytes, result.Flags);
            Publish("backup:completed", new
            {
                trigger = "scheduled",
                path = result.Path,
                files = result.FileCount,
                size = result.SizeBytes,
                contentFlags = result.Flags.ToString(),
            });

            // Prune: keep only the most recent N where N = backup.retainCount
            // (clamped to [MinRetainCount, MaxRetainCount]) or the default when
            // unset. Re-read per tick so operators can tune via Settings
            // without restarting the daemon.
            var retain = DefaultRetainCount;
            var retainRaw = _settings.GetString("backup", "retainCount");
            if (!string.IsNullOrWhiteSpace(retainRaw) && int.TryParse(retainRaw, out var r))
                retain = Math.Clamp(r, MinRetainCount, MaxRetainCount);

            // Only scheduler-created archives are pruned; manual backups and
            // auto-pre-restore safety copies are left for the user to manage.
            _backupManager.PruneScheduledBackups(retain);
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
    /// Broadcasts a backup outcome over SSE (the desktop app turns it into an
    /// OS notification). Fire-and-forget: the timer callback is synchronous and
    /// a slow SSE client must not hold up the scheduler.
    /// </summary>
    private void Publish(string eventType, object payload)
    {
        _ = Task.Run(async () =>
        {
            try { await _sse.BroadcastAsync(eventType, payload); }
            catch (Exception ex) { _logger.LogDebug(ex, "{Event} broadcast failed", eventType); }
        });
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
