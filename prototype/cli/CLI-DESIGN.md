# DevForge CLI — UX Design Specification

**Version:** 1.0.0  
**Status:** Design Spec  

---

## 1. Design Philosophy

DevForge CLI follows the conventions established by **Docker CLI**, **gh CLI**, and **Laravel Artisan**:

- Output is human-readable by default, machine-readable with `--json`
- Color is informational, never decorative
- Every destructive operation requires confirmation (or `--force` to skip)
- Errors always include a suggestion or next step
- Progress is always shown for operations > 1 second
- Verbose mode (`-v`) available on all commands

---

## 2. Global Flags

```
--json          Output as JSON (disables color, tables become arrays)
--quiet, -q     Suppress all output except errors
--verbose, -v   Show additional debug/trace information
--no-color      Disable ANSI color output
--help, -h      Show command help
--version       Show devforge version
```

---

## 3. Color Scheme

| Color      | Hex (approx) | ANSI          | Usage                                  |
|------------|-------------|---------------|----------------------------------------|
| Green      | #22c55e     | Bold Green    | Running, success, active, installed    |
| Red        | #ef4444     | Bold Red      | Stopped, error, critical, failed       |
| Yellow     | #f59e0b     | Bold Yellow   | Warning, degraded, pending, restarting |
| Blue       | #3b82f6     | Bold Cyan     | Info, in-progress, default version     |
| Magenta    | #a855f7     | Bold Magenta  | Highlight, selected item               |
| White      | #ffffff     | Bold White    | Primary text, column headers           |
| Gray       | #6b7280     | Dark White    | Disabled, metadata, paths, timestamps  |
| Dim        | —           | Dim White     | Secondary text, help hints, counts     |

---

## 4. Output Formatting Rules

### 4.1 Tables

- Header row: **bold white**, left-aligned
- Data rows: alternating (no background change in terminal — just spacing)
- Status columns: color-coded per state
- Empty tables: show "No items found." in dim gray
- Column separator: two spaces minimum padding

### 4.2 Prefixes / Icons

```
  ✓  Success / running          (green)
  ✗  Error / stopped            (red)
  ⚡  Warning / degraded         (yellow)
  →  In progress / info         (blue)
  ●  Active / default           (green bold)
  ○  Inactive / available       (gray)
  ⊙  Restarting                 (yellow)
```

### 4.3 Section Headers

```
=== Section Title ===      (bold white, padded with spaces)
```

### 4.4 Progress Indicators

Multi-step operations use a numbered step list:

```
  [1/4] Generating SSL certificate ...  ✓
  [2/4] Writing Apache vhost config ... ✓
  [3/4] Updating /etc/hosts ...         ✓
  [4/4] Reloading Apache ...            ✓
```

Single operations use a spinner or inline progress bar.

### 4.5 Key-Value Output

For single-item detail views:

```
  Name          myapp.test
  PHP Version   8.2.27
  Document Root /Users/dev/sites/myapp
  SSL           Enabled  (mkcert, expires 2027-04-09)
  Status        Running
```

---

## 5. Command Output Specifications

### 5.1 `devforge status`

Shows the overall health of all DevForge-managed services.

```
DevForge v1.0.0  ●  All systems operational

  SERVICE         STATUS     PID     UPTIME    MEMORY
  ─────────────────────────────────────────────────────
  Apache 2.4.58   ✓ running  12847   2h 14m    48 MB
  PHP-FPM 8.2     ✓ running  12851   2h 14m    32 MB
  PHP-FPM 8.3     ✓ running  12855   2h 14m    31 MB
  PHP-FPM 8.1     ✗ stopped  —       —         —
  MySQL 8.0       ✓ running  12860   2h 14m    312 MB
  Redis 7.2       ✓ running  12864   2h 14m    4 MB
  Mailpit 1.14    ✓ running  12868   2h 14m    12 MB
  ─────────────────────────────────────────────────────
  7 services  ·  5 running  ·  1 stopped  ·  1 disabled

  Hint: Use 'devforge start php-fpm@8.1' to start PHP-FPM 8.1
```

Degraded state example (Apache restarting):
```
DevForge v1.0.0  ⚡  Degraded — 1 service needs attention

  SERVICE         STATUS        PID     UPTIME    MEMORY
  ──────────────────────────────────────────────────────
  Apache 2.4.58   ⊙ restarting  —       —         —
  PHP-FPM 8.2     ✓ running     12851   2h 14m    32 MB
  MySQL 8.0       ✓ running     12860   2h 14m    312 MB
```

