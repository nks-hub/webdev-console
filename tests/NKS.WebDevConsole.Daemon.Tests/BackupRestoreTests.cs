using System.Diagnostics;
using System.IO.Compression;
using System.Text;
using Moq;
using NKS.WebDevConsole.Core.Interfaces;
using NKS.WebDevConsole.Core.Models;
using NKS.WebDevConsole.Daemon.Backup;
using Microsoft.Extensions.Logging.Abstractions;

namespace NKS.WebDevConsole.Daemon.Tests;

/// <summary>
/// MySQL client stand-in: dumps write "CREATE DATABASE `name`;", imports are
/// recorded. <c>databases: null</c> behaves like a stopped MySQL server.
/// </summary>
internal sealed class FakeMysql(IReadOnlyList<string>? databases, params string[] failing) : IMysqlBackupClient
{
    public Dictionary<string, string> Imported { get; } = new();

    public IReadOnlyList<string> ListDatabases() =>
        databases ?? throw new InvalidOperationException("Can't connect to MySQL server on '127.0.0.1:3306'");

    public void Dump(string database, Stream output)
    {
        output.Write(Encoding.UTF8.GetBytes($"CREATE DATABASE `{database}`;"));
        if (failing.Contains(database)) throw new InvalidOperationException("Lost connection");
    }

    public void Import(string database, Stream sql)
    {
        if (failing.Contains(database)) throw new InvalidOperationException("Lost connection");
        using var reader = new StreamReader(sql);
        Imported[database] = reader.ReadToEnd();
    }
}

/// <summary>
/// Opt-in restore of MySQL dumps and site files: archive round trip, the
/// default (configuration only), MySQL down, and that site files never land
/// outside the folder the backup recorded (zip slip, linked folders).
/// </summary>
public class BackupRestoreTests : IDisposable
{
    private readonly string _tempRoot;
    private readonly string _wdcDir;
    private readonly string _siteRoot;

    public BackupRestoreTests()
    {
        _tempRoot = Path.Combine(Path.GetTempPath(), $"wdc-restore-tests-{Guid.NewGuid():N}");
        _wdcDir = Path.Combine(_tempRoot, ".wdc");
        _siteRoot = Path.Combine(_tempRoot, "shop", "www");
        Directory.CreateDirectory(_wdcDir);
        Directory.CreateDirectory(Path.Combine(_siteRoot, "css"));
        File.WriteAllText(Path.Combine(_siteRoot, "index.php"), "<?php echo 1;");
        File.WriteAllText(Path.Combine(_siteRoot, "css", "app.css"), "body{}");
    }

    public void Dispose()
    {
        try { if (Directory.Exists(_tempRoot)) Directory.Delete(_tempRoot, recursive: true); } catch { }
    }

    private static ISiteRegistry Sites(params (string Domain, string Root)[] sites)
    {
        var map = sites.ToDictionary(s => s.Domain, s => new SiteConfig { Domain = s.Domain, DocumentRoot = s.Root });
        var mock = new Mock<ISiteRegistry>();
        mock.Setup(r => r.Sites).Returns(map);
        return mock.Object;
    }

    private BackupManager Bm(FakeMysql? mysql = null) =>
        new(NullLogger<BackupManager>.Instance, _wdcDir, Sites(("shop.loc", _siteRoot)), mysql ?? new FakeMysql(["shop"]));

    private string FullBackup(BackupManager bm) =>
        bm.CreateBackup("full.zip", BackupContentFlags.Default | BackupContentFlags.Databases | BackupContentFlags.Docroots).Path;

    [Fact]
    public void Listing_names_the_databases_and_site_folders_a_restore_can_bring_back()
    {
        var bm = Bm(new FakeMysql(["shop", "blog"]));
        FullBackup(bm);

        var entry = bm.ListBackups().Single();
        Assert.Equal(new[] { "shop", "blog" }, entry.Databases);
        Assert.Equal(Path.GetFullPath(_siteRoot), entry.Sites["shop.loc"]);
    }

    [Fact]
    public void Opt_in_restore_imports_dumps_and_writes_site_files_back()
    {
        var mysql = new FakeMysql(["shop"]);
        var bm = Bm(mysql);
        var archive = FullBackup(bm);
        File.WriteAllText(Path.Combine(_siteRoot, "index.php"), "changed");
        File.Delete(Path.Combine(_siteRoot, "css", "app.css"));
        File.WriteAllText(Path.Combine(_siteRoot, "new.txt"), "added after the backup");

        var (_, safety, items, skipped) = bm.RestoreBackup(archive, new BackupRestoreOptions(Databases: true, SiteFiles: true));

        Assert.Empty(skipped);
        Assert.Equal("CREATE DATABASE `shop`;", mysql.Imported["shop"]);
        Assert.Equal("<?php echo 1;", File.ReadAllText(Path.Combine(_siteRoot, "index.php")));
        Assert.Equal("body{}", File.ReadAllText(Path.Combine(_siteRoot, "css", "app.css")));
        // Files the backup does not hold stay where they are.
        Assert.True(File.Exists(Path.Combine(_siteRoot, "new.txt")));
        Assert.Contains(items, i => i.Content == BackupContentFlags.Databases && i.Name == "shop");
        Assert.Contains(items, i => i.Content == BackupContentFlags.Docroots && i.Name == "shop.loc" && i.Files == 2);
        // The safety backup holds what the restore overwrote.
        using var zip = ZipFile.OpenRead(safety);
        Assert.NotNull(zip.GetEntry("databases/shop.sql"));
        Assert.NotNull(zip.GetEntry("docroots/shop.loc/index.php"));
    }

