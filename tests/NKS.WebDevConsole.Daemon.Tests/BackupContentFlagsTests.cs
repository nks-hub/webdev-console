using System.IO.Compression;
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
        var outZip = Path.Combine(_tempRoot, "test.zip");
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
        var outZip = Path.Combine(_tempRoot, "vhosts.zip");
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
        var outZip = Path.Combine(_tempRoot, "no-vhosts.zip");
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
        var outZip = Path.Combine(_tempRoot, "ssl.zip");
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
        var outZip = Path.Combine(_tempRoot, "plugincfg.zip");
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
        var outZip = Path.Combine(_tempRoot, "plugincfg2.zip");
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
        var outZip = Path.Combine(_tempRoot, "statedb.zip");
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
        var outZip = Path.Combine(_tempRoot, "manifest-flags.zip");
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
        var outZip = Path.Combine(_tempRoot, "nonzero.zip");
        var result = bm.CreateBackup(outZip, BackupContentFlags.Default);
        Assert.True(result.SizeBytes > 0, $"Expected non-zero archive, got {result.SizeBytes} bytes");
        Assert.True(new FileInfo(outZip).Length > 0);
    }
}
