#compdef wdc
# NKS WDC Zsh completion
#
# Installation:
#   # Option 1 — place in a directory on your $fpath:
#   cp wdc.zsh ~/.zsh/completions/_wdc
#   # Then ensure ~/.zsh/completions is in fpath:
#   # fpath=(~/.zsh/completions $fpath)
#   # autoload -Uz compinit && compinit
#
#   # Option 2 — Oh My Zsh:
#   cp wdc.zsh ~/.oh-my-zsh/completions/_wdc
#
#   # Option 3 — Homebrew on macOS:
#   cp wdc.zsh "$(brew --prefix)/share/zsh/site-functions/_wdc"

_wdc() {
    local context state state_descr line
    typeset -A opt_args

    _arguments -C \
        '(-h --help)'{-h,--help}'[Show help information]' \
        '(-v --verbose)'{-v,--verbose}'[Enable verbose output]' \
        '(-q --quiet)'{-q,--quiet}'[Suppress all non-error output]' \
        '--json[Output as JSON]' \
        '--no-color[Disable ANSI colors]' \
        '--version[Show version]' \
        '1: :_wdc_commands' \
        '*:: :->args'

    case $state in
        args)
            case $words[1] in
                status)
                    _arguments \
                        '--json[Output as JSON]' \
                        '--watch[Watch mode, refresh every 2s]'
                    ;;

                start|stop|restart|reload)
                    _arguments \
                        '--all[Apply to all services]' \
                        '(-f --force)'{-f,--force}'[Force without confirmation]' \
                        '1: :_wdc_services'
                    ;;

                logs)
                    _arguments \
                        '--follow[Follow log output]' \
                        '--lines=[Number of lines to show]:lines:(10 50 100 500)' \
                        '1: :_wdc_services'
                    ;;

                site:list)
                    _arguments \
                        '--all[Include disabled sites]' \
                        '--json[Output as JSON]'
                    ;;

                site:create)
                    _arguments \
                        '--php=[PHP version]:version:_wdc_php_versions' \
                        '--docroot=[Document root path]:docroot:_files -/' \
                        '--ssl[Enable SSL (default)]' \
                        '--no-ssl[Disable SSL]' \
                        '--preset=[Framework preset]:preset:(laravel wordpress symfony none)' \
                        '--db=[Database name]:database:' \
                        '1:domain:'
                    ;;

                site:delete)
                    _arguments \
                        '(-f --force)'{-f,--force}'[Skip confirmation]' \
                        '--keep-files[Do not delete document root]' \
                        '1:domain:_wdc_sites'
                    ;;

                site:info|site:edit|site:open|site:enable|site:disable)
                    _arguments \
                        '1:domain:_wdc_sites'
                    ;;

                site:php)
                    _arguments \
                        '1:domain:_wdc_sites' \
                        '2:version:_wdc_php_versions'
                    ;;

                php:list)
                    _arguments \
                        '--json[Output as JSON]' \
                        '--available[Show available-to-install versions too]'
                    ;;

                php:install)
                    _arguments \
                        '--set-default[Set as default after install]' \
                        '1:version:_wdc_php_available_versions'
                    ;;

                php:uninstall)
                    _arguments \
                        '(-f --force)'{-f,--force}'[Skip confirmation]' \
                        '1:version:_wdc_php_versions'
                    ;;

                php:use|php:info)
                    _arguments \
                        '1:version:_wdc_php_versions'
                    ;;

                ssl:status)
                    _arguments \
                        '--json[Output as JSON]' \
                        '--expired[Show only expired/missing]'
                    ;;

                ssl:create|ssl:renew)
                    _arguments \
                        '--days=[Certificate validity in days]:days:(365 730)' \
                        '1:domain:_wdc_sites'
                    ;;

                db:list)
                    _arguments \
                        '--json[Output as JSON]' \
                        '--all[Include system databases]'
                    ;;

                db:create)
                    _arguments \
                        '--charset=[Character set]:charset:(utf8mb4 utf8 latin1)' \
                        '--collation=[Collation]:collation:' \
                        '1:name:'
                    ;;

                db:drop)
                    _arguments \
                        '(-f --force)'{-f,--force}'[Skip confirmation]' \
                        '1:database:_wdc_databases'
                    ;;

                db:import)
                    _arguments \
                        '--drop-first[Drop and recreate database before import]' \
                        '--no-create-db[Skip CREATE DATABASE statement]' \
                        '1:database:_wdc_databases' \
                        '2:file:_files -g "*.sql *.sql.gz"'
                    ;;

                db:export)
                    _arguments \
                        '--output=[Output file]:file:_files' \
                        '--compress[Compress with gzip]' \
                        '--no-routines[Skip stored routines]' \
                        '1:database:_wdc_databases'
                    ;;

                db:open)
                    _arguments \
                        '1:database:_wdc_databases'
                    ;;

                config:get|config:set)
                    _arguments \
                        '1:key:_wdc_config_keys'
                    ;;

                doctor)
                    _arguments \
                        '--fix[Attempt to auto-fix issues]' \
                        '--json[Output as JSON]'
                    ;;
            esac
            ;;
    esac
}

