using System.IO.Compression;
using Moq;
using NKS.WebDevConsole.Core.Interfaces;
using NKS.WebDevConsole.Core.Models;
using NKS.WebDevConsole.Daemon.Backup;
using Microsoft.Extensions.Logging.Abstractions;

namespace NKS.WebDevConsole.Daemon.Tests;

/// <summary>
/// Tests for BackupContentFlags-aware CreateBackup (tasks 22/35).
/// All tests run in isolated temp directories.
/// </summary>
public class BackupContentFlagsTests : IDisposable
{
    private readonly string _tempRoot;
    private readonly string _wdcDir;

    public BackupContentFlagsTests()
    {
        _tempRoot = Path.Combine(Path.GetTempPath(), $"wdc-flags-tests-{Guid.NewGuid():N}");
        _wdcDir = Path.Combine(_tempRoot, ".wdc");
        Directory.CreateDirectory(_wdcDir);
    }

    public void Dispose()
    {
        try { if (Directory.Exists(_tempRoot)) Directory.Delete(_tempRoot, recursive: true); } catch { }
    }

    private BackupManager Bm() => new(NullLogger<BackupManager>.Instance, _wdcDir);

    // ── CreateBackup returns 4-tuple with Flags field ─────────────────────

    [Fact]
    public void CreateBackup_returns_flags_in_result()
    {
        var bm = Bm();
        var outZip = Path.Combine(_wdcDir, "backups", "test.zip");
        var result = bm.CreateBackup(outZip, BackupContentFlags.Vhosts | BackupContentFlags.Ssl);
        Assert.Equal(BackupContentFlags.Vhosts | BackupContentFlags.Ssl, result.Flags);
    }

    // ── Vhosts flag packs sites/ ──────────────────────────────────────────

    [Fact]
    public void VhostsFlag_packs_sites_directory()
    {
        Directory.CreateDirectory(Path.Combine(_wdcDir, "sites"));
        File.WriteAllText(Path.Combine(_wdcDir, "sites", "foo.loc.toml"), "domain = \"foo.loc\"");

        var bm = Bm();
        var outZip = Path.Combine(_wdcDir, "backups", "vhosts.zip");
        bm.CreateBackup(outZip, BackupContentFlags.Vhosts);

        using var zip = ZipFile.OpenRead(outZip);
        Assert.Contains(zip.Entries, e => e.FullName.StartsWith("vhosts/sites/"));
        Assert.Contains(zip.Entries, e => e.FullName == "manifest.json");
    }

    [Fact]
    public void NoVhostsFlag_does_not_pack_sites_directory()
    {
        Directory.CreateDirectory(Path.Combine(_wdcDir, "sites"));
        File.WriteAllText(Path.Combine(_wdcDir, "sites", "bar.loc.toml"), "domain = \"bar.loc\"");

        var bm = Bm();
        var outZip = Path.Combine(_wdcDir, "backups", "no-vhosts.zip");
        bm.CreateBackup(outZip, BackupContentFlags.Ssl);

        using var zip = ZipFile.OpenRead(outZip);
        Assert.DoesNotContain(zip.Entries, e => e.FullName.StartsWith("vhosts/sites/"));
    }

    // ── SSL flag packs ssl/sites/ ─────────────────────────────────────────

    [Fact]
    public void SslFlag_packs_ssl_sites_directory()
    {
        var sslDir = Path.Combine(_wdcDir, "ssl", "sites", "mysite.loc");
        Directory.CreateDirectory(sslDir);
        File.WriteAllText(Path.Combine(sslDir, "cert.pem"), "CERT");
        File.WriteAllText(Path.Combine(sslDir, "key.pem"), "KEY");

        var bm = Bm();
        var outZip = Path.Combine(_wdcDir, "backups", "ssl.zip");
        bm.CreateBackup(outZip, BackupContentFlags.Ssl);

        using var zip = ZipFile.OpenRead(outZip);
        Assert.Contains(zip.Entries, e => e.FullName.Contains("ssl/sites/") && e.FullName.EndsWith("cert.pem"));
    }

