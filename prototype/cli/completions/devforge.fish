# NKS WDC Fish shell completion
#
# Installation:
#   cp wdc.fish ~/.config/fish/completions/wdc.fish
#
# Fish completions reload automatically — no sourcing needed.

# Disable file completion for wdc by default
complete -c wdc -f

# ─── helpers ──────────────────────────────────────────────────────────────────

function __wdc_sites
    wdc site:list --json 2>/dev/null | \
        string match -r '"domain": *"([^"]+)"' | \
        string replace -r '.*"domain": *"([^"]+)".*' '$1'
end

function __wdc_php_versions
    wdc php:list --json 2>/dev/null | \
        string match -r '"version": *"([^"]+)"' | \
        string replace -r '.*"version": *"([^"]+)".*' '$1'
end

function __wdc_databases
    wdc db:list --json 2>/dev/null | \
        string match -r '"name": *"([^"]+)"' | \
        string replace -r '.*"name": *"([^"]+)".*' '$1'
end

# True when no subcommand has been given yet
function __wdc_no_subcommand
    set -l cmd (commandline -opc)
    if [ (count $cmd) -eq 1 ]
        return 0
    end
    return 1
end

# True when the given subcommand is active
function __wdc_using_command
    set -l cmd (commandline -opc)
    if [ (count $cmd) -gt 1 ]
        if [ $argv[1] = $cmd[2] ]
            return 0
        end
    end
    return 1
end

# True when at position N (1-indexed from the subcommand, not including wdc itself)
function __wdc_arg_at
    set -l cmd (commandline -opc)
    # cmd[1] = wdc, cmd[2] = subcommand, cmd[3] = first arg ...
    set -l pos (math $argv[1] + 2)
    if [ (count $cmd) -eq $pos ]
        return 0
    end
    return 1
end

# ─── global flags (all commands) ─────────────────────────────────────────────

complete -c wdc -l json      -d 'Output as JSON'
complete -c wdc -l quiet  -s q -d 'Suppress all non-error output'
complete -c wdc -l verbose -s v -d 'Verbose output'
complete -c wdc -l no-color  -d 'Disable ANSI colors'
complete -c wdc -l help   -s h -d 'Show help'
complete -c wdc -l version   -d 'Show version'

# ─── top-level commands ───────────────────────────────────────────────────────

complete -c wdc -n '__wdc_no_subcommand' -a status      -d 'Show service status overview'
complete -c wdc -n '__wdc_no_subcommand' -a start       -d 'Start all or named service'
complete -c wdc -n '__wdc_no_subcommand' -a stop        -d 'Stop a service'
complete -c wdc -n '__wdc_no_subcommand' -a restart     -d 'Restart a service'
complete -c wdc -n '__wdc_no_subcommand' -a reload      -d 'Graceful reload'
complete -c wdc -n '__wdc_no_subcommand' -a logs        -d 'Tail service logs'
complete -c wdc -n '__wdc_no_subcommand' -a doctor      -d 'Run system diagnostics'

complete -c wdc -n '__wdc_no_subcommand' -a 'site:list'    -d 'List all configured sites'
complete -c wdc -n '__wdc_no_subcommand' -a 'site:create'  -d 'Create a new site'
complete -c wdc -n '__wdc_no_subcommand' -a 'site:delete'  -d 'Delete a site'
complete -c wdc -n '__wdc_no_subcommand' -a 'site:info'    -d 'Show site details'
complete -c wdc -n '__wdc_no_subcommand' -a 'site:edit'    -d 'Edit site configuration'
complete -c wdc -n '__wdc_no_subcommand' -a 'site:open'    -d 'Open site in browser'
complete -c wdc -n '__wdc_no_subcommand' -a 'site:php'     -d 'Change PHP version for site'
complete -c wdc -n '__wdc_no_subcommand' -a 'site:enable'  -d 'Enable a disabled site'
complete -c wdc -n '__wdc_no_subcommand' -a 'site:disable' -d 'Disable a site'

complete -c wdc -n '__wdc_no_subcommand' -a 'php:list'      -d 'List installed PHP versions'
complete -c wdc -n '__wdc_no_subcommand' -a 'php:install'   -d 'Install a PHP version'
complete -c wdc -n '__wdc_no_subcommand' -a 'php:uninstall' -d 'Remove a PHP version'
complete -c wdc -n '__wdc_no_subcommand' -a 'php:use'       -d 'Set default PHP version'
complete -c wdc -n '__wdc_no_subcommand' -a 'php:info'      -d 'Show PHP details'

