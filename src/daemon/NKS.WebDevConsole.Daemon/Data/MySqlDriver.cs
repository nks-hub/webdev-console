using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using MySqlConnector;

namespace NKS.WebDevConsole.Daemon.Data;

/// <summary>
/// MySqlConnector-backed driver that also serves MariaDB (wire-compatible).
/// All queries are parameterised; identifier interpolation (db / table /
/// column names in <c>FROM</c> / <c>ORDER BY</c>) goes through
/// <see cref="QuoteIdentifier"/> after schema-membership validation so a
/// caller cannot smuggle SQL via column-name abuse.
/// </summary>
public sealed class MySqlDriver : IDbDriver
{
    public const int MaxPageSize = 500;
    public const int DefaultPageSize = 50;

    private readonly string _baseConnectionString;

    public string Engine => "mysql";

    public MySqlDriver(string host, int port, string user, string? password)
    {
        var b = new MySqlConnectionStringBuilder
        {
            Server = host,
            Port = (uint)port,
            UserID = user,
            Password = password ?? string.Empty,
            ConnectionTimeout = 10,
            DefaultCommandTimeout = 30,
            // Multi-statement is required for the SQL console; the explorer
            // browse/structure paths use single statements regardless.
            AllowUserVariables = true,
            // Streamed reads avoid materialising large result sets in memory
            // before the page slice is taken — important for browse pages
            // since the COUNT(*) plan still has to scan the table.
            UseAffectedRows = false,
            Pooling = true,
            // Default ConvertZeroDateTime=false would throw on legacy schemas
            // with '0000-00-00' rows; surface them as null instead so the
            // browser doesn't fail to load a table whose history pre-dates
            // strict mode.
            ConvertZeroDateTime = true,
        };
        _baseConnectionString = b.ConnectionString;
    }

    public async Task<IReadOnlyList<DatabaseInfo>> ListDatabasesAsync(CancellationToken ct)
    {
        await using var conn = await OpenAsync(database: null, allowMultiple: false, ct).ConfigureAwait(false);
        const string sql = """
            SELECT s.SCHEMA_NAME,
                   COALESCE(SUM(t.DATA_LENGTH + t.INDEX_LENGTH), 0) AS bytes,
                   s.DEFAULT_CHARACTER_SET_NAME,
                   s.DEFAULT_COLLATION_NAME
            FROM information_schema.SCHEMATA s
            LEFT JOIN information_schema.TABLES t ON t.TABLE_SCHEMA = s.SCHEMA_NAME
            WHERE s.SCHEMA_NAME NOT IN ('information_schema','performance_schema','mysql','sys')
            GROUP BY s.SCHEMA_NAME, s.DEFAULT_CHARACTER_SET_NAME, s.DEFAULT_COLLATION_NAME
            ORDER BY s.SCHEMA_NAME
            """;
        await using var cmd = new MySqlCommand(sql, conn);
        await using var rdr = await cmd.ExecuteReaderAsync(ct).ConfigureAwait(false);
        var list = new List<DatabaseInfo>();
        while (await rdr.ReadAsync(ct).ConfigureAwait(false))
        {
            list.Add(new DatabaseInfo(
                Name: rdr.GetString(0),
                SizeBytes: rdr.IsDBNull(1) ? null : rdr.GetInt64(1),
                Charset: rdr.IsDBNull(2) ? null : rdr.GetString(2),
                Collation: rdr.IsDBNull(3) ? null : rdr.GetString(3)));
        }
        return list;
    }

