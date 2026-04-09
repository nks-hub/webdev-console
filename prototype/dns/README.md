# DevForge — DNS / Hosts File Manager

Manages entries in `C:\Windows\System32\drivers\etc\hosts` for local development
domains. All DevForge entries are contained within a clearly delimited block so
user content is never touched.

---

## Managed block format

```
# >>> DevForge Managed - DO NOT EDIT <<<
127.0.0.1	myapp.test
127.0.0.1	shop.test	www.shop.test
# <<< DevForge Managed >>>
```

---

## PowerShell tool — `hosts_manager.ps1`

### Requirements

- Windows 10 / 11
- PowerShell 5.1 or PowerShell 7+
- Administrator privileges (the script requests elevation automatically)

### Commands

| Action | Description |
|---|---|
| `add` | Add a domain (+ optional aliases) pointing to an IP |
| `remove` | Remove a domain entry from the managed block |
| `list` | Print all DevForge-managed entries |
| `check` | DNS resolution test for a domain (Resolve-DnsName + ping) |
| `backup` | Create a timestamped backup in `C:\DevForge\backups\` |
| `restore` | Restore from a backup (defaults to most recent) |
| `clean` | Remove **all** DevForge-managed entries |
| `port-check` | Check if a port is in use and who holds it |

### Parameters

| Parameter | Description | Default |
|---|---|---|
| `-Action` | One of the actions above | required |
| `-Domain` | Primary domain name | — |
| `-IP` | IP address to map | `127.0.0.1` |
| `-Aliases` | Comma-separated additional names | — |
| `-Port` | Port for `port-check` | — |
| `-BackupFile` | Backup path for `restore` | most recent |

### Usage examples

```powershell
# Add a domain
.\hosts_manager.ps1 -Action add -Domain "myapp.test"

# Add with aliases
.\hosts_manager.ps1 -Action add -Domain "myapp.test" -Aliases "www.myapp.test,api.myapp.test"

# Custom IP
.\hosts_manager.ps1 -Action add -Domain "myapp.test" -IP "192.168.1.10"

# Remove
.\hosts_manager.ps1 -Action remove -Domain "myapp.test"

# List managed entries
.\hosts_manager.ps1 -Action list

# DNS check
.\hosts_manager.ps1 -Action check -Domain "myapp.test"

# Backup
.\hosts_manager.ps1 -Action backup

# Restore (most recent backup)
.\hosts_manager.ps1 -Action restore

# Restore a specific backup
.\hosts_manager.ps1 -Action restore -BackupFile "C:\DevForge\backups\hosts.20240101-120000.bak"

# Remove all DevForge entries
.\hosts_manager.ps1 -Action clean

# Port check
.\hosts_manager.ps1 -Action port-check -Port 80
.\hosts_manager.ps1 -Action port-check -Port 443
```

### Safety features

- **Auto-elevation** — prompts for UAC if not already running as Administrator.
- **Backup before every write** — a timestamped `.bak` file is created in
  `C:\DevForge\backups\` before any modification to the hosts file.
- **Atomic write** — changes are written to a temp file first, then renamed over
  the hosts file to prevent corruption if interrupted.
- **Idempotent `add`** — re-adding an existing domain is a no-op.
- **Domain validation** — rejects entries that are not valid hostnames before
  touching the file.
- **DNS cache flush** — `Clear-DnsClientCache` is called automatically after
  every modification.

---

## Go library — `hosts_manager.go`

Used by the DevForge daemon. Exposes the same logic as the PowerShell script.

### API

```go
// Add a domain to the managed block
err := dns.AddEntry("myapp.test", "127.0.0.1", "www.myapp.test")

// Remove
err := dns.RemoveEntry("myapp.test")

// List
entries, err := dns.ListEntries()

// Clean all managed entries
err := dns.CleanEntries()

// Backup
path, err := dns.Backup()

// Restore (empty string = most recent)
err := dns.RestoreBackup("")
err := dns.RestoreBackup(`C:\DevForge\backups\hosts.20240101-120000.bak`)

// Admin check
ok := dns.IsAdmin()
```

### Build

```bash
cd C:\work\sources\nks-ws
go build ./prototype/dns/...
```

---

## UAC elevation helper — `elevate.go`

A small standalone utility that re-launches a program with elevated privileges
via `ShellExecute("runas")`.

### Build

```bash
go build -o elevate.exe ./prototype/dns/elevate.go
```

### Usage

```
elevate.exe <program> [args...]
elevate.exe -- powershell.exe -File hosts_manager.ps1 -Action add -Domain myapp.test
```

If the process is already running as Administrator the tool exits immediately
with code 0.

---

## Port scanner — `port_scanner.go`

Go library used by the daemon to check port availability.

### API

```go
result, err := dns.CheckPort(80)
if result.Primary.InUse {
    fmt.Printf("Port 80 used by PID %d (%s)\n",
        result.Primary.PID, result.Primary.ProcessName)
    if result.Alternative != nil && !result.Alternative.InUse {
        fmt.Printf("Try port %d instead\n", result.Alternative.Port)
    }
}

// Check multiple ports at once
statuses, err := dns.CheckPorts([]int{80, 443, 3306})
```

Default alternatives suggested:

| Port | Alternative |
|---|---|
| 80 | 8080 |
| 443 | 8443 |
| 3306 | 3307 |
| 5432 | 5433 |
| 6379 | 6380 |
| 27017 | 27018 |

---

## Integration tests — `test_hosts.ps1`

Runs a complete add / verify / remove / verify / restore cycle against the live
hosts file.

### Requirements

- Must be run as Administrator
- Requires `hosts_manager.ps1` in the same directory

### Run

```powershell
# Open an elevated PowerShell, then:
cd C:\work\sources\nks-ws\prototype\dns
.\test_hosts.ps1
```

### What is tested

1. Pre-test backup is created successfully
2. `add devforge-test.local` — entry appears in file
3. Entry is within the managed block markers
4. Idempotent add — file unchanged after duplicate
5. `add devforge-alias.local` with alias `www.devforge-alias.local`
6. `list` — output contains both domains
7. DNS resolution via `nslookup` returns `127.0.0.1`
8. DNS resolution via `Resolve-DnsName` returns `127.0.0.1`
9. `remove devforge-test.local` — entry gone, alias untouched
10. `remove devforge-alias.local` — entry gone
11. Invalid domain format is rejected without modifying the file
12. `restore` — original hosts file is recovered

---

## Directory layout

```
prototype/dns/
├── hosts_manager.ps1   PowerShell CLI tool
├── hosts_manager.go    Go library for daemon integration
├── elevate.go          UAC elevation helper (standalone binary)
├── port_scanner.go     Port availability checker (Go library)
├── test_hosts.ps1      Integration test suite
└── README.md           This file
```

---

## Backup location

All backups are stored in `C:\DevForge\backups\` with the naming convention:

```
hosts.YYYYMMDD-HHMMSS.bak
hosts.pretest.YYYYMMDD-HHMMSS.bak   (created by test suite)
```
