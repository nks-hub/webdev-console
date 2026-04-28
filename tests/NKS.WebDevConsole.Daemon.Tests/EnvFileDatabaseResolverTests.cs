using NKS.WebDevConsole.Daemon.Deploy;

namespace NKS.WebDevConsole.Daemon.Tests;

/// <summary>
/// Phase 6.3 — round-trip tests for the .env discovery used by
/// <see cref="PreDeploySnapshotter"/> when SQLite isn't found and we need
/// to fall back to mysqldump. Pure file-parsing — every test creates a
/// disposable temp project layout (siteRoot/.env + siteRoot/public/).
/// </summary>
public sealed class EnvFileDatabaseResolverTests : IDisposable
{
    private readonly string _root;
    private readonly string _docRoot;

    public EnvFileDatabaseResolverTests()
    {
        _root = Path.Combine(Path.GetTempPath(), $"wdc-envtest-{Guid.NewGuid():N}");
        _docRoot = Path.Combine(_root, "public");
        Directory.CreateDirectory(_docRoot);
    }

    public void Dispose()
    {
        try { Directory.Delete(_root, recursive: true); } catch { /* best effort */ }
    }

    private void WriteEnv(string contents, string fileName = ".env", bool inDocRoot = false)
    {
        var dir = inDocRoot ? _docRoot : _root;
        File.WriteAllText(Path.Combine(dir, fileName), contents);
    }

    [Fact]
    public void TryResolve_ReturnsNull_WhenNoEnvFileExists()
    {
        var conn = EnvFileDatabaseResolver.TryResolve(_docRoot);
        Assert.Null(conn);
    }

    [Fact]
    public void TryResolve_ReturnsNull_WhenEnvHasNoDbKeys()
    {
        WriteEnv("APP_NAME=demo\nAPP_KEY=xyz\n");
        var conn = EnvFileDatabaseResolver.TryResolve(_docRoot);
        Assert.Null(conn);
    }

    [Fact]
    public void TryResolve_ReturnsNull_WhenDbConnectionPresentButNoDatabaseName()
    {
        // DB_DATABASE is mandatory — without it we'd dump nothing useful.
        WriteEnv("DB_CONNECTION=mysql\nDB_HOST=127.0.0.1\n");
        var conn = EnvFileDatabaseResolver.TryResolve(_docRoot);
        Assert.Null(conn);
    }

    [Fact]
    public void TryResolve_ParsesLaravelStyleMysql()
    {
        WriteEnv("""
            DB_CONNECTION=mysql
            DB_HOST=127.0.0.1
            DB_PORT=3306
            DB_DATABASE=myapp_prod
            DB_USERNAME=appuser
            DB_PASSWORD=s3cret!
            """);
        var conn = EnvFileDatabaseResolver.TryResolve(_docRoot);
        Assert.NotNull(conn);
        Assert.Equal("mysql", conn!.Type);
        Assert.Equal("127.0.0.1", conn.Host);
        Assert.Equal(3306, conn.Port);
        Assert.Equal("myapp_prod", conn.Database);
        Assert.Equal("appuser", conn.User);
        Assert.Equal("s3cret!", conn.Password);
    }

    [Fact]
    public void TryResolve_StripsSurroundingDoubleQuotesFromValues()
    {
        WriteEnv("""
            DB_CONNECTION=mysql
            DB_DATABASE="my db"
            DB_PASSWORD="p@ss with spaces"
            """);
        var conn = EnvFileDatabaseResolver.TryResolve(_docRoot);
        Assert.Equal("my db", conn!.Database);
        Assert.Equal("p@ss with spaces", conn.Password);
    }

    [Fact]
    public void TryResolve_StripsSurroundingSingleQuotesFromValues()
    {
        WriteEnv("DB_CONNECTION=mysql\nDB_DATABASE='quoted'\n");
        var conn = EnvFileDatabaseResolver.TryResolve(_docRoot);
        Assert.Equal("quoted", conn!.Database);
    }

