# Binaries, Catalog & CI Plan

**Date:** 2026-05-04
**Scope:** cross-repo binary packaging, catalog validation, app compatibility, plugin compatibility, and GitHub Actions hardening for WDC-supported platforms.

## 1. Current Completed State

- PostgreSQL 18.3 packages were built for `windows-x64`, `linux-x64`, and `macos-arm64`.
- Windows PostgreSQL package is runtime-only and about 50.1 MB after removing installer/UI/test-only payload.
- Latest PostgreSQL binary workflow passed smoke checks on Windows, Linux, and macOS.
- Production catalog exposes PostgreSQL 18.3 with all three required platform downloads.
- Open PR cleanup completed across `webdev-console`, `webdev-console-plugins`, `webdev-console-binaries`, and `wdc-catalog-api`.
- Plugin logging/package alignment fixed cross-repo plugin build compatibility.

## 2. Owning Files

`webdev-console-binaries`:

- `.github/workflows/build-postgresql.yml`
- PostgreSQL README/support matrix generation
- release assets named `postgresql-{version}-{platform}.{zip|tar.gz}`

`wdc-catalog-api`:

- `app/data/apps/postgresql.json`
- PostgreSQL generator and catalog validation tests
- API/catalog health checks

`webdev-console-plugins`:

- `NKS.WebDevConsole.Plugin.PostgreSQL/*`
- SDK/package source configuration
- plugin build workflows

`nks-ws`:

- catalog client and binary catalog tests
- Binaries UI, service icons, i18n
- database engine APIs and Easy/Advanced UI integration

## 3. CI Hardening Targets

- Validate every catalog download URL has a matching release asset and SHA256.
- Fail when release assets exist for an app but catalog metadata omits the platform.
- Validate platform ids against the WDC known platform list.
- Surface unsupported-platform reasons instead of generic missing binary errors.
- Keep plugin CI independent of fragile ambient GitHub Packages credentials.
- Ensure app CI covers catalog integration, frontend type-check/build, daemon build/tests, and cross-repo plugin build.
- Keep protected-branch workflows opening PRs instead of pushing directly to `main`.

## 4. PostgreSQL Archive Validation

Every package must contain:

- `VERSION`
- license files
- `bin/postgres`
- `bin/pg_ctl`
- `bin/initdb`
- `bin/pg_isready`
- `bin/psql`
- `bin/createdb`
- `bin/dropdb`
- `bin/pg_dump`
- required runtime libraries

Smoke path:

1. Extract archive.
2. Run `postgres --version`.
3. Run `initdb` in a temporary data directory.
4. Start on a random free port.
5. Run `pg_isready`.
6. Create `wdc_smoke`.
7. Run `pg_dump`.
8. Stop cleanly.

## 5. Verification Commands

- `gh pr list --state open --json number,title,url`
- `gh run list --limit 20`
- `gh run view <run-id> --log`
- `dotnet test WebDevConsole.sln --filter CatalogClientTests`
- `dotnet test WebDevConsole.sln --filter BinaryCatalogTests`
- `cd src/frontend && npm run type-check`
- `cd src/frontend && npm run build`

For binary runtime smoke, prefer GitHub Actions on the target OS. Do not start privileged local WDC daemon/e2e paths as a normal user.

## 6. Remaining Tasks

- [ ] Add catalog-vs-release asset validation if not already present.
- [ ] Add unsupported-platform reason display in app/catalog UI.
- [ ] Add extended platform follow-ups for `windows-arm64`, `linux-arm64`, and `macos-x64`.
- [ ] Re-check all WDC GitHub Actions after each cross-repo package or catalog change.
- [ ] Store verification evidence in the relevant PR or plan update.
