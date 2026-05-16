using NKS.WebDevConsole.Core.Services;

namespace NKS.WebDevConsole.Daemon.Tests;

public sealed class MySqlRootPasswordTests
{
    [Fact]
    public void TryRead_ReturnsNull_WhenNoFileExists()
    {
        WithTempStore(() =>
        {
            Assert.Null(MySqlRootPassword.TryRead());
        });
    }

    [Fact]
    public void EnsureExists_ReturnsNonEmptyPassword()
    {
        WithTempStore(() =>
        {
            var password = MySqlRootPassword.EnsureExists();
            Assert.NotNull(password);
            Assert.True(password.Length >= 16);
        });
    }

    [Fact]
    public void EnsureExists_IsIdempotent()
    {
        WithTempStore(() =>
        {
            var p1 = MySqlRootPassword.EnsureExists();
            var p2 = MySqlRootPassword.EnsureExists();
            Assert.Equal(p1, p2);
        });
    }

    [Fact]
    public void Exists_ReturnsBool()
    {
        WithTempStore(() =>
        {
            MySqlRootPassword.EnsureExists();
            Assert.True(MySqlRootPassword.Exists());
        });
    }

    [Fact]
    public void Clear_RemovesStore()
    {
        WithTempStore(() =>
        {
            MySqlRootPassword.EnsureExists();
            MySqlRootPassword.Clear();

            Assert.False(MySqlRootPassword.Exists());
            Assert.Null(MySqlRootPassword.TryRead());
        });
    }

    private static void WithTempStore(Action action)
    {
        var previous = Environment.GetEnvironmentVariable("WDC_MYSQL_ROOT_PASSWORD_STORE");
        var dir = Directory.CreateTempSubdirectory("wdc-mysql-root-test-");
        var path = Path.Combine(dir.FullName, "mysql-root.dpapi");
        try
        {
            Environment.SetEnvironmentVariable("WDC_MYSQL_ROOT_PASSWORD_STORE", path);
            action();
        }
        finally
        {
            if (previous is null)
                Environment.SetEnvironmentVariable("WDC_MYSQL_ROOT_PASSWORD_STORE", null);
            else
                Environment.SetEnvironmentVariable("WDC_MYSQL_ROOT_PASSWORD_STORE", previous);
            dir.Delete(recursive: true);
        }
    }
}
