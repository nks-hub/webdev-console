namespace NKS.WebDevConsole.Daemon.Tests;

public sealed class MySqlPasswordHelperTests
{
    // ---- ValidatePassword ----

    [Fact]
    public void ValidatePassword_Null_ReturnsError()
    {
        Assert.NotNull(MySqlPasswordHelper.ValidatePassword(null));
    }

    [Fact]
    public void ValidatePassword_Empty_ReturnsError()
    {
        Assert.NotNull(MySqlPasswordHelper.ValidatePassword(""));
    }

    [Fact]
    public void ValidatePassword_TooShort_ReturnsError()
    {
        Assert.NotNull(MySqlPasswordHelper.ValidatePassword("short"));
    }

    [Fact]
    public void ValidatePassword_ExactlyEight_ReturnsNull()
    {
        Assert.Null(MySqlPasswordHelper.ValidatePassword("12345678"));
    }

    [Fact]
    public void ValidatePassword_NullByte_ReturnsError()
    {
        Assert.NotNull(MySqlPasswordHelper.ValidatePassword("password\0x"));
    }

    [Fact]
    public void ValidatePassword_SingleQuote_ReturnsError()
    {
        Assert.NotNull(MySqlPasswordHelper.ValidatePassword("pass'word1"));
    }

    [Fact]
    public void ValidatePassword_DoubleQuote_ReturnsError()
    {
        Assert.NotNull(MySqlPasswordHelper.ValidatePassword("pass\"word1"));
    }

    [Fact]
    public void ValidatePassword_Backslash_ReturnsError()
    {
        Assert.NotNull(MySqlPasswordHelper.ValidatePassword("pass\\word1"));
    }

    [Fact]
    public void ValidatePassword_TooLong_ReturnsError()
    {
        Assert.NotNull(MySqlPasswordHelper.ValidatePassword(new string('a', 129)));
    }

    [Fact]
    public void ValidatePassword_MaxLength_ReturnsNull()
    {
        Assert.Null(MySqlPasswordHelper.ValidatePassword(new string('a', 128)));
    }

    [Fact]
    public void ValidatePassword_ValidWithSpecialChars_ReturnsNull()
    {
        // Hyphens, dots, at-signs etc. are fine.
        Assert.Null(MySqlPasswordHelper.ValidatePassword("S3cur3-P@ss!word"));
    }

    // ---- BuildAlterUserSql ----

    [Fact]
    public void BuildAlterUserSql_ContainsFlushPrivileges()
    {
        var sql = MySqlPasswordHelper.BuildAlterUserSql("secret12");
        Assert.Contains("FLUSH PRIVILEGES", sql);
    }

    [Fact]
    public void BuildAlterUserSql_ContainsAllRootHosts()
    {
        var sql = MySqlPasswordHelper.BuildAlterUserSql("secret12");
        Assert.Contains("'root'@'localhost'", sql);
        Assert.Contains("'root'@'127.0.0.1'", sql);
        Assert.Contains("'root'@'%'", sql);
    }

    [Fact]
    public void BuildAlterUserSql_EscapesSingleQuoteInPassword()
    {
        // Even though ValidatePassword blocks quotes, BuildAlterUserSql still escapes them.
        var sql = MySqlPasswordHelper.BuildAlterUserSql("it''s12345");
        // The escaped form should appear — original single quotes doubled.
        Assert.Contains("it''''s12345", sql);
    }

    [Fact]
    public void BuildAlterUserSql_InjectsPassword()
    {
        const string pwd = "MySecure99";
        var sql = MySqlPasswordHelper.BuildAlterUserSql(pwd);
        Assert.Contains($"IDENTIFIED BY '{pwd}'", sql);
    }

    // ---- WriteTempInitFile ----

    [Fact]
    public void WriteTempInitFile_CreatesFileWithContent()
    {
        var sql = "SELECT 1;\n";
        var path = MySqlPasswordHelper.WriteTempInitFile(sql);
        try
        {
            Assert.True(File.Exists(path));
            Assert.Equal(sql, File.ReadAllText(path));
        }
        finally
        {
            File.Delete(path);
        }
    }

    [Fact]
    public void WriteTempInitFile_EachCallCreatesDistinctFile()
    {
        var p1 = MySqlPasswordHelper.WriteTempInitFile("A");
        var p2 = MySqlPasswordHelper.WriteTempInitFile("B");
        try
        {
            Assert.NotEqual(p1, p2);
        }
        finally
        {
            File.Delete(p1);
            File.Delete(p2);
        }
    }

    // ---- ResolveMysqlCli / ResolveMysqladmin ----

    [Fact]
    public void ResolveMysqlCli_ReturnsNull_WhenMysqldPathIsNull()
    {
        Assert.Null(MySqlPasswordHelper.ResolveMysqlCli(null!));
    }

    [Fact]
    public void ResolveMysqlCli_ReturnsNull_WhenBinaryMissing()
    {
        // Use a temp dir with no binaries.
        var tmp = Path.Combine(Path.GetTempPath(), $"wdc-test-{Guid.NewGuid():N}", "bin", "mysqld.exe");
        Assert.Null(MySqlPasswordHelper.ResolveMysqlCli(tmp));
    }

    [Fact]
    public void ResolveMysqladmin_ReturnsNull_WhenBinaryMissing()
    {
        var tmp = Path.Combine(Path.GetTempPath(), $"wdc-test-{Guid.NewGuid():N}", "bin", "mysqld.exe");
        Assert.Null(MySqlPasswordHelper.ResolveMysqladmin(tmp));
    }

    [Fact]
    public void ResolveMysqlCli_ReturnsPath_WhenBinaryExists()
    {
        var dir = Directory.CreateTempSubdirectory("wdc-test-");
        var binDir = Directory.CreateDirectory(Path.Combine(dir.FullName, "bin"));
        var ext = OperatingSystem.IsWindows() ? ".exe" : "";
        var fakeMysqld = Path.Combine(binDir.FullName, "mysqld" + ext);
        var fakeMysql = Path.Combine(binDir.FullName, "mysql" + ext);
        File.WriteAllText(fakeMysqld, "");
        File.WriteAllText(fakeMysql, "");
        try
        {
            var result = MySqlPasswordHelper.ResolveMysqlCli(fakeMysqld);
            Assert.Equal(fakeMysql, result);
        }
        finally
        {
            dir.Delete(recursive: true);
        }
    }
}
