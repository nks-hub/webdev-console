using Dapper;
using NKS.WebDevConsole.Daemon.Data;

namespace NKS.WebDevConsole.Daemon.Mcp;

/// <summary>
/// Phase 8 — SQLite-backed audit log for every MCP tool call (read or
/// write). Mirrors the pattern of <see cref="McpSessionGrantsRepository"/>:
/// per-call connection, no pooling, indexes carry the hot paths.
/// </summary>
public sealed class McpToolCallsRepository
{
    private readonly Database _db;

    public McpToolCallsRepository(Database db) { _db = db; }

    /// <summary>Insert a new audit row. Generates id if not supplied.</summary>
    public async Task<string> InsertAsync(McpToolCallRow row, CancellationToken ct)
    {
        var id = string.IsNullOrEmpty(row.Id) ? Guid.NewGuid().ToString("N") : row.Id;
        var calledAt = string.IsNullOrEmpty(row.CalledAt)
            ? DateTime.UtcNow.ToString("yyyy-MM-ddTHH:mm:ss.fffZ")
            : row.CalledAt;

        using var conn = _db.CreateConnection();
        await conn.OpenAsync(ct);
        await conn.ExecuteAsync(
            "INSERT INTO mcp_tool_calls " +
            "(id, called_at, session_id, caller, tool_name, args_summary, " +
            " args_hash, duration_ms, result_code, error_message, danger_level, intent_id) " +
            "VALUES (@Id, @CalledAt, @SessionId, @Caller, @ToolName, @ArgsSummary, " +
            " @ArgsHash, @DurationMs, @ResultCode, @ErrorMessage, @DangerLevel, @IntentId)",
            new
            {
                Id = id,
                CalledAt = calledAt,
                row.SessionId,
                Caller = string.IsNullOrEmpty(row.Caller) ? "unknown" : row.Caller,
                row.ToolName,
                row.ArgsSummary,
                row.ArgsHash,
                row.DurationMs,
                ResultCode = string.IsNullOrEmpty(row.ResultCode) ? "ok" : row.ResultCode,
                row.ErrorMessage,
                DangerLevel = string.IsNullOrEmpty(row.DangerLevel) ? "read" : row.DangerLevel,
                row.IntentId,
            });
        return id;
    }

    /// <summary>List recent calls with optional filters + paging.</summary>
    public async Task<IReadOnlyList<McpToolCallRow>> ListAsync(
        int limit,
        int offset,
        string? dangerLevel,
        string? toolName,
        string? sessionId,
        CancellationToken ct,
        string? searchQuery = null)
    {
        var where = new List<string>();
        var p = new DynamicParameters();
        p.Add("Limit", Math.Clamp(limit, 1, 1000));
        p.Add("Offset", Math.Max(0, offset));

        if (!string.IsNullOrEmpty(dangerLevel))
        {
            where.Add("danger_level = @DangerLevel");
            p.Add("DangerLevel", dangerLevel);
        }
        if (!string.IsNullOrEmpty(toolName))
        {
            where.Add("tool_name = @ToolName");
            p.Add("ToolName", toolName);
        }
        if (!string.IsNullOrEmpty(sessionId))
        {
            where.Add("session_id = @SessionId");
            p.Add("SessionId", sessionId);
        }
        if (!string.IsNullOrWhiteSpace(searchQuery))
        {
            // Phase 8 — full-text-ish search. SQLite LIKE matches against
            // tool_name OR args_summary so operator can find calls by
            // either the tool they fired or the parameters they passed.
            where.Add("(tool_name LIKE @SearchPattern OR args_summary LIKE @SearchPattern)");
            p.Add("SearchPattern", "%" + searchQuery.Trim() + "%");
        }

        var whereSql = where.Count > 0 ? "WHERE " + string.Join(" AND ", where) : "";

        using var conn = _db.CreateConnection();
        await conn.OpenAsync(ct);
        var rows = await conn.QueryAsync<McpToolCallRow>(
            $@"SELECT id AS Id, called_at AS CalledAt, session_id AS SessionId,
                       caller AS Caller, tool_name AS ToolName,
                       args_summary AS ArgsSummary, args_hash AS ArgsHash,
                       duration_ms AS DurationMs, result_code AS ResultCode,
                       error_message AS ErrorMessage, danger_level AS DangerLevel,
                       intent_id AS IntentId
                FROM mcp_tool_calls
                {whereSql}
                ORDER BY called_at DESC
                LIMIT @Limit OFFSET @Offset",
            p);
        return rows.AsList();
    }

