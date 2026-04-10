# NKS WebDev Console — E2E Test Scenarios (v1 Release Readiness)

**Stack**: Electron + Vue 3 | C# .NET 9 daemon | 8 plugin DLLs  
**Auth**: Bearer token — `%TEMP%\nks-wdc-daemon.port` (line 1 = port, line 2 = token)  
**Data root**: `%USERPROFILE%\.wdc\` | **CLI**: `wdc.exe`

```bash
PORT=$(head -1 "$TEMP/nks-wdc-daemon.port")
TOKEN=$(tail -1 "$TEMP/nks-wdc-daemon.port")
API="http://localhost:$PORT"
AUTH="Authorization: Bearer $TOKEN"
```

---

## Scenario 1 — WordPress + PHP 8.3 + MySQL + SSL (GUI wizard)

**Preconditions**: Apache, MySQL, PHP 8.3, SSL plugins Running. mkcert CA trusted. `myblog.loc` absent.

**Steps**
1. GUI: Sites → New Site → Domain `myblog.loc`, Docroot `C:\work\htdocs\myblog\wp`, PHP `8.3`, SSL on → Create.
2. Framework auto-detection:
   ```bash
   curl -s -H "$AUTH" "$API/api/sites/myblog.loc/detect-framework"
   ```
3. Verify generated vhost and cert:
   ```bash
   ls "$USERPROFILE/.wdc/generated/" | grep myblog
   ls "$USERPROFILE/.wdc/ssl/sites/myblog.loc/"
   ```
4. Verify hosts entry:
   ```powershell
   Get-Content C:\Windows\System32\drivers\etc\hosts | Select-String "myblog.loc"
   ```
5. Browser access and CLI confirmation:
   ```bash
   curl -sk https://myblog.loc -o /dev/null -w "%{http_code} %{ssl_verify_result}\n"
   wdc sites list --json | python -m json.tool | grep myblog
   ```

**Expected results**: `POST /api/sites` → 201. `detect-framework` → `{"framework":"wordpress"}`. Cert SAN matches domain. Hosts managed block contains `127.0.0.1\tmyblog.loc`. Browser: HTTP 200, ssl_verify_result=0.

**Cleanup**: `wdc sites delete myblog.loc`

---

## Scenario 2 — Laravel + PHP 8.2 + MariaDB (CLI)

**Preconditions**: PHP 8.2, Apache, MySQL Running. Docroot `C:\work\htdocs\myapp\public` contains `artisan`.

**Steps**
1. Create site:
   ```bash
   wdc sites create --domain myapp.loc --docroot "C:\work\htdocs\myapp\public" --php 8.2 --ssl
   wdc sites detect myapp.loc
   ```
2. Create DB and run migrations:
   ```bash
   curl -s -X POST -H "$AUTH" -H "Content-Type: application/json" \
     -d '{"name":"myapp_db"}' "$API/api/databases"
   cd C:\work\htdocs\myapp && php artisan migrate --force
   ```
3. Verify API endpoint and DB tables:
   ```bash
   curl -sk https://myapp.loc/api/health
   curl -s -H "$AUTH" "$API/api/databases/myapp_db/tables" | python -m json.tool
   ```

**Expected results**: `wdc sites create` exits 0 (201). `detect` returns `laravel`. `artisan migrate` exits 0. `https://myapp.loc/api/health` → 200 `{"status":"ok"}`. Tables include migration tables.

**Cleanup**:
```bash
wdc sites delete myapp.loc
curl -X DELETE -H "$AUTH" "$API/api/databases/myapp_db"
```

---

## Scenario 3 — Static HTML (no PHP)

**Preconditions**: Apache Running. `C:\work\htdocs\static\index.html` exists.

**Steps**
1. Create site with `phpVersion:"none"`:
   ```bash
   curl -s -X POST -H "$AUTH" -H "Content-Type: application/json" \
     -d '{"domain":"static.loc","documentRoot":"C:\\work\\htdocs\\static","phpVersion":"none","sslEnabled":false,"httpPort":80,"httpsPort":443,"aliases":[],"environment":{}}' \
     "$API/api/sites"
   ```
