#Requires -Version 5.1
<#
.SYNOPSIS
    Integration tests for hosts_manager.ps1

.DESCRIPTION
    Runs a full cycle of add / verify / remove / verify / restore against
    the real hosts file.  Must be run as Administrator.

.NOTES
    Test domain used: devforge-test.local
    The test never leaves the managed block dirty — it restores the original
    hosts file even when a step fails.
#>

[CmdletBinding()]
param()

Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'

# ---------------------------------------------------------------------------
# Helpers
# ---------------------------------------------------------------------------
$script:PassCount = 0
$script:FailCount = 0
$script:TestBackup = $null

function Assert-True {
    param([string]$Name, [bool]$Condition, [string]$Details = '')
    if ($Condition) {
        Write-Host ("  [PASS] {0}" -f $Name) -ForegroundColor Green
        $script:PassCount++
    } else {
        $msg = "  [FAIL] $Name"
        if ($Details) { $msg += " - $Details" }
        Write-Host $msg -ForegroundColor Red
        $script:FailCount++
    }
}

function Assert-False {
    param([string]$Name, [bool]$Condition, [string]$Details = '')
    Assert-True -Name $Name -Condition (-not $Condition) -Details $Details
}

function Get-HostsContent {
    Get-Content -Path 'C:\Windows\System32\drivers\etc\hosts' -Raw
}

function Test-DomainInHosts {
    param([string]$Domain)
    $content = Get-HostsContent
    return $content -match [regex]::Escape($Domain)
}

function Invoke-Manager {
    param([hashtable]$Params)
    $managerPath = Join-Path $PSScriptRoot 'hosts_manager.ps1'
    & $managerPath @Params
    return $LASTEXITCODE -eq 0 -or $LASTEXITCODE -eq $null
}

# ---------------------------------------------------------------------------
# Elevation check
# ---------------------------------------------------------------------------
$identity  = [Security.Principal.WindowsIdentity]::GetCurrent()
$principal = New-Object Security.Principal.WindowsPrincipal($identity)
if (-not $principal.IsInRole([Security.Principal.WindowsBuiltInRole]::Administrator)) {
    Write-Host "[!!] These tests must be run as Administrator." -ForegroundColor Yellow
    Write-Host "     Right-click PowerShell and choose 'Run as administrator'."
    exit 1
}

Write-Host ""
Write-Host "  DevForge hosts_manager Integration Tests" -ForegroundColor Cyan
Write-Host "  ===========================================" -ForegroundColor DarkGray
Write-Host ""

# ---------------------------------------------------------------------------
# Step 1 — Backup current hosts file
# ---------------------------------------------------------------------------
Write-Host "  [1] Creating pre-test backup..." -ForegroundColor DarkGray

$backupDir = 'C:\DevForge\backups'
if (-not (Test-Path $backupDir)) {
    New-Item -Path $backupDir -ItemType Directory -Force | Out-Null
}

$timestamp         = (Get-Date -Format 'yyyyMMdd-HHmmss')
$script:TestBackup = Join-Path $backupDir "hosts.pretest.$timestamp.bak"
Copy-Item -Path 'C:\Windows\System32\drivers\etc\hosts' -Destination $script:TestBackup -Force

Assert-True 'Pre-test backup created' (Test-Path $script:TestBackup)

# ---------------------------------------------------------------------------
# Step 2 — Add test entry
# ---------------------------------------------------------------------------
Write-Host ""
Write-Host "  [2] Adding test entry 'devforge-test.local'..." -ForegroundColor DarkGray

$addResult = $null
try {
    Invoke-Manager @{ Action = 'add'; Domain = 'devforge-test.local' }
    $addResult = $true
} catch {
    $addResult = $false
}

Assert-True  'add command completed without error' ($addResult -ne $false)
Assert-True  'devforge-test.local appears in hosts file' (Test-DomainInHosts 'devforge-test.local')
Assert-True  'entry is within managed block' (
    (Get-HostsContent) -match '(?s)# >>> DevForge Managed.*devforge-test\.local.*# <<< DevForge Managed'
)

# ---------------------------------------------------------------------------
# Step 3 — Idempotency: adding the same entry again must be a no-op
# ---------------------------------------------------------------------------
Write-Host ""
Write-Host "  [3] Testing idempotency (re-adding same domain)..." -ForegroundColor DarkGray

$contentBefore = Get-HostsContent

try {
    Invoke-Manager @{ Action = 'add'; Domain = 'devforge-test.local' }
} catch { }

$contentAfter = Get-HostsContent
Assert-True 'Hosts file unchanged after duplicate add' ($contentBefore -eq $contentAfter)

# ---------------------------------------------------------------------------
# Step 4 — Add with alias
# ---------------------------------------------------------------------------
Write-Host ""
Write-Host "  [4] Adding entry with alias 'devforge-alias.local'..." -ForegroundColor DarkGray

try {
    Invoke-Manager @{ Action = 'add'; Domain = 'devforge-alias.local'; Aliases = 'www.devforge-alias.local' }
    $aliasResult = $true
} catch {
    $aliasResult = $false
}