    // ── PluginConfigs flag packs caddy/ and plugins/ ──────────────────────

    [Fact]
    public void PluginConfigsFlag_packs_caddy_directory()
    {
        var caddyDir = Path.Combine(_wdcDir, "caddy");
        Directory.CreateDirectory(caddyDir);
        File.WriteAllText(Path.Combine(caddyDir, "Caddyfile"), "localhost { respond 200 }");

        var bm = Bm();
        var outZip = Path.Combine(_wdcDir, "backups", "plugincfg.zip");
        bm.CreateBackup(outZip, BackupContentFlags.PluginConfigs);

        using var zip = ZipFile.OpenRead(outZip);
        Assert.Contains(zip.Entries, e => e.FullName.StartsWith("plugin-configs/caddy/"));
    }

    [Fact]
    public void PluginConfigsFlag_packs_plugin_config_json()
    {
        var pluginDir = Path.Combine(_wdcDir, "plugins", "nks.wdc.apache");
        Directory.CreateDirectory(pluginDir);
        File.WriteAllText(Path.Combine(pluginDir, "config.json"), "{\"port\":80}");

        var bm = Bm();
        var outZip = Path.Combine(_wdcDir, "backups", "plugincfg2.zip");
        bm.CreateBackup(outZip, BackupContentFlags.PluginConfigs);

        using var zip = ZipFile.OpenRead(outZip);
        Assert.Contains(zip.Entries, e => e.FullName == "plugin-configs/nks.wdc.apache/config.json");
    }

    // ── StateDb is always included regardless of flags ────────────────────

    [Fact]
    public void StateDb_always_included_regardless_of_flags()
    {
        var dataDir = Path.Combine(_wdcDir, "data");
        Directory.CreateDirectory(dataDir);
        File.WriteAllBytes(Path.Combine(dataDir, "state.db"), new byte[] { 0x53, 0x51, 0x4C });

        var bm = Bm();
        var outZip = Path.Combine(_wdcDir, "backups", "statedb.zip");
        bm.CreateBackup(outZip, BackupContentFlags.None);

        using var zip = ZipFile.OpenRead(outZip);
        Assert.Contains(zip.Entries, e => e.FullName == "data/state.db");
    }

    // ── Default flag combination ──────────────────────────────────────────

    [Fact]
    public void Default_flags_include_vhosts_ssl_pluginconfigs()
    {
        Assert.True(BackupContentFlags.Default.HasFlag(BackupContentFlags.Vhosts));
        Assert.True(BackupContentFlags.Default.HasFlag(BackupContentFlags.Ssl));
        Assert.True(BackupContentFlags.Default.HasFlag(BackupContentFlags.PluginConfigs));
        Assert.False(BackupContentFlags.Default.HasFlag(BackupContentFlags.Databases));
        Assert.False(BackupContentFlags.Default.HasFlag(BackupContentFlags.Docroots));
    }

    // ── Manifest records flags ────────────────────────────────────────────

    [Fact]
    public void Manifest_records_selected_flags()
    {
        var bm = Bm();
        var outZip = Path.Combine(_wdcDir, "backups", "manifest-flags.zip");
        bm.CreateBackup(outZip, BackupContentFlags.Vhosts | BackupContentFlags.Ssl);

        using var zip = ZipFile.OpenRead(outZip);
        var manifestEntry = zip.GetEntry("manifest.json");
        Assert.NotNull(manifestEntry);
        using var sr = new StreamReader(manifestEntry!.Open());
        var json = sr.ReadToEnd();
        Assert.Contains("\"version\":\"2\"", json);
        Assert.Contains("flags", json);
    }

    // ── ListBackups returns BackupEntry with ContentFlags ─────────────────

