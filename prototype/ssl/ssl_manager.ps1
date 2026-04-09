<#
.SYNOPSIS
    DevForge SSL Certificate Manager - manages local dev SSL certificates via mkcert.

.DESCRIPTION
    Automates SSL certificate lifecycle for local development domains (*.test).
    Uses mkcert to generate locally-trusted certificates and stores them
    in a structured directory under ~/.devforge/ssl/sites/{domain}/.

.PARAMETER Action
    One of: install-ca, generate, list, verify, revoke

.PARAMETER Domain
    Primary domain name (e.g. "myapp.test")

.PARAMETER Aliases
    Additional SANs as comma-separated list (e.g. "www.myapp.test","*.myapp.test")

.EXAMPLE
    .\ssl_manager.ps1 -Action install-ca
    .\ssl_manager.ps1 -Action generate -Domain "myapp.test" -Aliases "www.myapp.test","*.myapp.test"
    .\ssl_manager.ps1 -Action list
    .\ssl_manager.ps1 -Action verify -Domain "myapp.test"
    .\ssl_manager.ps1 -Action revoke -Domain "myapp.test"
#>

[CmdletBinding()]
param(
    [Parameter(Mandatory = $true)]
    [ValidateSet("install-ca", "generate", "list", "verify", "revoke")]
    [string]$Action,

    [Parameter(Mandatory = $false)]
    [string]$Domain,

    [Parameter(Mandatory = $false)]
    [string[]]$Aliases
)

# -------------------------------------------------------------------
# Configuration
# -------------------------------------------------------------------
# Note: We use "Continue" because mkcert and openssl write informational
# output to stderr. PowerShell's "Stop" mode treats ANY stderr from native
# commands as a terminating error, which breaks normal mkcert operations.
$ErrorActionPreference = "Continue"

$MkcertPath = "C:\work\mkcert.exe"
$OpenSSLPath = "openssl"  # use system PATH; fallback below
$SslBaseDir  = Join-Path $env:USERPROFILE ".devforge\ssl\sites"
$MetadataDir = Join-Path $env:USERPROFILE ".devforge\ssl\metadata"

# Prefer Git-for-Windows openssl if system one is missing
if (-not (Get-Command $OpenSSLPath -ErrorAction SilentlyContinue)) {
    $gitOpenssl = "C:\Program Files\Git\mingw64\bin\openssl.exe"
    if (Test-Path $gitOpenssl) { $OpenSSLPath = $gitOpenssl }
    else {
        $mampOpenssl = "C:\MAMP\bin\apache\bin\openssl.exe"
        if (Test-Path $mampOpenssl) { $OpenSSLPath = $mampOpenssl }
    }
}

# -------------------------------------------------------------------
# Helpers
# -------------------------------------------------------------------
function Write-Status([string]$Message) {
    Write-Host "[DevForge SSL] " -ForegroundColor Cyan -NoNewline
    Write-Host $Message
}

function Write-Ok([string]$Message) {
    Write-Host "[OK] " -ForegroundColor Green -NoNewline
    Write-Host $Message
}

function Write-Err([string]$Message) {
    Write-Host "[ERROR] " -ForegroundColor Red -NoNewline
    Write-Host $Message
}

function Ensure-Directory([string]$Path) {
    if (-not (Test-Path $Path)) {
        New-Item -ItemType Directory -Path $Path -Force | Out-Null
    }
}

function Get-DomainDir([string]$DomainName) {
    return Join-Path $SslBaseDir $DomainName
}

function Get-CertPath([string]$DomainName) {
    return Join-Path (Get-DomainDir $DomainName) "cert.pem"
}

function Get-KeyPath([string]$DomainName) {
    return Join-Path (Get-DomainDir $DomainName) "key.pem"
}

function Get-MetadataPath([string]$DomainName) {
    return Join-Path $MetadataDir "$DomainName.json"
}

function Test-MkcertInstalled {
    if (-not (Test-Path $MkcertPath)) {
        Write-Err "mkcert not found at $MkcertPath"
        exit 1
    }
}

