using NKS.WebDevConsole.Daemon.Mcp;

namespace NKS.WebDevConsole.Daemon.Tests;

/// <summary>Same cases as services/mcp-server/src/tools/databases.test.ts.</summary>
public class SqlReadOnlyGuardTests
{
    [Theory]
    [InlineData(@"SELECT * FROM users")]
    [InlineData(@"select 1;")]
    [InlineData(@"SHOW CREATE TABLE users")]
    [InlineData(@"SHOW VARIABLES LIKE ""character_set%""")]
    [InlineData(@"SELECT * FROM logs WHERE msg = 'delete me'")]
    [InlineData(@"SELECT `delete`, `update` FROM t")]
    [InlineData(@"SELECT REPLACE(name, 'a', 'b'), INSERT(name, 1, 2, 'x') FROM t")]
    [InlineData(@"WITH x AS (SELECT 1 AS n) SELECT n FROM x")]
    [InlineData(@"DESCRIBE users")]
    [InlineData(@"EXPLAIN SELECT * FROM users")]
    [InlineData(@"SELECT 'it''s' FROM t")]
    [InlineData(@"SELECT 1 -- delete
")]
    public void Allows_read_only_statements(string sql) => Assert.True(SqlReadOnlyGuard.IsReadOnly(sql));

    [Theory]
    [InlineData(@"WITH x AS (SELECT 1) DELETE FROM users")]
    [InlineData(@"with x as (select id from t) update t set a = 1")]
    [InlineData(@"WITH x AS (SELECT 1) INSERT INTO t SELECT * FROM x")]
    [InlineData(@"SELECT * FROM users INTO OUTFILE '/tmp/x'")]
    [InlineData(@"SELECT 'x' INTO DUMPFILE '/tmp/x'")]
    [InlineData(@"EXPLAIN ANALYZE DELETE FROM users")]
    [InlineData(@"/* x */ DROP TABLE users")]
    [InlineData(@"SELECT 1; DROP TABLE users")]
    [InlineData(@"UPDATE t SET a = 1")]
    [InlineData(@"WITH x AS (SELECT 1 AS `a\`) DELETE FROM users -- `")]
    [InlineData(@"WITH c AS (SELECT 1--1) DELETE FROM users")]
    [InlineData(@"SELECT 1--1 INTO OUTFILE '/tmp/x'")]
    [InlineData(@"WITH c AS (SELECT 1) /*! DELETE FROM users */")]
    [InlineData(@"SELECT 1 /*M! INTO OUTFILE ""/tmp/x"" */")]
    public void Rejects_statements_that_can_write(string sql) => Assert.False(SqlReadOnlyGuard.IsReadOnly(sql));
}
