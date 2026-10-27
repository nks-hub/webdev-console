using NKS.WebDevConsole.Daemon.Services;

namespace NKS.WebDevConsole.Daemon.Mcp;

/// <summary>
/// A daemon route as (METHOD, route pattern). When <see cref="IntentKind"/> is
/// set, MCP-token callers also need a human-approved intent of that kind:
/// checked by <see cref="McpTokenPolicy"/> unless <see cref="IntentByEndpoint"/>
/// says the endpoint runs that gate itself. <see cref="IntentTarget"/> is the
/// intent's domain, a template: <c>{name}</c> is that route value,
/// <c>{body.name}</c> that string field of the JSON body, the rest literal.
/// <see cref="IntentHost"/> is the intent's host.
/// </summary>
public sealed record McpRoute(
    string Method,
    string Pattern,
    string? IntentKind = null,
    string? IntentTarget = null,
    bool IntentByEndpoint = false,
    string IntentHost = McpCapabilities.IntentHost);

/// <summary>
/// Request fields of a route that belong to a capability: JSON body fields,
/// or query-string parameters written as <c>?name</c>.
/// </summary>
public sealed record McpBodyFields(
    string Method,
    string Pattern,
    IReadOnlyList<string> Fields,
    string? IntentKind = null,
    string? IntentTarget = null);

/// <summary>
/// An operator-controlled capability of the MCP token. Off means the daemon
/// answers 403 <c>mcp_capability_disabled</c> for the routes / body fields it
/// covers. The switch is the setting <see cref="SettingKey"/>, which lives
/// under <c>mcp.</c> so only the WDC app (GUI token) can change it.
/// </summary>
public sealed record McpCapability(
    string Id,
    string Title,
    string Description,
    bool DefaultEnabled,
    IReadOnlyList<McpRoute> Routes,
    IReadOnlyList<McpBodyFields> BodyFields,
    IReadOnlyList<string> Tools)
{
    public string SettingKey => McpCapabilities.SettingPrefix + Id;
}

/// <summary>
/// What the MCP token (AI agents behind the MCP server) may do, in three tiers:
/// <list type="bullet">
///   <item><see cref="AlwaysAllowed"/>: the routes the shipped MCP tools use for
///   everyday work. Destructive ones keep their intent gate.</item>
///   <item><see cref="All"/>: capability groups the operator switches on in
///   WDC → Settings → MCP (<c>mcp.capabilities.&lt;id&gt;</c>). The dangerous
///   ones default off; <see cref="UnlistedRoutes"/> covers every route that
///   is in no other tier.</item>
///   <item>Never: the trust root (approving intents, managing grants, any
///   other write under /api/mcp, changing <c>mcp.*</c> settings), whatever
///   the capabilities say.</item>
/// </list>
/// </summary>
public static class McpCapabilities
{
    public const string SettingPrefix = "mcp.capabilities.";
    public const string IntentHost = "*mcp*";

    public const string SettingsOperator = "settings_operator";
    public const string ServiceConfig = "service_config";
    public const string HostsFile = "hosts_file";
    public const string NodeProcess = "node_process";
    public const string SiteAdvancedFields = "site_advanced_fields";
    public const string CloudflareCredentials = "cloudflare_credentials";
    public const string PluginInstall = "plugin_install";
    public const string Composer = "composer";
    public const string DatabaseAdmin = "database_admin";
    public const string BackupRestore = "backup_restore";
    public const string DeployConfig = "deploy_config";
    public const string DaemonAdmin = "daemon_admin";
    public const string UnlistedRoutes = "unlisted_routes";

