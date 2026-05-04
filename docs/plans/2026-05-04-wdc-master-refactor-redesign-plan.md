# WDC Master Refactor & Redesign Implementation Plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use `superpowers:subagent-driven-development` for parallel repo work or `superpowers:executing-plans` for inline execution. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** Deliver a coherent WDC refactor across Easy/Simple mode, PostgreSQL support, binary distribution, catalog/plugin integration, platform support, CI, and release verification.

**Architecture:** Treat WDC as one product made from several repositories: `nks-ws` owns the app shell, frontend, daemon, and UX; `webdev-console-binaries` owns reproducible binary packages; `wdc-catalog-api` owns binary metadata and catalog validation; `webdev-console-plugins` owns optional service integrations. The master plan coordinates independently testable sub-plans so each repo can move safely without waiting for a single large merge.

**Tech Stack:** Vue/TypeScript frontend, Electron/daemon backend, C# plugin SDK, GitHub Actions, generated binary catalog JSON, PostgreSQL/MySQL/MariaDB service management, Playwright, unit/API/build tests.

---

## 1. Master Scope

This is not only an Easy/Simple UI redesign. The product target is a complete local web-development cockpit:

- Easy/Simple mode for daily setup and operation.
- Advanced mode for raw ports, paths, plugin internals, catalog overrides, database administration, and destructive diagnostics.
- First-class PostgreSQL alongside MySQL and MariaDB.
- Binary packaging and catalog coverage for every WDC-supported platform.
- CI that proves binaries, catalog metadata, app integration, and plugin builds stay compatible.
- Clean branch integration from current WIP branches into `main` after tests pass.

Primary plan documents:

- `docs/plans/2026-05-04-wdc-master-refactor-redesign-plan.md` - this master plan.
- `docs/plans/2026-05-04-ui-ux-refactor-redesign.md` - unified Easy and Advanced UI/UX refactor plan.
- `docs/plans/2026-04-20-simple-mode-v2-redesign.md` - Easy/Simple mode UX and refactor sub-plan.
- `docs/plans/2026-05-04-postgresql-platform-support.md` - PostgreSQL runtime/platform ownership and remaining app work.
- `docs/plans/2026-05-04-binaries-catalog-ci.md` - binaries, catalog, release asset, and CI hardening plan.
- Future sub-plan: `docs/plans/2026-05-04-plugin-integration-hardening.md`.
- `docs/plans/2026-05-04-full-review-autonomous-execution.md` - full review and autonomous iteration sub-plan.

---

## 2. Repository Boundaries

### `nks-ws`

Owns:

- App UX, routing, settings pages, dashboard, sites list, site detail.
- Daemon APIs for Easy summaries, services, binaries, database engines, backups, and updates.
- Local service orchestration for app-managed services.
- Playwright and app-level regression tests.

Must not own:

- Raw binary build recipes.
- Catalog generation rules that belong in `wdc-catalog-api`.
- Plugin package implementation that belongs in `webdev-console-plugins`.

### `webdev-console-binaries`

Owns:

- Reproducible packages for Apache, PHP, MySQL, MariaDB, Redis, Nginx, mkcert, and PostgreSQL.
- GitHub Actions matrix for Windows, Linux, and macOS packages.
- Archive layout, checksums, release assets, and README/release metadata.

### `wdc-catalog-api`

Owns:

- Binary catalog schema and generated entries.
- Platform/version metadata validation.
- Catalog API health and compatibility checks.
- Tests proving every generated app has valid downloads, hashes, platform ids, and versions.

### `webdev-console-plugins`

Owns:

- C# plugin implementations that expose service capabilities to WDC.
- PostgreSQL plugin if PostgreSQL is handled as a plugin rather than built into the daemon.
- SDK compatibility tests and plugin GitHub Actions.

---

## 3. Mandatory Preflight: Memory, CLAUDE, Review

Every autonomous execution pass starts with the same preflight. Do not skip it when switching repositories.

Required context load:

