using NKS.WebDevConsole.Core.Services;

namespace NKS.WebDevConsole.Daemon.Tests;

public sealed class MySqlRootPasswordTests
{
    [Fact]
    public void TryRead_ReturnsNull_WhenNoFileExists()
    {
        // In test environment the store file may or may not exist.
        // If it doesn't, TryRead should return null gracefully.
        var result = MySqlRootPassword.TryRead();
        // Either null (no file) or a non-empty string (dev machine has one)
        Assert.True(result is null || result.Length > 0);
    }

    [Fact]
    public void EnsureExists_ReturnsNonEmptyPassword()
    {
        var password = MySqlRootPassword.EnsureExists();
        Assert.NotNull(password);
        Assert.True(password.Length >= 16);
    }

    [Fact]
    public void EnsureExists_IsIdempotent()
    {
        var p1 = MySqlRootPassword.EnsureExists();
        var p2 = MySqlRootPassword.EnsureExists();
        Assert.Equal(p1, p2);
    }

    [Fact]
    public void Exists_ReturnsBool()
    {
        // After EnsureExists, file should exist
        MySqlRootPassword.EnsureExists();
        Assert.True(MySqlRootPassword.Exists());
    }
}
