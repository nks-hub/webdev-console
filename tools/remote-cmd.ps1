param(
    [Parameter(Position = 0)]
    [ValidateSet('list', 'status', 'exec')]
    [string]$Command = 'status',

    [Parameter(Position = 1, ValueFromRemainingArguments = $true)]
    [string[]]$Args,

    [string]$Client = $env:REMOTECMD_DEFAULT_CLIENT,
    [string]$Url = $(if ($env:REMOTECMD_URL) { $env:REMOTECMD_URL } else { 'https://localhost:7890' }),
    [string]$Token = $env:REMOTECMD_TOKEN,
    [int]$TimeoutSeconds = 60
)

$ErrorActionPreference = 'Stop'

function Get-RemoteCmdToken {
    if ($Token) {
        return $Token
    }

    $proc = Get-CimInstance Win32_Process -Filter "Name='RemoteCmd.Server.exe'" |
        Select-Object -First 1
    if (-not $proc) {
        throw 'RemoteCmd.Server.exe is not running and REMOTECMD_TOKEN is not set.'
    }

    $match = [regex]::Match($proc.CommandLine, 'rcmd-[a-z0-9-]+')
    if (-not $match.Success) {
        throw 'Unable to discover RemoteCmd token from server command line.'
    }

    return $match.Value
}

function Invoke-RemoteCmdApi {
    param(
        [string]$Method,
        [string]$Path,
        [object]$Body = $null
    )

    [Net.ServicePointManager]::SecurityProtocol = [Net.SecurityProtocolType]::Tls12
    [Net.ServicePointManager]::ServerCertificateValidationCallback = { $true }
    $authToken = Get-RemoteCmdToken
    $headers = @{ Authorization = "Bearer $authToken" }
    $uri = "$($Url.TrimEnd('/'))$Path"

    if ($Body -ne $null) {
        $json = $Body | ConvertTo-Json -Compress -Depth 8
        return Invoke-RestMethod -Method $Method -Uri $uri -Headers $headers -ContentType 'application/json' -Body $json
    }

    try {
        return Invoke-RestMethod -Method $Method -Uri $uri -Headers $headers
    }
    catch {
        if ($Method -ne 'Get') {
            throw
        }

        $raw = & curl.exe -k -s -H "Authorization: Bearer $authToken" $uri
        if ($LASTEXITCODE -ne 0) {
            throw
        }
        return $raw | ConvertFrom-Json
    }
}

switch ($Command) {
    'list' {
        Invoke-RemoteCmdApi -Method Get -Path '/api/clients' | ConvertTo-Json -Depth 8
    }
    'status' {
        Invoke-RemoteCmdApi -Method Get -Path '/api/status' | ConvertTo-Json -Depth 8
    }
    'exec' {
        $remoteCommand = ($Args -join ' ').Trim()
        if (-not $remoteCommand) {
            throw 'No command supplied. Usage: tools/remote-cmd.ps1 exec [-Client name] <PowerShell command>'
        }

        if (-not $Client) {
            $clients = Invoke-RemoteCmdApi -Method Get -Path '/api/clients'
            $connected = @($clients.clients | Where-Object { $_.connected })
            if ($connected.Count -ne 1) {
                $names = ($connected | ForEach-Object { $_.name }) -join ', '
                throw "Specify -Client. Connected clients: $names"
            }
            $Client = $connected[0].name
        }

        Invoke-RemoteCmdApi -Method Post -Path '/api/exec' -Body @{
            client = $Client
            command = $remoteCommand
            timeoutSeconds = $TimeoutSeconds
        } | ConvertTo-Json -Depth 8
    }
}
