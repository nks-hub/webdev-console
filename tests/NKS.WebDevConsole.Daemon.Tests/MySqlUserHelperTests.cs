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
}
