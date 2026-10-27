namespace NKS.WebDevConsole.Daemon.Tests;

public sealed class MySqlUserHelperTests
{
    [Theory]
    [InlineData("app")]
    [InlineData("chatujme_user")]
    [InlineData("user-name")]
    public void ValidateUserName_AcceptsSafeNames(string userName)
    {
        Assert.Null(MySqlUserHelper.ValidateUserName(userName));
    }

    [Theory]
    [InlineData("")]
    [InlineData("bad'user")]
    [InlineData("bad\\user")]
    public void ValidateUserName_RejectsUnsafeNames(string userName)
    {
        Assert.NotNull(MySqlUserHelper.ValidateUserName(userName));
    }

    [Theory]
    [InlineData("localhost")]
    [InlineData("127.0.0.1")]
    [InlineData("%")]
    [InlineData("10.254.0.%")]
    public void ValidateHost_AcceptsLocalAndPatternHosts(string host)
    {
        Assert.Null(MySqlUserHelper.ValidateHost(host));
    }

    [Theory]
    [InlineData("")]
    [InlineData("bad'host")]
    [InlineData("bad host")]
    public void ValidateHost_RejectsUnsafeHosts(string host)
    {
        Assert.NotNull(MySqlUserHelper.ValidateHost(host));
    }

    [Fact]
    public void BuildCreateUserSql_CreatesAccountAndFlushes()
    {
        var sql = MySqlUserHelper.BuildCreateUserSql("app", "localhost", "secret123");

        Assert.Contains("CREATE USER IF NOT EXISTS 'app'@'localhost' IDENTIFIED BY 'secret123';", sql);
        Assert.Contains("FLUSH PRIVILEGES;", sql);
    }

    [Fact]
    public void BuildAlterPasswordSql_AltersExactAccount()
    {
        var sql = MySqlUserHelper.BuildAlterPasswordSql("app", "127.0.0.1", "secret123");

        Assert.Contains("ALTER USER 'app'@'127.0.0.1' IDENTIFIED BY 'secret123';", sql);
    }

    [Fact]
    public void BuildDropUserSql_DropsExactAccount()
    {
        var sql = MySqlUserHelper.BuildDropUserSql("app", "%");

        Assert.Equal("DROP USER IF EXISTS 'app'@'%';\nFLUSH PRIVILEGES;\n", sql);
    }

    [Theory]
    [InlineData("none", "")]
    [InlineData("read", "GRANT SELECT ON `chatujme`.* TO 'app'@'localhost';")]
    [InlineData("readWrite", "GRANT SELECT, INSERT, UPDATE, DELETE, CREATE TEMPORARY TABLES ON `chatujme`.* TO 'app'@'localhost';")]
    [InlineData("admin", "GRANT ALL PRIVILEGES ON `chatujme`.* TO 'app'@'localhost';")]
    public void BuildGrantDatabaseSql_MapsPrivilegePreset(string preset, string expectedGrant)
    {
        var sql = MySqlUserHelper.BuildGrantDatabaseSql("app", "localhost", "chatujme", preset);

        if (expectedGrant.Length == 0)
            Assert.DoesNotContain("GRANT", sql);
        else
            Assert.Contains(expectedGrant, sql);
        Assert.EndsWith("FLUSH PRIVILEGES;\n", sql);
    }

    [Fact]
    public void BuildGrantDatabaseSql_RejectsUnsafeDatabaseName()
    {
        Assert.Throws<ArgumentException>(() =>
            MySqlUserHelper.BuildGrantDatabaseSql("app", "localhost", "bad-name", "read"));
    }

    // H2: the grant tables and server schemas are never a grant target.
    [Theory]
    [InlineData("mysql")]
    [InlineData("MySQL")]
    [InlineData("sys")]
    [InlineData("information_schema")]
    [InlineData("PERFORMANCE_SCHEMA")]
    public void System_schemas_are_refused_as_a_grant_target(string database)
    {
        Assert.NotNull(MySqlUserHelper.ValidateDatabaseName(database));
        Assert.Throws<ArgumentException>(() => MySqlUserHelper.BuildGrantDatabaseSql("app", "localhost", database, "read"));
    }

    [Theory]
    [InlineData("shop")]
    [InlineData("mysql_app")]
    [InlineData("sysdb")]
    public void Ordinary_databases_stay_valid_grant_targets(string database)
    {
        Assert.Null(MySqlUserHelper.ValidateDatabaseName(database));
    }

    [Theory]
    [InlineData("localhost", null, false)]
    [InlineData("localhost", "read", false)]
    [InlineData("127.0.0.1", "readWrite", false)]
    [InlineData("::1", "none", false)]
    [InlineData("LOCALHOST", "read", false)]
    [InlineData("localhost", "admin", true)]
    [InlineData("%", null, true)]
    [InlineData("%", "read", true)]
    [InlineData("10.0.0.%", "readWrite", true)]
    [InlineData("local_ost", "read", true)]
    [InlineData("db.example.com", "read", true)]
    public void Admin_preset_or_a_non_local_host_is_elevated(string host, string? preset, bool elevated)
    {
        Assert.Equal(elevated, MySqlUserHelper.IsElevatedAccount(host, preset));
    }
}
