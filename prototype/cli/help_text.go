// help_text.go — DevForge CLI help text definitions
//
// All command help strings live here so they can be referenced from
// the cobra command definitions and tested in isolation.
//
// In production this would be package cli (a library).
// Here it shares package main with cli_mockup.go for standalone compilation.

package main

import "fmt"

// CommandHelp holds the help text fragments for a single command.
type CommandHelp struct {
	Short   string
	Long    string
	Example string
}

// HelpTexts is the master map: command name → help content.
var HelpTexts = map[string]CommandHelp{

	// ─── root ────────────────────────────────────────────────────────────────

	"root": {
		Short: "DevForge — local dev server management",
		Long: `DevForge is a local development server management tool.

It manages Apache, PHP-FPM (multiple versions), MySQL, Redis, and Mailpit
as a unified stack — with automatic SSL via mkcert, per-site PHP version
selection, and a single CLI to control everything.`,
		Example: `  devforge status
  devforge site:create myapp.test --php=8.2
  devforge start
  devforge doctor`,
	},

	// ─── status ──────────────────────────────────────────────────────────────

	"status": {
		Short: "Show the status of all DevForge-managed services",
		Long: `Displays a formatted table of all managed services with their
current status, PID, uptime, and memory usage.

Exit codes:
  0  all services are running
  1  one or more services are stopped or degraded
  2  daemon is not reachable`,
		Example: `  devforge status
  devforge status --json`,
	},

	// ─── start ───────────────────────────────────────────────────────────────

	"start": {
		Short: "Start all services, or a specific named service",
		Long: `Without arguments, starts all enabled services in dependency order:
  1. MySQL / databases
  2. PHP-FPM processes
  3. Apache / web server
  4. Redis, Mailpit, other auxiliary services

Pass a service name to start a single service only.

Service names:
  apache            Apache web server
  mysql             MySQL database server
  redis             Redis cache
  mailpit           Mailpit email catcher
  php-fpm           All PHP-FPM processes
  php-fpm@<ver>     Specific PHP-FPM version (e.g. php-fpm@8.2)`,
		Example: `  devforge start
  devforge start apache
  devforge start php-fpm@8.3
  devforge start --all`,
	},

	// ─── stop ────────────────────────────────────────────────────────────────

	"stop": {
		Short: "Stop all services, or a specific named service",
		Long: `Gracefully stops the named service. If stopping a service would
affect active sites, you will be prompted for confirmation unless
the --force flag is provided.

Without arguments, stops all running services in reverse dependency order.`,
		Example: `  devforge stop
  devforge stop apache
  devforge stop php-fpm@8.1
  devforge stop mysql --force`,
	},

	// ─── restart ─────────────────────────────────────────────────────────────

	"restart": {
		Short: "Restart all services, or a specific named service",
		Long: `Stops then starts the named service (or all services if no argument
is given). Equivalent to 'devforge stop <svc> && devforge start <svc>'.

For Apache, prefer 'devforge reload apache' which performs a graceful
restart without dropping active connections.`,
		Example: `  devforge restart
  devforge restart apache
  devforge restart mysql`,
	},

	// ─── reload ──────────────────────────────────────────────────────────────

	"reload": {
		Short: "Gracefully reload service configuration",
		Long: `Sends a graceful reload signal to the named service.
For Apache this triggers 'apachectl graceful' — existing connections
are not dropped and workers finish their current requests before reloading.

For MySQL and PHP-FPM this is equivalent to a restart.`,
		Example: `  devforge reload apache
  devforge reload php-fpm`,
	},

	// ─── logs ────────────────────────────────────────────────────────────────

	"logs": {
		Short: "Tail the logs of a service",
		Long: `Streams the log output of the named service to stdout.
By default shows the last 50 lines and follows (-f).

Available log sources:
  apache       Apache error_log + access_log
  mysql        MySQL error log
  php-fpm      PHP-FPM slow log + error log (all versions)
  php-fpm@X.Y  Specific PHP-FPM version logs
  redis        Redis log
  mailpit      Mailpit log`,
		Example: `  devforge logs apache
  devforge logs php-fpm@8.2 --lines=100
  devforge logs mysql --no-follow`,
	},

	// ─── site:list ───────────────────────────────────────────────────────────

	"site:list": {
		Short: "List all configured sites",
		Long: `Displays a formatted table of all configured sites with their
domain, PHP version, status, SSL state, and document root.

By default shows only enabled sites. Use --all to include disabled sites.`,
		Example: `  devforge site:list
  devforge site:list --all
  devforge site:list --json`,
	},

	// ─── site:create ─────────────────────────────────────────────────────────

	"site:create": {
		Short: "Create a new local development site",
		Long: `Creates a new virtual host with optional SSL, DNS entry, and database.

When called without arguments, launches an interactive wizard that
walks through all options. Supply the domain as an argument to skip
to flag-based configuration.

Steps performed:
  1. Validate domain name
  2. Generate SSL certificate via mkcert (unless --no-ssl)
  3. Write Apache virtual host configuration
  4. Add entry to /etc/hosts (requires elevation on Windows)
  5. Reload Apache

Supported framework presets (--preset):
  laravel     Sets document root to <path>/public/
  wordpress   Configures .htaccess rules and rewrites
  symfony     Sets document root to <path>/public/
  none        Plain directory, no rewrite rules`,
		Example: `  devforge site:create
  devforge site:create myapp.test
  devforge site:create myapp.test --php=8.2
  devforge site:create myapp.test --php=8.3 --docroot=~/projects/myapp
  devforge site:create myapp.test --preset=laravel --db=myapp
  devforge site:create myapp.test --no-ssl`,
	},

	// ─── site:delete ─────────────────────────────────────────────────────────

	"site:delete": {
		Short: "Delete a site and remove all associated configuration",
		Long: `Removes the virtual host configuration, SSL certificate, and
/etc/hosts entry for the given domain.

The document root directory is NOT deleted by default. Pass --delete-files
to also remove the document root.

You will be prompted to confirm by typing the domain name unless
the --force flag is provided.`,
		Example: `  devforge site:delete myapp.test
  devforge site:delete myapp.test --force
  devforge site:delete myapp.test --delete-files`,
	},

	// ─── site:info ───────────────────────────────────────────────────────────

	"site:info": {
		Short: "Show detailed information about a site",
		Long: `Displays all configuration details for the given site:
domain, PHP version, document root, SSL certificate details,
database links, Apache config path, and current status.`,
		Example: `  devforge site:info myapp.test
  devforge site:info myapp.test --json`,
	},

	// ─── site:edit ───────────────────────────────────────────────────────────

	"site:edit": {
		Short: "Open a site's configuration file in your editor",
		Long: `Opens the TOML configuration file for the given site in
your preferred editor (set via $EDITOR or config general.editor).

After saving, DevForge automatically validates and reloads the
configuration if changes are detected.`,
		Example: `  devforge site:edit myapp.test
  EDITOR=nano devforge site:edit myapp.test`,
	},

	// ─── site:open ───────────────────────────────────────────────────────────

	"site:open": {
		Short: "Open a site in your default browser",
		Long: `Opens the site's URL (https:// if SSL is enabled, http:// otherwise)
in the system default browser.`,
		Example: `  devforge site:open myapp.test`,
	},

	// ─── site:php ────────────────────────────────────────────────────────────

	"site:php": {
		Short: "Change the PHP version for a specific site",
		Long: `Updates the site's vhost configuration to use the specified
PHP-FPM version and reloads Apache.

The PHP version must already be installed. Use 'devforge php:list' to
see installed versions and 'devforge php:install' to install new ones.`,
		Example: `  devforge site:php myapp.test 8.3
  devforge site:php legacy.test 7.4`,
	},

	// ─── site:enable / site:disable ──────────────────────────────────────────

	"site:enable": {
		Short: "Enable a disabled site",
		Long: `Re-enables a previously disabled site. The vhost config is
restored, the /etc/hosts entry is re-added, and Apache is reloaded.`,
		Example: `  devforge site:enable myapp.test`,
	},

	"site:disable": {
		Short: "Disable a site without deleting its configuration",
		Long: `Disables the site by removing its vhost from Apache's active
configuration and removing its /etc/hosts entry. The site config
TOML file is preserved and can be re-enabled at any time.`,
		Example: `  devforge site:disable myapp.test`,
	},

	// ─── php:list ────────────────────────────────────────────────────────────

	"php:list": {
		Short: "List installed PHP versions",
		Long: `Displays all PHP versions managed by DevForge with their
status (running/stopped), FPM port, number of sites using that version,
and binary path.

The default version is highlighted with ●.

To see versions available to install, use --available.`,
		Example: `  devforge php:list
  devforge php:list --available
  devforge php:list --json`,
	},

	// ─── php:install ─────────────────────────────────────────────────────────

	"php:install": {
		Short: "Download and install a PHP version",
		Long: `Downloads the specified PHP version from the DevForge binary
repository, verifies the SHA-256 checksum, extracts it to
~/.devforge/bin/php-<version>/, and configures PHP-FPM.

Extensions included in all builds:
  bcmath, curl, gd, intl, mbstring, mysqli, openssl,
  pdo_mysql, pdo_sqlite, redis, zip, xdebug (disabled by default)

DevForge bundles its own OpenSSL 3.x to avoid Windows EC key issues.`,
		Example: `  devforge php:install 8.4
  devforge php:install 8.1 --set-default
  devforge php:install 7.4`,
	},

	// ─── php:uninstall ───────────────────────────────────────────────────────

	"php:uninstall": {
		Short: "Remove an installed PHP version",
		Long: `Stops the PHP-FPM process for the given version and removes
its files from ~/.devforge/bin/php-<version>/.

If any sites are currently using this PHP version you will be
prompted to migrate them first (or use --force to skip).`,
		Example: `  devforge php:uninstall 8.0
  devforge php:uninstall 7.4 --force`,
	},

	// ─── php:use ─────────────────────────────────────────────────────────────

	"php:use": {
		Short: "Set the default PHP version for new sites",
		Long: `Sets the global default PHP version. This affects only newly
created sites — existing sites keep their configured version.

The selected version must already be installed.`,
		Example: `  devforge php:use 8.3
  devforge php:use 8.2`,
	},

	// ─── php:info ────────────────────────────────────────────────────────────

	"php:info": {
		Short: "Show details and loaded extensions for a PHP version",
		Long: `Displays PHP version details, loaded extensions, php.ini path,
FPM pool configuration, and the equivalent of 'php -i' key values.`,
		Example: `  devforge php:info 8.2
  devforge php:info 8.3 --json`,
	},

	// ─── ssl:status ──────────────────────────────────────────────────────────

	"ssl:status": {
		Short: "Show SSL certificate status for all sites",
		Long: `Displays a table of all sites and their SSL certificate state:
issuer, expiry date, and days remaining.

Also shows the status of the DevForge local CA and whether it is
trusted by the system certificate store.`,
		Example: `  devforge ssl:status
  devforge ssl:status --expired
  devforge ssl:status --json`,
	},

	// ─── ssl:install ─────────────────────────────────────────────────────────

	"ssl:install": {
		Short: "Install and trust the DevForge local CA",
		Long: `Runs 'mkcert -install' to install the DevForge local CA into
the system trust store. This is a one-time operation that requires
elevation (sudo / UAC prompt).

After installation, all certificates signed by this CA will be
trusted by browsers without security warnings.`,
		Example: `  devforge ssl:install`,
	},

	// ─── ssl:create ──────────────────────────────────────────────────────────

	"ssl:create": {
		Short: "Generate a new SSL certificate for a domain",
		Long: `Generates a new certificate for the given domain using mkcert
and the DevForge local CA. The certificate and key are stored in
~/.devforge/certs/ and linked to the site's vhost configuration.

Wildcard certificates are supported: *.myapp.test`,
		Example: `  devforge ssl:create myapp.test
  devforge ssl:create "*.myapp.test"
  devforge ssl:create myapp.test --days=730`,
	},

	// ─── ssl:renew ───────────────────────────────────────────────────────────

	"ssl:renew": {
		Short: "Renew an expiring or expired SSL certificate",
		Long: `Regenerates the certificate for the given domain and reloads Apache.
Useful when a certificate is near expiry or if the CA has been reinstalled.

DevForge automatically detects and warns about certificates expiring
within 30 days when you run 'devforge status' or 'devforge ssl:status'.`,
		Example: `  devforge ssl:renew myapp.test
  devforge ssl:renew --all`,
	},

	// ─── db:list ─────────────────────────────────────────────────────────────

	"db:list": {
		Short: "List all databases managed by DevForge",
		Long: `Displays a table of all databases with their size, table count,
linked site (if any), and last modified time.

System databases (information_schema, performance_schema, mysql, sys)
are hidden by default. Use --all to include them.`,
		Example: `  devforge db:list
  devforge db:list --all
  devforge db:list --json`,
	},

	// ─── db:create ───────────────────────────────────────────────────────────

	"db:create": {
		Short: "Create a new MySQL database",
		Long: `Creates a new database with the given name using the configured
MySQL connection. Defaults to utf8mb4 character set and
utf8mb4_unicode_ci collation unless overridden.`,
		Example: `  devforge db:create myapp
  devforge db:create myapp --charset=utf8mb4 --collation=utf8mb4_unicode_ci`,
	},

	// ─── db:drop ─────────────────────────────────────────────────────────────

	"db:drop": {
		Short: "Drop a MySQL database",
		Long: `Permanently drops the given database. You will be prompted to
confirm by typing the database name unless --force is used.

This operation is irreversible. Consider 'devforge db:export' first.`,
		Example: `  devforge db:drop myapp
  devforge db:drop myapp --force`,
	},

	// ─── db:import ───────────────────────────────────────────────────────────

	"db:import": {
		Short: "Import a SQL file into a database",
		Long: `Imports a .sql or .sql.gz file into the specified database.
Gzip-compressed files are decompressed on the fly.

Use --drop-first to drop and recreate the database before importing
(useful for full restores). Use --no-create-db if your SQL file
already contains CREATE DATABASE and USE statements.`,
		Example: `  devforge db:import myapp dump.sql
  devforge db:import myapp backup.sql.gz --drop-first
  devforge db:import myapp export.sql --no-create-db`,
	},

	// ─── db:export ───────────────────────────────────────────────────────────

	"db:export": {
		Short: "Export a database to a SQL file",
		Long: `Exports the given database to a SQL file using mysqldump.
The output file defaults to <database>-<date>.sql in the current directory.

Use --compress to create a .sql.gz file. Use --no-routines to skip
stored procedures and functions.`,
		Example: `  devforge db:export myapp
  devforge db:export myapp --output=backup.sql
  devforge db:export myapp --output=backup.sql.gz --compress
  devforge db:export myapp --no-routines`,
	},

	// ─── db:open ─────────────────────────────────────────────────────────────

	"db:open": {
		Short: "Open a database in your configured GUI client",
		Long: `Opens the specified database in your preferred database GUI client.
DevForge attempts to detect installed clients in this order:
  1. TablePlus
  2. Sequel Pro (macOS)
  3. HeidiSQL (Windows)
  4. DBeaver
  5. MySQL Workbench

Set a specific client with: devforge config:set general.db_client tableplus`,
		Example: `  devforge db:open myapp`,
	},

	// ─── config ──────────────────────────────────────────────────────────────

	"config": {
		Short: "Show the current DevForge configuration",
		Long: `Displays the current configuration loaded from
~/.devforge/config.toml.

Use 'devforge config:get <key>' to retrieve a single value.
Use 'devforge config:set <key> <value>' to update a value.
Use 'devforge config:edit' to open the full config file in your editor.`,
		Example: `  devforge config
  devforge config --json`,
	},

	"config:get": {
		Short: "Get a single configuration value",
		Long:  `Prints the current value of the specified configuration key.`,
		Example: `  devforge config:get php.default_version
  devforge config:get apache.port`,
	},

	"config:set": {
		Short: "Set a configuration value",
		Long: `Updates the specified configuration key to the given value in
~/.devforge/config.toml. The value is validated before writing.

Available keys and their defaults:
  apache.port             80
  apache.ssl_port         443
  apache.workers          auto
  mysql.port              3306
  mysql.root_password     (empty)
  php.default_version     8.2
  php.fpm_base_port       9000
  ssl.ca_path             ~/.devforge/ca/rootCA.pem
  ssl.cert_days           365
  dns.method              hosts
  dns.hosts_file          /etc/hosts
  general.editor          (uses $EDITOR)
  general.browser         (uses system default)
  general.sites_dir       ~/sites`,
		Example: `  devforge config:set apache.port 8080
  devforge config:set php.default_version 8.3
  devforge config:set general.editor vim`,
	},

	"config:edit": {
		Short: "Open the configuration file in your editor",
		Long: `Opens ~/.devforge/config.toml in your preferred editor.
After saving, DevForge validates the configuration and reloads
affected services automatically.`,
		Example: `  devforge config:edit
  EDITOR=vim devforge config:edit`,
	},

	// ─── doctor ──────────────────────────────────────────────────────────────

	"doctor": {
		Short: "Run system diagnostics and report issues",
		Long: `Checks all aspects of the DevForge installation and reports:

  CORE        Daemon, config file, socket accessibility
  SERVICES    Binary paths, running state, config validation
  NETWORK     Port availability, DNS resolution for configured sites
  FILESYSTEM  Directory permissions for config, logs, and bin dirs

Each check is marked ✓ (ok), ⚡ (warning), or ✗ (error).

Use --fix to attempt automatic remediation of detected issues.
Some fixes (e.g. /etc/hosts permissions) require elevation.`,
		Example: `  devforge doctor
  devforge doctor --fix
  devforge doctor --json`,
	},
}

// Get returns the CommandHelp for the given command name.
// If the command is not found, it returns a zero-value CommandHelp.
func Get(command string) CommandHelp {
	if h, ok := HelpTexts[command]; ok {
		return h
	}
	return CommandHelp{}
}

// UsageLine returns a one-line usage string for the given command and usage pattern.
func UsageLine(command, usage string) string {
	return fmt.Sprintf("  devforge %s %s", command, usage)
}

// FlagLine returns a formatted flag description line.
func FlagLine(flag, description string) string {
	return fmt.Sprintf("  %-30s%s", flag, description)
}

// GlobalFlagsHelp returns the formatted global flags section.
func GlobalFlagsHelp() string {
	return `Global Flags:
  --json              Output as JSON (machine-readable)
  --quiet, -q         Suppress all output except errors
  --verbose, -v       Show additional debug information
  --no-color          Disable ANSI color output
  --help, -h          Show this help message
  --version           Show devforge version and exit`
}