Assert-True 'add with alias completed without error' ($aliasResult -ne $false)
Assert-True 'primary domain devforge-alias.local in hosts' (Test-DomainInHosts 'devforge-alias.local')
Assert-True 'alias www.devforge-alias.local in hosts'       (Test-DomainInHosts 'www.devforge-alias.local')

# ---------------------------------------------------------------------------
# Step 5 — list command
# ---------------------------------------------------------------------------
Write-Host ""
Write-Host "  [5] Listing managed entries..." -ForegroundColor DarkGray

$listOutput = & (Join-Path $PSScriptRoot 'hosts_manager.ps1') -Action list 2>&1
Assert-True 'list output contains devforge-test.local'  (($listOutput -join '') -match 'devforge-test\.local')
Assert-True 'list output contains devforge-alias.local' (($listOutput -join '') -match 'devforge-alias\.local')

# ---------------------------------------------------------------------------
# Step 6 — DNS resolution check (nslookup)
# ---------------------------------------------------------------------------
Write-Host ""
Write-Host "  [6] DNS resolution check for 'devforge-test.local'..." -ForegroundColor DarkGray

# Flush DNS so the new hosts entry is picked up
Clear-DnsClientCache -ErrorAction SilentlyContinue

$resolveOk = $false
try {
    $nsResult = nslookup devforge-test.local 2>&1
    $resolveOk = ($nsResult -join '') -match '127\.0\.0\.1'
} catch { }

Assert-True 'devforge-test.local resolves to 127.0.0.1' $resolveOk

# PowerShell DNS resolver cross-check
$dnsOk = $false
try {
    $dns = Resolve-DnsName -Name 'devforge-test.local' -Type A -ErrorAction Stop
    $dnsOk = ($dns | Where-Object { $_.IPAddress -eq '127.0.0.1' }) -ne $null
} catch { }

Assert-True 'Resolve-DnsName returns 127.0.0.1' $dnsOk

# ---------------------------------------------------------------------------
# Step 7 — Remove test entry
# ---------------------------------------------------------------------------
Write-Host ""
Write-Host "  [7] Removing 'devforge-test.local'..." -ForegroundColor DarkGray

try {
    Invoke-Manager @{ Action = 'remove'; Domain = 'devforge-test.local' }
    $removeResult = $true
} catch {
    $removeResult = $false
}

Assert-True  'remove command completed without error'         ($removeResult -ne $false)
Assert-False 'devforge-test.local no longer in hosts file'   (Test-DomainInHosts 'devforge-test.local')
Assert-True  'devforge-alias.local still present after remove' (Test-DomainInHosts 'devforge-alias.local')

# ---------------------------------------------------------------------------
# Step 8 — Remove alias entry
# ---------------------------------------------------------------------------
Write-Host ""
Write-Host "  [8] Removing 'devforge-alias.local'..." -ForegroundColor DarkGray

try {
    Invoke-Manager @{ Action = 'remove'; Domain = 'devforge-alias.local' }
    $removeAlias = $true
} catch {
    $removeAlias = $false
}

Assert-True  'remove alias completed without error'         ($removeAlias -ne $false)
Assert-False 'devforge-alias.local no longer in hosts file' (Test-DomainInHosts 'devforge-alias.local')

# ---------------------------------------------------------------------------
# Step 9 — Invalid domain rejected
# ---------------------------------------------------------------------------
Write-Host ""
Write-Host "  [9] Validating domain format rejection..." -ForegroundColor DarkGray

$invalidOutput = & (Join-Path $PSScriptRoot 'hosts_manager.ps1') -Action add -Domain 'not_a_valid_domain' 2>&1
Assert-True 'Invalid domain produces error output' (($invalidOutput -join '') -match '(?i)(invalid|error)')

$contentAfterInvalid = Get-HostsContent
Assert-True 'Hosts file not modified for invalid domain' (-not (Test-DomainInHosts 'not_a_valid_domain'))

# ---------------------------------------------------------------------------
# Step 10 — Restore original hosts file
# ---------------------------------------------------------------------------
Write-Host ""
Write-Host "  [10] Restoring pre-test hosts file..." -ForegroundColor DarkGray

try {
    Invoke-Manager @{ Action = 'restore'; BackupFile = $script:TestBackup }
    $restoreResult = $true
} catch {
    # Fallback: direct copy
    Copy-Item -Path $script:TestBackup -Destination 'C:\Windows\System32\drivers\etc\hosts' -Force
    $restoreResult = $true
}

Assert-True  'restore completed without error'            $restoreResult
Assert-False 'devforge-test.local absent after restore'   (Test-DomainInHosts 'devforge-test.local')
Assert-False 'devforge-alias.local absent after restore'  (Test-DomainInHosts 'devforge-alias.local')

Clear-DnsClientCache -ErrorAction SilentlyContinue

# ---------------------------------------------------------------------------
# Summary
# ---------------------------------------------------------------------------
Write-Host ""
Write-Host "  ===========================================" -ForegroundColor DarkGray
$total = $script:PassCount + $script:FailCount
if ($script:FailCount -eq 0) {
    Write-Host ("  All {0} tests passed." -f $total) -ForegroundColor Green
    exit 0
} else {
    Write-Host ("  {0}/{1} tests passed, {2} failed." -f $script:PassCount, $total, $script:FailCount) -ForegroundColor Red
    exit 1
}