function Get-CertDetails([string]$CertFile) {
    if (-not (Test-Path $CertFile)) { return $null }

    $text = & $OpenSSLPath x509 -in $CertFile -text -noout 2>&1
    if ($LASTEXITCODE -ne 0) { return $null }

    $result = @{
        Raw = ($text -join "`n")
    }

    # Extract Subject CN
    $subjectLine = $text | Where-Object { $_ -match "Subject:" } | Select-Object -First 1
    if ($subjectLine -match "CN\s*=\s*([^,/]+)") {
        $result.CN = $Matches[1].Trim()
    }

    # Extract Issuer
    $issuerLine = $text | Where-Object { $_ -match "Issuer:" } | Select-Object -First 1
    if ($issuerLine) {
        $result.Issuer = ($issuerLine -replace "^\s*Issuer:\s*", "").Trim()
    }

    # Extract validity dates
    $notBefore = $text | Where-Object { $_ -match "Not Before" } | Select-Object -First 1
    $notAfter  = $text | Where-Object { $_ -match "Not After" }  | Select-Object -First 1
    if ($notBefore -match "Not Before:\s*(.+)$") {
        $result.NotBefore = $Matches[1].Trim()
    }
    if ($notAfter -match "Not After\s*:\s*(.+)$") {
        $result.NotAfter = $Matches[1].Trim()
    }

    # Extract SANs
    $sanSection = $false
    $sans = @()
    foreach ($line in $text) {
        if ($line -match "X509v3 Subject Alternative Name") {
            $sanSection = $true
            continue
        }
        if ($sanSection) {
            $entries = $line -split "," | ForEach-Object {
                ($_ -replace "DNS:", "").Trim()
            } | Where-Object { $_ -ne "" }
            $sans += $entries
            $sanSection = $false
        }
    }
    $result.SANs = $sans

    # Extract fingerprint
    $fingerprint = & $OpenSSLPath x509 -in $CertFile -fingerprint -sha256 -noout 2>&1
    if ($fingerprint -match "=(.+)$") {
        $result.Fingerprint = $Matches[1].Trim()
    }

    return $result
}

function Save-CertMetadata([string]$DomainName, [hashtable]$Details, [string[]]$RequestedAliases) {
    Ensure-Directory $MetadataDir
    $meta = @{
        domain      = $DomainName
        aliases     = $RequestedAliases
        certPath    = Get-CertPath $DomainName
        keyPath     = Get-KeyPath $DomainName
        cn          = $Details.CN
        issuer      = $Details.Issuer
        notBefore   = $Details.NotBefore
        notAfter    = $Details.NotAfter
        sans        = $Details.SANs
        fingerprint = $Details.Fingerprint
        createdAt   = (Get-Date -Format "o")
    }
    $meta | ConvertTo-Json -Depth 5 | Set-Content -Path (Get-MetadataPath $DomainName) -Encoding UTF8
}

# -------------------------------------------------------------------
# Actions
# -------------------------------------------------------------------
function Invoke-InstallCA {
    Test-MkcertInstalled
    Write-Status "Checking mkcert CA installation..."

    # mkcert -install is idempotent; it skips if CA is already installed
    $caRoot = & $MkcertPath -CAROOT 2>&1
    Write-Status "CA root directory: $caRoot"

    $rootCA = Join-Path $caRoot "rootCA.pem"
    if (Test-Path $rootCA) {
        Write-Status "CA root certificate exists at $rootCA"
        # Verify it is in the trust store by checking mkcert -install output
    }

    Write-Status "Running mkcert -install (may require admin privileges)..."
    $output = & $MkcertPath -install 2>&1
    $exitCode = $LASTEXITCODE

    foreach ($line in $output) {
        Write-Host "  $line"
    }

    if ($exitCode -eq 0) {
        Write-Ok "mkcert CA is installed and trusted."
    } else {
        Write-Err "mkcert -install failed (exit $exitCode). Run as Administrator if needed."
        exit 1
    }
}

