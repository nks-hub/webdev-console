<#
.SYNOPSIS
    Integration test for DevForge SSL Manager.

.DESCRIPTION
    1. Generates a cert for "devforge-test.test"
    2. Creates a minimal Apache vhost config using the cert
    3. Verifies the cert with OpenSSL
    4. Shows cert details (domain, expiry, issuer)
    5. Cleans up
#>

[CmdletBinding()]
param()

$ErrorActionPreference = "Stop"

$TestDomain   = "devforge-test.test"
$TestAliases  = @("www.devforge-test.test", "*.devforge-test.test")
$ScriptDir    = Split-Path -Parent $MyInvocation.MyCommand.Path
$SslManager   = Join-Path $ScriptDir "ssl_manager.ps1"
$ApacheTempl  = Join-Path $ScriptDir "apache_ssl.conf.tmpl"
$SslBaseDir   = Join-Path $env:USERPROFILE ".devforge\ssl\sites"
$TestDocRoot  = "C:\MAMP\htdocs\devforge-test"

$OpenSSLPath  = "openssl"
if (-not (Get-Command $OpenSSLPath -ErrorAction SilentlyContinue)) {
    if (Test-Path "C:\Program Files\Git\mingw64\bin\openssl.exe") {
        $OpenSSLPath = "C:\Program Files\Git\mingw64\bin\openssl.exe"
    } elseif (Test-Path "C:\MAMP\bin\apache\bin\openssl.exe") {
        $OpenSSLPath = "C:\MAMP\bin\apache\bin\openssl.exe"
    }
}

$passed = 0
$failed = 0
$total  = 0

function Test-Assert([string]$Name, [scriptblock]$Test) {
    $script:total++
    try {
        $result = & $Test
        if ($result) {
            Write-Host "  PASS: $Name" -ForegroundColor Green
            $script:passed++
        } else {
            Write-Host "  FAIL: $Name" -ForegroundColor Red
            $script:failed++
        }
    } catch {
        Write-Host "  FAIL: $Name - $_" -ForegroundColor Red
        $script:failed++
    }
}

# -------------------------------------------------------------------
Write-Host ""
Write-Host "========================================" -ForegroundColor Cyan
Write-Host " DevForge SSL Manager - Integration Test" -ForegroundColor Cyan
Write-Host "========================================" -ForegroundColor Cyan
Write-Host ""

# -------------------------------------------------------------------
# Step 0: Preconditions
# -------------------------------------------------------------------
Write-Host "[Step 0] Checking preconditions..." -ForegroundColor Yellow

Test-Assert "ssl_manager.ps1 exists" {
    Test-Path $SslManager
}

Test-Assert "apache_ssl.conf.tmpl exists" {
    Test-Path $ApacheTempl
}

Test-Assert "mkcert binary exists" {
    Test-Path "C:\work\mkcert.exe"
}

Test-Assert "OpenSSL is available" {
    $null = & $OpenSSLPath version 2>&1
    $LASTEXITCODE -eq 0
}

# -------------------------------------------------------------------
# Step 1: Generate certificate
# -------------------------------------------------------------------
Write-Host ""
Write-Host "[Step 1] Generating certificate for $TestDomain..." -ForegroundColor Yellow

& $SslManager -Action generate -Domain $TestDomain -Aliases $TestAliases

$certFile = Join-Path $SslBaseDir "$TestDomain\cert.pem"
$keyFile  = Join-Path $SslBaseDir "$TestDomain\key.pem"

Test-Assert "Certificate file was created" {
    Test-Path $certFile
}

Test-Assert "Key file was created" {
    Test-Path $keyFile
}

Test-Assert "Certificate file is not empty" {
    (Get-Item $certFile).Length -gt 0
}

Test-Assert "Key file is not empty" {
    (Get-Item $keyFile).Length -gt 0
}

# -------------------------------------------------------------------
# Step 2: Create Apache vhost config
# -------------------------------------------------------------------
Write-Host ""
Write-Host "[Step 2] Creating Apache vhost config..." -ForegroundColor Yellow

$outputConfig = Join-Path $SslBaseDir "$TestDomain\vhost-ssl.conf"

$templateContent = Get-Content -Path $ApacheTempl -Raw
$config = $templateContent
$config = $config -replace [regex]::Escape("{{.Domain}}"), $TestDomain
$config = $config -replace [regex]::Escape("{{.DocRoot}}"), $TestDocRoot
$config = $config -replace [regex]::Escape("{{.CertPath}}"), $certFile
$config = $config -replace [regex]::Escape("{{.KeyPath}}"), $keyFile
$config = $config -replace [regex]::Escape("{{.ServerAliases}}"), ($TestAliases -join " ")

$config | Set-Content -Path $outputConfig -Encoding UTF8

Test-Assert "Apache vhost config was created" {
    Test-Path $outputConfig
}

Test-Assert "Config contains ServerName" {
    (Get-Content $outputConfig -Raw) -match "ServerName\s+$([regex]::Escape($TestDomain))"
}

