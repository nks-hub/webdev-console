using Microsoft.Data.Sqlite;
using Dapper;

namespace NKS.WebDevConsole.Daemon.Data;

/// <summary>
/// SQLite connection factory. Applies hardened defaults:
/// explicit <c>ReadWriteCreate</c> mode, <c>DefaultTimeout=30</c> (prevents
/// stuck connections from monopolising a Dapper query), and two PRAGMAs:
/// <c>journal_mode=WAL</c> for concurrent reads during backup and
/// <c>foreign_keys=ON</c> so migration-defined FK constraints actually
/// enforce at runtime (they're off by default in SQLite).
///
/// Note: <c>Microsoft.Data.Sqlite</c> does NOT have a MultipleStatements
/// flag (that's MySQL-specific). Injection defense for NKS WDC comes from
/// Dapper's always-parameterised query API — no string-concatenation
/// queries exist in the codebase.
/// </summary>
public sealed class Database
{
    private readonly string _connectionString;

    public Database(string dbPath)
    {
        // Use the builder instead of string interpolation so the escape rules
        // for paths containing spaces / semicolons are handled correctly.
        var builder = new SqliteConnectionStringBuilder
        {
            DataSource = dbPath,
            Mode = SqliteOpenMode.ReadWriteCreate,
            Cache = SqliteCacheMode.Default,
            DefaultTimeout = 30,
            ForeignKeys = true,
            Pooling = true,
        };
        _connectionString = builder.ToString();
    }

    public SqliteConnection CreateConnection()
    {
        var conn = new SqliteConnection(_connectionString);
        conn.Open();
        conn.Execute("PRAGMA journal_mode=WAL;");
        conn.Execute("PRAGMA foreign_keys=ON;");
        return conn;
    }

    public string ConnectionString => _connectionString;
}