    [Fact]
    public void ListBackups_returns_contentFlags_per_entry()
    {
        var bm = Bm();
        var outZip = Path.Combine(_wdcDir, "backups", "listed.zip");
        bm.CreateBackup(outZip, BackupContentFlags.Ssl);

        var list = bm.ListBackups();
        Assert.Single(list);
        // Flags may be Ssl or Default depending on manifest parse success
        Assert.NotEqual(BackupContentFlags.None, list[0].ContentFlags);
    }

    // ── Empty archive detection: verify zip is not 0 bytes ───────────────

    [Fact]
    public void CreateBackup_produces_nonzero_archive()
    {
        var bm = Bm();
        var outZip = Path.Combine(_wdcDir, "backups", "nonzero.zip");
        var result = bm.CreateBackup(outZip, BackupContentFlags.Default);
        Assert.True(result.SizeBytes > 0, $"Expected non-zero archive, got {result.SizeBytes} bytes");
        Assert.True(new FileInfo(outZip).Length > 0);
    }

    // ── Databases flag: one mysqldump per database ────────────────────────

    private sealed class FakeDumper(IReadOnlyList<string>? databases, params string[] failing) : IMysqlBackupDumper
    {
        public IReadOnlyList<string> ListDatabases() =>
            databases ?? throw new InvalidOperationException("Can't connect to MySQL server on '127.0.0.1:3306'");

        public void Dump(string database, Stream output)
        {
            var bytes = System.Text.Encoding.UTF8.GetBytes($"CREATE DATABASE `{database}`;");
            output.Write(bytes);
            if (failing.Contains(database)) throw new InvalidOperationException("Lost connection");
        }
    }

    private static string ReadEntry(ZipArchive zip, string name)
    {
        using var sr = new StreamReader(zip.GetEntry(name)!.Open());
        return sr.ReadToEnd();
    }

    [Fact]
    public void DatabasesFlag_packs_one_dump_per_database()
    {
        var bm = new BackupManager(NullLogger<BackupManager>.Instance, _wdcDir, mysqlDumper: new FakeDumper(["shop", "blog"]));
        var outZip = Path.Combine(_wdcDir, "backups", "db.zip");
        var result = bm.CreateBackup(outZip, BackupContentFlags.Databases);

        Assert.Equal(BackupContentFlags.Databases, result.Flags);
        Assert.Empty(result.Skipped);
        using var zip = ZipFile.OpenRead(outZip);
        Assert.Equal("CREATE DATABASE `shop`;", ReadEntry(zip, "databases/shop.sql"));
        Assert.Equal("CREATE DATABASE `blog`;", ReadEntry(zip, "databases/blog.sql"));
        Assert.Contains("\"flags\":\"Databases\"", ReadEntry(zip, "manifest.json"));
    }

    [Fact]
    public void DatabasesFlag_without_mysql_is_reported_and_left_out_of_the_manifest()
    {
        var bm = new BackupManager(NullLogger<BackupManager>.Instance, _wdcDir, mysqlDumper: new FakeDumper(null));
        var outZip = Path.Combine(_wdcDir, "backups", "db-down.zip");
        var result = bm.CreateBackup(outZip, BackupContentFlags.Vhosts | BackupContentFlags.Databases);

        Assert.Equal(BackupContentFlags.Vhosts, result.Flags);
        var skip = Assert.Single(result.Skipped);
        Assert.Equal(BackupContentFlags.Databases, skip.Content);
        Assert.Contains("Can't connect", skip.Detail);
        using var zip = ZipFile.OpenRead(outZip);
        Assert.DoesNotContain(zip.Entries, e => e.FullName.StartsWith("databases/"));
        Assert.Contains("\"flags\":\"Vhosts\"", ReadEntry(zip, "manifest.json"));
        Assert.Equal(BackupContentFlags.Vhosts, bm.ListBackups().Single().ContentFlags);
    }

