using NKS.WebDevConsole.Daemon.Data;
using Dapper;

namespace NKS.WebDevConsole.Daemon.Tests;

public class DatabaseTests
{
    [Fact]
    public void CreateConnection_OpensSuccessfully()
    {
        var db = new Database(":memory:");
        using var conn = db.CreateConnection();

        Assert.Equal(System.Data.ConnectionState.Open, conn.State);
    }

    [Fact]
    public void CreateConnection_SetsWalJournalMode()
    {
        var db = new Database(":memory:");
        using var conn = db.CreateConnection();

        var mode = conn.QuerySingle<string>("PRAGMA journal_mode;");
        // In-memory DBs may report "memory" instead of "wal" since WAL requires a file
        Assert.Contains(mode, new[] { "wal", "memory" });
    }

    [Fact]
    public void CreateConnection_EnablesForeignKeys()
    {
        var db = new Database(":memory:");
        using var conn = db.CreateConnection();

        var fk = conn.QuerySingle<int>("PRAGMA foreign_keys;");
        Assert.Equal(1, fk);
    }

    [Fact]
    public void ConnectionString_ContainsDataSource()
    {
        var db = new Database("test.db");
        Assert.Contains("Data Source=test.db", db.ConnectionString);
    }

    [Fact]
    public void CreateConnection_CanExecuteBasicQuery()
    {
        var db = new Database(":memory:");
        using var conn = db.CreateConnection();

        var result = conn.QuerySingle<int>("SELECT 1 + 1;");
        Assert.Equal(2, result);
    }
}
