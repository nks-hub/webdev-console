# DevForge PowerShell completion
#
# Installation:
#   # Option 1 — add to your PowerShell profile:
#   Add-Content $PROFILE ". C:\path\to\devforge.ps1"
#
#   # Option 2 — import directly in this session:
#   . .\devforge.ps1
#
#   # Option 3 — place in a module directory and auto-import:
#   # Copy to: $env:PSModulePath\DevForge\devforge.ps1
#   # Then add to $PROFILE: Import-Module DevForge
#
#   # Verify profile path:
#   $PROFILE
#   # Create profile if it doesn't exist:
#   New-Item -ItemType File -Path $PROFILE -Force

# ─── internal helpers ─────────────────────────────────────────────────────────

function _DevForge-GetSites {
    try {
        $json = & devforge site:list --json 2>$null | ConvertFrom-Json
        return $json.sites | Select-Object -ExpandProperty domain
    } catch {
        return @()
    }
}

function _DevForge-GetPhpVersions {
    try {
        $json = & devforge php:list --json 2>$null | ConvertFrom-Json
        return $json.versions | Select-Object -ExpandProperty version
    } catch {
        return @('5.6', '7.4', '8.0', '8.1', '8.2', '8.3', '8.4')
    }
}

function _DevForge-GetDatabases {
    try {
        $json = & devforge db:list --json 2>$null | ConvertFrom-Json
        return $json.databases | Select-Object -ExpandProperty name
    } catch {
        return @()
    }
}

# ─── completion scriptblock ───────────────────────────────────────────────────

