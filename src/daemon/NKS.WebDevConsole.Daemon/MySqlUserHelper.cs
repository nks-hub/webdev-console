using System.Text.RegularExpressions;

// No namespace - Program.cs uses top-level statements compiled into global scope.

internal static class MySqlUserHelper
{
    private static readonly Regex SafeUserRegex = new(@"^[A-Za-z0-9_.-]{1,64}$", RegexOptions.Compiled);
    private static readonly Regex SafeHostRegex = new(@"^[A-Za-z0-9_.:%-]{1,255}$", RegexOptions.Compiled);
    private static readonly Regex SafeDatabaseRegex = new(@"^[A-Za-z0-9_]{1,64}$", RegexOptions.Compiled);

    public static string? ValidateUserName(string? userName)
    {
        if (string.IsNullOrWhiteSpace(userName))
            return "userName is required";
        return SafeUserRegex.IsMatch(userName)
            ? null
            : "userName may contain only letters, digits, underscore, dot, dash";
    }

    public static string? ValidateHost(string? host)
    {
        if (string.IsNullOrWhiteSpace(host))
            return "host is required";
        return SafeHostRegex.IsMatch(host)
            ? null
            : "host may contain only letters, digits, dot, dash, underscore, percent and colon";
    }

    public static string? ValidateDatabaseName(string? database)
    {
        if (string.IsNullOrWhiteSpace(database))
            return "database is required";
        return SafeDatabaseRegex.IsMatch(database)
            ? null
            : "database may contain only letters, digits and underscore";
    }

    public static string BuildCreateUserSql(string userName, string host, string password)
    {
        ValidateUserAndHostOrThrow(userName, host);
        return
            $"CREATE USER IF NOT EXISTS {Account(userName, host)} IDENTIFIED BY {Literal(password)};\n" +
            "FLUSH PRIVILEGES;\n";
    }

    public static string BuildAlterPasswordSql(string userName, string host, string password)
    {
        ValidateUserAndHostOrThrow(userName, host);
        return
            $"ALTER USER {Account(userName, host)} IDENTIFIED BY {Literal(password)};\n" +
            "FLUSH PRIVILEGES;\n";
    }

    public static string BuildDropUserSql(string userName, string host)
    {
        ValidateUserAndHostOrThrow(userName, host);
        return
            $"DROP USER IF EXISTS {Account(userName, host)};\n" +
            "FLUSH PRIVILEGES;\n";
    }

    public static string BuildGrantDatabaseSql(string userName, string host, string database, string preset)
    {
        ValidateUserAndHostOrThrow(userName, host);
        if (ValidateDatabaseName(database) is not null)
            throw new ArgumentException("Invalid database name", nameof(database));

        var privileges = preset switch
        {
            "none" => "",
            "read" => "SELECT",
            "readWrite" => "SELECT, INSERT, UPDATE, DELETE, CREATE TEMPORARY TABLES",
            "admin" => "ALL PRIVILEGES",
            _ => throw new ArgumentException("Invalid privilege preset", nameof(preset))
        };

        return string.IsNullOrEmpty(privileges)
            ? "FLUSH PRIVILEGES;\n"
            : $"GRANT {privileges} ON {Identifier(database)}.* TO {Account(userName, host)};\n" +
              "FLUSH PRIVILEGES;\n";
    }

    public static string BuildListUsersSql() =>
        "SELECT User, Host, plugin, account_locked, password_expired " +
        "FROM mysql.user WHERE User <> '' ORDER BY User, Host;";

    private static void ValidateUserAndHostOrThrow(string userName, string host)
    {
        if (ValidateUserName(userName) is { } userError)
            throw new ArgumentException(userError, nameof(userName));
        if (ValidateHost(host) is { } hostError)
            throw new ArgumentException(hostError, nameof(host));
    }

    private static string Account(string userName, string host) => $"{Literal(userName)}@{Literal(host)}";

    private static string Literal(string value) => $"'{value.Replace("'", "''")}'";

    private static string Identifier(string value) => $"`{value.Replace("`", "``")}`";
}