    [Fact]
    public void TryResolve_DefaultsHostAndPortWhenMissing()
    {
        WriteEnv("DB_CONNECTION=mysql\nDB_DATABASE=test\n");
        var conn = EnvFileDatabaseResolver.TryResolve(_docRoot);
        Assert.Equal("127.0.0.1", conn!.Host);
        Assert.Equal(3306, conn.Port);
        Assert.Equal("root", conn.User);
        Assert.Null(conn.Password);
    }

    [Fact]
    public void TryResolve_NormalisesPostgresAliases()
    {
        WriteEnv("DB_CONNECTION=postgres\nDB_DATABASE=appdb\n");
        var conn = EnvFileDatabaseResolver.TryResolve(_docRoot);
        Assert.Equal("pgsql", conn!.Type);
        Assert.Equal(5432, conn.Port);
    }

    [Fact]
    public void TryResolve_NormalisesMariaDb()
    {
        WriteEnv("DB_CONNECTION=mariadb\nDB_DATABASE=appdb\n");
        var conn = EnvFileDatabaseResolver.TryResolve(_docRoot);
        Assert.Equal("mariadb", conn!.Type);
        Assert.Equal(3306, conn.Port);
    }

    [Fact]
    public void TryResolve_AcceptsDatabaseDriverAlias()
    {
        WriteEnv("DATABASE_DRIVER=mysql\nDATABASE_NAME=demo\nDATABASE_USER=admin\n");
        var conn = EnvFileDatabaseResolver.TryResolve(_docRoot);
        Assert.Equal("mysql", conn!.Type);
        Assert.Equal("demo", conn.Database);
        Assert.Equal("admin", conn.User);
    }

    [Fact]
    public void TryResolve_IgnoresCommentLinesAndEmptyLines()
    {
        WriteEnv("""
            # This is a comment
            DB_CONNECTION=mysql

            # another
            DB_DATABASE=cmtdb
            """);
        var conn = EnvFileDatabaseResolver.TryResolve(_docRoot);
        Assert.Equal("cmtdb", conn!.Database);
    }

    [Fact]
    public void TryResolve_PrefersRootEnvOverDocRootEnv()
    {
        // Convention: site-root .env is canonical; docroot .env is rare
        // (and dangerous — it's web-served on misconfigured nginx). When
        // both exist, root wins.
        WriteEnv("DB_CONNECTION=mysql\nDB_DATABASE=root_db\n", inDocRoot: false);
        WriteEnv("DB_CONNECTION=mysql\nDB_DATABASE=docroot_db\n", inDocRoot: true);
        var conn = EnvFileDatabaseResolver.TryResolve(_docRoot);
        Assert.Equal("root_db", conn!.Database);
    }

    [Fact]
    public void TryResolve_FallsBackToEnvLocal()
    {
        WriteEnv("DB_CONNECTION=mysql\nDB_DATABASE=localdb\n", fileName: ".env.local");
        var conn = EnvFileDatabaseResolver.TryResolve(_docRoot);
        Assert.Equal("localdb", conn!.Database);
    }

    [Fact]
    public void TryResolve_HonoursCustomPortWhenSpecified()
    {
        WriteEnv("DB_CONNECTION=mysql\nDB_DATABASE=demo\nDB_PORT=33060\n");
        var conn = EnvFileDatabaseResolver.TryResolve(_docRoot);
        Assert.Equal(33060, conn!.Port);
    }

    [Fact]
    public void TryResolve_DiscoveredAt_PointsToActualFile()
    {
        WriteEnv("DB_CONNECTION=mysql\nDB_DATABASE=demo\n");
        var conn = EnvFileDatabaseResolver.TryResolve(_docRoot);
        Assert.EndsWith(".env", conn!.DiscoveredAt);
        Assert.True(File.Exists(conn.DiscoveredAt));
    }
}
