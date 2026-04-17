# Catalog API — Split + Hardening + Backup Feature Implementation Plan

> **For agentic workers:** Execute task-by-task. Steps use checkbox (`- [ ]`) syntax. Phase 0 runs in the monorepo; Phases 1+ run in the new standalone repo `nks-hub/wdc-catalog-api`.

**Goal:** Extract catalog-api from the monorepo into a standalone public repo, then harden security (4 CRITICAL + 9 HIGH findings), then add versioned config backup feature.

**Architecture:** Python 3.12 FastAPI + SQLAlchemy 2.x + Alembic. Extract with `git filter-repo` to preserve history. OpenAPI artifact is the contract between the new repo and the desktop daemon's `CatalogClient.cs`.

**Working directories:**
- Monorepo: `C:\work\sources\nks-ws` — Phase 0 extraction + cleanup
- New repo clone: `C:\work\sources\wdc-catalog-api` — Phases 1+ all development

**Monorepo branch:** `feature/catalog-api-hardening`
**New-repo branch:** `main`

---

## Phase 0 — Extract to standalone repo (do first, commit atomically in new repo from here on)

### Task 0.1 — Verify prerequisites

**Files:** none

- [ ] **Step 1: Verify `git-filter-repo` available**
Run: `git filter-repo --version` (if missing, install via `pip install git-filter-repo`)

- [ ] **Step 2: Verify `gh` CLI authenticated to nks-hub org**
Run: `gh auth status && gh repo list nks-hub --limit 5`

- [ ] **Step 3: Audit for AI-attribution before extraction**
Run: `git log --all --format="%B" -- services/catalog-api | grep -i -E "claude|anthropic|generated with|co-authored.*claude"`
Expected: empty (if not, rewrite messages before extraction)

- [ ] **Step 4: Audit for secrets in history**
Run: `git log --all -p -- services/catalog-api | grep -i -E "password|secret|token" | head -20`
Inspect manually — expect only code references, no literal values.

### Task 0.2 — Extract with history via `git filter-repo`

**Files:** local clone only

