using Dapper;
using Microsoft.Data.Sqlite;
using Microsoft.Extensions.Logging.Abstractions;
using NKS.WebDevConsole.Daemon.Sites;

namespace NKS.WebDevConsole.Daemon.Tests;

/// <summary>
/// Unit tests for <see cref="MampMigrator"/> — specifically the MAMP PRO SQLite
/// db path added in commit 881778d. Each test spins up a temp SQLite file
/// mirroring the real MAMP PRO schema and calls the internal
/// <c>DiscoverFromPaths</c> overload with explicit fixture paths, avoiding
/// any %APPDATA% / C:\MAMP leakage onto the dev machine state.
///
/// Covers: NormalizePhpVersion, NormalizeDocumentRoot, dummy filtering, SSL
/// dedup, bulk alias join, empty-field skipping, read-only open mode.
/// </summary>
public sealed class MampMigratorTests : IDisposable
{
    private readonly string _tempDir;
    private readonly string _dbPath;

    public MampMigratorTests()
    {
        _tempDir = Path.Combine(Path.GetTempPath(), "wdc-mamp-test-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_tempDir);
        _dbPath = Path.Combine(_tempDir, "mamp.db");
    }

    public void Dispose()
    {
        SqliteConnection.ClearAllPools();
        try
        {
            if (Directory.Exists(_tempDir)) Directory.Delete(_tempDir, recursive: true);
        }
        catch
        {
            // Best-effort cleanup — SQLite may still hold a shared-cache file
            // handle on Windows for a few ms after the last connection closes.
        }
    }

    private void SeedDatabase(IEnumerable<(long id, string servername, string docroot, long ssl, string? phpversion)> hosts,
                              IEnumerable<(long hostId, string alias)>? aliases = null)
    {
        var connStr = new SqliteConnectionStringBuilder { DataSource = _dbPath }.ToString();
        using var conn = new SqliteConnection(connStr);
        conn.Open();
        conn.Execute("""
            CREATE TABLE VirtualHosts (
                id INTEGER PRIMARY KEY,
                servername TEXT,
                documentroot TEXT,
                sslenabled INTEGER,
                phpversion TEXT
            );
            CREATE TABLE VirtualHostServerAlias (
                id INTEGER PRIMARY KEY,
                VirtualHosts_id INTEGER,
                serveralias TEXT
            );
            """);
        foreach (var h in hosts)
        {
            conn.Execute(
                "INSERT INTO VirtualHosts (id, servername, documentroot, sslenabled, phpversion) " +
                "VALUES (@id, @servername, @docroot, @ssl, @phpversion)",
                new { h.id, h.servername, h.docroot, h.ssl, h.phpversion });
        }
        if (aliases != null)
        {
            long aliasId = 1;
            foreach (var a in aliases)
            {
                conn.Execute(
                    "INSERT INTO VirtualHostServerAlias (id, VirtualHosts_id, serveralias) " +
                    "VALUES (@id, @hostId, @alias)",
                    new { id = aliasId++, a.hostId, a.alias });
            }
        }
    }

    private static MampMigrator NewMigrator() => new(NullLogger<MampMigrator>.Instance);

    private IReadOnlyList<MampMigrator.DiscoveredSite> Discover()
        // Explicit empty mamp-root list so the fallback vhost scan does nothing
        // — we want to exercise the SQLite path in isolation.
        => NewMigrator().DiscoverFromPaths(new[] { _dbPath }, Array.Empty<string>());

    [Fact]
    public void Discover_ReadsBasicHostFromProDb()
    {
        SeedDatabase(new[] { (1L, "bim.loc", @"C:\work\htdocs\bim\www", 1L, (string?)"8.4.12") });
        var result = Discover();
        var site = Assert.Single(result);
        Assert.Equal("bim.loc", site.Domain);
        Assert.Equal(@"C:\work\htdocs\bim\www", site.DocumentRoot);
        Assert.Equal("8.4", site.PhpVersion); // normalized from 8.4.12
        Assert.True(site.SslEnabled);
        Assert.Empty(site.Aliases);
        Assert.Equal(_dbPath, site.SourcePath);
    }

    [Fact]
    public void Discover_NormalizesPhpVersionToMajorMinor()
    {
        SeedDatabase(new[]
        {
            (1L, "a.loc", @"C:\a", 0L, (string?)"8.4.12"),
            (2L, "b.loc", @"C:\b", 0L, (string?)"8.1.15"),
            (3L, "c.loc", @"C:\c", 0L, (string?)"7.4.30"),
            (4L, "d.loc", @"C:\d", 0L, (string?)""),    // blank → default 8.4
            (5L, "e.loc", @"C:\e", 0L, (string?)null),  // null  → default 8.4
        });
        var result = Discover().ToDictionary(s => s.Domain);
        Assert.Equal("8.4", result["a.loc"].PhpVersion);
        Assert.Equal("8.1", result["b.loc"].PhpVersion);
        Assert.Equal("7.4", result["c.loc"].PhpVersion);
        Assert.Equal("8.4", result["d.loc"].PhpVersion);
        Assert.Equal("8.4", result["e.loc"].PhpVersion);
    }

    [Fact]
    public void Discover_NormalizesLowercaseDriveLetterInDocumentRoot()
    {
        SeedDatabase(new[] { (1L, "busy.loc", @"c:\work\htdocs\blury\busy\www", 0L, (string?)"8.4.12") });
        var site = Assert.Single(Discover());
        Assert.StartsWith(@"C:\", site.DocumentRoot);
    }

    [Fact]
    public void Discover_FiltersApacheDummyPlaceholders()
    {
        SeedDatabase(new[]
        {
            (1L, "dummy-host.example.com",  @"C:\x", 0L, (string?)"8.4.12"),
            (2L, "dummy-host2.example.com", @"C:\x", 0L, (string?)"8.4.12"),
            (3L, "real.loc",                @"C:\real", 0L, (string?)"8.4.12"),
        });
        var result = Discover();
        var site = Assert.Single(result);
        Assert.Equal("real.loc", site.Domain);
    }

    [Fact]
    public void Discover_DeduplicatesDomain_PrefersSslVariant()
    {
        // MAMP PRO often creates two rows for the same servername: one http
        // and one https. Users typically pick different PHP versions for each.
        // We want the SSL one with its own PHP version.
        SeedDatabase(new[]
        {
            (1L, "nks-is.loc", @"C:\work\sites\ads-sniffer\www", 0L, (string?)"8.4.12"),
            (2L, "nks-is.loc", @"C:\work\sites\ads-sniffer\www", 1L, (string?)"8.1.15"),
        });
        var site = Assert.Single(Discover());
        Assert.True(site.SslEnabled);
        Assert.Equal("8.1", site.PhpVersion);
    }

    [Fact]
    public void Discover_LoadsAliasesFromJoinTable()
    {
        SeedDatabase(
            hosts:   new[] { (1L, "crm.loc", @"C:\work\sites\nks-crm\www", 1L, (string?)"8.4.12") },
            aliases: new[] { (1L, "demo.crm.loc"), (1L, "test.crm.loc") });
        var site = Assert.Single(Discover());
        Assert.Equal(2, site.Aliases.Length);
        Assert.Contains("demo.crm.loc", site.Aliases);
        Assert.Contains("test.crm.loc", site.Aliases);
    }

    [Fact]
    public void Discover_BulkAliasJoin_EachHostKeepsItsOwnAliases()
    {
        // Regression test shielding the bulk alias join against off-by-one
        // or wrong-FK bugs. 20 hosts × 2 aliases each — each host must pick
        // up only its own aliases, none of the neighbors'.
        var hosts = Enumerable.Range(1, 20)
            .Select(i => ((long)i, $"host{i}.loc", $@"C:\root\{i}", (long)(i % 2), (string?)"8.4.12"))
            .ToList();
        var aliases = Enumerable.Range(1, 20)
            .SelectMany(i => new[] { ((long)i, $"a.host{i}.loc"), ((long)i, $"b.host{i}.loc") })
            .ToList();
        SeedDatabase(hosts, aliases);

        var result = Discover();
        Assert.Equal(20, result.Count);
        foreach (var site in result)
        {
            Assert.Equal(2, site.Aliases.Length);
            Assert.Contains($"a.{site.Domain}", site.Aliases);
            Assert.Contains($"b.{site.Domain}", site.Aliases);
        }
    }

    [Fact]
    public void Discover_SkipsHostsWithEmptyServernameOrRoot()
    {
        SeedDatabase(new[]
        {
            (1L, "",      @"C:\a", 0L, (string?)"8.4.12"),  // blank servername → skip
            (2L, "b.loc", "",      0L, (string?)"8.4.12"),  // blank docroot   → skip
            (3L, "c.loc", @"C:\c", 0L, (string?)"8.4.12"),
        });
        var site = Assert.Single(Discover());
        Assert.Equal("c.loc", site.Domain);
    }

    [Fact]
    public void Discover_SortsByDomainCaseInsensitive()
    {
        SeedDatabase(new[]
        {
            (1L, "zeta.loc",  @"C:\z", 0L, (string?)"8.4.12"),
            (2L, "Alpha.loc", @"C:\a", 0L, (string?)"8.4.12"),
            (3L, "beta.loc",  @"C:\b", 0L, (string?)"8.4.12"),
        });
        var result = Discover().Select(s => s.Domain).ToList();
        Assert.Equal(new[] { "Alpha.loc", "beta.loc", "zeta.loc" }, result);
    }

    [Fact]
    public void Discover_MissingDbFile_ReturnsEmpty()
    {
        // Don't seed — db file does not exist at _dbPath.
        var result = Discover();
        Assert.Empty(result);
    }

    [Fact]
    public void Discover_OpensDatabaseReadOnly_DoesNotMutateFile()
    {
        // Ensures the read-only open mode doesn't rewrite the journal header
        // or acquire an exclusive lock — snapshot mtime, call Discover,
        // re-open from another reader, assert mtime unchanged.
        SeedDatabase(new[] { (1L, "x.loc", @"C:\x", 0L, (string?)"8.4.12") });
        SqliteConnection.ClearAllPools();
        var mtimeBefore = File.GetLastWriteTimeUtc(_dbPath);

        var site = Assert.Single(Discover());
        Assert.Equal("x.loc", site.Domain);

        // Second reader should succeed without any wait.
        var reopened = new SqliteConnectionStringBuilder
        {
            DataSource = _dbPath,
            Mode = SqliteOpenMode.ReadWrite,
        }.ToString();
        using var conn = new SqliteConnection(reopened);
        conn.Open();
        var count = conn.ExecuteScalar<long>("SELECT COUNT(*) FROM VirtualHosts");
        Assert.Equal(1, count);
        conn.Close();

        var mtimeAfter = File.GetLastWriteTimeUtc(_dbPath);
        Assert.Equal(mtimeBefore, mtimeAfter);
    }

    [Fact]
    public void DiscoveredSite_RecordShape()
    {
        var site = new MampMigrator.DiscoveredSite(
            "shop.loc", @"C:\htdocs\shop", "8.3", true,
            new[] { "www.shop.loc" }, "vhost.conf");
        Assert.Equal("shop.loc", site.Domain);
        Assert.Equal(@"C:\htdocs\shop", site.DocumentRoot);
        Assert.Equal("8.3", site.PhpVersion);
        Assert.True(site.SslEnabled);
        Assert.Single(site.Aliases);
        Assert.Equal("www.shop.loc", site.Aliases[0]);
        Assert.Equal("vhost.conf", site.SourcePath);
    }
}
