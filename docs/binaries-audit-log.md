# Binaries Audit Log

Rolling log of audit deltas against the live catalog at https://wdc.nks-hub.cz/api/v1/catalog and the GitHub releases on https://github.com/nks-hub/webdev-console-binaries. New sections appended at top.

For the full baseline audit with per-app coverage matrix, see [`binaries-audit-2026-04-18.md`](./binaries-audit-2026-04-18.md).

---

## 2026-04-18 16:09 UTC — Catalog + binaries live-verify sweep

**Catalog endpoints probed:** 61 URLs across 10 apps (`mkcert`, `mysql`, `redis`, `nginx`, `apache`, `mariadb`, `caddy`, `mailpit`, `cloudflared`, `php`)
**Binaries repo tags:** 22 tags, 57 assets total
**Method:** `curl -sI -L` per URL (HEAD, up to 5 redirects), size 0 tolerated (HEAD often omits Content-Length on CDN). Non-OK classes confirmed with a `GET -r 0-2047` range read.

### Anomalies

| App | Version | OS/Arch | Status | Notes |
|---|---|---|---|---|
| apache | 2.4.65 | windows/x64 | HTML_AS_BINARY | `https://www.apachelounge.com/download/VS18/binaries/httpd-2.4.65-250401-win64-VS18.zip` — ApacheLounge returns HTTP 200 with an HTML listing/error body when the file is no longer hosted; only 2.4.66 currently published. No canonical upstream for 2.4.65 Windows exists. |
| apache | 2.4.62 | windows/x64 | HTML_AS_BINARY | Same failure mode as 2.4.65 on `VS17/binaries/httpd-2.4.62-240718-win64-VS17.zip`. No canonical upstream. |
| php | 8.3.25 | windows/x64 | BROKEN_HTTP | `https://windows.php.net/downloads/releases/php-8.3.25-nts-Win32-vs16-x64.zip` → 404 (8.3.25 rotated out of /releases/ after 8.3.26 shipped). Fixed by re-routing to `/releases/archives/` — wdc-catalog-api commit `c21937a`. |

### Immediate action taken

- **php 8.3.25 windows/x64** → `wdc-catalog-api@c21937a` repoints URL to `/releases/archives/…` and adds a `notes` field flagging it as temporary until the catalog is bumped to the current 8.3 patch. Verified post-fix: HTTP 200, `application/zip`, 4,242,524 bytes, starts with `PK\x03\x04`.
- **apache 2.4.65 / 2.4.62 windows/x64** — NOT auto-patched. ApacheLounge does not host these builds anymore and nks-hub/webdev-console-binaries has no mirror tag for them (only `binaries-apache-2.4.66`). Remediation requires either (a) removing these versions from the catalog, (b) dispatching a new `build-apache.yml` run that targets 2.4.65/2.4.62 Windows so nks-hub gains a mirror tag, or (c) accepting that WDC cannot install Apache 2.4.65/2.4.62 on Windows at all. Owner decision needed.

### Stranded binaries releases (tag exists, no catalog URL references it)

All six are intentional: catalog points to an authoritative upstream instead of the in-house mirror, so the mirror tag sits as a fallback.

- `binaries-mariadb-12.3.1` — catalog uses `https://archive.mariadb.org/…-winx64.zip` (Windows only, no Linux/macOS in catalog)
- `binaries-mariadb-11.8.3` — same pattern
- `binaries-php-7.0.33` — catalog uses `https://windows.php.net/downloads/releases/archives/…`
- `binaries-php-5.6.40` — same pattern
- `binaries-redis-7.4.2` — catalog uses `https://github.com/redis-windows/redis-windows/releases/…`
- `binaries-mkcert-1.4.4` — catalog uses `https://github.com/FiloSottile/mkcert/releases/…`

### Broken catalog references (URL points to nonexistent release)

None. Every catalog URL pointing at `nks-hub/webdev-console-binaries` resolves to an existing tag and asset.

### Assets flagged in binaries repo

- `binaries-apache-2.4.66 / httpd-2.4.66-windows-x64.zip` — size 2,451 bytes (HTML error page, pre-existing known issue already temp-routed to ApacheLounge via `wdc-catalog-api@6b63ff9`). No other asset < 10 KB across the 57 assets.

### Summary

| Class | Count |
|---|---|
| OK | 58 |
| HTML_AS_BINARY | 2 |
| BROKEN_HTTP | 1 |
| TOO_SMALL | 0 |
| SIZE_MISMATCH | 0 |
| NO_FOLLOW | 0 |

---

## 2026-04-18 08:50 UTC — Triggered missing MariaDB builds

**Context:** Comprehensive audit published 08:38 UTC flagged `mariadb 11.8.3` and `12.3.1` as Windows-only (gap #2). Workflow `build-mariadb.yml` (id 262267922) exists and accepts a `version` input.

**Actions:**
- Dispatched `build-mariadb.yml` ref=main with `version=11.8.3` → run 24601143747 in_progress
- Dispatched `build-mariadb.yml` ref=main with `version=12.3.1`

**Expected outcome:** On success, the repo gains `binaries-mariadb-11.8.3` and `binaries-mariadb-12.3.1` release tags with Linux (+ possibly macOS) assets. The catalog entries will need a follow-up commit on `wdc-catalog-api` / `catalog-source` to add the new download URLs. Pure Windows-only state today; after the builds we can flip catalog to full triplet.

**CI baseline before trigger:**
- 5 most recent PHP builds 2026-04-17 — all success
- No active MariaDB runs before this dispatch


## 2026-04-18 09:08 UTC — MariaDB builds succeeded, catalog JSON updated (but live DB NOT updated)

**Builds completed:**
- `binaries-mariadb-11.8.3` published 08:50:56Z — linux-x64.tar.gz + windows-x64.msi
- `binaries-mariadb-12.3.1` published 08:51:39Z — linux-x64.tar.gz + windows-x64.msi

**Catalog source updated:** `wdc-catalog-api` commit `5dc6b7e` adds `linux/x64` downloads for both releases to `app/data/apps/mariadb.json`.

**BLOCKER for live rollout:** The JSON file is a first-boot seed only (`app/service.py:308 seed_from_json` short-circuits when `App` table has rows). The deployed DB at `wdc.nks-hub.cz` keeps the old windows-only entries. To publish the new URLs live, someone must apply them via the admin UI / API mutation endpoints (add_download per release), OR rebuild the prod DB from seed (data-loss risk).

**Next step:** Open a proper ticket for a "resync catalog JSON → DB" admin action, or accept that live updates flow only through the admin UI and not through git. Decision needed from owner.


## 2026-04-18 16:11 UTC — URL sweep second pass (range-GET probe)

**Method change:** HEAD+`-L` against GitHub-release URLs was returning final `302` (CDN signed URL doesn't support HEAD), producing 55 false positives. Switched to `GET -r 0-0` (1-byte range) which follows redirects to the real object → `200/206`. Zero false positives on this pass.

**Anomaly delta vs 16:09 sweep (same 3 real findings):**
- `apache 2.4.65 windows/x64` — HTML-as-ZIP (ApacheLounge rotated). **Owner decision pending** — no canonical upstream, no mirror tag.
- `apache 2.4.62 windows/x64` — same class as above. **Owner decision pending.**
- `php 8.3.25 windows/x64` — ✅ auto-patched in `wdc-catalog-api@c21937a` (redirect to `/releases/archives/`).

Full `/releases/archives/` scan confirms all PHP ≤8.3 Windows URLs still on `/releases/` (not rotated yet). PHP 8.3.25 was the only one at the cliff today.
