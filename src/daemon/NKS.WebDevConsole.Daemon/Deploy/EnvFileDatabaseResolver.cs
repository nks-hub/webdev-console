using System.Text.RegularExpressions;

namespace NKS.WebDevConsole.Daemon.Deploy;

/// <summary>
/// Phase 6.3 — resolves database connection details by scanning the
/// site's <c>.env</c> file. Universal across modern PHP frameworks
/// (Laravel, Symfony, Nette via dotenv) and Node.js (Next.js, Express).
///
/// Recognised keys (any of):
///   DB_CONNECTION / DATABASE_DRIVER → type
///   DB_HOST       / DATABASE_HOST    → host  (default 127.0.0.1)
///   DB_PORT       / DATABASE_PORT    → port  (default 3306 mysql / 5432 pg)
///   DB_DATABASE   / DATABASE_NAME / DB_NAME → database
///   DB_USERNAME   / DATABASE_USER / DB_USER → user
///   DB_PASSWORD   / DATABASE_PASSWORD / DB_PASS → password
///
/// Resolution is read-only and best-effort — missing fields fall back to
/// sensible defaults (root user / no password / localhost). The caller
/// (PreDeploySnapshotter) uses the result to invoke mysqldump / pg_dump;
/// auth failures surface as the dump tool's exit code, not here.
///
/// Security note: passwords pulled from .env are passed to mysqldump via
/// a temp <c>defaults-extra-file</c>, NEVER on the command line, so they
/// don't appear in <c>ps</c> / process listings.
/// </summary>
public static class EnvFileDatabaseResolver
{
    public sealed record DatabaseConnection(
        string Type,        // "mysql" | "mariadb" | "pgsql" | "sqlite" | "unknown"
        string Host,
        int Port,
        string Database,
        string User,
        string? Password,
        string DiscoveredAt); // path of the .env file we read from

    /// <summary>
    /// Try to resolve from <paramref name="documentRoot"/>'s parent directory.
    /// Most frameworks place .env at the project root which is the parent
    /// of the public/ docroot. Returns null if no recognisable config found.
    /// </summary>
    public static DatabaseConnection? TryResolve(string documentRoot)
    {
        var siteRoot = Directory.GetParent(documentRoot)?.FullName ?? documentRoot;
        // Try the common .env locations in a deterministic order so the
        // result is reproducible across daemon restarts.
        string[] candidates =
        {
            Path.Combine(siteRoot, ".env"),
            Path.Combine(siteRoot, ".env.local"),
            Path.Combine(documentRoot, ".env"),
        };
        foreach (var path in candidates)
        {
            if (!File.Exists(path)) continue;
            try
            {
                var conn = ParseEnvFile(path);
                if (conn is not null) return conn;
            }
            catch
            {
                // .env file unreadable — try the next candidate; never
                // fail the snapshot just because one config is malformed.
            }
        }
        return null;
    }

    private static DatabaseConnection? ParseEnvFile(string path)
    {
        var lines = File.ReadAllLines(path);
        var kv = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        foreach (var raw in lines)
        {
            var line = raw.TrimStart();
            if (line.Length == 0 || line[0] == '#') continue;
            var eq = line.IndexOf('=');
            if (eq <= 0) continue;
            var key = line[..eq].Trim();
            var value = line[(eq + 1)..].Trim();
            // Strip surrounding quotes if present (Laravel writes
            // DB_PASSWORD="..." when the value contains spaces).
            if (value.Length >= 2 &&
                (value[0] == '"' && value[^1] == '"' ||
                 value[0] == '\'' && value[^1] == '\''))
            {
                value = value[1..^1];
            }
            kv[key] = value;
        }

        var type = NormaliseType(
            FirstNonEmpty(kv, "DB_CONNECTION", "DATABASE_DRIVER", "DB_DRIVER"));
        if (type is null) return null; // No DB_* keys at all — not a DB-backed config

        var database = FirstNonEmpty(kv, "DB_DATABASE", "DATABASE_NAME", "DB_NAME");
        // Without a database name we can't dump — abort discovery so the
        // caller falls back to the scaffold stub instead of silently
        // dumping the wrong DB.
        if (string.IsNullOrWhiteSpace(database)) return null;

        var host = FirstNonEmpty(kv, "DB_HOST", "DATABASE_HOST") ?? "127.0.0.1";
        var portStr = FirstNonEmpty(kv, "DB_PORT", "DATABASE_PORT");
        var port = int.TryParse(portStr, out var p) && p > 0
            ? p
            : DefaultPortFor(type);
        var user = FirstNonEmpty(kv, "DB_USERNAME", "DATABASE_USER", "DB_USER") ?? "root";
        var password = FirstNonEmpty(kv, "DB_PASSWORD", "DATABASE_PASSWORD", "DB_PASS");

        return new DatabaseConnection(type, host, port, database, user, password, path);
    }

    private static string? FirstNonEmpty(IReadOnlyDictionary<string, string> kv, params string[] keys)
    {
        foreach (var k in keys)
        {
            if (kv.TryGetValue(k, out var v) && !string.IsNullOrWhiteSpace(v))
                return v;
        }
        return null;
    }

    private static string? NormaliseType(string? raw) => (raw?.ToLowerInvariant()) switch
    {
        null or "" => null,
        "mysql" => "mysql",
        "mariadb" => "mariadb",
        "pgsql" or "postgres" or "postgresql" => "pgsql",
        "sqlite" or "sqlite3" => "sqlite",
        _ => "unknown",
    };

    private static int DefaultPortFor(string type) => type switch
    {
        "mysql" or "mariadb" => 3306,
        "pgsql" => 5432,
        _ => 0,
    };
}
