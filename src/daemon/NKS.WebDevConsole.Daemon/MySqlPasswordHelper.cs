using System.Text.RegularExpressions;

// No namespace — Program.cs uses top-level statements compiled into global scope.

/// <summary>
/// Shared helpers for the MySQL change-password and reset-password endpoints.
/// All logic is static so endpoints in Program.cs can call it without DI ceremony.
/// </summary>
internal static class MySqlPasswordHelper
{
    private static readonly Regex SafePasswordRegex =
        new(@"^[^\x00""'\\]{8,128}$", RegexOptions.Compiled);

    /// <summary>
    /// Validates a candidate root password.
    /// Returns null on success, or an error string describing the problem.
    /// </summary>
    public static string? ValidatePassword(string? password)
    {
        if (string.IsNullOrEmpty(password))
            return "newPwd is required";
        if (password.Length < 8)
            return "newPwd must be at least 8 characters";
        if (password.Contains('\0'))
            return "newPwd must not contain null bytes";
        if (password.Contains('"') || password.Contains('\'') || password.Contains('\\'))
            return "newPwd must not contain quote or backslash characters";
        if (password.Length > 128)
            return "newPwd must not exceed 128 characters";
        return null;
    }

    /// <summary>
    /// Builds the SQL init-file content that sets the root password on all
    /// localhost variants. Uses single-quote escaping as a secondary safety
    /// layer even though we already reject quotes in ValidatePassword.
    /// </summary>
    public static string BuildAlterUserSql(string password)
    {
        var escaped = password.Replace("'", "''");
        return
            "FLUSH PRIVILEGES;\n" +
            $"ALTER USER 'root'@'localhost' IDENTIFIED BY '{escaped}';\n" +
            $"ALTER USER 'root'@'127.0.0.1' IDENTIFIED BY '{escaped}';\n" +
            $"ALTER USER 'root'@'%' IDENTIFIED BY '{escaped}';\n" +
            "FLUSH PRIVILEGES;\n";
    }

    /// <summary>
    /// Resolves the mysql CLI binary path from a mysqld executable path.
    /// Returns null if not found.
    /// </summary>
    public static string? ResolveMysqlCli(string mysqldPath)
    {
        var dir = Path.GetDirectoryName(mysqldPath);
        if (string.IsNullOrEmpty(dir)) return null;
        var ext = OperatingSystem.IsWindows() ? ".exe" : "";
        var cli = Path.Combine(dir, "mysql" + ext);
        return File.Exists(cli) ? cli : null;
    }

    /// <summary>
    /// Resolves the mysqladmin binary path from a mysqld executable path.
    /// Returns null if not found.
    /// </summary>
    public static string? ResolveMysqladmin(string mysqldPath)
    {
        var dir = Path.GetDirectoryName(mysqldPath);
        if (string.IsNullOrEmpty(dir)) return null;
        var ext = OperatingSystem.IsWindows() ? ".exe" : "";
        var admin = Path.Combine(dir, "mysqladmin" + ext);
        return File.Exists(admin) ? admin : null;
    }

    /// <summary>
    /// Writes SQL to a temp file and returns the path. The caller MUST delete
    /// it in a finally block — the file contains a plaintext password.
    /// </summary>
    public static string WriteTempInitFile(string sql)
    {
        var tmp = Path.Combine(Path.GetTempPath(), $"wdc-mysql-init-{Guid.NewGuid():N}.sql");
        File.WriteAllText(tmp, sql);
        if (!OperatingSystem.IsWindows())
        {
            try
            {
                new FileInfo(tmp).UnixFileMode = UnixFileMode.UserRead | UnixFileMode.UserWrite;
            }
            catch { /* best effort */ }
        }
        return tmp;
    }

    /// <summary>
    /// Polls a TCP port until it responds or the timeout elapses.
    /// </summary>
    public static async Task<bool> WaitForTcpPortAsync(int port, TimeSpan timeout, CancellationToken ct)
    {
        using var cts = CancellationTokenSource.CreateLinkedTokenSource(ct);
        cts.CancelAfter(timeout);
        while (!cts.Token.IsCancellationRequested)
        {
            try
            {
                using var tcp = new System.Net.Sockets.TcpClient();
                await tcp.ConnectAsync("127.0.0.1", port, cts.Token);
                return true;
            }
            catch when (!cts.Token.IsCancellationRequested)
            {
                await Task.Delay(500, cts.Token).ConfigureAwait(false);
            }
        }
        return false;
    }

    /// <summary>
    /// Polls for a PID file to appear and contain a valid integer, up to timeout.
    /// Returns the PID or -1.
    /// </summary>
    public static async Task<int> WaitForPidFileAsync(string pidFilePath, TimeSpan timeout)
    {
        var deadline = DateTime.UtcNow + timeout;
        while (DateTime.UtcNow < deadline)
        {
            try
            {
                if (File.Exists(pidFilePath) &&
                    int.TryParse(File.ReadAllText(pidFilePath).Trim(), out var pid) &&
                    pid > 0)
                    return pid;
            }
            catch { /* file not fully written yet */ }
            await Task.Delay(300).ConfigureAwait(false);
        }
        return -1;
    }
}