2. Verify no PHP directives in vhost and file is served:
   ```bash
   cat "$USERPROFILE/.wdc/generated/static.loc.conf" | grep -i "fcgi\|php"
   curl -s http://static.loc/index.html -o /dev/null -w "%{http_code}\n"
   ```
3. Confirm PHP not executed (raw source returned for .php files):
   ```bash
   echo '<?php echo "alive";' > C:\work\htdocs\static\test.php
   curl -s http://static.loc/test.php | grep -v "alive"
   ```

**Expected results**: Vhost has no `FCGIWrapper`. `index.html` → 200, `Content-Type: text/html`. PHP source returned as-is or 403 — never executed.

**Cleanup**: `wdc sites delete static.loc && rm C:\work\htdocs\static\test.php`

---

## Scenario 4 — PHP version switch (8.2 → 8.4, runtime change)

**Preconditions**: Site `phpswitch.loc` running PHP 8.2. PHP 8.4 binary installed. `phpinfo.php` in docroot.

**Steps**
1. Verify current version:
   ```bash
   curl -s http://phpswitch.loc/phpinfo.php | grep "PHP Version" | head -1
   ```
2. Update via CLI and verify config:
   ```bash
   wdc sites update phpswitch.loc --php 8.4
   curl -s -H "$AUTH" "$API/api/sites/phpswitch.loc" | python -m json.tool | grep phpVersion
   ```
3. Verify runtime change (no Apache restart):
   ```bash
   curl -s http://phpswitch.loc/phpinfo.php | grep "PHP Version" | head -1
   cat "$USERPROFILE/.wdc/generated/phpswitch.loc.conf" | grep "FCGIWrapper\|php-cgi"
   ```

**Expected results**: `PUT /api/sites/phpswitch.loc` → 200. Vhost regenerated, Apache graceful reload triggered. PHP 8.4 serves requests within 2 seconds. Vhost references 8.4 binary path.

**Cleanup**: `wdc sites delete phpswitch.loc`

---

## Scenario 5 — Database import/export round-trip (50 MB)

**Preconditions**: MySQL Running. `C:\work\test-data\dump.sql` (~50 MB) exists.

**Steps**
1. Create DB and import:
   ```bash
   curl -s -X POST -H "$AUTH" -H "Content-Type: application/json" \
     -d '{"name":"importtest"}' "$API/api/databases"
   curl -s -X POST -H "$AUTH" -F "file=@C:\work\test-data\dump.sql" \
     "$API/api/databases/importtest/import"
   ```
2. Note table list, then export:
   ```bash
   curl -s -H "$AUTH" "$API/api/databases/importtest/tables" | python -m json.tool
   curl -s -H "$AUTH" "$API/api/databases/importtest/export" -o exported.sql
   ```
3. Round-trip — import export into new DB and compare:
   ```bash
   curl -s -X POST -H "$AUTH" -H "Content-Type: application/json" \
     -d '{"name":"roundtrip"}' "$API/api/databases"
   curl -s -X POST -H "$AUTH" -F "file=@exported.sql" \
     "$API/api/databases/roundtrip/import"
   diff <(curl -s -H "$AUTH" "$API/api/databases/importtest/tables" | python -m json.tool | sort) \
        <(curl -s -H "$AUTH" "$API/api/databases/roundtrip/tables" | python -m json.tool | sort)
   ```

**Expected results**: Import → 200, completes under 60 s. Export is valid SQL with `CREATE TABLE` statements. Round-trip diff is empty. Daemon heap stays under 500 MB.

**Cleanup**:
```bash
curl -X DELETE -H "$AUTH" "$API/api/databases/importtest"
curl -X DELETE -H "$AUTH" "$API/api/databases/roundtrip"
rm exported.sql
```

---

## Scenario 6 — Hosts file lifecycle + backup rotation

