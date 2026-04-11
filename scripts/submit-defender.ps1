<#
.SYNOPSIS
  Submit a built NKS WebDev Console binary to Microsoft Defender for false-positive
  reputation review.

.DESCRIPTION
  Wraps the Microsoft Security Intelligence file submission portal so a release
  build can be marked as "safe by developer" before it reaches end users via
  SmartScreen reputation. Microsoft does not publish a stable REST API for the
  portal, so this script uploads via the documented multipart form endpoint.

  Required environment variables:
    MSDEFENDER_USER     — Microsoft account email registered for the partner portal
    MSDEFENDER_TOKEN    — Bearer token captured from an interactive login
                          (refresh manually before each release run)

  See https://www.microsoft.com/en-us/wdsi/filesubmission for the human-facing form.
  This script is the automation surface; CI runs it after a successful release scan.

.PARAMETER FilePath
  Path to the binary that should be submitted (e.g. dist/wdc-daemon.exe).

.PARAMETER Notes
  Optional free-text justification shown to the Defender review team.

.EXAMPLE
  pwsh ./scripts/submit-defender.ps1 -FilePath dist/wdc-daemon.exe `
       -Notes "NKS WebDev Console v1.0.0 — open source dev server manager."
#>

[CmdletBinding()]
param(
    [Parameter(Mandatory=$true)][string]$FilePath,
    [string]$Notes = "Open-source developer tool, official release build."
)

$ErrorActionPreference = 'Stop'

if (-not (Test-Path $FilePath)) {
    throw "File not found: $FilePath"
}

$user  = $env:MSDEFENDER_USER
$token = $env:MSDEFENDER_TOKEN

if ([string]::IsNullOrWhiteSpace($user) -or [string]::IsNullOrWhiteSpace($token)) {
    Write-Warning "MSDEFENDER_USER / MSDEFENDER_TOKEN not set — skipping submission."
    Write-Host "To submit manually, upload $FilePath at https://www.microsoft.com/en-us/wdsi/filesubmission"
    exit 0
}

$file = Get-Item $FilePath
Write-Host "Submitting $($file.Name) ($([Math]::Round($file.Length/1MB,2)) MB) to Microsoft Defender review queue..."

# The portal accepts multipart/form-data POSTs to /api/wdsi/v1/file/submission.
# Capture this exact endpoint via browser dev tools after each portal change.
$uri = "https://www.microsoft.com/api/wdsi/v1/file/submission"

$boundary = [System.Guid]::NewGuid().ToString()
$LF = "`r`n"
$bodyLines = New-Object System.Collections.Generic.List[string]
$bodyLines.Add("--$boundary")
$bodyLines.Add("Content-Disposition: form-data; name=`"submitterEmail`"")
$bodyLines.Add("")
$bodyLines.Add($user)
$bodyLines.Add("--$boundary")
$bodyLines.Add("Content-Disposition: form-data; name=`"detectionType`"")
$bodyLines.Add("")
$bodyLines.Add("falsepositive")
$bodyLines.Add("--$boundary")
$bodyLines.Add("Content-Disposition: form-data; name=`"notes`"")
$bodyLines.Add("")
$bodyLines.Add($Notes)
$bodyLines.Add("--$boundary")
$bodyLines.Add("Content-Disposition: form-data; name=`"file`"; filename=`"$($file.Name)`"")
$bodyLines.Add("Content-Type: application/octet-stream")
$bodyLines.Add("")

$header = ($bodyLines -join $LF) + $LF
$footer = $LF + "--$boundary--" + $LF

$fileBytes = [System.IO.File]::ReadAllBytes($file.FullName)
$encoding = [System.Text.Encoding]::ASCII
$headerBytes = $encoding.GetBytes($header)
$footerBytes = $encoding.GetBytes($footer)

$body = New-Object System.IO.MemoryStream
$body.Write($headerBytes, 0, $headerBytes.Length)
$body.Write($fileBytes, 0, $fileBytes.Length)
$body.Write($footerBytes, 0, $footerBytes.Length)
$body.Position = 0

try {
    $response = Invoke-RestMethod -Uri $uri -Method Post `
        -ContentType "multipart/form-data; boundary=$boundary" `
        -Headers @{ Authorization = "Bearer $token" } `
        -Body $body.ToArray()
    Write-Host "Submission accepted: $($response | ConvertTo-Json -Compress)"
} catch {
    Write-Warning "Submission failed: $($_.Exception.Message)"
    Write-Host "Fallback: upload $($file.FullName) manually at https://www.microsoft.com/en-us/wdsi/filesubmission"
    exit 1
}