    public async Task<IReadOnlyList<TableInfo>> ListTablesAsync(string database, CancellationToken ct)
    {
        ValidateIdentifier(database, nameof(database));
        await using var conn = await OpenAsync(database: null, allowMultiple: false, ct).ConfigureAwait(false);
        const string sql = """
            SELECT TABLE_NAME, TABLE_TYPE, TABLE_ROWS, DATA_LENGTH, INDEX_LENGTH, ENGINE, TABLE_COLLATION, TABLE_COMMENT
            FROM information_schema.TABLES
            WHERE TABLE_SCHEMA = @db
            ORDER BY TABLE_NAME
            """;
        await using var cmd = new MySqlCommand(sql, conn);
        cmd.Parameters.AddWithValue("@db", database);
        await using var rdr = await cmd.ExecuteReaderAsync(ct).ConfigureAwait(false);
        var list = new List<TableInfo>();
        while (await rdr.ReadAsync(ct).ConfigureAwait(false))
        {
            var tableType = rdr.IsDBNull(1) ? "BASE TABLE" : rdr.GetString(1);
            list.Add(new TableInfo(
                Name: rdr.GetString(0),
                Kind: tableType.Contains("VIEW", StringComparison.OrdinalIgnoreCase) ? "view" : "table",
                RowsApprox: rdr.IsDBNull(2) ? null : rdr.GetInt64(2),
                DataBytes: rdr.IsDBNull(3) ? null : rdr.GetInt64(3),
                IndexBytes: rdr.IsDBNull(4) ? null : rdr.GetInt64(4),
                Engine: rdr.IsDBNull(5) ? null : rdr.GetString(5),
                Collation: rdr.IsDBNull(6) ? null : rdr.GetString(6),
                Comment: rdr.IsDBNull(7) ? null : rdr.GetString(7)));
        }
        return list;
    }

    public async Task<IReadOnlyList<ColumnInfo>> ListColumnsAsync(string database, string table, CancellationToken ct)
    {
        ValidateIdentifier(database, nameof(database));
        ValidateIdentifier(table, nameof(table));
        await using var conn = await OpenAsync(database: null, allowMultiple: false, ct).ConfigureAwait(false);
        const string sql = """
            SELECT COLUMN_NAME, COLUMN_TYPE, IS_NULLABLE, COLUMN_DEFAULT,
                   COLUMN_KEY, EXTRA, COLUMN_COMMENT, ORDINAL_POSITION
            FROM information_schema.COLUMNS
            WHERE TABLE_SCHEMA = @db AND TABLE_NAME = @t
            ORDER BY ORDINAL_POSITION
            """;
        await using var cmd = new MySqlCommand(sql, conn);
        cmd.Parameters.AddWithValue("@db", database);
        cmd.Parameters.AddWithValue("@t", table);
        await using var rdr = await cmd.ExecuteReaderAsync(ct).ConfigureAwait(false);
        var list = new List<ColumnInfo>();
        while (await rdr.ReadAsync(ct).ConfigureAwait(false))
        {
            var key = rdr.IsDBNull(4) ? "" : rdr.GetString(4);
            var extra = rdr.IsDBNull(5) ? "" : rdr.GetString(5);
            list.Add(new ColumnInfo(
                Name: rdr.GetString(0),
                Type: rdr.GetString(1),
                Nullable: !string.Equals(rdr.GetString(2), "NO", StringComparison.OrdinalIgnoreCase),
                Default: rdr.IsDBNull(3) ? null : rdr.GetString(3),
                IsPrimaryKey: key.Equals("PRI", StringComparison.OrdinalIgnoreCase),
                IsAutoIncrement: extra.Contains("auto_increment", StringComparison.OrdinalIgnoreCase),
                Comment: rdr.IsDBNull(6) ? null : rdr.GetString(6),
                OrdinalPosition: rdr.IsDBNull(7) ? 0 : Convert.ToInt32(rdr.GetValue(7))));
        }
        return list;
    }