complete -c wdc -n '__wdc_no_subcommand' -a 'ssl:status'  -d 'Show SSL certificate status'
complete -c wdc -n '__wdc_no_subcommand' -a 'ssl:install' -d 'Install and trust CA'
complete -c wdc -n '__wdc_no_subcommand' -a 'ssl:create'  -d 'Create certificate for domain'
complete -c wdc -n '__wdc_no_subcommand' -a 'ssl:renew'   -d 'Renew a certificate'

complete -c wdc -n '__wdc_no_subcommand' -a 'db:list'   -d 'List all databases'
complete -c wdc -n '__wdc_no_subcommand' -a 'db:create' -d 'Create a database'
complete -c wdc -n '__wdc_no_subcommand' -a 'db:drop'   -d 'Drop a database'
complete -c wdc -n '__wdc_no_subcommand' -a 'db:import' -d 'Import SQL file'
complete -c wdc -n '__wdc_no_subcommand' -a 'db:export' -d 'Export database to SQL'
complete -c wdc -n '__wdc_no_subcommand' -a 'db:open'   -d 'Open in GUI client'

complete -c wdc -n '__wdc_no_subcommand' -a config       -d 'Show configuration'
complete -c wdc -n '__wdc_no_subcommand' -a 'config:get'  -d 'Get a config value'
complete -c wdc -n '__wdc_no_subcommand' -a 'config:set'  -d 'Set a config value'
complete -c wdc -n '__wdc_no_subcommand' -a 'config:edit' -d 'Open config in $EDITOR'

# ─── start / stop / restart / reload / logs ───────────────────────────────────

for __wdc_cmd in start stop restart reload logs
    complete -c wdc -n "__wdc_using_command $__wdc_cmd" -a apache        -d 'Apache web server'
    complete -c wdc -n "__wdc_using_command $__wdc_cmd" -a mysql         -d 'MySQL database server'
    complete -c wdc -n "__wdc_using_command $__wdc_cmd" -a redis         -d 'Redis cache'
    complete -c wdc -n "__wdc_using_command $__wdc_cmd" -a mailpit       -d 'Mailpit email catcher'
    complete -c wdc -n "__wdc_using_command $__wdc_cmd" -a php-fpm       -d 'All PHP-FPM processes'
    complete -c wdc -n "__wdc_using_command $__wdc_cmd" -a 'php-fpm@8.1' -d 'PHP-FPM 8.1'
    complete -c wdc -n "__wdc_using_command $__wdc_cmd" -a 'php-fpm@8.2' -d 'PHP-FPM 8.2'
    complete -c wdc -n "__wdc_using_command $__wdc_cmd" -a 'php-fpm@8.3' -d 'PHP-FPM 8.3'
    complete -c wdc -n "__wdc_using_command $__wdc_cmd" -a 'php-fpm@8.4' -d 'PHP-FPM 8.4'
end

# stop: add --all flag
complete -c wdc -n '__wdc_using_command stop'    -l all   -d 'Stop all services'
complete -c wdc -n '__wdc_using_command start'   -l all   -d 'Start all services'
complete -c wdc -n '__wdc_using_command restart' -l all   -d 'Restart all services'

# logs: --follow and --lines
complete -c wdc -n '__wdc_using_command logs' -l follow -s f -d 'Follow log output'
complete -c wdc -n '__wdc_using_command logs' -l lines  -r   -d 'Number of lines to show'

# ─── site: commands ───────────────────────────────────────────────────────────

# site:create — flags only (domain is free-text)
complete -c wdc -n '__wdc_using_command site:create' \
    -l php -r -a '(__wdc_php_versions)' -d 'PHP version'
complete -c wdc -n '__wdc_using_command site:create' \
    -l docroot -r -F -d 'Document root path'
complete -c wdc -n '__wdc_using_command site:create' \
    -l ssl -d 'Enable SSL (default)'
complete -c wdc -n '__wdc_using_command site:create' \
    -l no-ssl -d 'Disable SSL'
complete -c wdc -n '__wdc_using_command site:create' \
    -l preset -r -a 'laravel wordpress symfony none' -d 'Framework preset'
complete -c wdc -n '__wdc_using_command site:create' \
    -l db -r -d 'Database name to create'

# site:delete — site name + --force
complete -c wdc -n '__wdc_using_command site:delete' \
    -a '(__wdc_sites)' -d 'Site domain'
complete -c wdc -n '__wdc_using_command site:delete' \
    -l force -s f -d 'Skip confirmation'
