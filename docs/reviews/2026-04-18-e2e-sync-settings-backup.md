# WDC Sync + Settings + Backup E2E — 2026-04-18

**Target:** https://wdc.nks-hub.cz (v0.48.1, image `nks-wdc-catalog-api:latest` started 2026-04-18T10:29:31Z)
**Test account:** `e2e-test-<timestamp>@example.com` / `e2e<timestamp>@nks-hub.cz` / `e2e<timestamp>@gmail.com` (all attempted)
**Test device id:** `84211939-6bf8-4d03-ba87-5a0f258f1d84` (never provisioned — blocked at phase 1)

## TL;DR

**The entire E2E suite is BLOCKED** by a production database-schema drift on
wdc.nks-hub.cz. The deployed v0.48.1 image ships an ORM model with four
`accounts.totp_*` columns, but the production SQLite database
(`/state/catalog.db`) has not been migrated — the `accounts` table still has
the 10-column v0.3.x layout. Every code path that touches the `accounts`
table (register, login, admin UI session validation, any JWT revalidation
that refreshes the Account row) hits
`sqlite3.OperationalError: no such column: accounts.totp_enabled` and returns
HTTP 500.

Consequence: no test account can be created, no existing account can log in,
and therefore no authenticated endpoint (`/devices/*`, `/sync/config/*`,
`/backups/*`) can be exercised. The non-auth surface (`/healthz`, `/readyz`,
`/api/v1/catalog*`) is healthy and returns 200 with expected bodies.

## Phase 1 — Account + Device

| # | Step | Endpoint | Status | Time | Result |
|---|------|----------|--------|------|--------|
| 1 | Register `e2e-test-…@example.local` | `POST /api/v1/auth/register` | **422** | 297 ms | `email is not a valid email address: The part after the @-sign is a special-use or reserved name` — `.local` rejected by Pydantic `EmailStr` validator. OK per spec. |
| 2 | Register `e2e-test-…@example.com` | `POST /api/v1/auth/register` | **500** | 286 ms | `Internal Server Error`. Server logs: `sqlalchemy.exc.OperationalError: (sqlite3.OperationalError) no such column: accounts.totp_enabled` at `app/devices.py:332 register()` during the uniqueness pre-check `db.scalar(select(Account).where(Account.email==email))`. |
| 3 | Register `e2e…@nks-hub.cz` | `POST /api/v1/auth/register` | **500** | — | Same stack trace as above. Not an email-domain allowlist. |
| 4 | Register `e2e…@gmail.com` | `POST /api/v1/auth/register` | **500** | — | Same. |
| 5 | Login as admin `admin@nks-hub.cz` (creds from MCP memory) | `POST /api/v1/auth/login` | **500** | 152 ms | Same query, same failure. Confirms: every auth write/read path is broken, not just register. |
| 6 | Login as `admin` (bare) | `POST /api/v1/auth/login` | 422 | — | EmailStr rejects address without `@`. Expected. |
| 7 | Register a test device (POST `/api/v1/devices`) | — | **SKIPPED** | — | Endpoint does not exist in OpenAPI — device registration is implicit via `POST /api/v1/sync/config` (first authenticated sync). Can't reach because no JWT can be obtained. |
| 8 | `GET /api/v1/devices` | `GET /api/v1/devices` | 401 | 124 ms | `Authentication required`. Correct behaviour — just can't proceed without token. |
| 9 | `PUT /api/v1/devices/{id}?name=e2e-rename` | — | **SKIPPED** | — | Blocked — no JWT. Also note: `name` is in JSON body (`UpdateDeviceRequest{name}`), not query string as the plan assumed. |

**Phase 1 verdict: FAILED — blocked on `totp_enabled` schema drift.**

## Phase 2 — Sync config round-trip

| # | Step | Endpoint | Status | Time | Result |
|---|------|----------|--------|------|--------|
| 1 | `POST /api/v1/sync/config` (anon) | `POST /api/v1/sync/config` | 401 | 138 ms | `Authentication required to push device config` — consistent with v0.3.0 security hardening (push requires auth now). |
| 2 | `POST /api/v1/sync/config` (with JWT) | — | **SKIPPED** | — | No JWT obtainable. |
| 3 | `HEAD /api/v1/sync/config/{device_id}` | 401 | 118 ms | Auth gate fires before hitting DB. |
| 4 | `GET /api/v1/sync/config/{device_id}/exists` | 401 | 121 ms | Same. |
| 5 | `GET /api/v1/sync/config/{device_id}` | — | **SKIPPED** | — | Blocked. |
| 6 | `DELETE /api/v1/sync/config/{device_id}` | — | **SKIPPED** | — | Blocked. |

