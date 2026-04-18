# WDC Binaries Audit — 2026-04-18

Cross-reference between the binaries repo (`nks-hub/webdev-console-binaries`)
and the live catalog API at `https://wdc.nks-hub.cz/api/v1/catalog`.

## Summary

- Catalog responds `200 OK`, schema_version `1`, generated at
  `2026-04-18T08:41:13Z`. Declares 10 apps and 29 release rows.
- Binaries repo exposes **20 release tags** and **7 active workflows**
  (apache, mariadb, mkcert, nginx, php, redis, plus `Mirror Upstream Binaries`
  for caddy/cloudflared/mailpit). There is **no `build-mysql.yml` workflow** —
  mysql is always delegated to `dev.mysql.com`.
- All 15 catalog URLs spot-probed returned `HTTP 200` (GitHub release assets,
  nginx.org, MySQL CDN, apachelounge, mariadb.org archive, redis-windows,
  FiloSottile/mkcert). **No 404s observed.**
- However the catalog and binaries repo **diverge** on three apps: `redis`,
  `mkcert`, and `mariadb 11.4.4` — the repo ships linux/macOS assets that the
  catalog never advertises, so those artifacts are effectively orphaned.
- The local workflow in `nks-ws/.github/workflows/build-binaries.yml` is a
  **verbatim copy of the PHP workflow only** from the binaries repo; it does
  not mirror the other six build workflows that live in the binaries repo.

## Per-app coverage matrix

Legend — columns show the OS/arch triplets advertised by the catalog; square
brackets indicate extra assets that exist in the release but are not linked
from the catalog. `—` means no entry.

| App | Catalog versions | win-x64 | linux-x64 | linux-arm64 | macos-arm64 | macos-x64 | Source of truth |
|-----|------------------|---------|-----------|-------------|-------------|-----------|-----------------|
| apache | 2.4.66 / 2.4.65 / 2.4.62 | yes / yes / yes | 2.4.66 only | — | 2.4.66 only | — | nks-hub (2.4.66), apachelounge (older) |
| caddy | 2.10.2 | yes | yes | — | yes | — | nks-hub (mirror) |
| cloudflared | 2026.3.0 | yes | yes | yes | yes | yes | nks-hub (mirror) |
| mailpit | 1.29.6 | yes | yes | — | yes | — | nks-hub (mirror) |
| mariadb | 12.3.1 / 11.8.3 / 11.4.4 | yes / yes / yes | 11.4.4 only | — | — | — | mariadb.org (12.x/11.8), nks-hub (11.4.4) |
| mkcert | 1.4.4 | yes | yes | yes | yes | yes | **upstream FiloSottile** (catalog bypasses nks-hub mirror) |
| mysql | 8.4.8 / 8.0.43 | yes / yes | — | — | — | — | dev.mysql.com (no local build workflow) |
| nginx | 1.29.2 / 1.28.1 / 1.27.3 | yes / yes / yes | 1.27.3 only | — | 1.27.3 only | — | nginx.org (Windows-only), nks-hub (1.27.3 only) |
| php | 8.5.5 … 5.6.40 (12 versions) | all 12 | 8.5.5–7.1.33 (no 7.0.33 or 5.6.40) | — | 8.5.5–8.1.33 only (5 versions) | — | php.net (Windows), nks-hub (Linux/macOS source build) |
| redis | 8.2.2 / 7.4.2 | yes / yes | **[7.4.2 only, orphaned]** | — | **[7.4.2 only, orphaned]** | — | redis-windows (GitHub), nks-hub ships Linux/macOS but catalog never points there |

### PHP version-by-version asset detail

| Version | win-x64 | linux-x64 | macos-arm64 |
|---------|:-------:|:---------:|:-----------:|
| 8.5.5   | php.net | nks-hub   | nks-hub     |
| 8.4.20  | php.net | nks-hub   | nks-hub     |
| 8.3.25  | php.net | nks-hub   | nks-hub     |
| 8.2.30  | php.net | nks-hub   | nks-hub     |
| 8.1.33  | php.net | nks-hub   | nks-hub     |
| 8.0.30  | php.net | nks-hub   | — (missing) |
| 7.4.33  | php.net | nks-hub   | — (missing) |
| 7.3.33  | php.net | nks-hub   | — (missing) |
| 7.2.34  | php.net | nks-hub   | — (missing) |
| 7.1.33  | php.net | nks-hub   | — (missing) |
| 7.0.33  | php.net | — (skipped, ICU broken) | — (skipped) |
| 5.6.40  | php.net | — (skipped)             | — (skipped) |