$DevForgeCompleter = {
    param(
        $wordToComplete,
        $commandAst,
        $cursorPosition
    )

    # Parse the command line tokens (skip 'devforge' itself)
    $tokens = $commandAst.CommandElements
    $tokenCount = $tokens.Count

    # Commands list with descriptions
    $commands = @{
        'status'       = 'Show service status overview'
        'start'        = 'Start all or a named service'
        'stop'         = 'Stop a service'
        'restart'      = 'Restart a service'
        'reload'       = 'Graceful reload'
        'logs'         = 'Tail service logs'
        'doctor'       = 'Run system diagnostics'
        'site:list'    = 'List all configured sites'
        'site:create'  = 'Create a new site'
        'site:delete'  = 'Delete a site'
        'site:info'    = 'Show site details'
        'site:edit'    = 'Edit site configuration'
        'site:open'    = 'Open site in browser'
        'site:php'     = 'Change PHP version for a site'
        'site:enable'  = 'Enable a disabled site'
        'site:disable' = 'Disable a site'
        'php:list'     = 'List installed PHP versions'
        'php:install'  = 'Install a PHP version'
        'php:uninstall'= 'Remove a PHP version'
        'php:use'      = 'Set default PHP version'
        'php:info'     = 'Show PHP version details'
        'ssl:status'   = 'Show SSL certificate status'
        'ssl:install'  = 'Install and trust the CA'
        'ssl:create'   = 'Create certificate for a domain'
        'ssl:renew'    = 'Renew a certificate'
        'db:list'      = 'List all databases'
        'db:create'    = 'Create a database'
        'db:drop'      = 'Drop a database'
        'db:import'    = 'Import SQL file into database'
        'db:export'    = 'Export database to SQL file'
        'db:open'      = 'Open database in GUI client'
        'config'       = 'Show configuration'
        'config:get'   = 'Get a config value'
        'config:set'   = 'Set a config value'
        'config:edit'  = 'Open config in $EDITOR'
    }

    $services = @('apache', 'mysql', 'redis', 'mailpit', 'php-fpm',
                  'php-fpm@8.1', 'php-fpm@8.2', 'php-fpm@8.3', 'php-fpm@8.4')

    $phpAvailable = @('5.6', '7.4', '8.0', '8.1', '8.2', '8.3', '8.4')

    $configKeys = @(
        'apache.port', 'apache.ssl_port', 'apache.workers',
        'mysql.port', 'mysql.root_password',
        'php.default_version', 'php.fpm_base_port',
        'ssl.ca_path', 'ssl.cert_days',
        'dns.method', 'dns.hosts_file',
        'general.editor', 'general.browser', 'general.sites_dir'
    )

    $globalFlags = @(
        [System.Management.Automation.CompletionResult]::new('--json',     '--json',     'ParameterName', 'Output as JSON'),
        [System.Management.Automation.CompletionResult]::new('--quiet',    '--quiet',    'ParameterName', 'Suppress non-error output'),
        [System.Management.Automation.CompletionResult]::new('-q',         '-q',         'ParameterName', 'Suppress non-error output'),
        [System.Management.Automation.CompletionResult]::new('--verbose',  '--verbose',  'ParameterName', 'Verbose output'),
        [System.Management.Automation.CompletionResult]::new('-v',         '-v',         'ParameterName', 'Verbose output'),
        [System.Management.Automation.CompletionResult]::new('--no-color', '--no-color', 'ParameterName', 'Disable ANSI colors'),
        [System.Management.Automation.CompletionResult]::new('--help',     '--help',     'ParameterName', 'Show help'),
        [System.Management.Automation.CompletionResult]::new('-h',         '-h',         'ParameterName', 'Show help')
    )

    # Completing the subcommand itself (position 1)
    if ($tokenCount -le 2) {
        foreach ($cmd in $commands.Keys | Where-Object { $_ -like "$wordToComplete*" } | Sort-Object) {
            [System.Management.Automation.CompletionResult]::new(
                $cmd, $cmd, 'ParameterValue', $commands[$cmd]
            )
        }
        # Also surface global flags at top level
        if ($wordToComplete.StartsWith('-')) {
            $globalFlags | Where-Object { $_.CompletionText -like "$wordToComplete*" }
        }
        return
    }

    $subCommand = $tokens[1].Value

    # Global flags (available for all commands)
    if ($wordToComplete.StartsWith('-')) {
        $flagResults = $globalFlags | Where-Object { $_.CompletionText -like "$wordToComplete*" }

        # Command-specific flags
        $cmdFlags = switch ($subCommand) {
            'site:list'    { @('--all', '--json') }
            'site:create'  { @('--php=', '--docroot=', '--ssl', '--no-ssl', '--preset=', '--db=') }
            'site:delete'  { @('--force', '-f', '--keep-files') }
            'php:install'  { @('--set-default') }
            'php:uninstall'{ @('--force', '-f') }
            'db:drop'      { @('--force', '-f') }
            'db:import'    { @('--drop-first', '--no-create-db') }
            'db:export'    { @('--output=', '--compress', '--no-routines') }
            'doctor'       { @('--fix', '--json') }
            'start'        { @('--all') }
            'stop'         { @('--all', '--force', '-f') }
            'restart'      { @('--all') }
            'logs'         { @('--follow', '-f', '--lines=') }
            default        { @() }
        }

        $flagResults
        foreach ($flag in $cmdFlags | Where-Object { $_ -like "$wordToComplete*" }) {
            [System.Management.Automation.CompletionResult]::new(
                $flag, $flag, 'ParameterName', ''
            )
        }
        return
    }

    # Argument position (number of non-flag tokens after the subcommand)
    $argPos = 0
    for ($i = 2; $i -lt $tokenCount - 1; $i++) {
        if (-not $tokens[$i].Value.StartsWith('-')) { $argPos++ }
    }

    switch ($subCommand) {
        { $_ -in 'start', 'stop', 'restart', 'reload', 'logs' } {
            if ($argPos -eq 0) {
                foreach ($svc in $services | Where-Object { $_ -like "$wordToComplete*" }) {
                    [System.Management.Automation.CompletionResult]::new(
                        $svc, $svc, 'ParameterValue', "Service: $svc"
                    )
                }
            }
        }

        { $_ -in 'site:delete', 'site:info', 'site:edit', 'site:open', 'site:enable', 'site:disable' } {
            if ($argPos -eq 0) {
                foreach ($site in (_DevForge-GetSites) | Where-Object { $_ -like "$wordToComplete*" }) {
                    [System.Management.Automation.CompletionResult]::new(
                        $site, $site, 'ParameterValue', "Site: $site"
                    )
                }
            }
        }

        'site:php' {
            if ($argPos -eq 0) {
                foreach ($site in (_DevForge-GetSites) | Where-Object { $_ -like "$wordToComplete*" }) {
                    [System.Management.Automation.CompletionResult]::new(
                        $site, $site, 'ParameterValue', "Site: $site"
                    )
                }
            } elseif ($argPos -eq 1) {
                foreach ($ver in (_DevForge-GetPhpVersions) | Where-Object { $_ -like "$wordToComplete*" }) {
                    [System.Management.Automation.CompletionResult]::new(
                        $ver, $ver, 'ParameterValue', "PHP $ver"
                    )
                }
            }
        }

        'php:install' {
            if ($argPos -eq 0) {
                foreach ($ver in $phpAvailable | Where-Object { $_ -like "$wordToComplete*" }) {
                    [System.Management.Automation.CompletionResult]::new(
                        $ver, $ver, 'ParameterValue', "PHP $ver"
                    )
                }
            }
        }

        { $_ -in 'php:uninstall', 'php:use', 'php:info' } {
            if ($argPos -eq 0) {
                foreach ($ver in (_DevForge-GetPhpVersions) | Where-Object { $_ -like "$wordToComplete*" }) {
                    [System.Management.Automation.CompletionResult]::new(
                        $ver, $ver, 'ParameterValue', "PHP $ver"
                    )
                }
            }
        }

        { $_ -in 'ssl:create', 'ssl:renew' } {
            if ($argPos -eq 0) {
                foreach ($site in (_DevForge-GetSites) | Where-Object { $_ -like "$wordToComplete*" }) {
                    [System.Management.Automation.CompletionResult]::new(
                        $site, $site, 'ParameterValue', "Site: $site"
                    )
                }
            }
        }

        { $_ -in 'db:drop', 'db:export', 'db:open' } {
            if ($argPos -eq 0) {
                foreach ($db in (_DevForge-GetDatabases) | Where-Object { $_ -like "$wordToComplete*" }) {
                    [System.Management.Automation.CompletionResult]::new(
                        $db, $db, 'ParameterValue', "Database: $db"
                    )
                }
            }
        }

        'db:import' {
            if ($argPos -eq 0) {
                foreach ($db in (_DevForge-GetDatabases) | Where-Object { $_ -like "$wordToComplete*" }) {
                    [System.Management.Automation.CompletionResult]::new(
                        $db, $db, 'ParameterValue', "Database: $db"
                    )
                }
            } elseif ($argPos -eq 1) {
                # Complete .sql and .sql.gz files
                Get-ChildItem -Path "." -Filter "*.sql*" -ErrorAction SilentlyContinue |
                    Where-Object { $_.Name -like "$wordToComplete*" } |
                    ForEach-Object {
                        [System.Management.Automation.CompletionResult]::new(
                            $_.FullName, $_.Name, 'ProviderItem', $_.Name
                        )
                    }
            }
        }

        { $_ -in 'config:get', 'config:set' } {
            if ($argPos -eq 0) {
                foreach ($key in $configKeys | Where-Object { $_ -like "$wordToComplete*" }) {
                    [System.Management.Automation.CompletionResult]::new(
                        $key, $key, 'ParameterValue', "Config: $key"
                    )
                }
            }
        }

        'site:create' {
            # Handle --php= and --preset= inline value completion
            if ($wordToComplete -like '--php=*') {
                $partial = $wordToComplete.Substring('--php='.Length)
                foreach ($ver in (_DevForge-GetPhpVersions) | Where-Object { $_ -like "$partial*" }) {
                    [System.Management.Automation.CompletionResult]::new(
                        "--php=$ver", "--php=$ver", 'ParameterValue', "PHP $ver"
                    )
                }
            } elseif ($wordToComplete -like '--preset=*') {
                $partial = $wordToComplete.Substring('--preset='.Length)
                foreach ($preset in @('laravel', 'wordpress', 'symfony', 'none') | Where-Object { $_ -like "$partial*" }) {
                    [System.Management.Automation.CompletionResult]::new(
                        "--preset=$preset", "--preset=$preset", 'ParameterValue', "Preset: $preset"
                    )
                }
            }
        }
    }
}

# Register the completer
Register-ArgumentCompleter -Native -CommandName devforge -ScriptBlock $DevForgeCompleter