Test-Assert "Config contains SSLCertificateFile" {
    (Get-Content $outputConfig -Raw) -match "SSLCertificateFile"
}

Test-Assert "Config contains SSLProtocol with modern settings" {
    (Get-Content $outputConfig -Raw) -match "SSLProtocol.*-TLSv1\.1"
}

Test-Assert "Config contains HSTS header with max-age=0" {
    (Get-Content $outputConfig -Raw) -match "max-age=0"
}

Write-Host ""
Write-Host "  Generated Apache config:" -ForegroundColor Gray
Write-Host "  $outputConfig" -ForegroundColor Gray

# -------------------------------------------------------------------
# Step 3: Verify certificate with OpenSSL
# -------------------------------------------------------------------
Write-Host ""
Write-Host "[Step 3] Verifying certificate with OpenSSL..." -ForegroundColor Yellow

$certText = & $OpenSSLPath x509 -in $certFile -text -noout 2>&1
$certTextStr = $certText -join "`n"

Test-Assert "OpenSSL can parse the certificate" {
    $LASTEXITCODE -eq 0
}

Test-Assert "Certificate contains test domain in Subject" {
    $certTextStr -match $TestDomain
}

Test-Assert "Certificate contains wildcard SAN" {
    $certTextStr -match "\*\.$([regex]::Escape('devforge-test.test'))"
}

Test-Assert "Certificate is not expired" {
    $check = & $OpenSSLPath x509 -in $certFile -checkend 0 2>&1
    $LASTEXITCODE -eq 0
}

# Key match
$certMod = & $OpenSSLPath x509 -in $certFile -modulus -noout 2>&1
$keyMod  = & $OpenSSLPath rsa  -in $keyFile  -modulus -noout 2>&1

Test-Assert "Private key matches certificate" {
    $certMod -eq $keyMod
}

# -------------------------------------------------------------------
# Step 4: Display certificate details
# -------------------------------------------------------------------
Write-Host ""
Write-Host "[Step 4] Certificate details:" -ForegroundColor Yellow

# Subject
$subjectLine = $certText | Where-Object { $_ -match "Subject:" } | Select-Object -First 1
Write-Host "  Subject:   $($subjectLine -replace '^\s*Subject:\s*', '')" -ForegroundColor White

# Issuer
$issuerLine = $certText | Where-Object { $_ -match "Issuer:" } | Select-Object -First 1
Write-Host "  Issuer:    $($issuerLine -replace '^\s*Issuer:\s*', '')" -ForegroundColor White

# Validity
$notBefore = $certText | Where-Object { $_ -match "Not Before" } | Select-Object -First 1
$notAfter  = $certText | Where-Object { $_ -match "Not After" }  | Select-Object -First 1
Write-Host "  Not Before: $($notBefore -replace '^\s*Not Before:\s*', '')" -ForegroundColor White
Write-Host "  Not After:  $($notAfter -replace '^\s*Not After\s*:\s*', '')" -ForegroundColor White

# SANs
$sanSection = $false
foreach ($line in $certText) {
    if ($line -match "Subject Alternative Name") { $sanSection = $true; continue }
    if ($sanSection) {
        Write-Host "  SANs:      $($line.Trim())" -ForegroundColor White
        $sanSection = $false
    }
}

# Fingerprint
$fingerprint = & $OpenSSLPath x509 -in $certFile -fingerprint -sha256 -noout 2>&1
Write-Host "  SHA256:    $($fingerprint -replace '^.*=', '')" -ForegroundColor White

# -------------------------------------------------------------------
# Step 5: Test list and verify actions
# -------------------------------------------------------------------
Write-Host ""
Write-Host "[Step 5] Testing list and verify actions..." -ForegroundColor Yellow

Write-Host "  --- List output ---" -ForegroundColor Gray
& $SslManager -Action list

Write-Host ""
Write-Host "  --- Verify output ---" -ForegroundColor Gray
& $SslManager -Action verify -Domain $TestDomain

# -------------------------------------------------------------------
# Step 6: Cleanup
# -------------------------------------------------------------------
Write-Host ""
Write-Host "[Step 6] Cleaning up..." -ForegroundColor Yellow

& $SslManager -Action revoke -Domain $TestDomain

Test-Assert "Certificate directory was removed after revoke" {
    -not (Test-Path (Join-Path $SslBaseDir $TestDomain))
}

# -------------------------------------------------------------------
# Summary
# -------------------------------------------------------------------
Write-Host ""
Write-Host "========================================" -ForegroundColor Cyan
Write-Host " Test Summary" -ForegroundColor Cyan
Write-Host "========================================" -ForegroundColor Cyan
Write-Host "  Total:  $total" -ForegroundColor White
Write-Host "  Passed: $passed" -ForegroundColor Green
Write-Host "  Failed: $failed" -ForegroundColor $(if ($failed -gt 0) { "Red" } else { "Green" })
Write-Host ""

if ($failed -gt 0) {
    Write-Host "SOME TESTS FAILED." -ForegroundColor Red
    exit 1
} else {
    Write-Host "ALL TESTS PASSED." -ForegroundColor Green
    exit 0
}