JSON output (`devforge status --json`):
```json
{
  "version": "1.0.0",
  "status": "degraded",
  "services": [
    {
      "name": "apache",
      "version": "2.4.58",
      "status": "running",
      "pid": 12847,
      "uptime_seconds": 8040,
      "memory_mb": 48
    },
    {
      "name": "php-fpm",
      "version": "8.1",
      "status": "stopped",
      "pid": null,
      "uptime_seconds": 0,
      "memory_mb": 0
    }
  ]
}
```

---

### 5.2 `devforge site:list`

```
  3 sites configured

  DOMAIN              PHP    STATUS     SSL    ROOT
  ────────────────────────────────────────────────────────────────────────────
  myapp.test          8.2    ✓ active   ✓      ~/sites/myapp
  api.local           8.3    ✓ active   ✓      ~/sites/api
  legacy.test         7.4    ✗ stopped  ✗      ~/sites/legacy
  ────────────────────────────────────────────────────────────────────────────

  Hint: Use 'devforge site:info <domain>' for detailed info on a site
```

With `--all` flag (includes disabled sites):
```
  5 sites configured  (2 disabled)

  DOMAIN              PHP    STATUS      SSL    ROOT
  ────────────────────────────────────────────────────────────────────────────
  myapp.test          8.2    ✓ active    ✓      ~/sites/myapp
  api.local           8.3    ✓ active    ✓      ~/sites/api
  legacy.test         7.4    ✗ stopped   ✗      ~/sites/legacy
  old-project.test    8.0    ○ disabled  ✗      ~/sites/old
  staging.local       8.3    ○ disabled  ✓      ~/sites/staging
  ────────────────────────────────────────────────────────────────────────────
```

---

### 5.3 `devforge site:create myapp.test --php=8.2`

```
  Creating site myapp.test

  [1/5] Validating domain ...                        ✓
  [2/5] Generating SSL certificate (mkcert) ...      ✓
  [3/5] Writing Apache vhost config ...              ✓
  [4/5] Updating /etc/hosts (requires sudo) ...      ✓
  [5/5] Reloading Apache ...                         ✓

  ─────────────────────────────────────────────────────
  ✓  Site created successfully

  Domain        https://myapp.test
  PHP           8.2.27
  Document Root ~/sites/myapp
  Config        ~/.devforge/sites/myapp.test.toml
  SSL           ✓ Trusted (mkcert)
  ─────────────────────────────────────────────────────

  Open in browser: devforge site:open myapp.test
```

Error variant (domain already exists):
```
  ✗  Error: Domain 'myapp.test' already exists

  Existing config: ~/.devforge/sites/myapp.test.toml

  Suggestions:
    · devforge site:info myapp.test    — view current config
    · devforge site:edit myapp.test    — edit the site
    · devforge site:delete myapp.test  — remove and recreate
```

---

### 5.4 `devforge site:create` (interactive mode, no args)

```
  DevForge  Site Creation Wizard

  ? Domain name:  > myapp.test_

  ? Document root:  (~/sites/myapp) > _

  ? PHP version:
  ❯ 8.3  (latest)
    8.2  ● default
    8.1
    8.0
    7.4

  ? Enable SSL?  (Y/n) > _

  ? Database: (none)
  ❯ None
    MySQL 8.0  — create new database 'myapp'
    MySQL 8.0  — select existing database

  ? Framework preset:
  ❯ None (static/manual)
    Laravel
    WordPress
    Symfony
    Custom docroot (public/)

  ─────────────────────────────
  Ready to create site with:

    Domain     https://myapp.test
    Root       ~/sites/myapp
    PHP        8.3
    SSL        enabled
    DB         myapp (MySQL 8.0)
    Preset     Laravel

  ? Confirm creation? (Y/n) >
```

---

### 5.5 `devforge start`

Starts all services:

```
  Starting DevForge services

  ✓  Apache 2.4.58       started  (pid 14220)
  ✓  PHP-FPM 8.2         started  (pid 14224)
  ✓  PHP-FPM 8.3         started  (pid 14228)
  ○  PHP-FPM 8.1         skipped  (disabled)
  ✓  MySQL 8.0           started  (pid 14235)
  ✓  Redis 7.2           started  (pid 14240)
  ✓  Mailpit 1.14        started  (pid 14245)

  All services started in 1.2s
```

---

### 5.6 `devforge stop apache`

Stopping a single named service:

```
  Stopping apache ...  ✓  stopped  (was pid 12847)
```