complete -c wdc -n '__wdc_using_command site:delete' \
    -l keep-files -d 'Do not delete document root'

# site:info / site:edit / site:open / site:enable / site:disable
for __wdc_cmd in 'site:info' 'site:edit' 'site:open' 'site:enable' 'site:disable'
    complete -c wdc -n "__wdc_using_command $__wdc_cmd" \
        -a '(__wdc_sites)' -d 'Site domain'
end

# site:php — first arg = site, second arg = PHP version
complete -c wdc -n '__wdc_using_command site:php and __wdc_arg_at 1' \
    -a '(__wdc_sites)' -d 'Site domain'
complete -c wdc -n '__wdc_using_command site:php and __wdc_arg_at 2' \
    -a '(__wdc_php_versions)' -d 'PHP version'

# ─── php: commands ────────────────────────────────────────────────────────────

complete -c wdc -n '__wdc_using_command php:install' \
    -a '5.6 7.4 8.0 8.1 8.2 8.3 8.4' -d 'PHP version to install'
complete -c wdc -n '__wdc_using_command php:install' \
    -l set-default -d 'Set as default after install'

for __wdc_cmd in 'php:uninstall' 'php:use' 'php:info'
    complete -c wdc -n "__wdc_using_command $__wdc_cmd" \
        -a '(__wdc_php_versions)' -d 'PHP version'
end

complete -c wdc -n '__wdc_using_command php:uninstall' \
    -l force -s f -d 'Skip confirmation'

# ─── ssl: commands ────────────────────────────────────────────────────────────

for __wdc_cmd in 'ssl:create' 'ssl:renew'
    complete -c wdc -n "__wdc_using_command $__wdc_cmd" \
        -a '(__wdc_sites)' -d 'Site domain'
    complete -c wdc -n "__wdc_using_command $__wdc_cmd" \
        -l days -r -a '365 730' -d 'Certificate validity in days'
end

complete -c wdc -n '__wdc_using_command ssl:status' \
    -l expired -d 'Show only expired or missing certificates'

# ─── db: commands ─────────────────────────────────────────────────────────────

complete -c wdc -n '__wdc_using_command db:drop' \
    -a '(__wdc_databases)' -d 'Database name'
complete -c wdc -n '__wdc_using_command db:drop' \
    -l force -s f -d 'Skip confirmation'

complete -c wdc -n '__wdc_using_command db:open' \
    -a '(__wdc_databases)' -d 'Database name'

complete -c wdc -n '__wdc_using_command db:export' \
    -a '(__wdc_databases)' -d 'Database name'
complete -c wdc -n '__wdc_using_command db:export' \
    -l output -r -F -d 'Output file path'
complete -c wdc -n '__wdc_using_command db:export' \
    -l compress -d 'Compress with gzip'
complete -c wdc -n '__wdc_using_command db:export' \
    -l no-routines -d 'Skip stored routines'

# db:import — first arg = database name, second arg = SQL file
complete -c wdc -n '__wdc_using_command db:import and __wdc_arg_at 1' \
    -a '(__wdc_databases)' -d 'Database name'
complete -c wdc -n '__wdc_using_command db:import and __wdc_arg_at 2' \
    -F -d 'SQL file' -a '*.sql' -a '*.sql.gz'
complete -c wdc -n '__wdc_using_command db:import' \
    -l drop-first -d 'Drop and recreate database before import'
complete -c wdc -n '__wdc_using_command db:import' \
    -l no-create-db -d 'Skip CREATE DATABASE statement'

complete -c wdc -n '__wdc_using_command db:create' \
    -l charset -r -a 'utf8mb4 utf8 latin1' -d 'Character set'

# ─── config: commands ─────────────────────────────────────────────────────────

set -l __df_config_keys \
    'apache.port' 'apache.ssl_port' 'apache.workers' \
    'mysql.port' 'mysql.root_password' \
    'php.default_version' 'php.fpm_base_port' \
    'ssl.ca_path' 'ssl.cert_days' \
    'dns.method' 'dns.hosts_file' \
    'general.editor' 'general.browser' 'general.sites_dir'

for __wdc_cmd in 'config:get' 'config:set'
    complete -c wdc -n "__wdc_using_command $__wdc_cmd" \
        -a "$__df_config_keys" -d 'Config key'
end

# ─── doctor ───────────────────────────────────────────────────────────────────

complete -c wdc -n '__wdc_using_command doctor' \
    -l fix -d 'Attempt to auto-fix detected issues'