- Read this master plan.
- Read the active sub-plan for the current work package.
- Search MCP memory for `webdevconsole`, `nks-ws`, `wdc`, `binaries`, `catalog`, `github-actions`, `postgresql`, and current branch names.
- Read `CLAUDE.md` in this order: repository root, parent workspace, then global fallback. Current verified fallback is `C:\work\sources\CLAUDE.md`.
- Read `AGENTS.md` in each repository when present.
- Record active branch, upstream, ahead/behind, dirty tracked files, ignored plan/docs files, and open WIP branch purpose.

Rules loaded from memory and `CLAUDE.md`:

- Windows paths and PowerShell are the default local environment.
- Commit messages must never include AI attribution, signatures, or co-author lines.
- WDC commit convention is `type(scope): short desc`, English only, single-line header preferred.
- Do not run `tools/pre-push-check.sh` autonomously because prior WDC memory says it can trigger interactive elevation. Use individual gates instead: `npx vue-tsc --noEmit`, `npx playwright test --reporter=line`, `npx electron-vite build`, plus repo-specific .NET/API tests.
- For admin-only local checks, use CI or an approved noninteractive elevated path. Do not print or persist local elevation credentials, relay tokens, or machine-specific connection details.
- Always run the nearest build/test after changes, then broader suites before merge readiness.
- Prefer editing existing files. Create new files only for real decomposition, tests, generated plan docs, or required repo structure.
- Preserve user or WIP changes; do not reset, checkout, or rewrite history unless explicitly requested.

Full review must cover:

- Current branch diff against upstream and against `main`.
- Existing WIP branch merge risk.
- UI/UX consistency, i18n coverage, mobile layout, and screenshot evidence.
- Backend/API contracts, daemon lifecycle, process management, error handling, and persistence.
- Binary catalog compatibility and platform coverage.
- Plugin SDK compatibility and local feed/CI package source.
- GitHub Actions failures and logs via `gh` when authenticated.
- Security-sensitive paths: secrets, daemon tokens, certificates, backup archives, database credentials, destructive MCP/database operations.

Review output format:

- Findings first, ordered by severity, with file/line references where possible.
- Then redesign/refactor proposal.
- Then execution checklist and verification evidence.
- No merge into `main` without passing tests and an explicit merge decision.

### Admin-Only Verification Procedure

Use elevated execution only for work that genuinely needs it: hosts file writes, certificate store checks, Windows service lifecycle, firewall rules, and killing elevated process locks. Normal builds, unit tests, frontend checks, GitHub Actions review, and code refactors must run without elevation.

Rules:

- Prefer GitHub Actions or target-platform CI for admin-sensitive lifecycle tests.
- Keep every elevated command noninteractive, scoped, and reversible.
- Never persist local elevation credentials, relay tokens, machine-specific endpoints, or local mechanism details in repository files, commits, PRs, comments, or final reports.
- Before destructive elevated operations, create a backup or use the app's existing backup/rollback path.
- If no approved elevated path is currently available, mark the task `BLOCKED-UAC` and continue with non-admin work.

---

## 4. Product Model

Easy/Simple mode must be complete enough for normal daily work:

- Configure preferences, startup, telemetry, default PHP, default database engine, HTTPS, and tunnels.
- Install, start, stop, restart, and inspect Apache, PHP-FPM, MySQL, MariaDB, PostgreSQL, Mailpit, Redis, and Cloudflared.
- Create sites, duplicate sites, reveal folders, copy URLs, change PHP, enable SSL, enable tunnel, and run site-level maintenance.
- Create and restore backups, including database dumps when configured.
- Check app/catalog/plugin updates.
- See environment readiness and actionable fixes.

Advanced mode keeps:

- Raw ports and paths.
- Hosts-file editor and raw vhost/config editing.
- Full database admin, destructive drops, raw query tools.
- Plugin internals, MCP grants, deploy backend cutover, SSL CA management, catalog URL override, and factory reset.

---

## 5. PostgreSQL Support Definition

PostgreSQL is a first-class database engine, not a catalog-only entry.

Required runtime capabilities:

- Install PostgreSQL from WDC binary catalog.
- Initialize a data directory with `initdb`.
- Start/stop/restart via `pg_ctl` or the platform service wrapper.
- Check readiness with `pg_isready`.
- Create, drop, backup, and restore databases with `createdb`, `dropdb`, `pg_dump`, and `psql`.
- Expose connection details in UI without leaking generated secrets.
- Support per-site database selection: None, MySQL, MariaDB, PostgreSQL.
- Include PostgreSQL health in Easy Dashboard and Easy Services.