    /// <summary>
    /// Routes the MCP token may always call: what the shipped MCP tools
    /// (services/mcp-server/src/tools/*.ts, src/auditLog.ts) use, minus the
    /// routes that belong to a capability. services/mcp-server/daemon-routes.json
    /// lists every route the tools call; McpTokenPolicyTests checks that each is
    /// either here or in a capability.
    /// </summary>
    public static readonly IReadOnlyList<McpRoute> AlwaysAllowed =
    [
        // system.ts
        new("GET", "/api/status"),
        new("GET", "/api/system"),
        new("GET", "/api/activity"),
        // sites.ts (POST and PUT /api/sites: nodeStartCommand and the fields
        // beyond BasicSiteFields are capability-scoped). Rollback only restores
        // the site's own config history; the tool keeps its confirm.
        new("GET", "/api/sites"),
        new("GET", "/api/sites/{domain}"),
        new("GET", "/api/sites/{domain}/access-log"),
        new("GET", "/api/sites/{domain}/metrics"),
        new("GET", "/api/sites/{domain}/logs/errors"),
        new("GET", "/api/sites/{domain}/history"),
        new("POST", "/api/sites"),
        new("PUT", "/api/sites/{domain}"),
        new("DELETE", "/api/sites/{domain}"),
        new("POST", "/api/sites/{domain}/apply"),
        new("POST", "/api/sites/reapply-all"),
        new("POST", "/api/sites/{domain}/detect-framework"),
        new("POST", "/api/sites/{domain}/duplicate"),
        new("POST", "/api/sites/{domain}/rollback"),
        // composer.ts (install/require/remove: Composer capability)
        new("GET", "/api/sites/{domain}/composer/status"),
        new("GET", "/api/sites/{domain}/composer/outdated"),
        // node.ts (start/stop/restart: NodeProcess capability)
        new("GET", "/api/node/sites"),
        // services.ts (stop and restart keep their service_restart intent;
        // config writes: ServiceConfig capability)
        new("GET", "/api/services"),
        new("POST", "/api/services/{id}/start"),
        new("POST", "/api/services/{id}/stop"),
        new("POST", "/api/services/{id}/restart"),
        new("GET", "/api/services/{id}/logs"),
        new("GET", "/api/services/{id}/config"),
        new("POST", "/api/config/validate"),
        // hosts.ts (hosts file writes: HostsFile capability)
        new("GET", "/api/hosts"),
        new("POST", "/api/dns/flush"),
        // settings.ts (protected keys are capability-scoped, mcp.* never)
        new("GET", "/api/settings"),
        new("PUT", "/api/settings"),
        // backup.ts (restore and delete: BackupRestore capability)
        new("GET", "/api/backup/list"),
        new("GET", "/api/backup/stats"),
        new("POST", "/api/backup"),
        // binaries.ts
        new("GET", "/api/binaries/catalog"),
        new("GET", "/api/binaries/installed"),
        new("POST", "/api/binaries/catalog/refresh"),
        new("POST", "/api/binaries/install"),
        new("DELETE", "/api/binaries/{app}/{version}"),
        // cloudflare.ts (credential fields of PUT config are capability-scoped)
        new("GET", "/api/cloudflare/config"),
        new("PUT", "/api/cloudflare/config"),
        new("GET", "/api/cloudflare/verify"),
        new("GET", "/api/cloudflare/zones"),
        new("GET", "/api/cloudflare/zones/{zoneId}/dns"),
        new("POST", "/api/cloudflare/zones/{zoneId}/dns"),
        new("DELETE", "/api/cloudflare/zones/{zoneId}/dns/{recordId}"),
        new("GET", "/api/cloudflare/tunnels"),
        new("GET", "/api/cloudflare/suggest-subdomain"),
        // confirmation.ts, deploy.ts, auditLog.ts; capability list for the agent.
        // GET /api/mcp/intents (the inventory, no tokens) is the status
        // fallback for daemons without GET /api/mcp/intents/{intentId}.
        new("POST", "/api/mcp/intents"),
        new("POST", "/api/mcp/intents/confirm-request"),
        new("GET", "/api/mcp/intents/{intentId}"),
        new("GET", "/api/mcp/intents"),
        new("POST", "/api/mcp/tool-calls"),
        new("GET", "/api/mcp/capabilities"),
        // databases.ts (import: DatabaseAdmin capability; export is allowed
        // unless it names an outputPath, which is DatabaseAdmin too)
        new("GET", "/api/databases"),
        new("POST", "/api/databases"),
        new("DELETE", "/api/databases/{name}"),
        new("GET", "/api/databases/{name}/tables"),
        new("POST", "/api/databases/{name}/query"),
        new("GET", "/api/databases/{name}/size"),
        new("GET", "/api/databases/v2/{name}/tables/{table}/columns"),
        new("GET", "/api/databases/v2/{name}/tables/{table}/indexes"),
        new("GET", "/api/databases/v2/{name}/tables/{table}/data"),
        new("GET", "/api/databases/{name}/export"),
        // mysqlUsers.ts
        new("GET", "/api/plugins/mysql/users"),
        new("POST", "/api/plugins/mysql/users"),
        new("POST", "/api/plugins/mysql/users/password"),
        new("POST", "/api/plugins/mysql/users/grants"),
        new("POST", "/api/plugins/mysql/change-password"),
        // php.ts
        new("GET", "/api/php/versions"),
        new("GET", "/api/php/{version}/extensions"),
        new("POST", "/api/php/{version}/extensions/{name}"),
        // plugins.ts
        new("GET", "/api/plugins"),
        new("GET", "/api/plugins/marketplace"),
        new("POST", "/api/plugins/{id}/enable"),
        new("POST", "/api/plugins/{id}/disable"),
        // ssl.ts
        new("GET", "/api/ssl/certs"),
        new("POST", "/api/ssl/install-ca"),
        new("POST", "/api/ssl/generate"),
        new("DELETE", "/api/ssl/certs/{domain}"),
        // deploy.ts (the deploy, rollback, cancel and restore routes keep their intent gate)
        new("GET", "/api/nks.wdc.deploy/sites/{domain}/history"),
        new("GET", "/api/nks.wdc.deploy/sites/{domain}/deploys/{deployId}"),
        new("DELETE", "/api/nks.wdc.deploy/sites/{domain}/deploys/{deployId}"),
        new("POST", "/api/nks.wdc.deploy/sites/{domain}/deploy"),
        new("POST", "/api/nks.wdc.deploy/sites/{domain}/deploys/{deployId}/rollback"),
        new("GET", "/api/nks.wdc.deploy/sites/{domain}/groups"),
        new("POST", "/api/nks.wdc.deploy/sites/{domain}/groups"),
        new("POST", "/api/nks.wdc.deploy/sites/{domain}/groups/{groupId}/rollback"),
        new("GET", "/api/nks.wdc.deploy/sites/{domain}/snapshots"),
        new("POST", "/api/nks.wdc.deploy/sites/{domain}/snapshots/{snapshotId}/restore"),
    ];