**Preconditions**: Clean hosts file (no NKS managed block). Hosts plugin enabled.

**Steps**
1. Create 5 sites and verify managed block:
   ```bash
   for d in site1.loc site2.loc site3.loc site4.loc site5.loc; do
     wdc sites create --domain $d --docroot "C:\work\htdocs\$d" --php 8.4
   done
   Get-Content C:\Windows\System32\drivers\etc\hosts
   ls C:\Windows\System32\drivers\etc\hosts.wdc-backup.*
   ```
2. Delete 2 sites, verify block has 3 entries:
   ```bash
   wdc sites delete site4.loc && wdc sites delete site5.loc
   (Get-Content C:\Windows\System32\drivers\etc\hosts | Select-String "\.loc").Count
   ```
3. Verify content outside managed block is untouched:
   ```powershell
   ($h = Get-Content C:\Windows\System32\drivers\etc\hosts -Raw) -split "# BEGIN NKS WebDev Console" | Select-Object -First 1
   ```
4. Create 6th site, verify still exactly 5 backups (rotation):
   ```bash
   wdc sites create --domain site6.loc --docroot "C:\work\htdocs\site6" --php 8.4
   (ls C:\Windows\System32\drivers\etc\hosts.wdc-backup.*).Count
   ```

**Expected results**: Managed block isolated between `# BEGIN NKS WebDev Console` / `# END NKS WebDev Console`. Outside content unchanged. Backups shift .1→.2→.3→.4→.5, never exceed 5. UAC prompt only when content actually changes.

**Cleanup**: `for d in site1.loc site2.loc site3.loc site6.loc; do wdc sites delete $d; done`

---

## Scenario 7 — SSL regeneration

**Preconditions**: Site `ssl-test.loc` with valid cert. Browser previously visited the site.

**Steps**
1. Record cert fingerprint:
   ```bash
   openssl x509 -in "$USERPROFILE/.wdc/ssl/sites/ssl-test.loc/ssl-test.loc.pem" -fingerprint -noout
   ```
2. Delete and regenerate:
   ```bash
   curl -s -X DELETE -H "$AUTH" "$API/api/ssl/certs/ssl-test.loc"
   curl -s -X POST -H "$AUTH" -H "Content-Type: application/json" \
     -d '{"domain":"ssl-test.loc"}' "$API/api/ssl/generate"
   ```
3. Verify new fingerprint and Apache graceful reload:
   ```bash
   openssl x509 -in "$USERPROFILE/.wdc/ssl/sites/ssl-test.loc/ssl-test.loc.pem" -fingerprint -noout
   curl -s -H "$AUTH" "$API/api/services/apache/logs?lines=10" | grep -i "graceful\|reload"
   ```
4. Verify HTTPS and SAN:
   ```bash
   curl -sk https://ssl-test.loc -o /dev/null -w "%{http_code} %{ssl_verify_result}\n"
   openssl x509 -in "$USERPROFILE/.wdc/ssl/sites/ssl-test.loc/ssl-test.loc.pem" -text -noout | grep "DNS:"
   ```

**Expected results**: `DELETE` → 200. `generate` → 200. New cert has different fingerprint. Apache graceful reload in log. Browser: 200, ssl_verify_result=0. SAN: `DNS:ssl-test.loc`.

**Cleanup**: `wdc sites delete ssl-test.loc`

---

## Scenario 8 — Plugin enable/disable (no daemon restart)

**Preconditions**: Redis plugin installed and enabled. GUI shows Redis in sidebar.

**Steps**
1. Disable Redis:
   ```bash
   curl -s -X POST -H "$AUTH" "$API/api/plugins/redis/disable"
   curl -s -H "$AUTH" "$API/api/plugins" | python -m json.tool | grep -A3 '"redis"'
   ```