Full platform support means:

- Required WDC-supported platforms: `windows-x64`, `linux-x64`, `macos-arm64`.
- Extended target matrix: `windows-arm64`, `linux-arm64`, `macos-x64`.
- A platform may be marked unsupported only with an explicit catalog reason and a tracked follow-up issue.

---

## 6. Binary Platform Plan

### PostgreSQL binary package

Package contents:

- `bin/postgres`
- `bin/pg_ctl`
- `bin/initdb`
- `bin/pg_isready`
- `bin/psql`
- `bin/createdb`
- `bin/dropdb`
- `bin/pg_dump`
- Required runtime libraries and license files.

Archive layout:

```text
postgresql/
  VERSION
  LICENSES/
  bin/
  lib/
  share/
```

Release naming:

```text
binaries-postgresql-{version}
postgresql-{version}-{platform}.zip
postgresql-{version}-{platform}.tar.gz
```

Validation:

- Extract archive.
- Run `postgres --version`.
- Run `initdb` in a temporary data directory.
- Start PostgreSQL on a random free port.
- Run `pg_isready`.
- Create database `wdc_smoke`.
- Run `pg_dump`.
- Stop cleanly.

---

## 7. Catalog Plan

Add `postgresql` as a catalog app with:

- Stable app id: `postgresql`.
- Category: `database`.
- Default port: `5432`.
- Supported platforms and checksums.
- Download URL per platform.
- Binary command metadata for `postgres`, `pg_ctl`, `initdb`, `pg_isready`, `psql`, and `pg_dump`.
- Version compatibility fields for future major-version upgrades.

Catalog validation must fail when:

- A platform entry lacks SHA256.
- A download URL is missing.
- A binary command path is not declared.
- A platform id is not in the known WDC platform list.
- PostgreSQL exists in binaries release assets but not in the catalog.

---

## 8. App/Daemon Plan

Add a generic database-engine abstraction instead of hard-coding MySQL/MariaDB branches into UI components.

Core types:

```ts
type DatabaseEngineId = 'mysql' | 'mariadb' | 'postgresql'

interface DatabaseEngineSummary {
  id: DatabaseEngineId
  label: string
  installed: boolean
  running: boolean
  version?: string
  port: number
  autostart: boolean
  defaultForNewSites: boolean
  supportsDump: boolean
  supportsRestore: boolean
}
```

Required daemon/API endpoints:

- `GET /api/databases/engines`
- `POST /api/databases/{engine}/install`
- `POST /api/databases/{engine}/start`
- `POST /api/databases/{engine}/stop`
- `POST /api/databases/{engine}/restart`
- `POST /api/databases/{engine}/init`
- `POST /api/databases/{engine}/backup`
- `POST /api/databases/{engine}/restore`

Easy endpoints must include database health:

- `GET /api/easy/summary`
- `GET /api/easy/sites`

---

## 9. Easy, Simple, and Advanced UI Plan

The detailed Easy plan remains in `docs/plans/2026-04-20-simple-mode-v2-redesign.md`. The unified Easy/Advanced redesign is tracked in `docs/plans/2026-05-04-ui-ux-refactor-redesign.md`. This master plan adds the database/platform requirements that must be reflected in both.

Easy Settings / Projects:

- Default PHP version.
- Default database engine: None, MySQL, MariaDB, PostgreSQL.
- Default HTTPS.
- Default Cloudflare tunnel.
- MAMP import.

Easy Settings / Services:

- Apache or active web server.
- PHP-FPM versions in use.
- MySQL.
- MariaDB.
- PostgreSQL.
- Mailpit.
- Redis.
- Cloudflared.

Easy Settings / Backups:

- Include PostgreSQL dumps when a site uses PostgreSQL.
- Show which engines are included in the next backup.
- Restore must validate engine availability before starting.

Dashboard:

- Readiness checklist includes PostgreSQL only when selected, installed, or required by a site.
- Service health table shows PostgreSQL status and port.

Site Detail:

- Site database badge shows engine and database name.
- Maintenance includes database backup for that site.
- Restart scope names database services affected by the site.

Advanced UI:

- Reorganize Settings by task: Network & Ports, Paths & Binaries, Databases, Catalog & Plugins, Automation, Security & Access, About & Diagnostics.
- Keep Services, Databases, Binaries, Plugins, SSL, and deploy pages dense but scan-friendly.
- Use shared status badges, service rows, readiness checks, and advanced data tables so Easy and Advanced use the same state language.
- Separate inspect/edit/destructive actions visually and keep rare controls behind progressive disclosure.
- Remove visual clutter: no nested cards, no oversized hero treatment, no decorative blobs, no warnings without specific recovery actions.

---

## 10. Plugin Integration Plan

Decision point:

- Prefer a PostgreSQL plugin if current MySQL/MariaDB support is plugin-based.
- Prefer daemon-native PostgreSQL only if existing database service management already lives in `nks-ws`.

Plugin requirements:

- Plugin id: `nks.wdc.postgresql`.
- Service type: `Database`.
- Default port: `5432`.
- Capabilities: install, init, start, stop, restart, status, logs, backup, restore.
- Health probe: `pg_isready`.
- Logs: PostgreSQL server log and WDC wrapper log.
- Config: data directory, port, listen address, locale/encoding, superuser name.

Build requirements:

- Build plugin against the same SDK package source used by CI.
- Avoid direct dependency on private GitHub Packages during public CI.
- Add plugin tests for route/service registration and status mapping.

---

## 11. CI & GitHub Actions Plan

Cross-repo CI must prove compatibility without depending on unavailable private package feeds.

Required workflows:

- `webdev-console-binaries`: build PostgreSQL for every supported platform and run smoke tests.
- `wdc-catalog-api`: validate generated PostgreSQL catalog entries against release assets.
- `webdev-console-plugins`: build PostgreSQL plugin and existing plugins using a deterministic local SDK/package feed.
- `nks-ws`: unit tests, frontend build, daemon build, Playwright smoke, catalog integration smoke.

Known issue to keep fixed:

- GitHub Packages NuGet auth can fail with `401/403` in cross-repo builds. CI should consume deterministic SDK/Core packages from the monorepo release or an explicitly configured local feed, not rely on ambient credentials.

CI review workflow:

- Run `gh auth status` in each repo before using GitHub checks.
- Resolve the current branch PR with `gh pr view --json number,url`; if there is no PR, inspect workflow runs for the branch with `gh run list --branch <branch>`.
- Fetch failing GitHub Actions logs with `gh run view <run_id> --log` or the `gh-fix-ci` helper script.
- Summarize failing check name, run URL, relevant log snippet, and likely root cause before editing.
- Treat external checks as out of scope unless they are GitHub Actions.
- Fix workflow problems in the owning repo, not by hiding failures.
- Re-run the local equivalent of each failing job before calling it fixed.

CI hardening targets:

- Replace fragile private GitHub Packages consumption with deterministic SDK/Core package source.
- Validate workflow YAML and path filters for all WDC repos.
- Ensure `webdev-console-binaries` publishes checksums and release assets matching catalog expectations.
- Ensure `wdc-catalog-api` fails fast when catalog entries reference missing assets or unsupported platforms.
- Ensure `webdev-console-plugins` builds with the same SDK/Core package source as CI.
- Ensure `nks-ws` app tests can run without requiring privileged/UAC operations.

Branch integration rule:

- Do not merge a WIP branch into `main` until repo-local tests and affected cross-repo compatibility checks pass.
- If a branch is ahead because it merged `origin/main`, preserve that merge history unless user explicitly asks for rebase/squash.

---

## 12. Autonomous Review & Execution Program

This program is the user-requested complete review plus autonomous redesign/refactor/test/fix path. It wraps all phases below.

### Review pass

- [ ] Load MCP memory and list applicable findings in the session notes.
- [ ] Load `CLAUDE.md` and `AGENTS.md` rules.
- [ ] Inspect active WIP branches across `nks-ws`, `webdev-console-plugins`, `webdev-console-binaries`, and `wdc-catalog-api`.
- [ ] Review diffs against upstream and `main`.
- [ ] Inspect current plans and mark obsolete or conflicting sections.
- [ ] Produce findings ordered by severity.

### Redesign/refactor proposal

