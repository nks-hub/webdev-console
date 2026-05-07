using System;

namespace NKS.WebDevConsole.Daemon.Data;

/// <summary>
/// Builds engine-specific drivers from the daemon's resolved connection
/// settings. Frontend always asks /api/databases/v2/* — this factory
/// decides which concrete driver answers (MySQL today, PostgreSQL once
/// Npgsql lands).
/// </summary>
public static class DbDriverFactory
{
    /// <summary>
    /// Default MySQL/MariaDB driver instance reading the daemon-managed
    /// root password from <c>MySqlRootPassword</c> (DPAPI on Windows,
    /// 0600 plaintext on Unix). Caller is responsible for resolving the
    /// port via <c>ResolveMysqlPortWithFallback</c>.
    /// </summary>
    public static MySqlDriver CreateMySql(int port, string? password = null)
    {
        if (password is null)
        {
            try
            {
                password = NKS.WebDevConsole.Core.Services.MySqlRootPassword.TryRead();
            }
            catch
            {
                password = null;
            }
        }
        return new MySqlDriver(host: "127.0.0.1", port: port, user: "root", password: password);
    }
}