The "skip" decisions are intentional in `build-binaries.yml`: the workflow
hard-codes `skip_source=true` for all 5.x and `legacy_ssl=true` for 7.x/8.0.
The last CI re-run (`ci(php): skip macOS for 7.x/8.0 (icu4c@78 C++17 +
libxml2 ABI)`, 2026-04-17 13:27) made macOS a no-op for 7.x and 8.0, which
matches the catalog exactly.

## CI pipeline status

Last 50 workflow runs in `nks-hub/webdev-console-binaries`:

| Workflow | Total | Success | Failure |
|----------|------:|--------:|--------:|
| Build PHP Binaries | 39 | 24 | 15 |
| Build Apache HTTPD Binaries | 4 | 1 | 3 |
| Build Nginx Binaries | 2 | 1 | 1 |
| Build MariaDB Binaries | 1 | 1 | 0 |
| Build mkcert Binaries | 1 | 1 | 0 |
| Build Redis Binaries | 2 | 1 | 1 |
| Mirror Upstream Binaries (caddy, cloudflared, mailpit) | 1 | 1 | 0 |
| `.github/workflows/build-binaries.yml` (stale stub runs) | 3 | 0 | 3 |

Every release tag that currently exists in the repo was produced by a green
run; older failures were superseded by successful re-tags (e.g. `-r2` suffix
handling in the PHP workflow).

## Gaps found

1. **redis 7.4.2 — Linux/macOS assets orphaned.**
   Release `binaries-redis-7.4.2` ships `redis-7.4.2-linux-x64.tar.xz` and
   `redis-7.4.2-macos-arm64.tar.xz`, but the catalog's only redis download
   is the Windows MSYS2 zip from `redis-windows/redis-windows`. Either the
   catalog should expose the nks-hub Linux/macOS tarballs, or those assets
   should be removed from the release to avoid confusion.

2. **redis 8.2.2 has no nks-hub release at all.**
   Catalog advertises 8.2.2 via the community `redis-windows` repo only.
   There is no `binaries-redis-8.2.2` tag and no Linux/macOS build for 8.2.2.
   Coverage is Windows-only for the newer version.

3. **mkcert catalog bypasses the mirror.**
   Release `binaries-mkcert-1.4.4` exists with linux-x64, macos-arm64,
   windows-x64 assets, but the catalog points at the upstream FiloSottile
   URLs instead (and additionally lists linux-arm64 + macos-x64 that the
   mirror release does not carry). The local mirror is effectively dead
   code until the catalog generator is pointed at it, or the mirror
   release is broadened to match upstream's 5-triplet coverage.

4. **mariadb — no Linux or macOS coverage for 11.8.3 and 12.3.1.**
   Only Windows ZIPs from `archive.mariadb.org` are listed. The only
   Linux/macOS path is pinned to the old 11.4.4 tag produced by
   `build-mariadb.yml` (Linux+Windows, no macOS). 12.3.1 and 11.8.3 have
   no matching workflow run — they were added to the catalog manually
   without a corresponding build tag.

5. **nginx — only 1.27.3 has Linux/macOS.**
   1.29.2 and 1.28.1 are Windows-only in the catalog, because the
   `Build Nginx Binaries` workflow was only triggered for 1.27.3.

6. **apache — only 2.4.66 has Linux/macOS.**
   2.4.65 and 2.4.62 are Windows-only (apachelounge), matching the single
   `binaries-apache-2.4.66` release.

7. **mysql — no Linux or macOS binaries anywhere.**
   There is no `build-mysql.yml`; catalog delegates to `dev.mysql.com`.
   Both 8.0.43 and 8.4.8 are Windows-only. On Linux/macOS `wdc binaries
   install mysql` cannot succeed.

8. **PHP legacy 7.x/8.0 — no macOS binaries.**
   By design (icu4c@78 C++17 + libxml2 ABI mismatch). Documented in the
   workflow display title but not in the catalog description.

9. **PHP 7.0.33 and 5.6.40 — Windows only.**
   Explicitly `skip_source=true` in the PHP workflow.

