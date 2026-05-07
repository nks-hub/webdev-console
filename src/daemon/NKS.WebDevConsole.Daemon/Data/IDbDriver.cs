using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;

namespace NKS.WebDevConsole.Daemon.Data;

/// <summary>
/// Engine-agnostic database explorer driver. The Databases page in the
/// frontend talks to a fixed REST surface; concrete engine drivers
/// (MySQL/MariaDB, future PostgreSQL) translate that surface into
/// engine-specific SQL. Connection lifetime is per-call: drivers open,
/// run, close, and surface engine errors verbatim.
/// </summary>
public interface IDbDriver
{
    /// <summary>Engine identifier surfaced to the frontend (e.g. "mysql", "mariadb", "postgresql").</summary>
    string Engine { get; }

    /// <summary>List all user-visible databases (system schemas filtered).</summary>
    Task<IReadOnlyList<DatabaseInfo>> ListDatabasesAsync(CancellationToken ct);

    /// <summary>List tables and views inside a database with row count + size.</summary>
    Task<IReadOnlyList<TableInfo>> ListTablesAsync(string database, CancellationToken ct);

    /// <summary>Describe columns of a single table (name/type/nullability/default/extra).</summary>
    Task<IReadOnlyList<ColumnInfo>> ListColumnsAsync(string database, string table, CancellationToken ct);

    /// <summary>List indexes of a single table with column composition + uniqueness.</summary>
    Task<IReadOnlyList<IndexInfo>> ListIndexesAsync(string database, string table, CancellationToken ct);

    /// <summary>
    /// Page through rows of a table. Driver is responsible for safe identifier
    /// quoting of <paramref name="orderBy"/> and validating it appears in the
    /// table's column set before splicing into ORDER BY.
    /// </summary>
    Task<TableDataResult> BrowseTableAsync(string database, string table, BrowseOptions options, CancellationToken ct);

    /// <summary>
    /// Execute one or more statements and return structured results. Each
    /// statement that returns a result set produces a <see cref="QueryResultSet"/>;
    /// statements that don't (INSERT/UPDATE/DDL) produce a result set with
    /// <see cref="QueryResultSet.RowsAffected"/> set and no rows.
    /// </summary>
    Task<QueryExecutionResult> ExecuteQueryAsync(string database, string sql, CancellationToken ct);
}

public sealed record DatabaseInfo(string Name, long? SizeBytes, string? Charset, string? Collation);

public sealed record TableInfo(
    string Name,
    string Kind, // "table" | "view"
    long? RowsApprox,
    long? DataBytes,
    long? IndexBytes,
    string? Engine,
    string? Collation,
    string? Comment);

public sealed record ColumnInfo(
    string Name,
    string Type,           // raw engine type, e.g. "varchar(64)"
    bool Nullable,
    string? Default,
    bool IsPrimaryKey,
    bool IsAutoIncrement,
    string? Comment,
    int OrdinalPosition);

public sealed record IndexInfo(
    string Name,
    bool Unique,
    bool Primary,
    string Type,           // "BTREE" / "HASH" / etc.
    IReadOnlyList<string> Columns);

public sealed class BrowseOptions
{
    /// <summary>1-based.</summary>
    public int Page { get; init; } = 1;

    /// <summary>Capped to a hard ceiling per driver; 50 by default.</summary>
    public int PageSize { get; init; } = 50;

    /// <summary>Column name, validated against table schema before splicing.</summary>
    public string? OrderBy { get; init; }

    /// <summary>"asc" | "desc"; falls back to "asc" on anything else.</summary>
    public string OrderDir { get; init; } = "asc";

    /// <summary>
    /// Raw user filter expression (e.g. "id &gt; 10 AND name LIKE 'foo%'").
    /// Used as <c>WHERE</c>, parameter binding is the operator's responsibility
    /// — they have direct DB access anyway. Drivers reject obvious abuse
    /// (semicolons / nested SELECT) only as a soft guard, not a security one.
    /// </summary>
    public string? WhereClause { get; init; }
}

public sealed class TableDataResult
{
    public required IReadOnlyList<DataColumn> Columns { get; init; }
    public required IReadOnlyList<IReadOnlyList<object?>> Rows { get; init; }
    public required long TotalRows { get; init; }
    public required int Page { get; init; }
    public required int PageSize { get; init; }
    public long ExecutionTimeMs { get; init; }
    public string? AppliedOrderBy { get; init; }
    public string? AppliedOrderDir { get; init; }
}

public sealed record DataColumn(string Name, string Type, bool Nullable, bool IsPrimaryKey);

public sealed class QueryExecutionResult
{
    public required IReadOnlyList<QueryResultSet> Results { get; init; }
    public long ExecutionTimeMs { get; init; }
    public IReadOnlyList<QueryWarning>? Warnings { get; init; }
}

public sealed class QueryResultSet
{
    /// <summary>Statement text (truncated) for the result-set header in the UI.</summary>
    public required string StatementText { get; init; }
    public required IReadOnlyList<DataColumn> Columns { get; init; }
    public required IReadOnlyList<IReadOnlyList<object?>> Rows { get; init; }
    public required long RowsAffected { get; init; }
    public long ExecutionTimeMs { get; init; }
}

public sealed record QueryWarning(string Level, int Code, string Message);
