#Requires -Version 5.1
<#
.SYNOPSIS
    NKS WebDev Console hosts file management tool.

.DESCRIPTION
    Manages entries in C:\Windows\System32\drivers\etc\hosts for local development domains.
    All managed entries are placed within a clearly marked block to avoid touching user content.

.PARAMETER Action
    The operation to perform: add, remove, list, check, backup, restore, clean, port-check

.PARAMETER Domain
    The primary domain name to add/remove/check.

.PARAMETER IP
    IP address for the domain entry (default: 127.0.0.1).

.PARAMETER Aliases
    Comma-separated additional domain aliases that map to the same IP.

.PARAMETER Port
    Port number to check for port-check action.

.PARAMETER BackupFile
    Path to a backup file to restore from (used with restore action).

.EXAMPLE
    .\hosts_manager.ps1 -Action add -Domain "myapp.test"
    .\hosts_manager.ps1 -Action add -Domain "myapp.test" -Aliases "www.myapp.test,api.myapp.test"
    .\hosts_manager.ps1 -Action remove -Domain "myapp.test"
    .\hosts_manager.ps1 -Action list
    .\hosts_manager.ps1 -Action check -Domain "myapp.test"
    .\hosts_manager.ps1 -Action port-check -Port 80
    .\hosts_manager.ps1 -Action backup
    .\hosts_manager.ps1 -Action clean
    .\hosts_manager.ps1 -Action restore -BackupFile "C:\NKS WebDev Console\backups\hosts.20240101-120000.bak"
#>

[CmdletBinding(SupportsShouldProcess)]
param(
    [Parameter(Mandatory = $true)]
    [ValidateSet('add', 'remove', 'list', 'check', 'backup', 'restore', 'clean', 'port-check')]
    [string]$Action,

    [string]$Domain,
    [string]$IP = '127.0.0.1',
    [string]$Aliases,
    [int]$Port,
    [string]$BackupFile
)

Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'

# ---------------------------------------------------------------------------
# Constants
# ---------------------------------------------------------------------------
$HOSTS_FILE      = 'C:\Windows\System32\drivers\etc\hosts'
$BACKUP_DIR      = 'C:\NKS WebDev Console\backups'
$BLOCK_START     = '# >>> NKS WebDev Console Managed - DO NOT EDIT <<<'
$BLOCK_END       = '# <<< NKS WebDev Console Managed >>>'
$DOMAIN_REGEX    = '^(?:[a-zA-Z0-9](?:[a-zA-Z0-9\-]{0,61}[a-zA-Z0-9])?\.)+[a-zA-Z]{2,}$'

# ---------------------------------------------------------------------------
# Output helpers
# ---------------------------------------------------------------------------
function Write-Success { param([string]$Msg) Write-Host "[OK]  $Msg" -ForegroundColor Green }
function Write-Failure { param([string]$Msg) Write-Host "[ERR] $Msg" -ForegroundColor Red }
function Write-Info    { param([string]$Msg) Write-Host "[..] $Msg"  -ForegroundColor Cyan }
function Write-Warn    { param([string]$Msg) Write-Host "[!!] $Msg"  -ForegroundColor Yellow }

# ---------------------------------------------------------------------------
# Elevation helpers
# ---------------------------------------------------------------------------
function Test-IsAdmin {
    $identity  = [Security.Principal.WindowsIdentity]::GetCurrent()
    $principal = New-Object Security.Principal.WindowsPrincipal($identity)
    return $principal.IsInRole([Security.Principal.WindowsBuiltInRole]::Administrator)
}

function Request-Elevation {
    Write-Warn "Administrator privileges are required. Requesting elevation..."
    $scriptPath = $PSCommandPath
    $argsList   = "-NoProfile -ExecutionPolicy Bypass -File `"$scriptPath`""

    # Rebuild original parameters for the elevated process
    foreach ($key in $PSBoundParameters.Keys) {
        $val = $PSBoundParameters[$key]
        if ($val -is [switch]) {
            if ($val) { $argsList += " -$key" }
        } else {
            $argsList += " -$key `"$val`""
        }
    }

    try {
        $proc = Start-Process -FilePath 'powershell.exe' `
                              -ArgumentList $argsList `
                              -Verb RunAs `
                              -PassThru `
                              -Wait
        exit $proc.ExitCode
    } catch {
        Write-Failure "Elevation was denied or failed: $_"
        exit 1
    }
}

# ---------------------------------------------------------------------------
# Domain validation
# ---------------------------------------------------------------------------
function Test-DomainValid {
    param([string]$Name)
    return $Name -match $DOMAIN_REGEX
}

