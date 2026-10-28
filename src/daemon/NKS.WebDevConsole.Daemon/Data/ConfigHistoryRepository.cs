using Dapper;
using Microsoft.Extensions.Logging;
using NKS.WebDevConsole.Daemon.Mcp;

namespace NKS.WebDevConsole.Daemon.Data;

/// <summary>
/// Writes the rows the Overview "recent activity" feed lists (<c>GET /api/activity</c>
/// reads <c>config_history</c>). The settings, services and php_versions tables
/// are audited by database triggers; sites, backups and databases are not tables,
/// so SiteManager, BackupManager and the database endpoints record them here.
/// Recording is best effort: a failed write is logged and never fails the
/// operation it describes.
/// </summary>
public sealed class ConfigHistoryRepository
{
    private readonly Database _db;
    private readonly ILogger<ConfigHistoryRepository>? _logger;

    public ConfigHistoryRepository(Database db, ILogger<ConfigHistoryRepository>? logger = null)
    {
        _db = db;
        _logger = logger;
    }

    /// <param name="entityType">One of the entity_type values allowed by config_history ('site', 'backup', 'database', ...).</param>
    /// <param name="entityName">What the feed shows: the domain, the archive name, the database name.</param>
    /// <param name="operation">INSERT, UPDATE, DELETE or RESTORE.</param>
    /// <param name="changedFields">Names of the fields an UPDATE changed (a JSON array is stored).</param>
    /// <param name="oldValuesJson">JSON object with the previous values of the changed fields; must hold no secrets.</param>
    /// <param name="newValuesJson">JSON object with the new values of the changed fields; must hold no secrets.</param>
    public void Record(
        string entityType,
        string? entityName,
        string operation,
        IReadOnlyList<string>? changedFields = null,
        string? oldValuesJson = null,
        string? newValuesJson = null)
    {
        try
        {
            using var conn = _db.CreateConnection();
            conn.Execute(
                "INSERT INTO config_history " +
                "(entity_type, entity_id, entity_name, operation, old_values, new_values, changed_fields, source) " +
                "VALUES (@EntityType, 0, @EntityName, @Operation, @OldValues, @NewValues, @ChangedFields, @Source)",
                new
                {
                    EntityType = entityType,
                    EntityName = entityName,
                    Operation = operation,
                    OldValues = oldValuesJson,
                    NewValues = newValuesJson,
                    ChangedFields = changedFields is { Count: > 0 } ? System.Text.Json.JsonSerializer.Serialize(changedFields) : null,
                    Source = ActivityContext.Source,
                });
        }
        catch (Exception ex)
        {
            _logger?.LogWarning(ex, "Could not record {Operation} {EntityType} {EntityName} in config_history", operation, entityType, entityName);
        }
    }
}

/// <summary>
/// Who caused the change being recorded, read from the in-flight HTTP request:
/// the MCP token is decided by the auth middleware and cannot be faked, the
/// <c>wdc</c> CLI announces itself with <c>X-Wdc-Client: cli</c> (a label for
/// the feed, not a credential), anything else is the app. Work with no request
/// (the backup scheduler) counts as the app.
/// </summary>
public static class ActivityContext
{
    public const string ClientHeader = "X-Wdc-Client";

    private static readonly AsyncLocal<bool> _viaCli = new();

    public static bool ViaCli { get => _viaCli.Value; set => _viaCli.Value = value; }

    /// <summary>Value for config_history.source: 'mcp', 'cli' or 'app'.</summary>
    public static string Source => McpCallerContext.IsMcpToken ? "mcp" : ViaCli ? "cli" : "app";
}