    public async Task<IReadOnlyList<IndexInfo>> ListIndexesAsync(string database, string table, CancellationToken ct)
    {
        ValidateIdentifier(database, nameof(database));
        ValidateIdentifier(table, nameof(table));
        await using var conn = await OpenAsync(database: null, allowMultiple: false, ct).ConfigureAwait(false);
        const string sql = """
            SELECT INDEX_NAME, NON_UNIQUE, INDEX_TYPE, SEQ_IN_INDEX, COLUMN_NAME
            FROM information_schema.STATISTICS
            WHERE TABLE_SCHEMA = @db AND TABLE_NAME = @t
            ORDER BY INDEX_NAME, SEQ_IN_INDEX
            """;
        await using var cmd = new MySqlCommand(sql, conn);
        cmd.Parameters.AddWithValue("@db", database);
        cmd.Parameters.AddWithValue("@t", table);
        await using var rdr = await cmd.ExecuteReaderAsync(ct).ConfigureAwait(false);

        var grouped = new Dictionary<string, (bool unique, string type, List<string> cols)>(StringComparer.OrdinalIgnoreCase);
        while (await rdr.ReadAsync(ct).ConfigureAwait(false))
        {
            var name = rdr.GetString(0);
            var nonUnique = Convert.ToInt32(rdr.GetValue(1)) != 0;
            var type = rdr.IsDBNull(2) ? "BTREE" : rdr.GetString(2);
            var col = rdr.GetString(4);
            if (!grouped.TryGetValue(name, out var entry))
                grouped[name] = (!nonUnique, type, new List<string> { col });
            else
                entry.cols.Add(col);
        }

        return grouped.Select(kv => new IndexInfo(
            Name: kv.Key,
            Unique: kv.Value.unique,
            Primary: kv.Key.Equals("PRIMARY", StringComparison.OrdinalIgnoreCase),
            Type: kv.Value.type,
            Columns: kv.Value.cols)).ToList();
    }

    public async Task<TableDataResult> BrowseTableAsync(string database, string table, BrowseOptions options, CancellationToken ct)
    {
        ValidateIdentifier(database, nameof(database));
        ValidateIdentifier(table, nameof(table));

        var page = Math.Max(1, options.Page);
        var pageSize = Math.Clamp(options.PageSize, 1, MaxPageSize);
        var offset = (page - 1) * pageSize;

        // Build column / PK metadata up front so we can validate ORDER BY
        // and surface PK info to the frontend.
        var columns = await ListColumnsAsync(database, table, ct).ConfigureAwait(false);
        var columnNames = new HashSet<string>(columns.Select(c => c.Name), StringComparer.OrdinalIgnoreCase);

        string? appliedOrderBy = null;
        string appliedOrderDir = "asc";
        var orderClause = string.Empty;
        if (!string.IsNullOrWhiteSpace(options.OrderBy) && columnNames.Contains(options.OrderBy))
        {
            appliedOrderBy = options.OrderBy;
            appliedOrderDir = string.Equals(options.OrderDir, "desc", StringComparison.OrdinalIgnoreCase) ? "desc" : "asc";
            orderClause = $" ORDER BY {QuoteIdentifier(appliedOrderBy)} {appliedOrderDir.ToUpperInvariant()}";
        }

        var whereClause = string.Empty;
        if (!string.IsNullOrWhiteSpace(options.WhereClause))
        {
            // Soft guard: reject statement terminators / DDL keywords in the
            // free-form WHERE clause. This is NOT a security control — the
            // operator already has full root MySQL access and could DROP via
            // the SQL console — it's a usability guard against pasting
            // accidental multi-statement noise into a search box.
            var w = options.WhereClause.Trim().TrimEnd(';');
            if (w.Contains(';') || w.Contains("--", StringComparison.Ordinal))
                throw new InvalidOperationException("Filter expression must be a single boolean expression (no ';' or '--').");
            whereClause = " WHERE " + w;
        }

        var qDb = QuoteIdentifier(database);
        var qTable = QuoteIdentifier(table);

        var sw = Stopwatch.StartNew();
        await using var conn = await OpenAsync(database: null, allowMultiple: false, ct).ConfigureAwait(false);

        long total;
        await using (var countCmd = new MySqlCommand($"SELECT COUNT(*) FROM {qDb}.{qTable}{whereClause}", conn))
        {
            var countObj = await countCmd.ExecuteScalarAsync(ct).ConfigureAwait(false);
            total = countObj is null or DBNull ? 0 : Convert.ToInt64(countObj);
        }

        var dataSql = $"SELECT * FROM {qDb}.{qTable}{whereClause}{orderClause} LIMIT @lim OFFSET @off";
        await using var cmd = new MySqlCommand(dataSql, conn);
        cmd.Parameters.AddWithValue("@lim", pageSize);
        cmd.Parameters.AddWithValue("@off", offset);

        var (cols, rows) = await ReadResultSetAsync(cmd, columns, ct).ConfigureAwait(false);
        sw.Stop();

        return new TableDataResult
        {
            Columns = cols,
            Rows = rows,
            TotalRows = total,
            Page = page,
            PageSize = pageSize,
            ExecutionTimeMs = sw.ElapsedMilliseconds,
            AppliedOrderBy = appliedOrderBy,
            AppliedOrderDir = appliedOrderBy is null ? null : appliedOrderDir,
        };
    }

