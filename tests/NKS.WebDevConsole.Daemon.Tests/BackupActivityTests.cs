using System.Globalization;
using System.IO.Compression;
using System.Text.Json;
using Microsoft.Extensions.Logging.Abstractions;
using NKS.WebDevConsole.Daemon.Backup;

namespace NKS.WebDevConsole.Daemon.Tests;

/// <summary>What BackupManager records for the Overview activity feed: a backup made and a backup restored.</summary>
public sealed class BackupActivityTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "wdc-backup-activity-" + Guid.NewGuid().ToString("N"));
    private readonly ActivityTestDb _db = new();
    private readonly BackupManager _backups;

    public BackupActivityTests()
    {
        Directory.CreateDirectory(Path.Combine(_root, "sites"));
        File.WriteAllText(Path.Combine(_root, "sites", "shop.loc.toml"), "domain = \"shop.loc\"");
        _backups = new BackupManager(NullLogger<BackupManager>.Instance, _root, history: _db.Repository());
    }

    public void Dispose()
    {
        _db.Dispose();
        try { if (Directory.Exists(_root)) Directory.Delete(_root, recursive: true); } catch { /* best effort */ }
    }

    [Fact]
    public void A_backup_is_recorded_under_its_file_name()
    {
        var path = _backups.CreateBackup("manual.zip").Path;

        var row = Assert.Single(_db.Rows("entity_type = 'backup'"));
        Assert.Equal(Path.GetFileName(path), (string)row.entity_name);
        Assert.Equal("INSERT", (string)row.operation);
    }

    [Fact]
    public void A_restore_is_recorded_once_and_its_safety_copy_is_not_listed_as_a_backup()
    {
        var archive = _backups.CreateBackup("manual.zip").Path;

        var result = _backups.RestoreBackup(archive);

        Assert.True(File.Exists(result.SafetyBackupPath));
        var rows = _db.Rows("entity_type = 'backup'");
        Assert.Equal(["INSERT", "RESTORE"], rows.Select(r => (string)r.operation));
        Assert.Equal("manual.zip", (string)rows[1].entity_name);
        Assert.DoesNotContain(rows, r => ((string)r.entity_name).StartsWith("auto-pre-restore"));
    }

    [Fact]
    public void A_restored_archive_gets_its_create_row_back_stamped_with_the_archive_time()
    {
        // state.db inside the backup root, so the archive packs it and the restore swaps it.
        using var live = new ActivityTestDb(path: Path.Combine(_root, "data", "state.db"));
        var backups = new BackupManager(NullLogger<BackupManager>.Instance, _root, history: live.Repository());
        var archive = backups.CreateBackup("manual.zip").Path;
        Assert.Single(live.Rows("entity_type = 'backup' AND operation = 'INSERT'"));
        Thread.Sleep(50);

        backups.RestoreBackup(archive);

        var rows = live.Rows("entity_type = 'backup'");
        Assert.Equal(["INSERT", "RESTORE"], rows.Select(r => (string)r.operation));
        Assert.All(rows, r => Assert.Equal("manual.zip", (string)r.entity_name));
        using var zip = ZipFile.OpenRead(archive);
        using var manifest = JsonDocument.Parse(zip.GetEntry("manifest.json")!.Open());
        var madeAt = DateTime.Parse(manifest.RootElement.GetProperty("created").GetString()!,
            CultureInfo.InvariantCulture, DateTimeStyles.RoundtripKind);
        Assert.Equal(madeAt.ToUniversalTime().ToString("yyyy-MM-dd'T'HH:mm:ss.fff'Z'", CultureInfo.InvariantCulture),
            (string)rows[0].created_at);
        Assert.NotEqual((string)rows[0].created_at, (string)rows[1].created_at);
    }

    [Fact]
    public void A_backup_made_without_a_history_repository_still_works()
    {
        var path = new BackupManager(NullLogger<BackupManager>.Instance, _root).CreateBackup("plain.zip").Path;

        Assert.True(File.Exists(path));
        Assert.Empty(_db.Rows("entity_type = 'backup'"));
    }
}
