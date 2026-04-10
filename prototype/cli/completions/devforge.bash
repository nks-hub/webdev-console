#!/usr/bin/env bash
# NKS WDC Bash completion
#
# Installation:
#   # Option 1 — source directly in ~/.bashrc:
#   source /path/to/wdc.bash
#
#   # Option 2 — system-wide (Linux):
#   sudo cp wdc.bash /etc/bash_completion.d/wdc
#
#   # Option 3 — Homebrew on macOS:
#   cp wdc.bash "$(brew --prefix)/etc/bash_completion.d/wdc"

_wdc_commands=(
    "status"
    "start"
    "stop"
    "restart"
    "reload"
    "logs"
    "doctor"
    "site:list"
    "site:create"
    "site:delete"
    "site:info"
    "site:edit"
    "site:open"
    "site:php"
    "site:enable"
    "site:disable"
    "php:list"
    "php:install"
    "php:uninstall"
    "php:use"
    "php:info"
    "ssl:status"
    "ssl:install"
    "ssl:create"
    "ssl:renew"
    "db:list"
    "db:create"
    "db:drop"
    "db:import"
    "db:export"
    "db:open"
    "config"
    "config:get"
    "config:set"
    "config:edit"
)

_wdc_services=(
    "apache"
    "mysql"
    "redis"
    "mailpit"
    "php-fpm"
    "php-fpm@8.1"
    "php-fpm@8.2"
    "php-fpm@8.3"
    "php-fpm@8.4"
)

_wdc_php_versions=(
    "5.6"
    "7.4"
    "8.0"
    "8.1"
    "8.2"
    "8.3"
    "8.4"
)

_wdc_global_flags=(
    "--json"
    "--quiet"
    "-q"
    "--verbose"
    "-v"
    "--no-color"
    "--help"
    "-h"
    "--version"
    "--force"
    "-f"
)

# Retrieve configured site domains from wdc (falls back to empty on error)
_wdc_get_sites() {
    wdc site:list --json 2>/dev/null | \
        grep '"domain"' | \
        sed 's/.*"domain": *"\([^"]*\)".*/\1/'
}

# Retrieve installed PHP versions from wdc
_wdc_get_php_versions() {
    wdc php:list --json 2>/dev/null | \
        grep '"version"' | \
        sed 's/.*"version": *"\([^"]*\)".*/\1/'
}

# Retrieve databases
_wdc_get_databases() {
    wdc db:list --json 2>/dev/null | \
        grep '"name"' | \
        sed 's/.*"name": *"\([^"]*\)".*/\1/'
}