2. Verify Redis absent from active services. Re-enable:
   ```bash
   curl -s -H "$AUTH" "$API/api/services" | python -m json.tool | grep redis
   curl -s -X POST -H "$AUTH" "$API/api/plugins/redis/enable"
   curl -s -H "$AUTH" "$API/api/plugins" | python -m json.tool | grep -A3 '"redis"'
   ```
3. Start Redis and verify Running:
   ```bash
   curl -s -X POST -H "$AUTH" "$API/api/services/redis/start"
   sleep 2
   curl -s -H "$AUTH" "$API/api/services/redis" | python -m json.tool | grep status
   ```

**Expected results**: `disable` → 200. Plugin `enabled:false`. Service absent or `"disabled"` status. `enable` → 200. GUI sidebar updates via SSE within 2 s (no page reload). `start` → service `"running"`.

**Cleanup**: `curl -s -X POST -H "$AUTH" "$API/api/services/redis/stop"`

---

## Scenario 9 — Backup/restore after state.db corruption

**Preconditions**: At least 2 sites configured, daemon Running.

**Steps**
1. Snapshot state and create backup:
   ```bash
   wdc sites list --json > /tmp/sites-before.json
   $ts = Get-Date -Format "yyyyMMdd-HHmm"
   Copy-Item -Recurse "$env:USERPROFILE\.wdc" "$env:TEMP\wdc-backup-$ts" -Force
   ```
2. Corrupt state.db and restart daemon:
   ```bash
   python -c "
   with open('$USERPROFILE/.wdc/data/state.db','r+b') as f: f.seek(16); f.write(b'\x00'*20)"
   # Restart daemon — expect degraded/error state
   wdc status --json
   ```
3. Restore from backup:
   ```powershell
   Stop-Process -Name "nks-wdc-daemon" -Force -ErrorAction SilentlyContinue
   $latest = (ls "$env:TEMP\wdc-backup-*" | Sort-Object LastWriteTime | Select-Object -Last 1).FullName
   Remove-Item -Recurse "$env:USERPROFILE\.wdc\data" -Force
   Copy-Item -Recurse "$latest\data" "$env:USERPROFILE\.wdc\data"
   ```
4. Restart daemon and verify integrity:
   ```bash
   wdc status --json
   wdc sites list --json > /tmp/sites-after.json
   diff /tmp/sites-before.json /tmp/sites-after.json
   ls "$USERPROFILE/.wdc/generated/"
   ```

**Expected results**: After restore, MigrationRunner succeeds. All sites intact (TOML files survive, not in state.db). Daemon starts within 5 s. `diff` is empty. Generated vhosts present.

**Cleanup**: `rm /tmp/sites-before.json /tmp/sites-after.json`

---

## Scenario 10 — Caddy as alternative web server

**Preconditions**: Caddy plugin installed with binary. Apache Running on port 80.

**Steps**
1. Start Caddy and create site on port 8080:
   ```bash
   curl -s -X POST -H "$AUTH" "$API/api/services/caddy/start"
   sleep 2
   curl -s -X POST -H "$AUTH" -H "Content-Type: application/json" \
     -d '{"domain":"caddy-site.loc","documentRoot":"C:\\work\\htdocs\\caddy-site","phpVersion":"none","sslEnabled":false,"httpPort":8080,"httpsPort":8443,"aliases":[],"environment":{}}' \
     "$API/api/sites"
   ```
2. Add Caddyfile fragment and reload:
   ```bash
   cat > "$USERPROFILE/.wdc/caddy/sites-enabled/caddy-site.loc.caddy" << 'EOF'
   caddy-site.loc:8080 {
     root * C:\work\htdocs\caddy-site
     file_server
   }
   EOF
   curl -s -X POST -H "$AUTH" "$API/api/services/caddy/restart"
   sleep 2
   ```
3. Verify Caddy serves on 8080 and Apache still serves on 80:
   ```bash
   curl -s http://caddy-site.loc:8080/ -o /dev/null -w "%{http_code}\n"
   curl -s http://site1.loc/ -o /dev/null -w "%{http_code}\n"
   curl -s -H "$AUTH" "$API/api/services/caddy/config" | python -m json.tool
   ```

