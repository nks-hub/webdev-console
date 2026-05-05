namespace NKS.WebDevConsole.Daemon.Tests;

public sealed class PostgreSqlHelperTests
{
    [Fact]
    public void ValidatePassword_RejectsMissing()
    {
        Assert.NotNull(PostgreSqlHelper.ValidatePassword(""));
    }

    [Fact]
    public void ValidatePassword_RejectsShortPassword()
    {
        Assert.NotNull(PostgreSqlHelper.ValidatePassword("short"));
    }

    [Fact]
    public void ValidatePassword_AcceptsSafePassword()
    {
        Assert.Null(PostgreSqlHelper.ValidatePassword("PgLocal99!"));
    }

    [Fact]
    public void GetPayloadValue_ReturnsFrontendAlias()
    {
        var body = new Dictionary<string, string> { ["newPassword"] = "PgLocal99!" };

        Assert.Equal("PgLocal99!", PostgreSqlHelper.GetPayloadValue(body, "newPwd", "newPassword"));
    }

    [Fact]
    public void GetPayloadValue_IsCaseInsensitive()
    {
        var body = new Dictionary<string, string> { ["NewPassword"] = "PgLocal99!" };

        Assert.Equal("PgLocal99!", PostgreSqlHelper.GetPayloadValue(body, "newPwd", "newPassword"));
    }

    [Fact]
    public void BuildAlterUserSql_UsesPostgresUser()
    {
        var sql = PostgreSqlHelper.BuildAlterUserSql("PgLocal99!");

        Assert.Equal("ALTER USER postgres WITH PASSWORD 'PgLocal99!';", sql);
    }

    [Fact]
    public void ResolveSiblingTool_ReturnsNull_WhenToolMissing()
    {
        var fake = Path.Combine(Path.GetTempPath(), $"wdc-pg-{Guid.NewGuid():N}", "bin", "postgres.exe");

        Assert.Null(PostgreSqlHelper.ResolveSiblingTool(fake, "psql"));
    }
}