    [Fact]
    public void Without_the_flags_dumps_and_site_files_are_left_alone()
    {
        var mysql = new FakeMysql(["shop"]);
        var bm = Bm(mysql);
        var archive = FullBackup(bm);
        File.WriteAllText(Path.Combine(_siteRoot, "index.php"), "changed");

        var (_, safety, items, skipped) = bm.RestoreBackup(archive);

        Assert.Empty(items);
        Assert.Empty(skipped);
        Assert.Empty(mysql.Imported);
        Assert.Equal("changed", File.ReadAllText(Path.Combine(_siteRoot, "index.php")));
        using var zip = ZipFile.OpenRead(safety);
        Assert.DoesNotContain(zip.Entries, e => e.FullName.StartsWith("docroots/") || e.FullName.StartsWith("databases/"));
    }

    [Fact]
    public void Mysql_down_is_reported_and_the_rest_of_the_restore_still_runs()
    {
        var archive = FullBackup(Bm());
        File.WriteAllText(Path.Combine(_siteRoot, "index.php"), "changed");
        var bm = Bm(new FakeMysql(null));

        var (_, _, items, skipped) = bm.RestoreBackup(archive, new BackupRestoreOptions(Databases: true, SiteFiles: true));

        var skip = Assert.Single(skipped);
        Assert.Equal(BackupContentFlags.Databases, skip.Content);
        Assert.Contains("Can't connect", skip.Detail);
        Assert.Equal("<?php echo 1;", File.ReadAllText(Path.Combine(_siteRoot, "index.php")));
        Assert.Contains(items, i => i.Content == BackupContentFlags.Docroots);
    }

    [Fact]
    public void A_failing_import_skips_that_database_only()
    {
        var archive = FullBackup(Bm(new FakeMysql(["shop", "blog"])));
        var mysql = new FakeMysql(["shop", "blog"], "blog");

        var (_, _, items, skipped) = Bm(mysql).RestoreBackup(archive, new BackupRestoreOptions(Databases: true));

        Assert.Equal("blog: Lost connection", Assert.Single(skipped).Detail);
        Assert.True(mysql.Imported.ContainsKey("shop"));
        Assert.Single(items);
    }

    /// <summary>Hand-built archive: a manifest recording <paramref name="root"/> for evil.loc, plus the given entries.</summary>
    private string CraftedArchive(string root, params string[] entries)
    {
        var path = Path.Combine(_wdcDir, "backups", $"crafted-{Guid.NewGuid():N}.zip");
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        using var fs = File.Create(path);
        using var zip = new ZipArchive(fs, ZipArchiveMode.Create);
        var manifest = zip.CreateEntry("manifest.json");
        using (var w = new StreamWriter(manifest.Open()))
            w.Write(System.Text.Json.JsonSerializer.Serialize(new
            {
                version = "2", flags = "Docroots, Databases",
                databases = new[] { "shop" },
                docroots = new Dictionary<string, string> { ["evil.loc"] = root },
            }));
        foreach (var name in entries)
        {
            using var w = new StreamWriter(zip.CreateEntry(name).Open());
            w.Write("payload");
        }
        return path;
    }

    [Fact]
    public void Site_files_never_land_outside_the_recorded_folder()
    {
        var archive = CraftedArchive(_siteRoot,
            "docroots/evil.loc/ok.txt",
            "docroots/evil.loc/../../escape.txt",
            "docroots/evil.loc/sub/../../../escape2.txt",
            "docroots/other.loc/unrecorded.txt",
            "databases/../../evil.sql",
            "databases/drop;table.sql");
        var mysql = new FakeMysql(["shop"]);

        var (_, _, items, skipped) = Bm(mysql).RestoreBackup(archive, new BackupRestoreOptions(Databases: true, SiteFiles: true));

        Assert.Equal("payload", File.ReadAllText(Path.Combine(_siteRoot, "ok.txt")));
        Assert.False(File.Exists(Path.Combine(_tempRoot, "escape.txt")));
        Assert.False(File.Exists(Path.Combine(_tempRoot, "shop", "escape.txt")));
        Assert.False(File.Exists(Path.Combine(_tempRoot, "escape2.txt")));
        Assert.Contains(skipped, s => s.Detail.StartsWith("other.loc: "));
        Assert.Equal(2, skipped.Count(s => s.Detail.Contains("would land outside")));
        Assert.Empty(mysql.Imported);
        Assert.Contains(skipped, s => s.Detail.StartsWith("drop;table: "));
        Assert.Single(items, i => i.Content == BackupContentFlags.Docroots && i.Files == 1);
    }