# ---------------------------------------------------------------------------
# Hosts file I/O — reads the raw file once, works on in-memory arrays
# ---------------------------------------------------------------------------
function Read-HostsFile {
    if (-not (Test-Path $HOSTS_FILE)) {
        Write-Failure "Hosts file not found: $HOSTS_FILE"
        exit 1
    }
    # Use Get-Content so line endings are normalised
    return Get-Content -Path $HOSTS_FILE -Encoding UTF8
}

function Write-HostsFile {
    param([string[]]$Lines)

    # Atomic write: write to a temp file, then replace
    $tmp = [System.IO.Path]::GetTempFileName()
    try {
        [System.IO.File]::WriteAllLines($tmp, $Lines, [System.Text.Encoding]::UTF8)
        Copy-Item -Path $tmp -Destination $HOSTS_FILE -Force
    } finally {
        if (Test-Path $tmp) { Remove-Item $tmp -Force }
    }
}

# ---------------------------------------------------------------------------
# Managed block helpers
# ---------------------------------------------------------------------------
function Get-ManagedBlockBounds {
    param([string[]]$Lines)

    $start = -1
    $end   = -1
    for ($i = 0; $i -lt $Lines.Count; $i++) {
        if ($Lines[$i].Trim() -eq $BLOCK_START) { $start = $i }
        if ($Lines[$i].Trim() -eq $BLOCK_END)   { $end   = $i }
    }
    return $start, $end
}

function Get-ManagedEntries {
    param([string[]]$Lines)

    $start, $end = Get-ManagedBlockBounds $Lines
    if ($start -lt 0 -or $end -lt 0 -or $end -le $start) {
        return @()
    }

    $entries = @()
    for ($i = $start + 1; $i -lt $end; $i++) {
        $line = $Lines[$i].Trim()
        if ($line -ne '' -and -not $line.StartsWith('#')) {
            $entries += $line
        }
    }
    return $entries
}

# Returns lines with a fresh managed block inserted/replaced
function Set-ManagedBlock {
    param(
        [string[]]$Lines,
        [string[]]$BlockContent   # lines to place inside the block (no markers)
    )

    $start, $end = Get-ManagedBlockBounds $Lines

    $newBlock  = @($BLOCK_START) + $BlockContent + @($BLOCK_END)
    $newLines  = [System.Collections.Generic.List[string]]::new()

    if ($start -lt 0) {
        # No block yet — append at end with a blank separator
        $newLines.AddRange([string[]]$Lines)
        $newLines.Add('')
        $newLines.AddRange([string[]]$newBlock)
    } else {
        # Replace existing block
        for ($i = 0; $i -lt $Lines.Count; $i++) {
            if ($i -eq $start) {
                $newLines.AddRange([string[]]$newBlock)
                $i = $end   # skip old block lines
            } else {
                $newLines.Add($Lines[$i])
            }
        }
    }

    return $newLines.ToArray()
}

# ---------------------------------------------------------------------------
# Backup / Restore
# ---------------------------------------------------------------------------
function Invoke-Backup {
    param([switch]$Silent)

    if (-not (Test-Path $BACKUP_DIR)) {
        New-Item -Path $BACKUP_DIR -ItemType Directory -Force | Out-Null
    }

    $timestamp  = (Get-Date -Format 'yyyyMMdd-HHmmss')
    $backupPath = Join-Path $BACKUP_DIR "hosts.$timestamp.bak"

    Copy-Item -Path $HOSTS_FILE -Destination $backupPath -Force

    if (-not $Silent) {
        Write-Success "Backup created: $backupPath"
    }
    return $backupPath
}

function Invoke-Restore {
    param([string]$BackupPath)

    if (-not $BackupPath) {
        # Find most recent backup
        $latest = Get-ChildItem -Path $BACKUP_DIR -Filter 'hosts.*.bak' -ErrorAction SilentlyContinue |
                  Sort-Object LastWriteTime -Descending |
                  Select-Object -First 1

        if (-not $latest) {
            Write-Failure "No backups found in $BACKUP_DIR"
            exit 1
        }
        $BackupPath = $latest.FullName
        Write-Info "Using most recent backup: $BackupPath"
    }

    if (-not (Test-Path $BackupPath)) {
        Write-Failure "Backup file not found: $BackupPath"
        exit 1
    }

    Invoke-Backup -Silent | Out-Null   # back up current state before restore
    Copy-Item -Path $BackupPath -Destination $HOSTS_FILE -Force
    Write-Success "Restored from: $BackupPath"
}