    [Fact]
    public void DatabasesFlag_failed_dump_leaves_no_partial_file()
    {
        var bm = new BackupManager(NullLogger<BackupManager>.Instance, _wdcDir, mysqlDumper: new FakeDumper(["shop", "blog"], "blog"));
        var outZip = Path.Combine(_wdcDir, "backups", "db-partial.zip");
        var result = bm.CreateBackup(outZip, BackupContentFlags.Databases);

        Assert.Equal(BackupContentFlags.Databases, result.Flags);
        Assert.Equal("blog: Lost connection", Assert.Single(result.Skipped).Detail);
        using var zip = ZipFile.OpenRead(outZip);
        Assert.NotNull(zip.GetEntry("databases/shop.sql"));
        Assert.Null(zip.GetEntry("databases/blog.sql"));
        Assert.Empty(Directory.GetFiles(Path.Combine(_wdcDir, "backups"), ".dump-*"));
    }

    // ── Docroots flag: each site's document root ──────────────────────────

    private static ISiteRegistry Sites(params (string Domain, string Root)[] sites)
    {
        var map = sites.ToDictionary(s => s.Domain, s => new SiteConfig { Domain = s.Domain, DocumentRoot = s.Root });
        var mock = new Mock<ISiteRegistry>();
        mock.Setup(r => r.Sites).Returns(map);
        return mock.Object;
    }

    [Fact]
    public void DocrootsFlag_packs_site_files_and_reports_missing_folders()
    {
        var www = Path.Combine(_tempRoot, "shop", "www");
        Directory.CreateDirectory(Path.Combine(www, "css"));
        File.WriteAllText(Path.Combine(www, "index.php"), "<?php echo 1;");
        File.WriteAllText(Path.Combine(www, "css", "app.css"), "body{}");
        File.WriteAllText(Path.Combine(www, ".htaccess"), "RewriteEngine On");
        var missing = Path.Combine(_tempRoot, "gone");

        var bm = new BackupManager(NullLogger<BackupManager>.Instance, _wdcDir,
            Sites(("shop.loc", www), ("gone.loc", missing)));
        var outZip = Path.Combine(_wdcDir, "backups", "docroots.zip");
        var result = bm.CreateBackup(outZip, BackupContentFlags.Docroots);

        Assert.Equal(BackupContentFlags.Docroots, result.Flags);
        var skip = Assert.Single(result.Skipped);
        Assert.StartsWith("gone.loc: ", skip.Detail);
        using var zip = ZipFile.OpenRead(outZip);
        Assert.Equal("<?php echo 1;", ReadEntry(zip, "docroots/shop.loc/index.php"));
        Assert.NotNull(zip.GetEntry("docroots/shop.loc/css/app.css"));
        Assert.NotNull(zip.GetEntry("docroots/shop.loc/.htaccess"));
    }

    [Fact]
    public void DocrootsFlag_never_packs_the_backups_folder()
    {
        File.WriteAllText(Path.Combine(_wdcDir, "note.txt"), "x");
        var bm = new BackupManager(NullLogger<BackupManager>.Instance, _wdcDir, Sites(("home.loc", _wdcDir)));
        var outZip = Path.Combine(_wdcDir, "backups", "self.zip");
        bm.CreateBackup(outZip, BackupContentFlags.Docroots);

        using var zip = ZipFile.OpenRead(outZip);
        Assert.NotNull(zip.GetEntry("docroots/home.loc/note.txt"));
        Assert.DoesNotContain(zip.Entries, e => e.FullName.StartsWith("docroots/home.loc/backups/"));
    }

    [Fact]
    public void Opt_in_flags_without_sources_are_reported_not_claimed()
    {
        var bm = Bm();
        var outZip = Path.Combine(_wdcDir, "backups", "no-sources.zip");
        var result = bm.CreateBackup(outZip, BackupContentFlags.All);

        Assert.False(result.Flags.HasFlag(BackupContentFlags.Databases));
        Assert.False(result.Flags.HasFlag(BackupContentFlags.Docroots));
        Assert.Equal(2, result.Skipped.Count);
    }
}