    /// <summary>Total count for paging.</summary>
    public async Task<int> CountAsync(
        string? dangerLevel, string? toolName, string? sessionId, CancellationToken ct,
        string? searchQuery = null)
    {
        var where = new List<string>();
        var p = new DynamicParameters();
        if (!string.IsNullOrEmpty(dangerLevel)) { where.Add("danger_level = @DangerLevel"); p.Add("DangerLevel", dangerLevel); }
        if (!string.IsNullOrEmpty(toolName)) { where.Add("tool_name = @ToolName"); p.Add("ToolName", toolName); }
        if (!string.IsNullOrEmpty(sessionId)) { where.Add("session_id = @SessionId"); p.Add("SessionId", sessionId); }
        if (!string.IsNullOrWhiteSpace(searchQuery))
        {
            where.Add("(tool_name LIKE @SearchPattern OR args_summary LIKE @SearchPattern)");
            p.Add("SearchPattern", "%" + searchQuery.Trim() + "%");
        }
        var whereSql = where.Count > 0 ? "WHERE " + string.Join(" AND ", where) : "";

        using var conn = _db.CreateConnection();
        await conn.OpenAsync(ct);
        return await conn.ExecuteScalarAsync<int>(
            $"SELECT COUNT(*) FROM mcp_tool_calls {whereSql}", p);
    }

    /// <summary>
    /// Aggregate stats for the Activity header card. Now includes
    /// percentile latencies (p50/p95/p99) and calls-per-minute rate so
    /// the Activity feed can render a "performance" KPI strip alongside
    /// the count tiles.
    /// </summary>
    public async Task<McpToolCallStats> GetStatsAsync(int withinMinutes, CancellationToken ct)
    {
        using var conn = _db.CreateConnection();
        await conn.OpenAsync(ct);
        var minutes = Math.Max(1, withinMinutes);
        var since = DateTime.UtcNow.AddMinutes(-minutes)
            .ToString("yyyy-MM-ddTHH:mm:ss.fffZ");

        var basic = await conn.QueryFirstOrDefaultAsync<McpToolCallStats>(
            @"SELECT
                COUNT(*) AS Total,
                SUM(CASE WHEN danger_level = 'read' THEN 1 ELSE 0 END) AS Reads,
                SUM(CASE WHEN danger_level = 'mutate' THEN 1 ELSE 0 END) AS Mutates,
                SUM(CASE WHEN danger_level = 'destructive' THEN 1 ELSE 0 END) AS Destructives,
                SUM(CASE WHEN result_code != 'ok' THEN 1 ELSE 0 END) AS Errors,
                MAX(called_at) AS LastCalledAt,
                COUNT(DISTINCT session_id) AS DistinctSessions
              FROM mcp_tool_calls
              WHERE called_at >= @Since",
            new { Since = since }) ?? new McpToolCallStats();

        // SQLite has no native percentile_cont. Instead of materialising every
        // duration_ms into a List<int> (could be 100k+ rows in a busy AI
        // session, ~400KB+ allocation per stats refresh), use ORDER BY +
        // LIMIT 1 OFFSET <position>. SQLite streams through the index until
        // it reaches the offset row, then returns just that single value.
        // The ORDER BY duration_ms isn't backed by an index right now, but
        // for typical bounded windows it's still much cheaper than the
        // full materialisation.
        async Task<int> percentileAsync(double pct)
        {
            if (basic.Total == 0) return 0;
            var idx = (int)Math.Floor(pct * (basic.Total - 1));
            var v = await conn.ExecuteScalarAsync<int?>(
                @"SELECT duration_ms FROM mcp_tool_calls
                  WHERE called_at >= @Since
                  ORDER BY duration_ms ASC
                  LIMIT 1 OFFSET @Offset",
                new { Since = since, Offset = idx });
            return v ?? 0;
        }
        var p50 = await percentileAsync(0.50);
        var p95 = await percentileAsync(0.95);
        var p99 = await percentileAsync(0.99);

        var perMinute = basic.Total > 0
            ? Math.Round((double)basic.Total / minutes, 2)
            : 0;
        var errorRate = basic.Total > 0
            ? Math.Round(100.0 * basic.Errors / basic.Total, 2)
            : 0;

        return basic with
        {
            P50DurationMs = p50,
            P95DurationMs = p95,
            P99DurationMs = p99,
            CallsPerMinute = perMinute,
            ErrorRatePercent = errorRate,
        };
    }