    [Fact]
    public void A_drive_root_or_relative_folder_is_never_a_restore_target()
    {
        var driveRoot = Path.GetPathRoot(_tempRoot)!;
        Assert.NotNull(BackupManager.SiteRootProblem(driveRoot));
        Assert.NotNull(BackupManager.SiteRootProblem("relative/www"));
        Assert.NotNull(BackupManager.SiteRootProblem(""));
        Assert.Null(BackupManager.SiteRootProblem(_siteRoot));

        var archive = CraftedArchive(driveRoot, "docroots/evil.loc/planted.txt");
        var (_, _, items, skipped) = Bm().RestoreBackup(archive, new BackupRestoreOptions(SiteFiles: true));

        Assert.False(File.Exists(Path.Combine(driveRoot, "planted.txt")));
        Assert.Contains(skipped, s => s.Detail.Contains("drive root"));
        Assert.Empty(items);
    }

    [Fact]
    public void Absolute_and_parent_paths_have_no_target()
    {
        Assert.Null(BackupManager.SiteFileTarget(_siteRoot, "../x.txt"));
        Assert.Null(BackupManager.SiteFileTarget(_siteRoot, "a/../../x.txt"));
        Assert.Null(BackupManager.SiteFileTarget(_siteRoot, Path.Combine(_tempRoot, "abs.txt")));
        Assert.Null(BackupManager.SiteFileTarget(_siteRoot, ""));
        Assert.Equal(Path.Combine(_siteRoot, "css", "app.css"), BackupManager.SiteFileTarget(_siteRoot, "css/app.css"));
    }

    [Fact]
    public void A_linked_folder_inside_the_site_is_not_written_through()
    {
        var outside = Path.Combine(_tempRoot, "outside");
        Directory.CreateDirectory(outside);
        var link = Path.Combine(_siteRoot, "linked");
        if (!TryLinkDirectory(link, outside)) return; // the OS refused to create a link here

        Assert.Null(BackupManager.SiteFileTarget(_siteRoot, "linked/x.txt"));
        Assert.NotNull(BackupManager.SiteFileTarget(_siteRoot, "css/x.txt"));
    }

    private static bool TryLinkDirectory(string link, string target)
    {
        try
        {
            if (!OperatingSystem.IsWindows())
            {
                Directory.CreateSymbolicLink(link, target);
                return true;
            }
            // A junction needs no admin rights, unlike a Windows symlink.
            using var p = Process.Start(new ProcessStartInfo("cmd.exe", $"/c mklink /J \"{link}\" \"{target}\"")
            {
                CreateNoWindow = true, UseShellExecute = false, RedirectStandardOutput = true, RedirectStandardError = true,
            })!;
            p.WaitForExit(10_000);
            return Directory.Exists(link);
        }
        catch { return false; }
    }

    [Fact]
    public void Mysql_commands_keep_the_password_off_the_command_line()
    {
        var (exe, args, env) = MysqlBackupClient.Command(
            Path.Combine(_tempRoot, "mysql", "bin", OperatingSystem.IsWindows() ? "mysqld.exe" : "mysqld"),
            "mysql", 3307, "s3cret", MysqlBackupClient.ImportArgs("shop"));

        Assert.Equal(Path.Combine(_tempRoot, "mysql", "bin", OperatingSystem.IsWindows() ? "mysql.exe" : "mysql"), exe);
        Assert.Equal(new[] { "-h", "127.0.0.1", "-P", "3307", "-u", "root", "--connect-timeout=5", "shop" }, args);
        Assert.Equal("s3cret", env["MYSQL_PWD"]);
        Assert.DoesNotContain(args, a => a.Contains("s3cret"));
        Assert.Empty(MysqlBackupClient.Command(exe, "mysql", 3306, null, []).Env);
    }

    [Fact]
    public void Create_database_statement_quotes_a_checked_name()
    {
        Assert.Equal(new[] { "--connect-timeout=5", "-e", "CREATE DATABASE IF NOT EXISTS `shop_db`" },
            MysqlBackupClient.CreateDatabaseArgs("shop_db"));
        Assert.Equal("shop", MysqlBackupClient.DumpArgs("shop")[^1]);

        foreach (var ok in new[] { "shop", "shop_db", "my-db", "a1" })
            Assert.True(MysqlBackupClient.IsSafeDatabaseName(ok), ok);
        foreach (var bad in new[] { "", "-e", "--help", "a`b", "a b", "../x", "x;drop", new string('a', 65), null })
            Assert.False(MysqlBackupClient.IsSafeDatabaseName(bad), bad ?? "null");
    }
}
