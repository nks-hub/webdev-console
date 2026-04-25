namespace NKS.WebDevConsole.Core.Models;

/// <summary>
/// A single downloadable binary release.
/// </summary>
public sealed record BinaryRelease(
    string App,         // "apache", "php", "mysql", ...
    string Version,     // "2.4.66", "8.4.20", ...
    string MajorMinor,  // "2.4", "8.4", ...
    string Url,         // direct download URL (zip/tar.gz/msi)
    string Os,          // "windows", "linux", "macos"
    string Arch,        // "x64", "arm64"
    string ArchiveType, // "zip", "tar.gz", "tar.xz"
    string Source,      // "apachelounge", "php.net", "dev.mysql.com" — for attribution
    string? UserAgent = null,  // some sites (apachelounge) require browser UA
    // Lowercase hex SHA-256 of the archive bytes. Optional today (the
    // static catalog ships entries without hashes) but the BinaryDownloader
    // verifies whenever a value is present, so populating this field on
    // an existing entry retroactively hardens the download against MITM
    // / cache-poisoning of the upstream CDN. New entries SHOULD include it.
    string? Sha256 = null
);

/// <summary>
/// Static catalog of binary releases NKS WDC supports for download.
/// Maintained by us — not fetched at runtime from any third-party API.
/// URLs sourced from official upstream repositories (apachelounge, php.net,
/// dev.mysql.com, mariadb.org, github releases, nginx.org, etc.).
/// FUTURE: replace with sync from our own NKS catalog API
/// (catalog.nks.cz/wdc/binaries) so we can ship updates without re-releasing the daemon.
/// </summary>
public static class BinaryCatalog
{
    private const string BrowserUA =
        "Mozilla/5.0 (Windows NT 10.0; Win64; x64) AppleWebKit/537.36 (KHTML, like Gecko) Chrome/126.0.0.0 Safari/537.36";

