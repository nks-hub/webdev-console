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
/// also prunes old backups, keeping the 5 most recent.
/// </summary>
public sealed class BackupScheduler : IDisposable
{
    private readonly BackupManager _backupManager;
    private readonly SettingsStore _settings;
    private readonly ILogger<BackupScheduler> _logger;
    private Timer? _timer;

    private const int MaxBackups = 10;

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
        try
        {
            var hours = _settings.GetString("backup", "scheduleHours");
            var scheduleHours = 0;
            if (!string.IsNullOrWhiteSpace(hours) && int.TryParse(hours, out var parsed) && parsed > 0)
                scheduleHours = parsed;

            if (scheduleHours == 0)
                return; // disabled

            // Check if enough time has passed since the last backup
            var backups = _backupManager.ListBackups();
            if (backups.Count > 0)
            {
                var newest = backups[0]; // already sorted newest-first
                var age = DateTime.UtcNow - newest.Created;
                if (age.TotalHours < scheduleHours)
                    return; // not due yet
            }

            _logger.LogInformation("Scheduled backup triggered (every {Hours}h)", scheduleHours);
            var result = _backupManager.CreateBackup();
            _logger.LogInformation("Scheduled backup created: {Path} ({Files} files, {Size} bytes)",
                result.Path, result.FileCount, result.SizeBytes);

            // Prune: keep only the most recent MaxBackups
            var all = _backupManager.ListBackups();
            if (all.Count > MaxBackups)
            {
                foreach (var old in all.Skip(MaxBackups))
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
    }

    public void Dispose()
    {
        _timer?.Dispose();
    }
}