function Invoke-Generate {
    if ([string]::IsNullOrWhiteSpace($Domain)) {
        Write-Err "Domain is required for generate action. Use -Domain 'myapp.test'"
        exit 1
    }

    Test-MkcertInstalled

    $domainDir = Get-DomainDir $Domain
    Ensure-Directory $domainDir

    $certFile = Get-CertPath $Domain
    $keyFile  = Get-KeyPath $Domain

    # Build argument list: primary domain + aliases
    $allDomains = @($Domain)
    if ($Aliases) {
        $allDomains += $Aliases
    }

    Write-Status "Generating certificate for: $($allDomains -join ', ')"
    Write-Status "Output directory: $domainDir"

    $mkcertArgs = @(
        "-cert-file", $certFile,
        "-key-file", $keyFile
    ) + $allDomains

    $output = & $MkcertPath @mkcertArgs 2>&1
    $exitCode = $LASTEXITCODE

    foreach ($line in $output) {
        Write-Host "  $line"
    }

    if ($exitCode -ne 0) {
        Write-Err "mkcert failed to generate certificate (exit $exitCode)."
        exit 1
    }

    if (-not (Test-Path $certFile)) {
        Write-Err "Certificate file was not created at $certFile"
        exit 1
    }

    # Read back and display details
    $details = Get-CertDetails $certFile
    if ($details) {
        Save-CertMetadata $Domain $details $Aliases

        Write-Host ""
        Write-Ok "Certificate generated successfully!"
        Write-Host ""
        Write-Host "  Domain:      $($details.CN)" -ForegroundColor White
        Write-Host "  Issuer:      $($details.Issuer)" -ForegroundColor White
        Write-Host "  Valid from:  $($details.NotBefore)" -ForegroundColor White
        Write-Host "  Valid until: $($details.NotAfter)" -ForegroundColor White
        Write-Host "  SANs:        $($details.SANs -join ', ')" -ForegroundColor White
        Write-Host "  Fingerprint: $($details.Fingerprint)" -ForegroundColor White
        Write-Host ""
        Write-Host "  Cert: $certFile" -ForegroundColor Gray
        Write-Host "  Key:  $keyFile" -ForegroundColor Gray
    } else {
        Write-Err "Certificate generated but could not read details."
    }
}

function Invoke-List {
    Write-Status "Listing all managed SSL certificates..."
    Write-Host ""

    if (-not (Test-Path $SslBaseDir)) {
        Write-Status "No certificates found. Directory does not exist: $SslBaseDir"
        return
    }

    $domains = Get-ChildItem -Path $SslBaseDir -Directory -ErrorAction SilentlyContinue
    if ($domains.Count -eq 0) {
        Write-Status "No certificates found."
        return
    }

    $tableData = @()

    foreach ($dir in $domains) {
        $certFile = Join-Path $dir.FullName "cert.pem"
        $keyFile  = Join-Path $dir.FullName "key.pem"

        $row = @{
            Domain  = $dir.Name
            Status  = "MISSING"
            Expiry  = "-"
            SANs    = "-"
        }

        if (Test-Path $certFile) {
            $details = Get-CertDetails $certFile
            if ($details) {
                # Check if expired
                try {
                    $verifyOutput = & $OpenSSLPath x509 -in $certFile -checkend 0 2>&1
                    if ($LASTEXITCODE -eq 0) {
                        $row.Status = "VALID"
                    } else {
                        $row.Status = "EXPIRED"
                    }
                } catch {
                    $row.Status = "UNKNOWN"
                }

                $row.Expiry = $details.NotAfter
                $row.SANs = ($details.SANs -join ", ")
            }
        }

        $tableData += [PSCustomObject]$row
    }

    # Display as table
    $tableData | Format-Table -Property Domain, Status, Expiry, SANs -AutoSize

    Write-Status "Total: $($tableData.Count) certificate(s)"
}