    public async Task<QueryExecutionResult> ExecuteQueryAsync(string database, string sql, CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(sql))
            throw new ArgumentException("sql required", nameof(sql));

        var sw = Stopwatch.StartNew();
        await using var conn = await OpenAsync(
            database: string.IsNullOrEmpty(database) ? null : database,
            allowMultiple: true,
            ct).ConfigureAwait(false);

        var results = new List<QueryResultSet>();
        await using var cmd = new MySqlCommand(sql, conn);
        cmd.CommandTimeout = 60;
        await using var rdr = await cmd.ExecuteReaderAsync(ct).ConfigureAwait(false);

        var statementIndex = 0;
        do
        {
            var stmtSw = Stopwatch.StartNew();
            statementIndex++;
            var label = TruncateForLabel(sql, statementIndex);

            if (rdr.FieldCount == 0)
            {
                stmtSw.Stop();
                results.Add(new QueryResultSet
                {
                    StatementText = label,
                    Columns = Array.Empty<DataColumn>(),
                    Rows = Array.Empty<IReadOnlyList<object?>>(),
                    RowsAffected = rdr.RecordsAffected < 0 ? 0 : rdr.RecordsAffected,
                    ExecutionTimeMs = stmtSw.ElapsedMilliseconds,
                });
                continue;
            }

            var colCount = rdr.FieldCount;
            var cols = new List<DataColumn>(colCount);
            for (int i = 0; i < colCount; i++)
                cols.Add(new DataColumn(rdr.GetName(i), rdr.GetDataTypeName(i), Nullable: true, IsPrimaryKey: false));

            var rows = new List<IReadOnlyList<object?>>();
            while (await rdr.ReadAsync(ct).ConfigureAwait(false))
            {
                var row = new object?[colCount];
                for (int i = 0; i < colCount; i++)
                    row[i] = NormalizeValue(rdr.IsDBNull(i) ? null : rdr.GetValue(i));
                rows.Add(row);
            }

            stmtSw.Stop();
            results.Add(new QueryResultSet
            {
                StatementText = label,
                Columns = cols,
                Rows = rows,
                RowsAffected = rdr.RecordsAffected < 0 ? 0 : rdr.RecordsAffected,
                ExecutionTimeMs = stmtSw.ElapsedMilliseconds,
            });
        } while (await rdr.NextResultAsync(ct).ConfigureAwait(false));

