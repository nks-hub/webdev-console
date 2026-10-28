using System.Diagnostics;
using System.IO.Compression;
using System.Text;
using Moq;
using NKS.WebDevConsole.Core.Interfaces;
using NKS.WebDevConsole.Core.Models;
using NKS.WebDevConsole.Daemon.Backup;
using NKS.WebDevConsole.Daemon.Data;
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

    // Live v0.5.4: every restored database failed with ERROR 3546, because
    // mysqldump wrote SET @@GLOBAL.GTID_PURGED into the dump.

    private const string MySqlDumpHelp = """
        mysqldump  Ver 9.7.1 for Win64 on x86_64 (MySQL Community Server - GPL)
          --single-transaction
                              Creates a consistent snapshot by dumping all tables in a
          --set-gtid-purged[=name]
                              Add 'SET @@GLOBAL.GTID_PURGED' to the output. Possible
        """;

    private const string MariaDbDumpHelp = """
        mysqldump from 12.3.1-MariaDB, client 10.19 for Win64 (AMD64)
          --gtid              Used together with --master-data=1 or --dump-slave=1.
          --single-transaction
        """;

    [Fact]
    public void Mysql_dumps_leave_out_the_gtid_set_and_mariadb_gets_no_unknown_option()
    {
        Assert.Equal(new[] { "--set-gtid-purged=OFF" }, MysqlDumpCompat.DumpOptionsFromHelp(MySqlDumpHelp));
        Assert.Empty(MysqlDumpCompat.DumpOptionsFromHelp(MariaDbDumpHelp));
        Assert.Empty(MysqlDumpCompat.DumpOptionsFromHelp(""));
        Assert.Empty(MysqlDumpCompat.DumpOptionsFromHelp(null));
        // A missing or broken binary adds nothing rather than failing the export.
        Assert.Empty(MysqlDumpCompat.DumpOptions(Path.Combine(_tempRoot, "no-such", "mysqldump.exe")));

        var (_, args, _) = MysqlBackupClient.Command("C:/mysql/bin/mysqld.exe", "mysqldump", 3306, null,
            MysqlBackupClient.DumpArgs("shop"), MysqlDumpCompat.DumpOptionsFromHelp(MySqlDumpHelp));
        Assert.Equal(new[]
        {
            "-h", "127.0.0.1", "-P", "3306", "-u", "root", "--set-gtid-purged=OFF",
            "--single-transaction", "--routines", "--events", "--triggers", "--databases", "shop",
        }, args);
    }

    [Fact]
    public void Import_options_go_before_the_database_name()
    {
        var (_, args, _) = MysqlBackupClient.Command("C:/mysql/bin/mysqld.exe", "mysql", 3306, null,
            MysqlBackupClient.ImportArgs("shop"), ["--system-command=OFF"]);
        Assert.Equal(new[] { "-h", "127.0.0.1", "-P", "3306", "-u", "root", "--system-command=OFF", "--connect-timeout=5", "shop" }, args);
    }

    private static async Task<byte[]> Filter(byte[] dump)
    {
        using var output = new MemoryStream();
        await MysqlDumpCompat.CopyWithoutServerStateAsync(new MemoryStream(dump), output, CancellationToken.None);
        return output.ToArray();
    }

    [Fact]
    public async Task Restore_drops_the_gtid_and_log_bin_statements_of_old_dumps()
    {
        var dump = string.Join("\r\n",
            "-- MySQL dump 10.13",
            "SET @MYSQLDUMP_TEMP_LOG_BIN = @@SESSION.SQL_LOG_BIN;",
            "SET @@SESSION.SQL_LOG_BIN= 0;",
            "",
            "SET @@GLOBAL.GTID_PURGED=/*!80000 '+'*/ '3e11fa47-71ca-11e1-9e33-c80aa9429562:1-23,",
            "4a1b2c3d-71ca-11e1-9e33-c80aa9429562:1-5';",
            "CREATE DATABASE /*!32312 IF NOT EXISTS*/ `shop`;",
            "INSERT INTO `t` VALUES (1,'mentions SET @@GLOBAL.GTID_PURGED in data');",
            "set @@global.gtid_purged='x:1';",
            "SET @@SESSION.SQL_LOG_BIN = @MYSQLDUMP_TEMP_LOG_BIN;",
            "-- Dump completed");

        var text = Encoding.UTF8.GetString(await Filter(Encoding.UTF8.GetBytes(dump)));

        Assert.DoesNotContain("GTID_PURGED=", text);
        Assert.DoesNotContain("4a1b2c3d", text);
        Assert.DoesNotContain("gtid_purged='x:1'", text);
        Assert.DoesNotContain("SET @@SESSION.SQL_LOG_BIN", text);
        Assert.Contains("CREATE DATABASE /*!32312 IF NOT EXISTS*/ `shop`;\r\n", text);
        Assert.Contains("INSERT INTO `t` VALUES (1,'mentions SET @@GLOBAL.GTID_PURGED in data');", text);
        Assert.EndsWith("-- Dump completed", text);
    }

    [Fact]
    public async Task Restore_filter_keeps_binary_data_byte_for_byte()
    {
        // Not valid UTF-8 on purpose: a dump without --hex-blob holds raw bytes.
        var insert = Encoding.ASCII.GetBytes("INSERT INTO `b` VALUES ('")
            .Concat(new byte[] { 0xFF, 0xFE, 0x00, 0xC3, 0x28, 0x80 })
            .Concat(Encoding.ASCII.GetBytes("');\n")).ToArray();
        var gtid = Encoding.ASCII.GetBytes("SET @@GLOBAL.GTID_PURGED='u:1';\n");

        // Long enough to cross the 80 KB read buffer in the middle of a line.
        var bigLine = Encoding.ASCII.GetBytes("INSERT INTO `c` VALUES ('" + new string('x', 100_000) + "');\n");
        var output = await Filter([.. gtid, .. insert, .. bigLine, .. insert]);

        Assert.Equal([.. insert, .. bigLine, .. insert], output);
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