**Expected results**: Caddy `"running"`. Port 8080 → 200. Apache port 80 → 200. No port conflicts. Caddyfile fragment loaded via `import ~/.wdc/caddy/sites-enabled/*.caddy`.

**Cleanup**:
```bash
curl -s -X POST -H "$AUTH" "$API/api/services/caddy/stop"
wdc sites delete caddy-site.loc
rm "$USERPROFILE/.wdc/caddy/sites-enabled/caddy-site.loc.caddy"
```

---

## Scenario 11 — Wildcard alias site

**Preconditions**: Apache, SSL, Hosts plugins Running. mkcert supports wildcard certs.

**Steps**
1. Create site with wildcard alias:
   ```bash
   curl -s -X POST -H "$AUTH" -H "Content-Type: application/json" \
     -d '{"domain":"myapp.loc","documentRoot":"C:\\work\\htdocs\\myapp\\public","phpVersion":"8.4","sslEnabled":true,"httpPort":80,"httpsPort":443,"aliases":["*.myapp.loc"],"environment":{}}' \
     "$API/api/sites"
   ```
2. Verify vhost ServerAlias, cert SAN, and hosts file:
   ```bash
   cat "$USERPROFILE/.wdc/generated/myapp.loc.conf" | grep "ServerAlias"
   openssl x509 -in "$USERPROFILE/.wdc/ssl/sites/myapp.loc/myapp.loc.pem" -text -noout | grep "DNS:"
   Get-Content C:\Windows\System32\drivers\etc\hosts | Select-String "myapp.loc"
   ```