    /// <summary>
    /// Hourly bucket counts for the last N hours. Returns one row per
    /// bucket with read/mutate/destructive subtotals so the UI can render
    /// a stacked-bar timeline without paginating through every entry.
    /// </summary>
    public async Task<IReadOnlyList<McpToolCallTimelineBucket>> GetTimelineAsync(
        int withinHours, CancellationToken ct)
    {
        using var conn = _db.CreateConnection();
        await conn.OpenAsync(ct);
        var since = DateTime.UtcNow.AddHours(-Math.Clamp(withinHours, 1, 168))
            .ToString("yyyy-MM-ddTHH:mm:ss.fffZ");

        // SQLite strftime '%Y-%m-%dT%H' yields the hour bucket label.
        var rows = await conn.QueryAsync<McpToolCallTimelineBucket>(
            @"SELECT strftime('%Y-%m-%dT%H', called_at) AS Hour,
                     SUM(CASE WHEN danger_level = 'read' THEN 1 ELSE 0 END) AS Reads,
                     SUM(CASE WHEN danger_level = 'mutate' THEN 1 ELSE 0 END) AS Mutates,
                     SUM(CASE WHEN danger_level = 'destructive' THEN 1 ELSE 0 END) AS Destructives,
                     SUM(CASE WHEN result_code != 'ok' THEN 1 ELSE 0 END) AS Errors,
                     COUNT(*) AS Total
              FROM mcp_tool_calls
              WHERE called_at >= @Since
              GROUP BY Hour
              ORDER BY Hour ASC",
            new { Since = since });
        return rows.AsList();
    }

    /// <summary>
    /// Top-N tool aggregation — tool name × call count + avg duration over
    /// the last N hours. Powers the "what is AI mostly doing" panel.
    /// </summary>
    public async Task<IReadOnlyList<McpToolCallByToolRow>> GetByToolAsync(
        int withinHours, int limit, CancellationToken ct)
    {
        using var conn = _db.CreateConnection();
        await conn.OpenAsync(ct);
        var since = DateTime.UtcNow.AddHours(-Math.Clamp(withinHours, 1, 168))
            .ToString("yyyy-MM-ddTHH:mm:ss.fffZ");

        var rows = await conn.QueryAsync<McpToolCallByToolRow>(
            @"SELECT tool_name AS ToolName,
                     COUNT(*) AS Count,
                     ROUND(AVG(duration_ms)) AS AvgDurationMs,
                     MAX(danger_level) AS DangerLevel,
                     SUM(CASE WHEN result_code != 'ok' THEN 1 ELSE 0 END) AS Errors
              FROM mcp_tool_calls
              WHERE called_at >= @Since
              GROUP BY tool_name
              ORDER BY Count DESC
              LIMIT @Limit",
            new { Since = since, Limit = Math.Clamp(limit, 1, 100) });
        return rows.AsList();
    }

    /// <summary>Trim rows older than retention. Called by background sweeper.</summary>
    public async Task<int> PruneAsync(int retentionDays, CancellationToken ct)
    {
        using var conn = _db.CreateConnection();
        await conn.OpenAsync(ct);
        var cutoff = DateTime.UtcNow.AddDays(-Math.Max(1, retentionDays))
            .ToString("yyyy-MM-ddTHH:mm:ss.fffZ");
        return await conn.ExecuteAsync(
            "DELETE FROM mcp_tool_calls WHERE called_at < @Cutoff",
            new { Cutoff = cutoff });
    }
}

public sealed record McpToolCallRow
{
    public string Id { get; init; } = string.Empty;
    public string CalledAt { get; init; } = string.Empty;
    public string? SessionId { get; init; }
    public string Caller { get; init; } = "unknown";
    public string ToolName { get; init; } = string.Empty;
    public string? ArgsSummary { get; init; }
    public string? ArgsHash { get; init; }
    public int DurationMs { get; init; }
    public string ResultCode { get; init; } = "ok";
    public string? ErrorMessage { get; init; }
    public string DangerLevel { get; init; } = "read";
    public string? IntentId { get; init; }
}

public sealed record McpToolCallStats
{
    public int Total { get; init; }
    public int Reads { get; init; }
    public int Mutates { get; init; }
    public int Destructives { get; init; }
    public int Errors { get; init; }
    public string? LastCalledAt { get; init; }
    public int DistinctSessions { get; init; }
    public int P50DurationMs { get; init; }
    public int P95DurationMs { get; init; }
    public int P99DurationMs { get; init; }
    public double CallsPerMinute { get; init; }
    public double ErrorRatePercent { get; init; }
}

public sealed record McpToolCallTimelineBucket
{
    public string Hour { get; init; } = string.Empty;
    public int Reads { get; init; }
    public int Mutates { get; init; }
    public int Destructives { get; init; }
    public int Errors { get; init; }
    public int Total { get; init; }
}

public sealed record McpToolCallByToolRow
{
    public string ToolName { get; init; } = string.Empty;
    public int Count { get; init; }
    public int AvgDurationMs { get; init; }
    public string DangerLevel { get; init; } = "read";
    public int Errors { get; init; }
}