Stopping with dependent services warning:
```
  ⚡  Warning: Stopping Apache will affect 3 active sites:
       myapp.test, api.local, legacy.test

  ? Continue? (y/N) > y

  Stopping apache ...  ✓  stopped
```

---

### 5.7 `devforge php:list`

```
  Installed PHP versions

  VERSION   STATUS       FPM PORT  SITES  PATH
  ────────────────────────────────────────────────────────────────────────────
  8.3.6     ✓ running    9083      2      ~/.devforge/bin/php-8.3/bin/php
  8.2.27    ● default    9082      5      ~/.devforge/bin/php-8.2/bin/php
  8.1.28    ✓ running    9081      1      ~/.devforge/bin/php-8.1/bin/php
  8.0.30    ✗ stopped    9080      0      ~/.devforge/bin/php-8.0/bin/php
  7.4.33    ✗ stopped    9074      1      ~/.devforge/bin/php-7.4/bin/php
  ────────────────────────────────────────────────────────────────────────────
  5 installed  ·  3 running  ·  ● 8.2.27 is default

  Available to install: 8.4 (RC2),  5.6.40
  Run 'devforge php:install 8.4' to install
```

---

### 5.8 `devforge php:install 8.4`

```
  Installing PHP 8.4.0-RC2

  [1/4] Downloading php-8.4.0RC2-win32-vs17-x64.zip (28.4 MB) ...
  ████████████████████████████████████████████  100%  3.2s

  [2/4] Verifying checksum (SHA-256) ...                        ✓
  [3/4] Extracting to ~/.devforge/bin/php-8.4/ ...              ✓
  [4/4] Configuring PHP-FPM (port 9084) ...                     ✓

  ─────────────────────────────────────────────────
  ✓  PHP 8.4.0-RC2 installed

  Extensions:  bcmath, curl, gd, intl, mbstring, mysqli, 
               openssl, pdo_mysql, redis, zip
  FPM port:    9084
  Path:        ~/.devforge/bin/php-8.4/bin/php

  Run 'devforge php:use 8.4' to set as default
  ─────────────────────────────────────────────────
```

---

### 5.9 `devforge ssl:status`

```
  SSL Certificates

  DOMAIN              ISSUER        EXPIRES        STATUS
  ────────────────────────────────────────────────────────────────────────────
  myapp.test          DevForge CA   2027-04-09     ✓ valid  (366 days)
  api.local           DevForge CA   2027-04-09     ✓ valid  (366 days)
  legacy.test         —             —              ✗ no cert
  ────────────────────────────────────────────────────────────────────────────
  CA Status:  ✓ DevForge Local CA installed and trusted
  CA Cert:    ~/.devforge/ca/rootCA.pem
  Cert Store: System (macOS Keychain / Windows Cert Store)

  Hint: Use 'devforge ssl:renew <domain>' to renew a certificate
```

---

### 5.10 `devforge db:list`

```
  Databases  (MySQL 8.0 · 127.0.0.1:3306)

  NAME              SIZE      TABLES  LINKED SITE      MODIFIED
  ────────────────────────────────────────────────────────────────────────────
  myapp             12.4 MB   18      myapp.test       2026-04-08 14:22
  api_development   4.1 MB    9       api.local        2026-04-07 11:01
  legacy_db         98.2 MB   47      legacy.test      2025-12-11 09:44
  wordpress_test    8.3 MB    12      —                2026-03-20 16:30
  ────────────────────────────────────────────────────────────────────────────
  4 databases  ·  total 123.0 MB

  Hint: 'devforge db:create <name>' · 'devforge db:open <name>'
```

---

## 6. Error Formatting

All errors follow a consistent format:

```
  ✗  Error: <short description>

  <One sentence explanation of what went wrong>

  Suggestions:
    · <actionable fix 1>
    · <actionable fix 2>
    · Run 'devforge <cmd> --help' for usage
```

Example — port conflict:
```
  ✗  Error: Port 80 is already in use

  Apache cannot start because another process is using port 80.
  Conflict: Microsoft-HTTPAPI/2.0  (pid 4, System)

  Suggestions:
    · devforge config set apache.port 8080  — use alternate port
    · Stop IIS:  net stop w3svc             — free port 80
    · devforge doctor                       — run diagnostics
```

Example — PHP version not installed:
```
  ✗  Error: PHP 8.4 is not installed

  Site 'myapp.test' requires PHP 8.4 which is not installed.

  Suggestions:
    · devforge php:install 8.4  — install PHP 8.4
    · devforge php:list         — see installed versions
```

---

## 7. Confirmation Dialogs