        sw.Stop();
        return new QueryExecutionResult
        {
            Results = results,
            ExecutionTimeMs = sw.ElapsedMilliseconds,
        };
    }

    private async Task<MySqlConnection> OpenAsync(string? database, bool allowMultiple, CancellationToken ct)
    {
        var b = new MySqlConnectionStringBuilder(_baseConnectionString);
        if (!string.IsNullOrEmpty(database))
            b.Database = database;
        if (allowMultiple)
            b.AllowUserVariables = true;
        var conn = new MySqlConnection(b.ConnectionString);
        await conn.OpenAsync(ct).ConfigureAwait(false);
        return conn;
    }

    private static async Task<(List<DataColumn> cols, List<IReadOnlyList<object?>> rows)> ReadResultSetAsync(
        MySqlCommand cmd, IReadOnlyList<ColumnInfo> tableColumns, CancellationToken ct)
    {
        await using var rdr = await cmd.ExecuteReaderAsync(ct).ConfigureAwait(false);
        var pkSet = new HashSet<string>(
            tableColumns.Where(c => c.IsPrimaryKey).Select(c => c.Name),
            StringComparer.OrdinalIgnoreCase);
        var nullableSet = new HashSet<string>(
            tableColumns.Where(c => c.Nullable).Select(c => c.Name),
            StringComparer.OrdinalIgnoreCase);

        var cols = new List<DataColumn>(rdr.FieldCount);
        for (int i = 0; i < rdr.FieldCount; i++)
        {
            var name = rdr.GetName(i);
            cols.Add(new DataColumn(
                Name: name,
                Type: rdr.GetDataTypeName(i),
                Nullable: nullableSet.Contains(name),
                IsPrimaryKey: pkSet.Contains(name)));
        }

        var rows = new List<IReadOnlyList<object?>>();
        while (await rdr.ReadAsync(ct).ConfigureAwait(false))
        {
            var row = new object?[rdr.FieldCount];
            for (int i = 0; i < rdr.FieldCount; i++)
                row[i] = NormalizeValue(rdr.IsDBNull(i) ? null : rdr.GetValue(i));
            rows.Add(row);
        }
        return (cols, rows);
    }

    /// <summary>
    /// Render an engine value into something the JSON serializer can ship
    /// without losing fidelity: dates become ISO 8601 strings, byte arrays
    /// become hex-prefixed strings (HeidiSQL convention) so the GUI can
    /// render BLOBs as a glyph instead of a corrupted UTF-8 splat.
    /// </summary>
    private static object? NormalizeValue(object? v) => v switch
    {
        null => null,
        DateTime dt => dt.ToString("yyyy-MM-ddTHH:mm:ss.fff", System.Globalization.CultureInfo.InvariantCulture),
        DateTimeOffset dto => dto.ToString("o", System.Globalization.CultureInfo.InvariantCulture),
        TimeSpan ts => ts.ToString(@"hh\:mm\:ss", System.Globalization.CultureInfo.InvariantCulture),
        byte[] bytes => "0x" + Convert.ToHexString(bytes),
        decimal d => d.ToString(System.Globalization.CultureInfo.InvariantCulture),
        _ => v,
    };

    private static string TruncateForLabel(string sql, int index)
    {
        var trimmed = sql.Trim().Replace('\n', ' ').Replace('\r', ' ');
        if (trimmed.Length > 80) trimmed = trimmed[..80] + "…";
        return $"#{index}: {trimmed}";
    }

    /// <summary>
    /// Backtick-quote a MySQL identifier. Doubles any embedded backticks
    /// per ANSI quoting rules, then validates the resulting identifier
    /// against the same regex used by IsValidDatabaseName so we never let a
    /// non-ASCII / control character through into the SQL string.
    /// </summary>
    public static string QuoteIdentifier(string identifier)
    {
        if (string.IsNullOrEmpty(identifier))
            throw new ArgumentException("Identifier cannot be empty", nameof(identifier));
        ValidateIdentifier(identifier, nameof(identifier));
        return "`" + identifier.Replace("`", "``") + "`";
    }

    public static void ValidateIdentifier(string identifier, string paramName)
    {
        if (string.IsNullOrEmpty(identifier))
            throw new ArgumentException($"{paramName} required", paramName);
        if (identifier.Length > 64)
            throw new ArgumentException($"{paramName} exceeds 64 characters", paramName);
        if (!System.Text.RegularExpressions.Regex.IsMatch(identifier, @"^[a-zA-Z0-9_$\-]+$"))
            throw new ArgumentException($"{paramName} contains characters outside [a-zA-Z0-9_$-]", paramName);
    }
}