10. **cloudflared macOS-x64 release asset lacks `.tgz`.**
    The release has both a bare `cloudflared-2026.3.0-macos-x64` binary
    and a `cloudflared-2026.3.0-macos-arm64.tgz` archive, but only the
    bare binary for `macos-x64`. Catalog's `archive_type` classification
    is inconsistent across the 5 cloudflared triplets (arm64 has both
    bare + tgz, x64 only bare).

## Stale releases

- `binaries-php-8.0.30` is currently marked `isLatest: true` by GitHub
  (simply because it was the most recently published tag), which is
  misleading — the newest PHP line is 8.5.5. Consider flipping the
  `Latest` flag to `binaries-php-8.5.5` or turning it off entirely for
  binary tags.
- No draft or pre-release tags are lingering. All 20 tags are published.

## Workflow/catalog divergence

| Catalog app | Expected repo workflow | Present in repo? | Notes |
|-------------|------------------------|------------------|-------|
| apache      | `build-apache.yml`     | yes              | Only 2.4.66 built |
| caddy       | `build-mirror.yml`     | yes              | Shared with cloudflared + mailpit |
| cloudflared | `build-mirror.yml`     | yes              | All 5 triplets |
| mailpit     | `build-mirror.yml`     | yes              | 3 triplets |
| mariadb     | `build-mariadb.yml`    | yes              | 11.4.4 only — 11.8.3 / 12.3.1 never built |
| mkcert      | `build-mkcert.yml`     | yes              | Assets not referenced by catalog |
| mysql       | — (no workflow)        | **missing**      | Catalog relies fully on dev.mysql.com |
| nginx       | `build-nginx.yml`      | yes              | 1.27.3 only |
| php         | `build-binaries.yml`   | yes              | All 12 versions built; macOS 7.x/8.0 skipped |
| redis       | `build-redis.yml`      | yes              | 7.4.2 only; catalog uses upstream for 7.4.2 AND 8.2.2 |

Local copy in `nks-ws/.github/workflows/build-binaries.yml` tracks only the
PHP workflow. The other six workflows live exclusively in the binaries repo
and are not reproduced under `nks-ws`. The 3 recorded failures of
`nks-ws/.github/workflows/build-binaries.yml` are from before the
`binaries-php-*` tag convention was finalised and can be ignored.

## Recommended actions

Ordered by impact:

1. **Decide ownership of redis Linux/macOS.** Either (a) point the catalog at
   the nks-hub `binaries-redis-7.4.2` tarballs and add a `binaries-redis-8.2.2`
   build, or (b) delete the orphan Linux/macOS assets from
   `binaries-redis-7.4.2` so the release matches the catalog's
   Windows-only stance.
2. **Triggers for mariadb 11.8.3 and 12.3.1 Linux/macOS builds.** The
   `build-mariadb.yml` workflow exists but was only ever run for 11.4.4.
   Tag `binaries-mariadb-11.8.3` + `binaries-mariadb-12.3.1` to close the
   gap, or update the catalog to advertise Windows-only for those versions
   as an explicit limitation.
3. **Broaden nginx and apache coverage or drop the older versions.** 1.29.2 /
   1.28.1 (nginx) and 2.4.65 / 2.4.62 (apache) are Windows-only. Tag
   `binaries-nginx-1.29.2`, `binaries-apache-2.4.67` (or similar) so every
   advertised version ships all triplets, or remove the Windows-only entries
   from the catalog.
4. **Point the catalog at the `binaries-mkcert-1.4.4` mirror** (or expand
   the mirror to linux-arm64 + macos-x64 and then flip the catalog source
   from `github` → `nks-hub-binaries`). As-is the mirror is dead weight.
5. **Ship a mysql Linux/macOS build** (likely via docker-in-docker for
   Linux and Homebrew formulae for macOS) or document that `wdc binaries
   install mysql` is Windows-only. A `build-mysql.yml` in the binaries
   repo is the natural home.
6. **Normalise cloudflared macOS-x64 archive.** Add a matching
   `cloudflared-2026.3.0-macos-x64.tgz` so all triplets expose a
   consistent archive type, or drop the `.tgz` from arm64.
7. **Fix the `isLatest` flag** on binaries repo releases — either always
   keep it on the newest PHP line (8.5.5) or disable it globally for
   binary tags (they are not product releases).
8. **Document the intentional PHP legacy skips** in the catalog
   `description` field for 7.x/8.0/7.0.33/5.6.40 so consumers know why
   certain triplets are empty.