- [ ] Propose Easy/Simple redesign changes with screenshots or wire-level descriptions.
- [ ] Propose backend/API refactor boundaries.
- [ ] Propose PostgreSQL/binaries/catalog/plugin ownership.
- [ ] Propose CI fixes per repository.
- [ ] Convert accepted proposals into tracked sub-plan tasks.

### Autonomous implementation

- [ ] Execute tasks in small commits or logical working-tree checkpoints.
- [ ] Preserve user edits and ignored plan docs.
- [ ] Keep cross-repo changes in their owning repositories.
- [ ] Prefer generic abstractions over PostgreSQL-only UI branches.
- [ ] Update `AGENTS.md` when contributor rules change.

### Complete testing

- [ ] Run nearest tests after each behavior change.
- [ ] Run full repo-local build/test gates before merge.
- [ ] Run Playwright screenshots for UI changes.
- [ ] Use approved noninteractive elevated execution only for admin-only test slices and record only the high-level checks run.
- [ ] Run affected GitHub Actions locally where possible or inspect remote logs with `gh`.
- [ ] Record commands and outcomes in the final review notes.

---

## 13. Execution Phases

### Phase 0: Inventory, memory, and branch hygiene

- [x] Read global fallback `C:\work\sources\CLAUDE.md`.
- [x] Search MCP memory for WDC/refactor/binaries/catalog/GitHub Actions/PostgreSQL context.
- [x] Record admin-only verification guardrails.
- [x] Record active branches in all WDC repos.
- [x] Record dirty files and ignored plan/docs files.
- [x] Fetch remotes.
- [x] Review WIP branches for merge conflicts and CI risk.
- [x] Document blockers before implementation.

### Phase 1: Master docs and design freeze

- [ ] Save this master plan.
- [ ] Update Easy/Simple plan with PostgreSQL and platform scope.
- [x] Create unified Easy/Advanced UI/UX refactor sub-plan.
- [x] Create PostgreSQL/platform sub-plan.
- [x] Create binaries/catalog/CI sub-plan.
- [ ] Link all plans from `AGENTS.md` or contributor docs if appropriate.

### Phase 2: Easy/Simple extraction

- [ ] Split `Settings.vue` into `SettingsShell.vue` and focused Easy/Advanced components.
- [ ] Add `environmentHealth`, `settingsDraft`, `backups`, and `databaseEngines` stores.
- [ ] Keep behavior unchanged until extraction tests pass.
- [ ] Commit extraction separately from visual redesign.

### Phase 3: Easy/Simple redesign

- [ ] Implement Easy Settings IA.
- [ ] Redesign Dashboard as operational health overview.
- [ ] Refactor Simple Sites and Site Detail onto shared components.
- [ ] Add desktop and mobile Playwright screenshots.

### Phase 3B: Advanced UI simplification

- [ ] Reorganize Advanced Settings into task groups.
- [ ] Refactor Services, Databases, Binaries, Plugins, and SSL pages onto shared status/table primitives.
- [ ] Move rare/destructive controls behind explicit advanced disclosure.
- [ ] Add desktop and mobile screenshots for Advanced Settings, Databases, Binaries, and Services.

### Phase 4: PostgreSQL binaries

- [ ] Add PostgreSQL package workflow to `webdev-console-binaries`.
- [ ] Add platform matrix and smoke validation.
- [ ] Publish/check release asset naming.
- [ ] Update binary README generation.

### Phase 5: PostgreSQL catalog

- [ ] Add `postgresql` catalog metadata.
- [ ] Add catalog validation tests.
- [ ] Add API/admin visibility if catalog API exposes app lists.
- [ ] Verify generated catalog against binaries release assets.

### Phase 6: PostgreSQL app/plugin runtime

- [ ] Add generic database engine model.
- [ ] Add PostgreSQL install/init/start/stop/restart/status/backup/restore support.
- [ ] Add plugin implementation if plugin-owned.
- [ ] Add daemon/plugin tests for service lifecycle and status mapping.

### Phase 7: UI database integration

- [ ] Add default database engine selector to Easy Projects.
- [ ] Add PostgreSQL row to Easy Services.
- [ ] Add PostgreSQL readiness and backup visibility.
- [ ] Add site-level database engine badges and maintenance actions.