# Sub-completions

_wdc_commands() {
    local commands=(
        'status:Show service status overview'
        'start:Start all or named service'
        'stop:Stop a service'
        'restart:Restart a service'
        'reload:Graceful reload (e.g. Apache graceful)'
        'logs:Tail service logs'
        'doctor:Run system diagnostics'
        'site\:list:List all configured sites'
        'site\:create:Create a new site'
        'site\:delete:Delete a site'
        'site\:info:Show site details'
        'site\:edit:Edit site configuration'
        'site\:open:Open site in browser'
        'site\:php:Change PHP version for a site'
        'site\:enable:Enable a disabled site'
        'site\:disable:Disable a site'
        'php\:list:List installed PHP versions'
        'php\:install:Install a PHP version'
        'php\:uninstall:Remove a PHP version'
        'php\:use:Set default PHP version'
        'php\:info:Show PHP version details'
        'ssl\:status:Show SSL certificate status'
        'ssl\:install:Install and trust the CA'
        'ssl\:create:Create certificate for a domain'
        'ssl\:renew:Renew a certificate'
        'db\:list:List all databases'
        'db\:create:Create a database'
        'db\:drop:Drop a database'
        'db\:import:Import SQL file into database'
        'db\:export:Export database to SQL file'
        'db\:open:Open database in GUI client'
        'config:Show configuration'
        'config\:get:Get a config value'
        'config\:set:Set a config value'
        'config\:edit:Open config in $EDITOR'
    )
    _describe 'command' commands
}

_wdc_services() {
    local services=(
        'apache:Apache web server'
        'mysql:MySQL database'
        'redis:Redis cache'
        'mailpit:Mailpit email catcher'
        'php-fpm:All PHP-FPM processes'
        'php-fpm@8.1:PHP-FPM 8.1'
        'php-fpm@8.2:PHP-FPM 8.2'
        'php-fpm@8.3:PHP-FPM 8.3'
        'php-fpm@8.4:PHP-FPM 8.4'
    )
    _describe 'service' services
}

_wdc_sites() {
    local sites
    sites=(${(f)"$(wdc site:list --json 2>/dev/null | \
        grep '"domain"' | \
        sed 's/.*"domain": *"\([^"]*\)".*/\1/')"})
    _describe 'site' sites
}

_wdc_php_versions() {
    local versions
    versions=(${(f)"$(wdc php:list --json 2>/dev/null | \
        grep '"version"' | \
        sed 's/.*"version": *"\([^"]*\)".*/\1/')"})
    if [[ ${#versions[@]} -eq 0 ]]; then
        versions=(5.6 7.4 8.0 8.1 8.2 8.3 8.4)
    fi
    _describe 'PHP version' versions
}

_wdc_php_available_versions() {
    local versions=(5.6 7.4 8.0 8.1 8.2 8.3 8.4)
    _describe 'PHP version' versions
}

_wdc_databases() {
    local dbs
    dbs=(${(f)"$(wdc db:list --json 2>/dev/null | \
        grep '"name"' | \
        sed 's/.*"name": *"\([^"]*\)".*/\1/')"})
    _describe 'database' dbs
}

_wdc_config_keys() {
    local keys=(
        'apache.port:HTTP port (default 80)'
        'apache.ssl_port:HTTPS port (default 443)'
        'apache.workers:Number of Apache worker processes'
        'mysql.port:MySQL port (default 3306)'
        'mysql.root_password:MySQL root password'
        'php.default_version:Default PHP version'
        'php.fpm_base_port:Base port for PHP-FPM (e.g. 9000)'
        'ssl.ca_path:Path to CA certificate'
        'ssl.cert_days:Certificate validity in days'
        'dns.method:DNS method (hosts/dnsmasq/resolver)'
        'dns.hosts_file:Path to hosts file'
        'general.editor:Preferred text editor'
        'general.browser:Preferred browser command'
        'general.sites_dir:Default directory for new sites'
    )
    _describe 'config key' keys
}

_wdc "$@"