### Destructive operations (site:delete, db:drop, php:uninstall)

```
  ⚡  Warning: This will permanently delete site 'myapp.test'

  The following will be removed:
    · Apache vhost config
    · SSL certificate
    · /etc/hosts entry
    · Site config ~/.devforge/sites/myapp.test.toml

  Note: Document root ~/sites/myapp will NOT be deleted.

  ? Type the domain name to confirm: > myapp.test_

  Deleting site myapp.test ...
    Removing vhost config ...    ✓
    Removing SSL certificate ...  ✓
    Removing hosts entry ...     ✓
    Removing site config ...     ✓

  ✓  Site deleted
```

Bypass with `--force` flag:
```bash
devforge site:delete myapp.test --force
```

---

## 8. Interactive PHP Version Selector

Used in `site:create`, `site:edit`, `site:php`:

```
  ? PHP version:
  ❯ 8.3.6   ✓ running   (latest)
    8.2.27  ✓ running   ● default
    8.1.28  ✓ running
    8.0.30  ✗ stopped
    7.4.33  ✗ stopped
    ──────────────────
    Install new version...
```

Navigation: arrow keys, Enter to select, Esc to cancel.

---

## 9. `devforge doctor` Output

```
  DevForge Doctor  —  System Diagnostics

  CORE
  ✓  devforge daemon         running (v1.0.0, pid 1024)
  ✓  devforge config         valid (~/.devforge/config.toml)
  ✓  socket                  /tmp/devforge.sock  accessible

  SERVICES
  ✓  Apache 2.4.58           /usr/local/opt/httpd/bin/httpd
  ✓  MySQL 8.0.36            /usr/local/opt/mysql/bin/mysqld
  ⚡  PHP-FPM 7.4             config warning: pm.max_children low (1)
  ✓  mkcert 1.4.4            CA installed and trusted

  NETWORK
  ✓  Port 80                 free / bound by devforge
  ✓  Port 443                free / bound by devforge
  ✗  Port 3306               bound by external process (pid 991)
  ✓  DNS resolution          myapp.test → 127.0.0.1  ✓

  FILESYSTEM
  ✓  Config dir              ~/.devforge/  (rw)
  ✓  Log dir                 ~/.devforge/logs/  (rw)
  ✓  Bin dir                 ~/.devforge/bin/  (rw)
  ✗  /etc/hosts              not writable (run: devforge fix:hosts)

  ──────────────────────────────────────────────────
  2 issues found

  Run 'devforge doctor --fix' to automatically resolve issues
```

---

## 10. Command Hierarchy

```
devforge
├── status               Global status overview
├── start [service]      Start all or named service
├── stop [service]       Stop all or named service
├── restart [service]    Restart all or named service
├── reload [service]     Graceful reload (Apache: graceful)
├── logs [service]       Tail service logs
│
├── site:list            List all sites
├── site:create          Create new site (interactive or args)
├── site:delete          Remove a site
├── site:info            Show site details
├── site:edit            Edit site config
├── site:open            Open site in browser
├── site:php             Change PHP version for a site
├── site:enable          Enable a disabled site
├── site:disable         Disable (but keep config) a site
│
├── php:list             List installed PHP versions
├── php:install          Install a PHP version
├── php:uninstall        Remove a PHP version
├── php:use              Set default PHP version
├── php:info             Show PHP version details/extensions
│
├── ssl:status           Show SSL certificate status
├── ssl:install          Install/trust CA
├── ssl:create           Create cert for domain
├── ssl:renew            Renew expiring cert
│
├── db:list              List databases
├── db:create            Create a database
├── db:drop              Drop a database
├── db:import            Import SQL file
├── db:export            Export database to SQL
├── db:open              Open in Tableplus/Sequel Pro/etc.
│
├── config               Show/set configuration values
├── config:get           Get a config value
├── config:set           Set a config value
├── config:edit          Open config in $EDITOR
│
└── doctor               System diagnostics
    └── --fix            Auto-fix common issues
```

---

## 11. Timing and Animation

| Operation              | Duration | UI Treatment              |
|------------------------|----------|---------------------------|
| Status query           | < 100ms  | No spinner                |
| Single service start   | < 2s     | Inline "starting..." text |
| Multi-service start    | 2-5s     | Step list with checkmarks |
| PHP install            | 10-60s   | Progress bar with ETA     |
| Site creation          | < 1s     | Step list (numbered)      |
| SSL cert generation    | < 1s     | Inline step               |
| db:import              | variable | Progress bar (file size)  |