_wdc() {
    local cur prev words cword
    _init_completion || return

    cur="${COMP_WORDS[COMP_CWORD]}"
    prev="${COMP_WORDS[COMP_CWORD-1]}"
    words=("${COMP_WORDS[@]}")
    cword=$COMP_CWORD

    # Complete the main command
    if [[ $cword -eq 1 ]]; then
        COMPREPLY=($(compgen -W "${_wdc_commands[*]}" -- "$cur"))
        return
    fi

    local cmd="${words[1]}"

    # Global flags available for all commands
    if [[ "$cur" == --* || "$cur" == -* ]]; then
        local cmd_flags=("${_wdc_global_flags[@]}")

        case "$cmd" in
            site:list)
                cmd_flags+=("--all" "--json")
                ;;
            site:create)
                cmd_flags+=("--php=" "--docroot=" "--ssl" "--no-ssl" "--preset=")
                ;;
            site:delete)
                cmd_flags+=("--force" "-f" "--keep-files")
                ;;
            php:install)
                cmd_flags+=("--set-default")
                ;;
            php:uninstall)
                cmd_flags+=("--force" "-f")
                ;;
            db:import)
                cmd_flags+=("--database=" "--drop-first" "--no-create-db")
                ;;
            db:export)
                cmd_flags+=("--output=" "--compress" "--no-routines")
                ;;
            doctor)
                cmd_flags+=("--fix" "--json")
                ;;
            start|stop|restart|reload)
                cmd_flags+=("--all")
                ;;
        esac

        COMPREPLY=($(compgen -W "${cmd_flags[*]}" -- "$cur"))
        return
    fi

    # Command-specific argument completion
    case "$cmd" in
        start|stop|restart|reload)
            if [[ $cword -eq 2 ]]; then
                COMPREPLY=($(compgen -W "${_wdc_services[*]}" -- "$cur"))
            fi
            ;;

        logs)
            if [[ $cword -eq 2 ]]; then
                COMPREPLY=($(compgen -W "${_wdc_services[*]}" -- "$cur"))
            fi
            ;;

        site:delete|site:info|site:edit|site:open|site:enable|site:disable)
            if [[ $cword -eq 2 ]]; then
                local sites
                sites=$(_wdc_get_sites)
                COMPREPLY=($(compgen -W "$sites" -- "$cur"))
            fi
            ;;

        site:php)
            if [[ $cword -eq 2 ]]; then
                local sites
                sites=$(_wdc_get_sites)
                COMPREPLY=($(compgen -W "$sites" -- "$cur"))
            elif [[ $cword -eq 3 ]]; then
                local versions
                versions=$(_wdc_get_php_versions)
                COMPREPLY=($(compgen -W "$versions ${_wdc_php_versions[*]}" -- "$cur"))
            fi
            ;;

        php:install)
            if [[ $cword -eq 2 ]]; then
                COMPREPLY=($(compgen -W "${_wdc_php_versions[*]}" -- "$cur"))
            fi
            ;;

        php:uninstall|php:use|php:info)
            if [[ $cword -eq 2 ]]; then
                local versions
                versions=$(_wdc_get_php_versions)
                COMPREPLY=($(compgen -W "$versions" -- "$cur"))
            fi
            ;;

        ssl:create|ssl:renew)
            if [[ $cword -eq 2 ]]; then
                local sites
                sites=$(_wdc_get_sites)
                COMPREPLY=($(compgen -W "$sites" -- "$cur"))
            fi
            ;;

        db:drop|db:import|db:export|db:open)
            if [[ $cword -eq 2 ]]; then
                local dbs
                dbs=$(_wdc_get_databases)
                COMPREPLY=($(compgen -W "$dbs" -- "$cur"))
            fi
            ;;

        db:import)
            # After database name, complete file paths for SQL files
            if [[ $cword -ge 3 ]]; then
                COMPREPLY=($(compgen -f -X '!*.sql' -- "$cur"))
                COMPREPLY+=($(compgen -f -X '!*.sql.gz' -- "$cur"))
            fi
            ;;

        config:get|config:set)
            if [[ $cword -eq 2 ]]; then
                local config_keys=(
                    "apache.port"
                    "apache.ssl_port"
                    "apache.workers"
                    "mysql.port"
                    "mysql.root_password"
                    "php.default_version"
                    "php.fpm_base_port"
                    "ssl.ca_path"
                    "ssl.cert_days"
                    "dns.method"
                    "dns.hosts_file"
                    "general.editor"
                    "general.browser"
                    "general.sites_dir"
                )
                COMPREPLY=($(compgen -W "${config_keys[*]}" -- "$cur"))
            fi
            ;;

        site:create)
            if [[ $cword -eq 2 ]]; then
                # Offer no completions — domain is free text
                :
            elif [[ "$prev" == "--php" || "$prev" == "--php="* ]]; then
                local versions
                versions=$(_wdc_get_php_versions)
                COMPREPLY=($(compgen -W "$versions ${_wdc_php_versions[*]}" -- "$cur"))
            elif [[ "$prev" == "--preset" || "$prev" == "--preset="* ]]; then
                COMPREPLY=($(compgen -W "laravel wordpress symfony none" -- "$cur"))
            fi
            ;;
    esac
}

complete -F _wdc wdc