3. Test base domain and a subdomain (add manual hosts entry for subdomain):
   ```bash
   curl -sk https://myapp.loc -o /dev/null -w "%{http_code}\n"
   Add-Content C:\Windows\System32\drivers\etc\hosts "127.0.0.1`ttenant1.myapp.loc"
   curl -sk https://tenant1.myapp.loc -o /dev/null -w "%{http_code}\n"
   ```

**Expected results**: Vhost: `ServerName myapp.loc` + `ServerAlias *.myapp.loc`. Cert SAN: `DNS:myapp.loc, DNS:*.myapp.loc`. Hosts: only `127.0.0.1 myapp.loc` (wildcard silently skipped). Both `myapp.loc` and `tenant1.myapp.loc` → 200, no cert warning.

**Cleanup**: `wdc sites delete myapp.loc` + remove `tenant1.myapp.loc` hosts entry.

---

## Scenario 12 — Concurrent service start (Start All)

**Preconditions**: All 6 services (Apache, MySQL, PHP, Redis, Mailpit, Caddy) installed and Stopped.

**Steps**
1. Ensure all stopped:
   ```bash
   for svc in apache mysql php redis mailpit caddy; do
     curl -s -X POST -H "$AUTH" "$API/api/services/$svc/stop" > /dev/null; done
   sleep 3
   curl -s -H "$AUTH" "$API/api/services" | python -m json.tool | grep '"status"'
   ```
2. GUI: Dashboard → click "Start All". Monitor SSE stream:
   ```bash
   curl -s -H "$AUTH" "$API/api/events" &
   ```
3. After 30 s verify all Running:
   ```bash
   sleep 30
   curl -s -H "$AUTH" "$API/api/services" | python -m json.tool | grep '"status"'
   curl -s -H "$AUTH" "$API/api/services" | python -m json.tool | grep -c '"running"'
   ```

**Expected results**: All 6 services → `"running"` within 30 s. SSE emits `starting` then `running` events per service. No port conflicts. Dashboard sparklines update for all services. `grep -c '"running"'` → 6.

---

## Scenario 13 — Daemon crash recovery and auto-reconnect

**Preconditions**: Electron app open, daemon Running, one service active.

**Steps**
1. Trigger long operation then kill daemon:
   ```bash
   curl -s -X POST -H "$AUTH" "$API/api/services/apache/restart" > /dev/null &
   taskkill /F /IM nks-wdc-daemon.exe
   ```
2. Observe GUI: connection indicator goes orange/red.
3. Wait for auto-reconnect (Electron preload restarts daemon):
   ```bash
   sleep 5
   cat "$TEMP/nks-wdc-daemon.port"
   wdc status --json
   curl -s "http://localhost:$(head -1 $TEMP/nks-wdc-daemon.port)/healthz"
   ```
4. Verify no zombie child processes:
   ```bash
   Get-Process httpd, mysqld, redis-server -ErrorAction SilentlyContinue | Select-Object Id, ProcessName
   ```

**Expected results**: EPIPE handled gracefully (commit 4854147). Daemon restarts, writes new port file. Electron reconnects within 2 s, resumes SSE stream. `/healthz` → `{"ok":true}`. Site TOML configs intact (on disk, not in RAM).

---

## Scenario 14 — Large log stream (10k lines, xterm.js)

**Preconditions**: Apache Running, LogViewer page open.

**Steps**
1. Generate 10k log lines:
   ```bash
   for i in $(seq 1 10000); do curl -s http://localhost/ -o /dev/null; done &
   ```
2. In GUI: open LogViewer for Apache, observe rendering (no UI freeze, progressive scroll).
3. Test Find (Ctrl+F) and copy:
   - Search `GET /` — verify matches highlighted, count badge shown
   - Select 100 lines, Ctrl+C, paste into text editor — verify integrity
4. Verify API returns efficiently:
   ```bash
   time curl -s -H "$AUTH" "$API/api/services/apache/logs?lines=10000" -o /tmp/logs.json
   cat /tmp/logs.json | python -c "import json,sys; d=json.load(sys.stdin); print(len(d.get('lines',d)))"
   ```

**Expected results**: xterm.js renders 10k lines, no >100 ms frame drops. Find highlights correct lines. API log endpoint responds under 3 s. Renderer memory under 500 MB. Line count up to 10000.

**Cleanup**: `rm /tmp/logs.json`

---

## Scenario 15 — Monaco config edit + apply + rollback

**Preconditions**: Apache Running. At least one site configured.

**Steps**
1. Open Apache service → "Edit Config" in GUI (Monaco editor loads with Apache syntax highlighting).
2. Retrieve current config for reference:
   ```bash
   curl -s -H "$AUTH" "$API/api/services/apache/config" -o /tmp/apache-before.txt
   ```
3. Validate before editing:
   ```bash
   curl -s -X POST -H "$AUTH" -H "Content-Type: text/plain" \
     --data-binary @/tmp/apache-before.txt "$API/api/config/validate"
   ```
4. In Monaco: add `Header always set X-WDC-Test "e2e"` inside a VirtualHost block. Save (Ctrl+S).
5. Verify Apache graceful reload and new header:
   ```bash
   curl -s -H "$AUTH" "$API/api/services/apache/logs?lines=5" | grep -i "graceful\|reload"
   curl -sI http://localhost/ | grep "X-WDC-Test"
   ```
6. Test rollback via history API:
   ```bash
   DOMAIN=$(wdc sites list --json | python -c "import json,sys; d=json.load(sys.stdin); print(d[0]['domain'])")
   curl -s -H "$AUTH" "$API/api/sites/$DOMAIN/history" | python -m json.tool
   # Get a timestamp and rollback:
   TS=$(curl -s -H "$AUTH" "$API/api/sites/$DOMAIN/history" | python -c "import json,sys; h=json.load(sys.stdin); print(h[1]['timestamp'])")
   curl -s -X POST -H "$AUTH" "$API/api/sites/$DOMAIN/rollback/$TS"
   curl -sI http://localhost/ | grep "X-WDC-Test"
   ```

**Expected results**: Monaco loads with Apache syntax grammar. `validate` → `{"valid":true}`. Save triggers graceful reload. `X-WDC-Test: e2e` header present. After rollback, header absent. History endpoint returns JSON array with previous timestamps.

**Cleanup**: `rm /tmp/apache-before.txt`

---

## Agentní automatizace

### Přístup k aplikaci

**A) Playwright přes CDP** — spustit Electron s `--remote-debugging-port=9222`:
```typescript
import { chromium } from 'playwright';
const browser = await chromium.connectOverCDP('http://localhost:9222');
const page = browser.contexts()[0].pages().find(p => p.url().startsWith('app://'))!;
```

**B) REST API + CLI (headless)** — scénáře 1–11 nevyžadují GUI. Kombinace `curl` + `wdc.exe` + PowerShell.

### Shell script šablona (scénář 1)

```bash
#!/usr/bin/env bash
set -euo pipefail
PORT=$(head -1 "$TEMP/nks-wdc-daemon.port")
TOKEN=$(tail -1 "$TEMP/nks-wdc-daemon.port")
API="http://localhost:$PORT"; AUTH="Authorization: Bearer $TOKEN"
DOMAIN="myblog-e2e.loc"
trap "wdc sites delete $DOMAIN 2>/dev/null" EXIT