function Invoke-Verify {
    if ([string]::IsNullOrWhiteSpace($Domain)) {
        Write-Err "Domain is required for verify action. Use -Domain 'myapp.test'"
        exit 1
    }

    $certFile = Get-CertPath $Domain
    $keyFile  = Get-KeyPath $Domain

    Write-Status "Verifying certificate for: $Domain"

    if (-not (Test-Path $certFile)) {
        Write-Err "Certificate not found: $certFile"
        exit 1
    }
    if (-not (Test-Path $keyFile)) {
        Write-Err "Key not found: $keyFile"
        exit 1
    }

    # Full certificate text
    Write-Host ""
    Write-Host "--- Certificate Details ---" -ForegroundColor Yellow
    & $OpenSSLPath x509 -in $certFile -text -noout 2>&1 | ForEach-Object { Write-Host "  $_" }
    Write-Host ""

    # Check expiry
    Write-Host "--- Validity Check ---" -ForegroundColor Yellow
    $checkOutput = & $OpenSSLPath x509 -in $certFile -checkend 0 2>&1
    if ($LASTEXITCODE -eq 0) {
        Write-Ok "Certificate is currently valid."
    } else {
        Write-Err "Certificate has EXPIRED."
    }

    # Check 30-day expiry warning
    $checkSoon = & $OpenSSLPath x509 -in $certFile -checkend 2592000 2>&1
    if ($LASTEXITCODE -ne 0) {
        Write-Host "[WARN] Certificate expires within 30 days." -ForegroundColor Yellow
    }

    # Verify key matches cert
    Write-Host ""
    Write-Host "--- Key Match ---" -ForegroundColor Yellow
    $certModulus = & $OpenSSLPath x509 -in $certFile -modulus -noout 2>&1
    $keyModulus  = & $OpenSSLPath rsa  -in $keyFile  -modulus -noout 2>&1

    if ($certModulus -eq $keyModulus) {
        Write-Ok "Private key matches the certificate."
    } else {
        Write-Err "Private key does NOT match the certificate!"
    }

    # Verify trust chain via mkcert CA
    $caRoot = & $MkcertPath -CAROOT 2>&1
    $caCert = Join-Path $caRoot.Trim() "rootCA.pem"
    if (Test-Path $caCert) {
        Write-Host ""
        Write-Host "--- Trust Chain ---" -ForegroundColor Yellow
        $verifyChain = & $OpenSSLPath verify -CAfile $caCert $certFile 2>&1
        foreach ($line in $verifyChain) { Write-Host "  $line" }
        if ($LASTEXITCODE -eq 0) {
            Write-Ok "Certificate is trusted by mkcert CA."
        } else {
            Write-Err "Certificate is NOT trusted by mkcert CA."
        }
    }
}

function Invoke-Revoke {
    if ([string]::IsNullOrWhiteSpace($Domain)) {
        Write-Err "Domain is required for revoke action. Use -Domain 'myapp.test'"
        exit 1
    }

    $domainDir = Get-DomainDir $Domain
    $metaFile  = Get-MetadataPath $Domain

    if (-not (Test-Path $domainDir)) {
        Write-Err "No certificate found for domain: $Domain"
        exit 1
    }

    Write-Status "Revoking certificate for: $Domain"

    # Remove cert files
    Remove-Item -Path $domainDir -Recurse -Force
    Write-Ok "Removed certificate directory: $domainDir"

    # Remove metadata
    if (Test-Path $metaFile) {
        Remove-Item -Path $metaFile -Force
        Write-Ok "Removed metadata: $metaFile"
    }

    Write-Ok "Certificate for $Domain has been revoked and removed."
}

# -------------------------------------------------------------------
# Main dispatcher
# -------------------------------------------------------------------
switch ($Action) {
    "install-ca" { Invoke-InstallCA }
    "generate"   { Invoke-Generate }
    "list"       { Invoke-List }
    "verify"     { Invoke-Verify }
    "revoke"     { Invoke-Revoke }
}