    /// <summary>Protected setting prefixes/keys other than <c>mcp.*</c>; writing them needs <see cref="SettingsOperator"/>.</summary>
    public static readonly string[] OperatorSettingPrefixes =
        ["deploy.", "daemon.", "plugins.", "sync.", "paths.", "telemetry."];
    public static readonly string[] OperatorSettingKeys = ["backup.dir"];

    /// <summary>Site fields of the wdc_create_site schema (services/mcp-server/src/tools/sites.ts).</summary>
    public static readonly string[] BasicSiteFields =
        ["domain", "documentRoot", "phpVersion", "sslEnabled", "aliases", "framework"];

    /// <summary>Query parameter of GET /api/databases/{name}/export naming the file the MCP server writes.</summary>
    public const string ExportOutputPathParameter = "outputPath";

    public static readonly string[] CloudflareCredentialFields =
        ["cloudflaredPath", "tunnelToken", "apiToken", "accountId"];

    public static readonly IReadOnlyList<McpCapability> All =
    [
        new(SettingsOperator,
            "Operator settings",
            "Write settings under deploy.*, daemon.*, plugins.*, sync.*, paths.*, telemetry.* and backup.dir " +
            "(catalog URL, data paths, deploy and sync switches). mcp.* settings are never writable with the MCP token.",
            DefaultEnabled: false,
            Routes: [],
            BodyFields: [new("PUT", "/api/settings", [.. OperatorSettingPrefixes.Select(p => p + "*"), .. OperatorSettingKeys])],
            Tools: ["wdc_update_settings"]),
        new(ServiceConfig,
            "Service configuration files",
            "Rewrite service config files such as httpd.conf and php.ini. Each write needs an approved intent " +
            "(checked by the endpoint).",
            DefaultEnabled: false,
            // ServiceConfigEndpoints runs McpIntentGate (kind service_config_write, host *svc*).
            Routes: [new("POST", "/api/services/{id}/config", "service_config_write", "{id}", IntentByEndpoint: true, IntentHost: "*svc*")],
            BodyFields: [],
            Tools: ["wdc_update_service_config"]),
        new(HostsFile,
            "Hosts file",
            "Rewrite or restore the system hosts file. Each write needs an approved intent.",
            DefaultEnabled: false,
            Routes:
            [
                new("POST", "/api/hosts/apply", "hosts_write", "hosts"),
                new("POST", "/api/hosts/restore", "hosts_write", "hosts"),
                new("POST", "/api/hosts/backup"),
            ],
            BodyFields: [],
            Tools: []),
        new(NodeProcess,
            "Node.js start commands",
            "Set a site's nodeStartCommand (a shell command the daemon runs) and start or restart Node.js site processes. " +
            "Each needs an approved intent for the site.",
            DefaultEnabled: false,
            Routes:
            [
                new("POST", "/api/node/sites/{domain}/start", "node_process", "{domain}"),
                new("POST", "/api/node/sites/{domain}/restart", "node_process", "{domain}"),
                new("POST", "/api/node/sites/{domain}/stop"),
            ],
            BodyFields:
            [
                new("POST", "/api/sites", ["nodeStartCommand"], "node_process", "{body.domain}"),
                new("PUT", "/api/sites/{domain}", ["nodeStartCommand"], "node_process", "{domain}"),
            ],
            Tools: ["wdc_create_site", "wdc_start_node_site", "wdc_stop_node_site", "wdc_restart_node_site"]),
        new(SiteAdvancedFields,
            "Advanced site fields",
            "Site fields beyond domain, documentRoot, phpVersion, sslEnabled, aliases and framework: environment variables, " +
            "bind addresses, ports, Node upstream, PHP/Apache overrides, Cloudflare tunnel exposure.",
            DefaultEnabled: false,
            Routes: [],
            BodyFields:
            [
                new("POST", "/api/sites", ["* except " + string.Join(", ", BasicSiteFields)]),
                new("PUT", "/api/sites/{domain}", ["* except " + string.Join(", ", BasicSiteFields)]),
            ],
            Tools: ["wdc_create_site", "wdc_update_site"]),
        new(CloudflareCredentials,
            "Cloudflare credentials and tunnel routing",
            "Set the Cloudflare API token, account id, tunnel token and cloudflared path, rewrite tunnel ingress and run " +
            "auto-setup. Each needs an approved intent.",
            DefaultEnabled: false,
            Routes:
            [
                new("PUT", "/api/cloudflare/tunnels/{tunnelId}/configuration", "cloudflare_credentials", "{tunnelId}"),
                new("POST", "/api/cloudflare/auto-setup", "cloudflare_credentials", "auto-setup"),
            ],
            BodyFields: [new("PUT", "/api/cloudflare/config", CloudflareCredentialFields, "cloudflare_credentials", "config")],
            Tools: ["wdc_save_cloudflare_config"]),
        new(PluginInstall,
            "Plugin install and uninstall",
            "Install a plugin DLL from a URL or uninstall one. Each needs an approved intent (checked by the endpoint).",
            DefaultEnabled: false,
            Routes:
            [
                new("POST", "/api/plugins/install", "plugin_install", IntentByEndpoint: true),
                new("DELETE", "/api/plugins/{id}", "plugin_uninstall", IntentByEndpoint: true),
            ],
            BodyFields: [],
            Tools: ["wdc_install_plugin_from_marketplace", "wdc_uninstall_plugin"]),
        new(Composer,
            "Composer commands",
            "Run Composer install/require/remove in site folders (package scripts run).",
            DefaultEnabled: false,
            Routes:
            [
                new("POST", "/api/sites/{domain}/composer/install"),
                new("POST", "/api/sites/{domain}/composer/require"),
                new("POST", "/api/sites/{domain}/composer/remove"),
            ],
            BodyFields: [],
            Tools: ["wdc_composer_install", "wdc_composer_require", "wdc_composer_remove"]),
        new(DatabaseAdmin,
            "Database server administration",
            "Reset the MySQL or PostgreSQL root password, drop MySQL accounts, read or set the stored root password, " +
            "import SQL dumps, export a database to a file path. Resets, drops and imports need an approved intent " +
            "(checked by the endpoint).",
            DefaultEnabled: false,
            Routes:
            [
                // The endpoints run these intent gates (DatabaseEndpoints.IntentGateAsync).
                new("POST", "/api/plugins/mysql/reset-password", "mysql_root_reset", "root", IntentByEndpoint: true, IntentHost: "*mysql*"),
                new("POST", "/api/plugins/mysql/users/drop", "mysql_user_drop", "{body.userName}@{body.host}", IntentByEndpoint: true, IntentHost: "*mysql*"),
                new("POST", "/api/plugins/postgresql/reset-password", "postgres_root_reset", "postgres", IntentByEndpoint: true, IntentHost: "*postgres*"),
                new("POST", "/api/databases/{name}/import", "database_import", "{name}", IntentByEndpoint: true, IntentHost: "*db*"),
                new("GET", "/api/databases/root-password"),
                new("POST", "/api/databases/root-password"),
            ],
            // An export is always allowed; one the MCP server writes to a file
            // path names it (wdc_export_database outputPath) and needs this group.
            BodyFields: [new("GET", "/api/databases/{name}/export", ["?" + ExportOutputPathParameter])],
            Tools: ["wdc_reset_mysql_root_password", "wdc_drop_mysql_user", "wdc_import_database", "wdc_export_database"]),
        new(BackupRestore,
            "Backup restore and delete",
            "Restore a WDC backup over the current state, or delete a backup. Each needs an approved intent.",
            DefaultEnabled: false,
            Routes:
            [
                new("POST", "/api/restore", "backup_restore", IntentByEndpoint: true),
                new("POST", "/api/backup/{id}/restore", "backup_restore", IntentByEndpoint: true),
                new("DELETE", "/api/backup/{id}", "backup_delete", "{id}", IntentHost: "*backup*"),
            ],
            BodyFields: [],
            Tools: ["wdc_restore_backup", "wdc_restore_backup_by_id", "wdc_delete_backup"]),
        new(DeployConfig,
            "Deploy configuration",
            "Read and change per-site deploy settings (hosts, hooks run on deploy, notification webhooks, env vars), " +
            "test-run hooks and notifications, probe hosts.",
            DefaultEnabled: false,
            Routes:
            [
                // The settings file holds notification webhooks and env vars besides the hosts.
                new("GET", "/api/nks.wdc.deploy/sites/{domain}/settings"),
                new("PUT", "/api/nks.wdc.deploy/sites/{domain}/settings", "settings_write", IntentByEndpoint: true),
                new("POST", "/api/nks.wdc.deploy/sites/{domain}/hooks/test", "test_hook", IntentByEndpoint: true),
                new("POST", "/api/nks.wdc.deploy/sites/{domain}/notifications/test"),
                new("POST", "/api/nks.wdc.deploy/test-host-connection"),
            ],
            BodyFields: [],
            Tools: ["wdc_deploy_list_targets"]),
        new(DaemonAdmin,
            "Daemon administration",
            "Restart, shut down, reset or uninstall the daemon. Each needs an approved intent.",
            DefaultEnabled: false,
            Routes:
            [
                new("POST", "/api/admin/restart", "daemon_admin", "restart"),
                new("POST", "/api/admin/shutdown", "daemon_admin", "shutdown"),
                new("POST", "/api/admin/reset", "daemon_admin", "reset"),
                new("POST", "/api/uninstall", "daemon_admin", "uninstall"),
                new("GET", "/api/admin/plugin-readiness"),
            ],
            BodyFields: [],
            Tools: []),
        new(UnlistedRoutes,
            "Other routes",
            "Every other daemon route that is not always allowed and not in a group above (Docker Compose " +
            "lifecycle, catalog sync, read-only MCP audit routes, ...).",
            DefaultEnabled: false,
            Routes: [],
            BodyFields: [],
            Tools: []),
    ];

    public static McpCapability Get(string id) => All.First(c => c.Id == id);

    /// <summary>
    /// Intent kinds that belong to a capability group. A grant with the
    /// wildcard kind pattern never auto-approves these; the operator has to
    /// name the kind in the grant.
    /// </summary>
    public static readonly IReadOnlySet<string> CapabilityIntentKinds = All
        .SelectMany(c => c.Routes.Select(r => r.IntentKind).Concat(c.BodyFields.Select(f => f.IntentKind)))
        .OfType<string>()
        .ToHashSet(StringComparer.OrdinalIgnoreCase);

    /// <summary>Reads a capability switch from settings (<c>mcp.capabilities.&lt;id&gt;</c>).</summary>
    public static bool IsEnabled(SettingsStore settings, string id) =>
        settings.GetBool("mcp", "capabilities." + id, Get(id).DefaultEnabled);
}
