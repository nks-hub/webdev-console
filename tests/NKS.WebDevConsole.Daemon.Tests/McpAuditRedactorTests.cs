using System.Text.Json;
using Dapper;
using NKS.WebDevConsole.Daemon.Data;
using NKS.WebDevConsole.Daemon.Mcp;

namespace NKS.WebDevConsole.Daemon.Tests;

/// <summary>
/// M1: secrets in MCP tool arguments never reach the audit log. The MCP
/// server redacts them (auditLog.ts); the daemon strips them again before storing.
/// </summary>
public sealed class McpAuditRedactorTests
{
    [Theory]
    [InlineData("password")]
    [InlineData("Password")]
    [InlineData("newPwd")]
    [InlineData("newPassword")]
    [InlineData("currentPwd")]
    [InlineData("rootPassword")]
    [InlineData("apiToken")]
    [InlineData("tunnelToken")]
    [InlineData("intentToken")]
    [InlineData("clientSecret")]
    public void Secret_keys_are_recognised(string key) => Assert.True(McpAuditRedactor.IsSecretKey(key));

    [Theory]
    [InlineData("userName")]
    [InlineData("host")]
    [InlineData("database")]
    [InlineData("sql")]
    public void Ordinary_keys_are_kept(string key) => Assert.False(McpAuditRedactor.IsSecretKey(key));

    [Fact]
    public void Whole_json_is_redacted_at_any_depth()
    {
        var redacted = McpAuditRedactor.Redact(
            """{"userName":"app","PASSWORD":"hunter2xx","nested":{"rootPassword":"r00tpass","list":[{"apiToken":"cf-tok"}]},"tunnelToken":{"a":"inner-secret"},"port":3306,"newPwd":12345678}""");
        using var doc = JsonDocument.Parse(redacted!);
        var root = doc.RootElement;
        Assert.Equal("app", root.GetProperty("userName").GetString());
        Assert.Equal(McpAuditRedactor.Placeholder, root.GetProperty("PASSWORD").GetString());
        Assert.Equal(McpAuditRedactor.Placeholder, root.GetProperty("nested").GetProperty("rootPassword").GetString());
        Assert.Equal(McpAuditRedactor.Placeholder, root.GetProperty("nested").GetProperty("list")[0].GetProperty("apiToken").GetString());
        Assert.Equal(McpAuditRedactor.Placeholder, root.GetProperty("tunnelToken").GetString());
        Assert.Equal(McpAuditRedactor.Placeholder, root.GetProperty("newPwd").GetString());
        Assert.Equal(3306, root.GetProperty("port").GetInt32());
        foreach (var secret in new[] { "hunter2xx", "r00tpass", "cf-tok", "inner-secret", "12345678" })
            Assert.DoesNotContain(secret, redacted);
    }

    [Fact]
    public void Summaries_without_secrets_are_stored_unchanged()
    {
        const string summary = """{"name":"žluťoučký","database":"shop"}""";
        Assert.Same(summary, McpAuditRedactor.Redact(summary));
        Assert.Null(McpAuditRedactor.Redact(null));
        Assert.Equal("", McpAuditRedactor.Redact(""));
    }

    [Theory]
    // The MCP server cuts summaries at 500 chars ("..." appended): not valid JSON.
    [InlineData("""{"userName":"app","password":"hunter2xx","data":"xxxxx...""")]
    [InlineData("""{"data":"xxxx","newPwd":"hunter2xx...""")]
    [InlineData("""{"currentPwd" : "hunter2xx", "x":1, "secret":true, "sql":"SELECT 1""")]
    [InlineData("""{"tunnelToken":{"inner":"hunter2xx","more":"text...""")]
    [InlineData("""{"user":"a","password":"hunter2xx","x":"y...""")]
    public void Truncated_summaries_are_redacted_textually(string summary)
    {
        var redacted = McpAuditRedactor.Redact(summary)!;
        Assert.DoesNotContain("hunter2xx", redacted);
        Assert.Contains(McpAuditRedactor.Placeholder, redacted);
    }

    [Fact]
    public async Task Repository_stores_the_redacted_summary()
    {
        var path = Path.Combine(Path.GetTempPath(), $"mcp-redact-test-{Guid.NewGuid():N}.db");
        try
        {
            var db = new Database(path);
            using (var conn = db.CreateConnection())
            {
                conn.Open();
                conn.Execute("""
                    CREATE TABLE mcp_tool_calls (
                        id TEXT PRIMARY KEY, called_at TEXT NOT NULL, session_id TEXT,
                        caller TEXT NOT NULL DEFAULT 'unknown', tool_name TEXT NOT NULL,
                        args_summary TEXT, args_hash TEXT, duration_ms INTEGER NOT NULL DEFAULT 0,
                        result_code TEXT NOT NULL DEFAULT 'ok', error_message TEXT,
                        danger_level TEXT NOT NULL DEFAULT 'read', intent_id TEXT);
                    """);
            }
            var repo = new McpToolCallsRepository(db);
            var id = await repo.InsertAsync(new McpToolCallRow
            {
                ToolName = "wdc_change_mysql_root_password",
                ArgsSummary = """{"currentPwd":"","newPwd":"N3wR00tPass"}""",
            }, CancellationToken.None);

            using var read = db.CreateConnection();
            read.Open();
            var stored = read.ExecuteScalar<string>("SELECT args_summary FROM mcp_tool_calls WHERE id = @id", new { id });
            Assert.DoesNotContain("N3wR00tPass", stored);
            Assert.Contains(McpAuditRedactor.Placeholder, stored);
        }
        finally
        {
            Microsoft.Data.Sqlite.SqliteConnection.ClearAllPools();
            try { File.Delete(path); } catch { /* best effort */ }
        }
    }
}
