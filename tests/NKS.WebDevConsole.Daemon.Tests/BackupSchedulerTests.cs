using Dapper;
using Microsoft.Extensions.Logging.Abstractions;
using NKS.WebDevConsole.Daemon.Backup;
using NKS.WebDevConsole.Daemon.Data;
using NKS.WebDevConsole.Daemon.Services;

namespace NKS.WebDevConsole.Daemon.Tests;

/// <summary>
/// Scheduled backups: only scheduler-created archives are pruned, and a
/// non-positive <c>backup.scheduleHours</c> disables the schedule.
/// </summary>
public sealed class BackupSchedulerTests : IDisposable
{
    private readonly string _tempRoot;
    private readonly string _wdcDir;
    private readonly string _backupDir;
    private readonly string _dbPath;
    private readonly SettingsStore _settings;
    private readonly BackupManager _manager;

    public BackupSchedulerTests()
    {
        _tempRoot = Path.Combine(Path.GetTempPath(), $"wdc-backup-sched-{Guid.NewGuid():N}");
        _wdcDir = Path.Combine(_tempRoot, ".wdc");
        Directory.CreateDirectory(_wdcDir);
        _backupDir = Path.Combine(_wdcDir, "backups");

        _dbPath = Path.Combine(_tempRoot, "settings.db");
        var database = new Database(_dbPath);
        using (var conn = database.CreateConnection())
        {
            conn.Execute(@"
                CREATE TABLE settings (
                    id INTEGER PRIMARY KEY AUTOINCREMENT,
                    category TEXT NOT NULL,
                    key TEXT NOT NULL,
                    value TEXT NOT NULL,
                    value_type TEXT NOT NULL DEFAULT 'string',
                    description TEXT,
                    is_readonly INTEGER NOT NULL DEFAULT 0,
                    created_at TEXT NOT NULL DEFAULT (strftime('%Y-%m-%dT%H:%M:%fZ', 'now')),
                    updated_at TEXT NOT NULL DEFAULT (strftime('%Y-%m-%dT%H:%M:%fZ', 'now')),
                    UNIQUE(category, key)
                );
            ");
        }
        _settings = new SettingsStore(database);
        _manager = new BackupManager(NullLogger<BackupManager>.Instance, _wdcDir);
    }

    public void Dispose()
    {
        Microsoft.Data.Sqlite.SqliteConnection.ClearAllPools();
        try { if (Directory.Exists(_tempRoot)) Directory.Delete(_tempRoot, recursive: true); }
        catch { /* best effort */ }
    }

    private BackupScheduler NewScheduler() =>
        new(_manager, _settings, new SseService(), NullLogger<BackupScheduler>.Instance);

    private string Touch(string name)
    {
        var path = Path.Combine(_backupDir, name);
        File.WriteAllBytes(path, [0x50, 0x4B]);
        return path;
    }

    private string[] BackupFileNames() =>
        Directory.GetFiles(_backupDir).Select(Path.GetFileName).OrderBy(n => n, StringComparer.Ordinal).ToArray()!;

    [Theory]
    [InlineData("wdc-scheduled-20260101-120000.zip", true)]
    [InlineData("wdc-scheduled-20260101-120000-2.zip", true)]
    [InlineData("wdc-backup-20260101-120000.zip", false)]
    [InlineData("auto-pre-restore-20260101-120000.zip", false)]
    [InlineData("wdc-scheduled-latest.zip", false)]
    [InlineData("wdc-scheduled-20260101-120000.zip.bak", false)]
    public void IsScheduledBackup_MatchesOnlySchedulerNames(string name, bool expected)
    {
        Assert.Equal(expected, BackupManager.IsScheduledBackup(Path.Combine(_backupDir, name)));
    }

    [Fact]
    public void ScheduledBackupPath_IsRecognisedAsScheduled()
    {
        var path = _manager.ScheduledBackupPath();
        Assert.Equal(_backupDir, Path.GetDirectoryName(path));
        Assert.True(BackupManager.IsScheduledBackup(path));
    }

    [Fact]
    public void PruneScheduledBackups_KeepsNewestScheduled_AndNeverTouchesOtherBackups()
    {
        Touch("wdc-scheduled-20260101-000000.zip");
        Touch("wdc-scheduled-20260102-000000.zip");
        Touch("wdc-scheduled-20260103-000000.zip");
        Touch("wdc-scheduled-20260104-000000.zip");
        Touch("wdc-backup-20250101-000000.zip");          // manual, older than all
        Touch("auto-pre-restore-20250101-000000.zip");    // restore safety copy
        Touch("my-export.zip");                           // user-named manual backup

        var deleted = _manager.PruneScheduledBackups(retain: 2);

        Assert.Equal(
            new[] { "wdc-scheduled-20260101-000000.zip", "wdc-scheduled-20260102-000000.zip" },
            deleted.Select(Path.GetFileName).OrderBy(n => n, StringComparer.Ordinal).ToArray());
        Assert.Equal(
            new[]
            {
                "auto-pre-restore-20250101-000000.zip",
                "my-export.zip",
                "wdc-backup-20250101-000000.zip",
                "wdc-scheduled-20260103-000000.zip",
                "wdc-scheduled-20260104-000000.zip",
            },
            BackupFileNames());
    }

    [Fact]
    public void PruneScheduledBackups_WithinRetention_DeletesNothing()
    {
        Touch("wdc-scheduled-20260101-000000.zip");
        Touch("wdc-backup-20260101-000000.zip");

        Assert.Empty(_manager.PruneScheduledBackups(retain: 1));
        Assert.Equal(2, BackupFileNames().Length);
    }

    [Theory]
    [InlineData("0")]
    [InlineData("-1")]
    [InlineData("-24")]
    public void RunTick_NonPositiveSchedule_CreatesNoBackup(string hours)
    {
        _settings.Set("backup", "scheduleHours", hours);

        NewScheduler().RunTick();

        Assert.Empty(BackupFileNames());
    }

    [Fact]
    public void RunTick_DueWithNoBackups_CreatesScheduledBackup()
    {
        _settings.Set("backup", "scheduleHours", "1");
        _settings.Set("backup", "retainCount", "1");

        NewScheduler().RunTick();

        var created = Assert.Single(BackupFileNames());
        Assert.True(BackupManager.IsScheduledBackup(created));
    }
}