    /// <summary>All known releases. Filter by app/os/arch.</summary>
    public static readonly IReadOnlyList<BinaryRelease> All = new List<BinaryRelease>
    {
        // ── Apache HTTP Server (Windows: Apache Lounge VS18 builds) ─────────────
        new("apache", "2.4.66", "2.4",
            "https://www.apachelounge.com/download/VS18/binaries/httpd-2.4.66-260223-Win64-VS18.zip",
            "windows", "x64", "zip", "apachelounge", BrowserUA),

        // ── PHP (Windows: official php.net builds) ──────────────────────────────
        new("php", "8.5.5",  "8.5", "https://downloads.php.net/~windows/releases/archives/php-8.5.5-Win32-vs17-x64.zip",  "windows", "x64", "zip", "php.net"),
        new("php", "8.4.20", "8.4", "https://downloads.php.net/~windows/releases/archives/php-8.4.20-Win32-vs17-x64.zip", "windows", "x64", "zip", "php.net"),
        new("php", "8.3.30", "8.3", "https://downloads.php.net/~windows/releases/archives/php-8.3.30-Win32-vs16-x64.zip", "windows", "x64", "zip", "php.net"),
        new("php", "8.2.30", "8.2", "https://downloads.php.net/~windows/releases/archives/php-8.2.30-Win32-vs16-x64.zip", "windows", "x64", "zip", "php.net"),
        new("php", "8.1.34", "8.1", "https://downloads.php.net/~windows/releases/archives/php-8.1.34-Win32-vs16-x64.zip", "windows", "x64", "zip", "php.net"),
        new("php", "8.0.30", "8.0", "https://downloads.php.net/~windows/releases/archives/php-8.0.30-Win32-vs16-x64.zip", "windows", "x64", "zip", "php.net"),
        new("php", "7.4.33", "7.4", "https://downloads.php.net/~windows/releases/archives/php-7.4.33-Win32-vc15-x64.zip", "windows", "x64", "zip", "php.net"),
        new("php", "7.3.33", "7.3", "https://downloads.php.net/~windows/releases/archives/php-7.3.33-Win32-VC15-x64.zip", "windows", "x64", "zip", "php.net"),
        new("php", "7.2.34", "7.2", "https://downloads.php.net/~windows/releases/archives/php-7.2.34-Win32-VC15-x64.zip", "windows", "x64", "zip", "php.net"),
        new("php", "7.1.33", "7.1", "https://downloads.php.net/~windows/releases/archives/php-7.1.33-Win32-VC14-x64.zip", "windows", "x64", "zip", "php.net"),

        // ── MySQL (Windows: official Oracle community zip) ──────────────────────
        new("mysql", "9.6.0",  "9.6", "https://dev.mysql.com/get/Downloads/MySQL-9.6/mysql-9.6.0-winx64.zip",  "windows", "x64", "zip", "dev.mysql.com"),
        new("mysql", "8.4.8",  "8.4", "https://dev.mysql.com/get/Downloads/MySQL-8.4/mysql-8.4.8-winx64.zip",  "windows", "x64", "zip", "dev.mysql.com"),
        new("mysql", "8.0.45", "8.0", "https://dev.mysql.com/get/Downloads/MySQL-8.0/mysql-8.0.45-winx64.zip", "windows", "x64", "zip", "dev.mysql.com"),
        new("mysql", "5.7.44", "5.7", "https://dev.mysql.com/get/Downloads/MySQL-5.7/mysql-5.7.44-winx64.zip", "windows", "x64", "zip", "dev.mysql.com"),

        // ── MariaDB (Windows: official mariadb.org archive) ─────────────────────
        new("mariadb", "12.3.1",  "12.3", "https://archive.mariadb.org/mariadb-12.3.1/winx64-packages/mariadb-12.3.1-winx64.zip",   "windows", "x64", "zip", "mariadb.org"),
        new("mariadb", "11.8.6",  "11.8", "https://archive.mariadb.org/mariadb-11.8.6/winx64-packages/mariadb-11.8.6-winx64.zip",   "windows", "x64", "zip", "mariadb.org"),
        new("mariadb", "11.4.10", "11.4", "https://archive.mariadb.org/mariadb-11.4.10/winx64-packages/mariadb-11.4.10-winx64.zip", "windows", "x64", "zip", "mariadb.org"),

        // ── Redis (Windows: redis-windows community fork using msys2) ───────────
        new("redis", "8.6.2",  "8.6", "https://github.com/redis-windows/redis-windows/releases/download/8.6.2/Redis-8.6.2-Windows-x64-msys2.zip",   "windows", "x64", "zip", "github/redis-windows"),
        new("redis", "8.0.6",  "8.0", "https://github.com/redis-windows/redis-windows/releases/download/8.0.6/Redis-8.0.6-Windows-x64-msys2.zip",   "windows", "x64", "zip", "github/redis-windows"),
        new("redis", "7.4.8",  "7.4", "https://github.com/redis-windows/redis-windows/releases/download/7.4.8/Redis-7.4.8-Windows-x64-msys2.zip",   "windows", "x64", "zip", "github/redis-windows"),
        new("redis", "7.2.13", "7.2", "https://github.com/redis-windows/redis-windows/releases/download/7.2.13/Redis-7.2.13-Windows-x64-msys2.zip", "windows", "x64", "zip", "github/redis-windows"),

        // ── PostgreSQL (Windows: EnterpriseDB binaries) ─────────────────────────
        new("postgresql", "18.3",  "18", "https://get.enterprisedb.com/postgresql/postgresql-18.3-1-windows-x64-binaries.zip",  "windows", "x64", "zip", "enterprisedb"),
        new("postgresql", "17.9",  "17", "https://get.enterprisedb.com/postgresql/postgresql-17.9-1-windows-x64-binaries.zip",  "windows", "x64", "zip", "enterprisedb"),
        new("postgresql", "16.13", "16", "https://get.enterprisedb.com/postgresql/postgresql-16.13-1-windows-x64-binaries.zip", "windows", "x64", "zip", "enterprisedb"),

        // ── MongoDB (Windows: official fastdl) ──────────────────────────────────
        new("mongodb", "8.0.21", "8.0", "https://fastdl.mongodb.org/windows/mongodb-windows-x86_64-8.0.21-rc1.zip", "windows", "x64", "zip", "mongodb"),
        new("mongodb", "7.0.32", "7.0", "https://fastdl.mongodb.org/windows/mongodb-windows-x86_64-7.0.32-rc2.zip", "windows", "x64", "zip", "mongodb"),

        // ── Memcached (Windows: nono303 community port) ─────────────────────────
        new("memcached", "1.6.41", "1.6", "https://github.com/nono303/memcached/archive/1.6.41.zip", "windows", "x64", "zip", "github/nono303"),

        // ── Mailpit (Windows: official axllent release) ─────────────────────────
        new("mailpit", "1.29.6", "1.29", "https://github.com/axllent/mailpit/releases/download/v1.29.6/mailpit-windows-amd64.zip", "windows", "x64", "zip", "github/axllent"),
        new("mailpit", "1.28.4", "1.28", "https://github.com/axllent/mailpit/releases/download/v1.28.4/mailpit-windows-amd64.zip", "windows", "x64", "zip", "github/axllent"),

        // ── Nginx (Windows: official nginx.org) ─────────────────────────────────
        new("nginx", "1.29.8", "1.29", "https://nginx.org/download/nginx-1.29.8.zip", "windows", "x64", "zip", "nginx.org"),
        new("nginx", "1.28.3", "1.28", "https://nginx.org/download/nginx-1.28.3.zip", "windows", "x64", "zip", "nginx.org"),
        new("nginx", "1.26.3", "1.26", "https://nginx.org/download/nginx-1.26.3.zip", "windows", "x64", "zip", "nginx.org"),
        new("nginx", "1.24.0", "1.24", "https://nginx.org/download/nginx-1.24.0.zip", "windows", "x64", "zip", "nginx.org"),
    };

    /// <summary>Available releases for a given app on a given OS/arch.</summary>
    public static IEnumerable<BinaryRelease> ForApp(string app, string os = "windows", string arch = "x64")
        => All.Where(r =>
            r.App.Equals(app, StringComparison.OrdinalIgnoreCase) &&
            r.Os == os && r.Arch == arch);

    /// <summary>Find the latest stable release for an app/major.minor version.</summary>
    public static BinaryRelease? FindLatest(string app, string? majorMinor = null, string os = "windows", string arch = "x64")
    {
        var releases = ForApp(app, os, arch);
        if (majorMinor is not null)
            releases = releases.Where(r => r.MajorMinor == majorMinor);
        return releases.FirstOrDefault();
    }

    /// <summary>Find a specific exact version.</summary>
    public static BinaryRelease? Find(string app, string version, string os = "windows", string arch = "x64")
        => ForApp(app, os, arch).FirstOrDefault(r => r.Version == version);
}