STATUS=$(curl -s -o /dev/null -w "%{http_code}" -X POST -H "$AUTH" \
  -H "Content-Type: application/json" \
  -d "{\"domain\":\"$DOMAIN\",\"documentRoot\":\"C:\\\\work\\\\htdocs\\\\myblog-e2e\\\\wp\",\"phpVersion\":\"8.3\",\"sslEnabled\":true,\"httpPort\":80,\"httpsPort\":443,\"aliases\":[],\"environment\":{}}" \
  "$API/api/sites")
[ "$STATUS" = "201" ] || { echo "FAIL create=$STATUS"; exit 1; }

FW=$(curl -s -X POST -H "$AUTH" "$API/api/sites/$DOMAIN/detect-framework" \
  | python -m json.tool | grep -o '"wordpress"')
[ "$FW" = '"wordpress"' ] || { echo "FAIL framework"; exit 1; }
[ -f "$USERPROFILE/.wdc/ssl/sites/$DOMAIN/$DOMAIN.pem" ] || { echo "FAIL cert"; exit 1; }
echo "PASS: Scenario 1"
```

### Playwright GUI (scénář 12)

```typescript
import { test, expect } from '@playwright/test';
test('Start All → 6 services running', async ({ page }) => {
  await page.goto('app://./index.html#/');
  await page.getByRole('button', { name: /start all/i }).click();
  await expect(page.locator('[data-status="running"]')).toHaveCount(6, { timeout: 30000 });
});
```

### MCP Playwright sekvence

```
browser_navigate    → app://./index.html
browser_snapshot    → zachytit stav DOM
browser_click       → "Start All"
browser_wait_for    → [data-status="running"] count=6
browser_take_screenshot → evidence
```

### CI/CD (windows-latest)

```yaml
jobs:
  e2e:
    runs-on: windows-latest
    steps:
      - uses: actions/checkout@v4
      - run: dotnet build src/daemon/NKS.WebDevConsole.sln -c Release
      - run: |
          Start-Process "src\daemon\NKS.WebDevConsole.Daemon\bin\Release\net9.0\win-x64\NKS.WebDevConsole.Daemon.exe"
          Start-Sleep 3
        shell: pwsh
      - run: bash e2e/run-all-scenarios.sh
        shell: bash
      - if: always()
        run: Stop-Process -Name NKS.WebDevConsole.Daemon -Force
        shell: pwsh
```

### Prioritizace pro CI

| Priorita | Scénáře | Typ | Odhadovaný čas |
|----------|---------|-----|----------------|
| P0 smoke | 3, 4, 8, 13 | API only | ~2 min |
| P1 regression | 1, 2, 5, 6, 12 | API + CLI | ~8 min |
| P2 full | 7, 9, 10, 11, 14, 15 | GUI + API | ~20 min |

P0 na každý PR, P1 na merge do main, P2 před release.