# ---------------------------------------------------------------------------
# Add
# ---------------------------------------------------------------------------
function Invoke-Add {
    param(
        [string]$AddDomain,
        [string]$AddIP,
        [string]$AddAliases
    )

    if (-not $AddDomain) {
        Write-Failure "Domain is required for 'add' action."
        exit 1
    }

    if (-not (Test-DomainValid $AddDomain)) {
        Write-Failure "Invalid domain format: '$AddDomain'"
        exit 1
    }

    # Build list of all names for this entry
    $names = [System.Collections.Generic.List[string]]::new()
    $names.Add($AddDomain)

    if ($AddAliases) {
        foreach ($alias in ($AddAliases -split ',')) {
            $a = $alias.Trim()
            if ($a -ne '') {
                if (-not (Test-DomainValid $a)) {
                    Write-Failure "Invalid alias format: '$a'"
                    exit 1
                }
                $names.Add($a)
            }
        }
    }

    $lines = Read-HostsFile

    # Check idempotency — if every name already present, no-op
    $existingEntries = Get-ManagedEntries $lines
    $allExist        = $true
    foreach ($name in $names) {
        $found = $false
        foreach ($entry in $existingEntries) {
            $parts = $entry -split '\s+'
            if ($parts -contains $name) { $found = $true; break }
        }
        if (-not $found) { $allExist = $false; break }
    }

    if ($allExist) {
        Write-Info "'$AddDomain' is already present in NKS WebDev Console managed block. No changes made."
        return
    }

    Invoke-Backup -Silent | Out-Null

    # Remove any pre-existing lines for the same names (handles partial overlaps)
    $blockContent = [System.Collections.Generic.List[string]]::new()
    foreach ($entry in $existingEntries) {
        $parts   = $entry -split '\s+'
        $overlap = $false
        foreach ($name in $names) {
            if ($parts -contains $name) { $overlap = $true; break }
        }
        if (-not $overlap) {
            $blockContent.Add($entry)
        }
    }

    # Append the new combined entry: IP  domain  alias1  alias2 ...
    $newLine = "$AddIP`t" + ($names -join "`t")
    $blockContent.Add($newLine)

    $lines = Set-ManagedBlock -Lines $lines -BlockContent $blockContent.ToArray()
    Write-HostsFile $lines

    Write-Success "Added: $newLine"
    Flush-DnsCache
}

# ---------------------------------------------------------------------------
# Remove
# ---------------------------------------------------------------------------
function Invoke-Remove {
    param([string]$RemoveDomain)

    if (-not $RemoveDomain) {
        Write-Failure "Domain is required for 'remove' action."
        exit 1
    }

    $lines   = Read-HostsFile
    $entries = Get-ManagedEntries $lines

    $kept    = [System.Collections.Generic.List[string]]::new()
    $removed = $false

    foreach ($entry in $entries) {
        $parts = $entry -split '\s+'
        if ($parts -contains $RemoveDomain) {
            $removed = $true
        } else {
            $kept.Add($entry)
        }
    }

    if (-not $removed) {
        Write-Warn "'$RemoveDomain' was not found in NKS WebDev Console managed block."
        return
    }

    Invoke-Backup -Silent | Out-Null
    $lines = Set-ManagedBlock -Lines $lines -BlockContent $kept.ToArray()
    Write-HostsFile $lines

    Write-Success "Removed entries for '$RemoveDomain'."
    Flush-DnsCache
}

# ---------------------------------------------------------------------------
# List
# ---------------------------------------------------------------------------
function Invoke-List {
    $lines   = Read-HostsFile
    $entries = Get-ManagedEntries $lines

    if ($entries.Count -eq 0) {
        Write-Info "No NKS WebDev Console-managed entries found."
        return
    }

    Write-Host ""
    Write-Host "  NKS WebDev Console Managed Hosts Entries" -ForegroundColor Cyan
    Write-Host "  --------------------------------" -ForegroundColor DarkGray
    foreach ($entry in $entries) {
        $parts = $entry -split '\s+'
        $ip    = $parts[0]
        $hosts = $parts[1..($parts.Count - 1)] -join '  '
        Write-Host ("  {0,-16} {1}" -f $ip, $hosts) -ForegroundColor White
    }
    Write-Host ""
}