### Phase 8: CI hardening

- [ ] Fix remaining GitHub Actions package-feed failures.
- [ ] Add workflow validation for PostgreSQL platform matrix.
- [ ] Add cross-repo compatibility smoke path.
- [ ] Store exact build/test commands in contributor docs.

### Phase 9: Full review and autonomous fix sweep

- [ ] Run full review of changed code and current WIP branches.
- [ ] Fix actionable review findings in the owning repo.
- [x] Inspect first failing GitHub Actions issue with `gh`: `webdev-console-binaries` README update rejected by protected `main`.
- [x] Repair first workflow issue: README update now opens/updates a PR instead of pushing to protected `main`.
- [ ] Continue inspecting remaining failing GitHub Actions and package-feed issues.
- [ ] Update `AGENTS.md` from `CLAUDE.md` and project-specific evidence.
- [ ] Re-run all relevant verification gates.

### Phase 10: Merge and release readiness

- [ ] Run all repo-local tests.
- [ ] Run affected cross-repo builds where possible.
- [ ] Review final diffs.
- [ ] Merge WIP branches into `main` only after passing verification.
- [ ] Create release notes covering Easy/Simple, PostgreSQL, binaries, and platform support.

---

## 14. Verification Matrix

Minimum before merge:

| Area | Command/check | Expected result |
|---|---|---|
| `nks-ws` frontend | `npm run build` or repo equivalent | Pass |
| `nks-ws` tests | repo unit/API test command | Pass |
| UI smoke | Playwright desktop + mobile screenshots | No blank screens, clipping, or overlap |
| daemon APIs | Easy summary and database engines tests | Stable response shape |
| binaries | PostgreSQL archive smoke per supported platform | `initdb`, start, `pg_isready`, dump, stop pass |
| catalog | generated catalog validation | PostgreSQL platforms and SHA256 valid |
| plugins | `dotnet build` with deterministic local feed | Pass |
| GitHub Actions | affected workflow logs | No package auth failure |
| memory/context | MCP memory + `CLAUDE.md` + `AGENTS.md` loaded | Session notes include applied rules |
| Admin-only checks | CI or approved noninteractive elevated path | No local interactive UAC prompts |
| commit hygiene | `git log -1 --format=%B` after commits | No AI attribution; WDC convention followed |

---

## 15. Acceptance Criteria

- Easy/Simple mode lets a normal user configure and operate WDC without leaving Easy mode for daily tasks.
- Advanced mode still exposes all raw controls and destructive tooling.
- PostgreSQL is available as a first-class database engine in binaries, catalog, runtime APIs, plugins if applicable, Easy UI, backups, and readiness checks.
- All required WDC-supported platforms have PostgreSQL binary coverage or an explicit tracked exception.
- Cross-repo CI no longer depends on fragile ambient GitHub Packages credentials.
- WIP branch integration into `main` is backed by build/test evidence.
- Contributor docs point agents to the master plan and relevant repo-specific rules.
- Complete review has been performed before merge: findings, redesign/refactor proposal, fixes, and verification evidence are recorded.
- GitHub Actions problems across WDC repositories are either fixed or documented with run URLs, root cause, and explicit blockers.

---

## 16. Immediate Next Actions

- [x] Patch `docs/plans/2026-04-20-simple-mode-v2-redesign.md` so PostgreSQL appears in Easy Settings, Services, Backups, Dashboard, Site Detail, test plan, and acceptance criteria.
- [x] Add complete review, MCP memory, `CLAUDE.md`, autonomous execution, complete testing, and GitHub Actions repair requirements to this master plan.
- [x] Add admin-only verification guardrails to this master plan and `AGENTS.md`.
- [x] Create the dedicated PostgreSQL/platform sub-plan with exact repo files and test commands.
- [x] Create the binaries/catalog/CI sub-plan with exact GitHub Actions and catalog generator changes.
- [x] Create the unified Easy/Advanced UI/UX sub-plan and connect it to the master plan.
- [x] Create the full-review/autonomous-execution sub-plan with exact per-repo commands and evidence format.
- [ ] Continue review of `webdev-console-plugins` WIP branch and decide whether PostgreSQL belongs there or daemon-native.
