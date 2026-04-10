using Microsoft.Data.Sqlite;
using Dapper;

namespace NKS.WebDevConsole.Daemon.Data;

public class Database
{
    private readonly string _connectionString;

    public Database(string dbPath)
    {
        _connectionString = $"Data Source={dbPath}";
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