- [ ] **Step 1: Fresh clone for extraction (don't mutate working repo)**
```bash
cd /c/work/sources
git clone nks-ws wdc-catalog-api-extract
cd wdc-catalog-api-extract
```

- [ ] **Step 2: `git filter-repo --subdirectory-filter services/catalog-api`**

- [ ] **Step 3: Verify history preserved**
`git log --oneline | head -20` shows catalog commits with original authors/dates.

- [ ] **Step 4: Analyze for stray refs/secrets**
`git filter-repo --analyze` then inspect `.git/filter-repo/analysis/blob-shas-and-paths.txt`.

### Task 0.3 — Scaffold new repo files

**Files (in extracted clone):**
- Create: `README.md` (standalone, not the fragment)
- Create: `LICENSE` (MIT)
- Create: `.github/workflows/ci.yml`
- Create: `.github/workflows/release.yml`
- Create: `.github/dependabot.yml`
- Create: `CODEOWNERS`
- Create: `SECURITY.md`
- Create: `.gitignore` (Python + editor patterns)

- [ ] **Step 1: Write `README.md` with quickstart, architecture diagram, env vars, deploy**
- [ ] **Step 2: `LICENSE` — MIT, copyright "2026 NKS Hub"**
- [ ] **Step 3: `ci.yml` — ruff, mypy, pytest, pip-audit, docker build smoke**
- [ ] **Step 4: `release.yml` — on tag `v*`: docker build + push to `ghcr.io/nks-hub/wdc-catalog-api`, upload `openapi.json` as release asset**
- [ ] **Step 5: `.gitignore` with nondescript pattern `.[a-z]*/` + whitelist `!.github/`, `!.gitignore`, plus Python artifacts**
- [ ] **Step 6: `CODEOWNERS` → `@nks-hub/backend` for default**
- [ ] **Step 7: `SECURITY.md` — disclosure policy**
- [ ] **Step 8: Commit** `chore: standalone repo scaffolding`

### Task 0.4 — Create GitHub repo + push

- [ ] **Step 1: `gh repo create nks-hub/wdc-catalog-api --public --license MIT --description "NKS WebDev Console cloud catalog + config sync API"`**

- [ ] **Step 2: Set topics**
```bash
gh repo edit nks-hub/wdc-catalog-api \
  --add-topic fastapi --add-topic python --add-topic docker \
  --add-topic webdev-console --add-topic nks-hub \
  --add-topic config-sync --add-topic binary-catalog \
  --add-topic api --add-topic public-api
```

- [ ] **Step 3: Push main + tags**
```bash
git remote remove origin
git remote add origin git@github.com:nks-hub/wdc-catalog-api.git
git push -u origin main
```

- [ ] **Step 4: Verify landing page renders README, topics show**

### Task 0.5 — Monorepo cleanup commit

**Files (back in `C:\work\sources\nks-ws`):**
- Delete: `services/catalog-api/`
- Create: `services/catalog-api-MOVED.md`
- Modify: root `README.md` — update catalog section with link

- [ ] **Step 1: Tag pre-split state**
```bash
cd /c/work/sources/nks-ws
git tag catalog-api-pre-split
git push --tags
```

- [ ] **Step 2: `git rm -r services/catalog-api`**

- [ ] **Step 3: Create `services/catalog-api-MOVED.md` pointing to new repo**

- [ ] **Step 4: Commit** `chore: extract catalog-api to nks-hub/wdc-catalog-api`

- [ ] **Step 5: Push `feature/catalog-api-hardening` branch**

### Task 0.6 — Switch working directory

- [ ] **Step 1: All subsequent Phase 1+ work happens in `C:\work\sources\wdc-catalog-api-extract` (rename to `wdc-catalog-api`)**

- [ ] **Step 2: From here on, paths in the plan are relative to the new repo root (no `services/catalog-api/` prefix)**

---

## Phase 1 — Critical security hotfix (in new repo)

### Task 1.1 — Fail-fast missing secrets in production

**Files:**
- Modify: `app/devices.py:34-41`
- Modify: `app/auth.py:37-42`
- Modify: `tests/test_auth.py` (new test)
- Modify: `tests/test_devices.py` (new test)

- [ ] **Step 1: Add failing test `test_devices_module_fails_without_secret_in_prod`**
```python
def test_devices_module_fails_without_secret_in_prod(monkeypatch):
    import importlib
    monkeypatch.delenv("NKS_WDC_CATALOG_SECRET", raising=False)
    monkeypatch.delenv("NKS_WDC_CATALOG_DEV", raising=False)
    import app.devices as devices_module
    with pytest.raises(RuntimeError, match="NKS_WDC_CATALOG_SECRET"):
        importlib.reload(devices_module)
```

- [ ] **Step 2: Same for auth signer in `test_auth.py`**

- [ ] **Step 3: Run tests, verify FAIL**

- [ ] **Step 4: Fix `devices.py:34-41` — raise in non-DEV mode, ephemeral random key in DEV**

- [ ] **Step 5: Fix `auth.py` `_secret_key()` similarly**

- [ ] **Step 6: Run tests, verify PASS**

- [ ] **Step 7: Commit** `fix: fail-fast on missing secrets in production`

### Task 1.2 — Close IDOR on `POST /api/v1/sync/config`

**Files:**
- Modify: `app/main.py:191-242`
- Modify: `tests/test_devices.py`

- [ ] **Step 1: Test `test_sync_upsert_rejects_anonymous_overwrite`**
- [ ] **Step 2: Verify failure**
- [ ] **Step 3: Add ownership check — if `row.user_id IS NOT NULL` and account is None or `row.user_id != account.id`, raise 403**
- [ ] **Step 4: Verify pass**
- [ ] **Step 5: Commit** `fix: reject anonymous overwrite of owned device (F-11)`

### Task 1.3 — Require auth + ownership on GET/DELETE/HEAD `/sync/config/{id}`

**Files:**
- Modify: `app/main.py:245-282`
- Modify: `tests/test_devices.py`

- [ ] **Step 1: Tests for anonymous GET/DELETE rejected, owner allowed, other account 404**
- [ ] **Step 2: Verify failure**
- [ ] **Step 3: Replace `optional_account` with `get_current_account`, check `row.user_id == account.id`**
- [ ] **Step 4: Verify pass**
- [ ] **Step 5: Commit** `fix: require auth + ownership on sync config read/delete (F-12)`

### Task 1.4 — Split secrets + harden session cookie

**Files:**
- Modify: `app/auth.py`, `app/devices.py`
- Modify: `app/main.py:316-322`

- [ ] **Step 1: Read env `NKS_WDC_SESSION_SECRET` with fallback to `NKS_WDC_CATALOG_SECRET`**
- [ ] **Step 2: Read env `NKS_WDC_JWT_SECRET` with fallback to `NKS_WDC_CATALOG_SECRET`**
- [ ] **Step 3: set_cookie(secure=True, httponly=True, samesite="strict")**
- [ ] **Step 4: README — document new env vars + fallback chain**
- [ ] **Step 5: Tests green**
- [ ] **Step 6: Commit** `fix: split session/jwt secrets + harden cookie flags`

### Task 1.5 — Generic auth error message

**Files:**
- Modify: `app/devices.py:107-108`

- [ ] **Step 1: Log detail server-side, return generic `"Invalid token"` to client**
- [ ] **Step 2: Commit** `fix: avoid leaking jwt error internals in 401 response`

---

## Phase 2 — Foundation cleanup

### Task 2.1 — `Settings(BaseSettings)` + dependency injection

**Files:**
- Create: `app/settings.py`
- Modify: `app/db.py`, `app/auth.py`, `app/devices.py`, `app/main.py`
- Modify: `requirements.txt` (add `pydantic-settings`)

- [ ] **Steps 1-5 as in original plan**
- [ ] **Step 6: Commit** `refactor: centralize config via pydantic-settings`

### Task 2.2 — Migrate python-jose → PyJWT

**Files:**
- Modify: `requirements.txt` (remove `python-jose`, add `PyJWT>=2.9`)
- Modify: `app/devices.py`

- [ ] **Steps 1-4 as in original plan**
- [ ] **Step 5: Commit** `refactor: replace python-jose with pyjwt`

### Task 2.3 — Alembic setup + baseline

**Files:**
- Create: `alembic.ini`, `alembic/env.py`
- Create: `alembic/versions/20260417_0000_baseline.py`

- [ ] **Step 1: `alembic init alembic`**
- [ ] **Step 2: Wire `env.py` to `app.db.Base.metadata`**
- [ ] **Step 3: Baseline with `--autogenerate`**
- [ ] **Step 4: Startup runs `alembic upgrade head` when `NKS_WDC_CATALOG_AUTO_MIGRATE=1`**
- [ ] **Step 5: Commit** `feat: alembic baseline migration`

### Task 2.4 — Rate limiting (slowapi)

- [ ] Apply limits: `/auth/login` 5/min/IP, `/auth/register` 3/hour/IP, `/sync/config` 30/min/device, `/login` 10/min/IP
- [ ] Tests for 429
- [ ] Commit: `feat: rate-limit auth and sync endpoints (F-21)`

### Task 2.5 — Payload size limit middleware

- [ ] Reject `Content-Length > 1 MB` → 413
- [ ] Commit: `feat: reject oversized payloads (F-15)`

### Task 2.6 — ETag + Cache-Control on `/api/v1/catalog`

- [ ] Hash body, If-None-Match → 304, `Cache-Control: public, max-age=60`
- [ ] Commit: `feat: etag + cache headers on catalog endpoint`

### Task 2.7 — Healthz readiness probe

- [ ] `SELECT 1` in healthz, 503 on fail
- [ ] Commit: `feat: healthz verifies db connectivity`

### Task 2.8 — Non-root Docker user

- [ ] `useradd` + `USER app` in Dockerfile
- [ ] Commit: `fix: run container as non-root`

---

## Phase 3 — Role-based access control (RBAC) + admin panel expansion

### Task 3.0 — Unified identity model

Current state: two identity tables (`users` admin-UI, `accounts` desktop-JWT). Merge into a single `users` table with a `role` column.

**Files:**
- Modify: `app/db.py` — new `Role` enum + merged `User` model; deprecate `Account`
- Create: `alembic/versions/20260417_0002_unify_identity.py`
- Modify: `app/auth.py`, `app/devices.py`

`Role` enum: `owner`, `admin`, `operator`, `support`, `user`, `readonly`
- `owner` — single superuser (you), cannot be demoted, full god-mode
- `admin` — full management except role changes on owner
- `operator` — catalog edits (releases, downloads, generators), can suspend users
- `support` — read-only user data + reset passwords, no catalog mutations
- `user` — default for desktop-registered accounts (self-service own devices)
- `readonly` — temporarily disabled account (cannot log in, data preserved)

- [ ] **Step 1: Add `Role` enum + `role` column to users (backfill from current admin flag)**
- [ ] **Step 2: Migrate `accounts` rows into `users` (role=`user`), keep `accounts` as DEPRECATED view (or drop + update FKs)**
- [ ] **Step 3: Update FK `device_configs.user_id` to point at unified `users.id`**
- [ ] **Step 4: Rewrite auth flows — both session cookie and JWT issue for the same User record**
- [ ] **Step 5: Tests for migration + auth**
- [ ] **Step 6: Commit** `refactor: unify identity model with role enum`

### Task 3.1 — Permission decorator + role gate

**Files:**
- Create: `app/permissions.py`
- Modify: existing admin endpoints in `app/main.py` to gate by role

- [ ] **Step 1: `require_role(*roles)` FastAPI dependency**
```python
def require_role(*allowed: Role):
    def _dep(user: User = Depends(current_user)) -> User:
        if user.role not in allowed and user.role != Role.owner:
            raise HTTPException(403, "Insufficient privileges")
        return user
    return _dep
```
- [ ] **Step 2: Tag every admin POST/DELETE route with `Depends(require_role(Role.admin, Role.operator))`**
- [ ] **Step 3: Support-only endpoints with `Depends(require_role(Role.support))`**
- [ ] **Step 4: Tests for 403 when insufficient role, 200 when allowed**
- [ ] **Step 5: Commit** `feat: role-based permission gate for admin endpoints`

### Task 3.2 — Admin: user list + detail

**Files:**
- Create: `app/routers/admin_users.py`
- Create: `templates/admin/users.html`, `templates/admin/user_detail.html`
- Modify: `templates/admin/layout.html` (add Users nav item gated by role)

Endpoints:
- `GET /admin/users` — paginated list with columns: email, role, created, last_login, device_count, snapshot_bytes, status
- `GET /admin/users/{id}` — detail + devices + recent snapshots + audit events
- `POST /admin/users/{id}/role` — change role (owner-only for non-trivial changes, admin for demoting support↔operator)
- `POST /admin/users/{id}/suspend` — set role to readonly, invalidate all JWTs (bump `password_changed_at` or add `sessions_invalidated_at`)
- `POST /admin/users/{id}/reset-password` — generate temporary password, email/show once
- `DELETE /admin/users/{id}` — GDPR-style delete, cascades to devices + snapshots

- [ ] **Step 1: TDD each endpoint + template**
- [ ] **Step 2: CSRF token on every form**
- [ ] **Step 3: Audit log entry on every mutation (extends `snapshot_exports` → rename to `audit_events`)**
- [ ] **Step 4: Commit per endpoint group**

### Task 3.3 — Admin: audit log viewer

**Files:**
- Modify: `app/db.py` — generalize `SnapshotExport` → `AuditEvent` with `actor_user_id`, `action`, `resource_type`, `resource_id`, `diff_json`, `ip`, `ua`, `created_at`
- Create: `app/routers/admin_audit.py`
- Create: `templates/admin/audit.html`

- [ ] **Step 1: Refactor model + migration**
- [ ] **Step 2: Every mutation emits audit event (helper `audit.emit(db, actor, action, ...)`)**
- [ ] **Step 3: Admin page with filters (actor, action, date range, resource)**
- [ ] **Step 4: Commit per step**

### Task 3.4 — Admin: storage & quota dashboard

**Files:**
- Create: `app/routers/admin_stats.py`
- Create: `templates/admin/stats.html`

Metrics:
- Total users / accounts / devices / snapshots
- Storage per user (bytes, snapshot count, top 10)
- Snapshot kind distribution (auto vs manual vs pre_restore)
- Retention effectiveness (deletes per day)
- Catalog fetch QPS (from prometheus)

- [ ] **Step 1: SQL aggregates + UI**
- [ ] **Step 2: Commit** `feat: admin storage and quota dashboard`

### Task 3.5 — Admin: global retention + quota policies

**Files:**
- Modify: `app/db.py` — `GlobalPolicy` singleton row (default retention, max bytes per user)
- Create: `app/routers/admin_policies.py`
- Create: `templates/admin/policies.html`

- [ ] **Step 1: CRUD endpoints + template**
- [ ] **Step 2: Snapshot service respects global max_bytes_per_user cap (reject new snapshot > cap)**
- [ ] **Step 3: Commit** `feat: admin-managed global retention + quota policies`

### Task 3.6 — Admin: session / token management

**Files:**
- Modify: `app/devices.py` — JWT carries `jti` + check against `revoked_tokens` table
- Create: `app/routers/admin_sessions.py`

- [ ] **Step 1: Add `revoked_tokens(jti, user_id, revoked_at, reason)` table**
- [ ] **Step 2: `decode_token` rejects revoked jtis**
- [ ] **Step 3: Admin UI: list active JWTs per user (by issued-at), revoke individual or all**
- [ ] **Step 4: On password change: auto-revoke all user tokens**
- [ ] **Step 5: Commit per step**

### Task 3.7 — Admin: owner invite flow

**Files:**
- Modify: `app/routers/admin_users.py`
- Create: `templates/admin/invite.html`

- [ ] **Step 1: `POST /admin/users/invite` generates signed invite link (itsdangerous) with role, 48 h expiry**
- [ ] **Step 2: `GET /invite/{token}` → accept form → creates user with chosen role**
- [ ] **Step 3: Commit** `feat: admin-issued signed invites with role pre-assignment`

---

## Phase 4 — Backup feature

### Task 3.1 — Snapshot schema + Alembic migration

**Files:**
- Modify: `app/db.py` (add `DeviceSnapshot`, `DeviceHead`, `SnapshotRetentionPolicy`, `SnapshotExport`, `AccountEncryptionKey`)
- Create: `alembic/versions/20260417_0001_snapshots.py` (with backfill)

- [ ] As per DB agent report
- [ ] Commit: `feat: schema for versioned config snapshots`

### Task 3.2 — Snapshot service module

**Files:**
- Create: `app/snapshots.py`
- Create: `tests/test_snapshots.py`

- [ ] Storage tiering (inline JSON / zstd blob / uri placeholder)
- [ ] `create_snapshot`, `get_head`, `set_head`, `fetch_payload`, `diff` (jsonpatch)
- [ ] Commit: `feat: snapshot service layer`

### Task 3.3 — Backup endpoints (TDD per endpoint group)

**Files:**
- Create: `app/routers/backups.py`, `app/schemas_backup.py`

- [ ] list+detail → commit
- [ ] create+delete → commit
- [ ] restore+diff → commit
- [ ] import+export → commit
- [ ] policy → commit

### Task 3.4 — Bridge legacy `/sync/config` → auto-snapshot

- [ ] Each sync push creates auto snapshot + updates HEAD
- [ ] `Deprecation: true` + `Sunset` headers
- [ ] Commit: `feat: bridge legacy sync into snapshot history`

### Task 3.5 — Retention runner (APScheduler)

- [ ] Daily 03:00 UTC, delete auto snapshots beyond `keep_last_n_auto` or past `auto_expire_days`
- [ ] Never delete labeled or HEAD
- [ ] Commit: `feat: retention runner for auto snapshots`

### Task 3.6 — Encryption at rest (Variant A — KMS-managed KEK)

**Files:**
- Create: `app/crypto.py` (AES-256-GCM envelope)
- Modify: `app/snapshots.py` (encrypt on store, decrypt on fetch)
- Modify: `app/settings.py` (master key env)

- [ ] `generate_dek`, `wrap_dek`, `unwrap_dek`, `encrypt/decrypt` with AAD
- [ ] On register → create `AccountEncryptionKey`
- [ ] Tests: round-trip + tampering detection
- [ ] Commit: `feat: envelope encryption for snapshot payloads`

---

## Phase 5 — Observability + CI contract

### Task 4.1 — Structured logs + request-id

- [ ] JSON log formatter + middleware
- [ ] Commit: `feat: structured json logs with request id`

### Task 4.2 — Prometheus metrics

- [ ] `/metrics` endpoint with custom counters (snapshots, retention deletes)
- [ ] Commit: `feat: prometheus metrics endpoint`

### Task 4.3 — OpenAPI artifact + desktop daemon drift-check

**Files (in new repo):**
- Create: `scripts/export-openapi.py`
- Modify: `.github/workflows/release.yml` (upload openapi.json as release asset)

**Files (in monorepo `nks-ws`):**
- Create: `scripts/fetch-catalog-openapi.mjs` (or extend `generate-api-types.mjs`)
- Modify: `.github/workflows/build.yml` (fetch pinned `openapi.json`, regenerate C# DTOs, diff-check)
- Modify: `src/daemon/NKS.WebDevConsole.Daemon/Binaries/CatalogClient.cs` — extract DTOs into `CatalogClient.generated.cs` with header matching the Pydantic spec

- [ ] Export script + CI upload
- [ ] Monorepo drift-check + pinned version via env `CATALOG_API_VERSION`
- [ ] Commit (both repos)

### Task 4.4 — Deploy rollover to new image

- [ ] Tag `v0.2.0` in new repo → GHCR image published
- [ ] On wdc.nks-hub.cz: `docker compose pull && up -d` (SQLite volume agnostic)
- [ ] Smoke tests against public URL
- [ ] Commit runbook in new repo `docs/deploy.md`

---

## Success Criteria

- `github.com/nks-hub/wdc-catalog-api` exists, populated with history, CI green
- `services/catalog-api/` removed from monorepo; tag `catalog-api-pre-split` preserves forensic history
- Phase 1 security: CRITICAL + targeted HIGH findings verified closed
- Phase 3 RBAC: unified user/role model, admin UI can manage users/audit/quotas/sessions, invite flow works
- Backup endpoints live end-to-end
- Retention runner + encryption round-trip tests pass
- OpenAPI drift-check integrated in desktop daemon CI
- wdc.nks-hub.cz serves from the new repo's image

## Deferred

- MinIO external blob storage (> 2 MB payloads)
- Encryption Variant B (password-derived KEK, zero-knowledge)
- Postgres production DB migration
- Admin UI CSRF double-submit token (beyond SameSite=strict)