# ---------------------------------------------------------------------------
# Check DNS resolution
# ---------------------------------------------------------------------------
function Invoke-Check {
    param([string]$CheckDomain)

    if (-not $CheckDomain) {
        Write-Failure "Domain is required for 'check' action."
        exit 1
    }

    Write-Info "Checking DNS resolution for '$CheckDomain'..."

    try {
        $result = Resolve-DnsName -Name $CheckDomain -ErrorAction Stop
        $ips    = ($result | Where-Object { $_.Type -eq 'A' } | Select-Object -ExpandProperty IPAddress) -join ', '
        Write-Success "Resolves to: $ips"
    } catch {
        Write-Failure "DNS resolution failed for '$CheckDomain': $_"
    }

    # Ping check
    Write-Info "Ping test..."
    $ping = Test-Connection -ComputerName $CheckDomain -Count 2 -Quiet -ErrorAction SilentlyContinue
    if ($ping) {
        Write-Success "Ping: reachable"
    } else {
        Write-Warn "Ping: no response (host may be down or ICMP blocked)"
    }
}

# ---------------------------------------------------------------------------
# Clean
# ---------------------------------------------------------------------------
function Invoke-Clean {
    $lines   = Read-HostsFile
    $entries = Get-ManagedEntries $lines

    if ($entries.Count -eq 0) {
        Write-Info "No NKS WebDev Console-managed entries to remove."
        return
    }

    if (-not $PSCmdlet.ShouldProcess("all $($entries.Count) NKS WebDev Console-managed entries", "Remove")) {
        return
    }

    Invoke-Backup -Silent | Out-Null
    $lines = Set-ManagedBlock -Lines $lines -BlockContent @()
    Write-HostsFile $lines

    Write-Success "Removed all $($entries.Count) NKS WebDev Console-managed entries."
    Flush-DnsCache
}

# ---------------------------------------------------------------------------
# Port check
# ---------------------------------------------------------------------------
function Invoke-PortCheck {
    param([int]$CheckPort)

    if ($CheckPort -le 0) {
        Write-Failure "A valid port number is required for 'port-check' action."
        exit 1
    }

    Write-Info "Checking port $CheckPort..."

    $alternatives = @{ 80 = 8080; 443 = 8443; 3306 = 3307; 5432 = 5433 }

    # Parse netstat output
    $netstatLines = netstat -ano 2>$null | Where-Object { $_ -match "LISTENING" }
    $portInUse    = $false
    $ownerPid     = $null

    foreach ($netLine in $netstatLines) {
        if ($netLine -match ":$CheckPort\s+.*LISTENING\s+(\d+)") {
            $portInUse = $true
            $ownerPid  = $Matches[1]
            break
        }
    }

    if (-not $portInUse) {
        Write-Success "Port $CheckPort is available."
        return
    }

    # Resolve process name from PID
    $processName = 'unknown'
    if ($ownerPid) {
        try {
            $proc = Get-Process -Id $ownerPid -ErrorAction SilentlyContinue
            if ($proc) { $processName = $proc.ProcessName }
        } catch { }
    }

    Write-Warn "Port $CheckPort is in use by PID $ownerPid ($processName)"

    if ($alternatives.ContainsKey($CheckPort)) {
        $alt = $alternatives[$CheckPort]
        Write-Info "Suggested alternative: $alt"

        # Check if alternative is also in use
        $altInUse = $netstatLines | Where-Object { $_ -match ":$alt\s+.*LISTENING" }
        if ($altInUse) {
            Write-Warn "Alternative port $alt is also in use."
        } else {
            Write-Success "Alternative port $alt is available."
        }
    }
}

# ---------------------------------------------------------------------------
# DNS cache flush
# ---------------------------------------------------------------------------
function Flush-DnsCache {
    try {
        Clear-DnsClientCache -ErrorAction SilentlyContinue
        Write-Info "DNS cache flushed."
    } catch {
        Write-Warn "Could not flush DNS cache: $_"
    }
}

# ---------------------------------------------------------------------------
# Entry point — require elevation for mutating actions
# ---------------------------------------------------------------------------
$mutatingActions = @('add', 'remove', 'clean', 'restore')
$needsAdmin      = $mutatingActions -contains $Action

if ($needsAdmin -and -not (Test-IsAdmin)) {
    Request-Elevation
}

switch ($Action) {
    'add'        { Invoke-Add    -AddDomain $Domain -AddIP $IP -AddAliases $Aliases }
    'remove'     { Invoke-Remove -RemoveDomain $Domain }
    'list'       { Invoke-List }
    'check'      { Invoke-Check  -CheckDomain $Domain }
    'backup'     { Invoke-Backup }
    'restore'    { Invoke-Restore -BackupPath $BackupFile }
    'clean'      { Invoke-Clean }
    'port-check' { Invoke-PortCheck -CheckPort $Port }
}