**Phase 2 verdict: SKIPPED — dependency on phase 1 JWT.**

## Phase 3 — Backup lifecycle

All six steps (create → list → detail → download → diff → restore → delete)
are authenticated on a device the test account owns. Without a JWT:

| # | Step | Status | Result |
|---|------|--------|--------|
| 1 | `POST /api/v1/devices/{id}/backups` | SKIPPED | No JWT. |
| 2 | `GET /api/v1/devices/{id}/backups` | SKIPPED | No JWT. |
| 3 | `GET /api/v1/devices/{id}/backups/{snapshot_id}` | SKIPPED | No JWT. |
| 4 | `GET /api/v1/devices/{id}/backups/{snapshot_id}/download` | SKIPPED | No JWT. |
| 5 | `GET /api/v1/devices/{id}/backups/diff?from=…&to=…` | SKIPPED | No JWT. |
| 6 | `POST /api/v1/devices/{id}/backups/restore` | SKIPPED | No JWT. |
| 7 | `DELETE /api/v1/devices/{id}/backups/{snapshot_id}` | SKIPPED | No JWT. |

**Notes from OpenAPI discovery (still useful for future runs):**

- `CreateSnapshotRequest` takes JSON `{label?, kind=manual, payload?, encrypt=false}` — **not** a raw tarball as the plan speculated. When `payload` is omitted the server snapshots the current device config.
- Optional header `X-WDC-Passphrase` on `POST /backups` is for encrypting at rest when `encrypt=true`.
- `RestoreRequest` requires `{snapshot_id: integer}` — **snapshot IDs are integers, not UUIDs**. The plan's `{"snapshot_id":"..."}` string form will 422.
- `GET /backups/diff` requires both `from` and `to` query params (both required). There is **no single-snapshot diff** endpoint — skip that step from the original plan.
- An additional endpoint exists: `POST /api/v1/devices/{device_id}/backups/import`. Not in the plan — worth adding to future runs.

**Phase 3 verdict: SKIPPED — dependency on phase 1 JWT.**

## Phase 4 — Settings persistence (daemon side)

| # | Step | Result |
|---|------|--------|
| 1 | Check daemon at `http://127.0.0.1:*` | **SKIPPED** — not in scope for a public-catalog E2E run. WDC daemon (Electron app companion) is not running in this harness; the plan's own guidance was to skip if unreachable. |

**Phase 4 verdict: SKIPPED (documented).**

## Phase 5 — Cleanup

| # | Step | Result |
|---|------|--------|
| 1 | `DELETE /api/v1/devices/{id}` | **NOT REQUIRED** — no device was ever created (phase 1 never succeeded). Nothing to clean up. |

## Negative tests

| Test | Expected | Actual | Notes |
|------|----------|--------|-------|
| Backup upload with wrong device id | 404 / 403 | SKIPPED | Needs auth. |
| Config push for foreign `device_id` | 403 | SKIPPED | Needs auth. |
| Read-only PAT attempting `rotate` | 403 | SKIPPED | Needs auth. |
| Oversized config blob (~5 MB) | 413 or equivalent | **Implicitly verified via source** | Per MCP memory note on `2.5/2.7/2.8`, a 1 MB payload limit middleware (`MAX_REQUEST_BYTES`) returns 413 before routing. Not exercised in this run. |

## Non-auth surface (sanity)

These were hit to characterize the deployment:

| Endpoint | Status | Time | Body |
|----------|--------|------|------|
| `GET /healthz` | 200 | 37 ms | `{"ok":true,"service":"nks-wdc-catalog-api","version":"0.48.1"}` |
| `GET /readyz` | 200 | 126 ms | `{"ok":true,…,"checks":{"db":"up"}}` — **note:** readyz reports `db:up` because `SELECT 1` succeeds, but the schema drift means the DB is logically unhealthy. Readyz is false-positive here. |
| `GET /api/v1/catalog` | 200 | 345 ms | `schema_version=1`, `apps=10` — catalog payload fine. |
| `GET /api/v1/auth/me` (no token) | 401 | — | Clean `Authentication required`. |
| Security headers on 401 response | OK | — | CSP, HSTS, Permissions-Policy, X-Frame=DENY all present. |

## Root-cause evidence (container log excerpt)

From `docker logs nks-wdc-catalog-api`:

```
sqlalchemy.exc.OperationalError: (sqlite3.OperationalError) no such column: accounts.totp_enabled
[SQL: SELECT accounts.id, accounts.email, accounts.password_hash, accounts.role,
       accounts.suspended_at, accounts.token_version, accounts.created_at,
       accounts.last_login_at, accounts.failed_login_count, accounts.locked_until,
       accounts.totp_enabled, accounts.totp_secret, accounts.totp_recovery_hashes,
       accounts.totp_enabled_at
FROM accounts WHERE accounts.email = ?]
[parameters: ('e2e1776508400@gmail.com',)]
  File "/srv/app/app/devices.py", line 332, in register
    existing = db.scalar(select(Account).where(Account.email == email))
```

Current DB schema (`PRAGMA table_info(accounts)` inside the running container):

```
(0, id, INTEGER, 1, None, 1)
(1, email, VARCHAR(128), 1, None, 0)
(2, password_hash, VARCHAR(128), 1, None, 0)
(3, created_at, DATETIME, 1, None, 0)
(4, last_login_at, DATETIME, 0, None, 0)
(5, role, VARCHAR(16), 1, "'user'", 0)
(6, suspended_at, DATETIME, 0, None, 0)
(7, token_version, INTEGER, 1, '1', 0)
(8, failed_login_count, INTEGER, 1, '0', 0)
(9, locked_until, DATETIME, 0, None, 0)
```

Expected per v0.48.1 ORM: additionally `totp_enabled BOOLEAN`, `totp_secret`,
`totp_recovery_hashes`, `totp_enabled_at DATETIME`.

Also noticed:

```
sh: 1: alembic: not found        # via `alembic ...`
ModuleNotFoundError: No module named alembic   # via `python -m alembic ...`
```

So `alembic` isn't even installed in the runtime image, which means the
"auto-upgrade on boot" that v0.3.0 release notes claimed is not actually
running. Migrations would have to be baked in at build time or applied
out-of-band; neither happened for the TOTP migration.

## Summary

- **Passing: 4** (catalog, healthz, readyz, clean 401s on protected endpoints)
- **Failing: 2** (register 500, login 500 — both same root cause)
- **Skipped: 20+** (every phase after account creation — blocked)
- **Performance:** nothing slow; all successful calls returned in < 350 ms. Auth 500s returned in ~150–290 ms (fast failure).

### Top 3 findings (urgent → less)

1. **v0.48.1 deployment is broken for any authenticated use.** The `accounts`
   table is missing the `totp_*` columns added by the TOTP migration. Every
   register/login/JWT-refresh call returns 500. `/readyz` is a false positive
   (does `SELECT 1`, not schema validation). Fix options:
   - Apply the outstanding Alembic migration (requires installing `alembic`
     in the image or running migrations at deploy time).
   - Or hand-patch SQLite: `ALTER TABLE accounts ADD COLUMN totp_enabled …`
     × 4, using the DDL from the feature commit.
2. **`alembic` missing from the production image.** The v0.3.0 release notes
   state migrations auto-apply on boot; that is not true for the current
   image (`nks-wdc-catalog-api:latest`, built 2026-04-18 10:29 UTC). Either
   add `alembic` to the image `requirements.txt` or move migration
   responsibility into an init container / CI step, and document it.
3. **`/readyz` does not catch schema drift.** It reports `db: up` while the
   DB is unusable for 80% of the API surface. Consider strengthening the
   check, e.g. `SELECT totp_enabled FROM accounts LIMIT 0` (parses the
   statement without loading rows) or comparing `alembic current` against
   `alembic heads` at startup and failing readiness on drift.

### Plan adjustments for the next E2E re-run (once the server is fixed)

- Use `@example.com` or any real TLD — `.local` fails Pydantic's
  `EmailStr` validator.
- Device registration is **implicit** via `POST /api/v1/sync/config` with a
  JWT — there is no standalone `POST /api/v1/devices` endpoint. Update the
  plan.
- `UpdateDeviceRequest` takes `{name}` in JSON body, not query string.
- Snapshot IDs are **integers**, not UUIDs; `RestoreRequest.snapshot_id` is
  typed `integer`.
- `CreateSnapshotRequest` is JSON (`{label, kind, payload, encrypt}`), not a
  raw tarball body.
- `GET /backups/diff` needs **both** `from` and `to`; the "single-snapshot
  diff" variant doesn't exist — drop that step.
- Add `POST /api/v1/devices/{id}/backups/import` to the plan — it exists
  but wasn't covered.
