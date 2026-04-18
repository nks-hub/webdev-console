# WDC Binaries Repo Comprehensive Audit — 2026-04-18

**Target:** github.com/nks-hub/webdev-console-binaries
**Workflows reviewed:** 7 (`build-apache.yml`, `build-binaries.yml` [PHP], `build-mariadb.yml`, `build-mirror.yml` [caddy/cloudflared/mailpit], `build-mkcert.yml`, `build-nginx.yml`, `build-redis.yml`)
**Releases reviewed:** 22 tags, ~60 assets across 10 apps (sampled 3 in depth: PHP 8.3.25, Apache 2.4.66, MariaDB 12.3.1; spot-probed Redis/mkcert/caddy/mariadb-11.4.4)
**Prior audits referenced:** `docs/binaries-audit-2026-04-18.md`, `docs/binaries-audit-log.md`

## Executive summary

Severity totals: **2 CRITICAL · 6 HIGH · 8 MEDIUM · 5 LOW · 3 INFO** (24 findings).

Top 3 urgent follow-ups:

1. **[CRITICAL] No integrity verification anywhere in the pipeline.** Upstream tarballs are fetched over HTTPS with no GPG / checksum verification (`build-binaries.yml:210` php.net tarball, `build-apache.yml:102` archive.apache.org, `build-nginx.yml:80` nginx.org, `build-redis.yml:86` download.redis.io). Released assets themselves ship no `SHA256SUMS`, no cosign signature, no SLSA provenance. The daemon's `BinaryDownloader` (`BinaryDownloader.cs:44-73`) downloads and extracts blindly — a single compromise of any upstream or of the GitHub release storage bucket would silently poison every downstream WDC install.
2. **[CRITICAL] GitHub Actions pinned to floating tags across all 7 workflows** (`softprops/action-gh-release@v2` everywhere). A tag-jack or maintainer account compromise on that single action re-publishes your release assets through arbitrary code with `contents: write` token scope. Combined with `secret_scanning: disabled` and `dependabot_security_updates: disabled` on the repo (`gh api repos/...` output), the blast radius is large.
3. **[HIGH] Catalog ↔ binaries-repo drift is 100% manual.** Catalog JSONs live in `wdc-catalog-api/app/data/apps/*.json` — there is no sync automation from binaries-repo releases to catalog entries. When CI publishes a new tag the catalog will not know about it until a human edits a JSON, commits, and redeploys the catalog-api. The prior audit already flagged three orphan-asset cases (`redis` linux/macOS, `mkcert` upstream bypass, `mariadb 11.4.4`) which are direct symptoms of this.

## 1. Supply-chain security

### [CRITICAL] C1 — Unpinned GitHub Action everywhere
**Where:** every workflow, e.g. `.github/workflows/build-apache.yml:164`, `build-binaries.yml` (multiple), `build-mariadb.yml:73`, `build-mirror.yml:99`, `build-mkcert.yml:52`, `build-nginx.yml:115`, `build-redis.yml:107` — all use `softprops/action-gh-release@v2`.
**Issue:** Floating major-tag pinning re-resolves on every run. If the action is hijacked (tag force-push, maintainer compromise), attacker gets `GITHUB_TOKEN` with `contents: write` and can rewrite release assets, inject payloads into archives, or exfiltrate the token (no `permissions:` scoping beyond `contents: write`).
**Fix:** Pin to the immutable commit SHA, e.g. `softprops/action-gh-release@<40-char-sha>  # v2.x.x`. Dependabot for GitHub Actions is already configured (`/.github/dependabot.yml`) and will auto-bump these. Apply the same to any future `actions/checkout`, `actions/setup-*`.

### [CRITICAL] C2 — No integrity verification of upstream tarballs in build jobs
**Where:**
- `build-binaries.yml:205` and `build-binaries.yml:~340` — `curl -fsSL -o php-src.tar.xz https://www.php.net/distributions/php-${VER}.tar.xz` (no `.sig` / `sha256sum -c`)
- `build-binaries.yml:215` Apache ApacheLounge ZIP is fetched via HTML scrape with no hash check
- `build-apache.yml:102` `curl -fsSL -o httpd-src.tar.gz "https://archive.apache.org/dist/httpd/httpd-${VER}.tar.gz"` (Apache publishes PGP signatures + sha256 sidecars — both ignored)
- `build-nginx.yml:80` `curl -fsSL -o nginx-src.tar.gz "https://nginx.org/download/nginx-${VER}.tar.gz"` (nginx.org publishes PGP signatures — ignored)
- `build-redis.yml:86` `curl -fsSL -o redis-src.tar.gz "https://download.redis.io/releases/..."` (unverified)
- `build-mirror.yml` — every `curl -fsSL` against github.com/caddyserver, cloudflare/cloudflared, axllent/mailpit (all of which publish checksum files) is unverified
**Issue:** MITM / upstream compromise / DNS hijack of any one of these domains silently poisons releases. SECURITY.md acknowledges this explicitly ("Source tarballs are not signature-verified in the workflow yet") and lists it on the roadmap.
**Fix:** Add a signature verification step per workflow:
```bash
# Apache example
curl -fsSL -o httpd-src.tar.gz "https://archive.apache.org/dist/httpd/httpd-${VER}.tar.gz"
curl -fsSL -o httpd-src.tar.gz.asc "https://archive.apache.org/dist/httpd/httpd-${VER}.tar.gz.asc"
curl -fsSL https://downloads.apache.org/httpd/KEYS | gpg --import
gpg --verify httpd-src.tar.gz.asc httpd-src.tar.gz
```
Where upstream publishes only sha256 (cloudflared, mailpit, caddy), download and verify the `*.sha256` file.

### [HIGH] H1 — No signing / SBOM / attestation on published release assets
**Where:** all 22 releases; zero `.sig`, zero `SHA256SUMS`, zero SLSA provenance file.
**Issue:** Downstream consumers (the WDC daemon) have no way to verify that an asset was actually produced by the documented workflow. An attacker with write access to the binaries repo (e.g. a leaked PAT / compromised maintainer session) can upload an arbitrary file under the established naming convention and every daemon will accept it. SECURITY.md documents the roadmap item but it is not implemented.
**Fix:** Minimum bar — add `sha256sum httpd-*-*.* > SHA256SUMS` step before `softprops/action-gh-release` and include `SHA256SUMS` in `files:` list. Better — use `actions/attest-build-provenance` (GitHub-native SLSA L3) which needs only `id-token: write` + `attestations: write` permissions.

### [HIGH] H2 — Over-broad `GITHUB_TOKEN` permissions not minimized per-job
**Where:** all 7 workflows set `permissions: contents: write` at the top (workflow-level), never at the job level, and never restrict to the final upload step.
**Issue:** Every intermediate step (curl, tar, make, phpize, PECL compile) runs with the token able to rewrite any release in the repo. The long PECL install step in `build-binaries.yml` runs thousands of lines of downloaded shell from `pecl.php.net` (via `curl -fsSL -o ext.tgz https://pecl.php.net/get/${name}-${ver}.tgz`) inside a shell with that token exposed in `GITHUB_TOKEN`/`env`.
**Fix:** Move `permissions: contents: write` to the final `Upload to release` job, and split the build into a separate `build` job that uploads an artifact (`actions/upload-artifact`) with `permissions: contents: read`. Then a small `publish` job that downloads artifacts and runs `action-gh-release`.

### [HIGH] H3 — ApacheLounge HTML scrape is a fragile supply-chain vector
**Where:** `build-apache.yml:52-61` — `HTML=$(curl -fsSL -A "Mozilla/5.0 NKS-WDC/1.0" "https://www.apachelounge.com/download/VS17/")` then `grep -oE "httpd-${VER}-[0-9]+-win64-VS17\\.zip"`.
**Issue:** (a) no TLS pinning / cert check beyond default, (b) no integrity check on the downloaded `.zip`, (c) ApacheLounge is a single-maintainer site with no formal provenance — the daemon-side installers then consume this as Apache "official" binaries. A compromise of `apachelounge.com` rolls directly into every WDC Apache install on Windows. (d) The scrape parses the VS17 index — any site restructure breaks Windows builds silently at the next version tag.
**Fix:** At minimum, capture the ApacheLounge-published SHA1 from the same index page and verify. Consider switching to official `httpd-*.tar.gz` + cross-compile on `windows-2022`, or mirror the ApacheLounge zip into the binaries repo once per release after manual GPG-like verification.

### [MEDIUM] M1 — `curl | bash`-equivalent pattern inside docker build
**Where:** `build-binaries.yml:190-210` — legacy PHP Linux build writes a custom `icu-config` shim via `printf ... > /usr/local/bin/icu-config` inside a `docker run --rm ubuntu:20.04 bash -c "..."` block. The outer bash double-quote substitution means any variable expansion in the `printf` content is interpolated on the runner host, then run inside root in the container. The content here is constant so it isn't exploitable today, but the pattern is brittle — a future edit that interpolates a version string or env var into the shim will silently execute it.
**Fix:** Move the icu-config shim to a checked-in file (`scripts/ci/php-legacy-icu-config`) and `cp` it in, or use `cat > /usr/local/bin/icu-config <<'EOF'` (heredoc-quoted) to disable interpolation.

### [MEDIUM] M2 — No `concurrency:` block on any workflow
**Where:** all 7 workflows.
**Issue:** Two concurrent tag pushes (e.g. a quick `-r2` retry) race each other at the `softprops/action-gh-release` step. Both upload to the same release; last writer wins; the loser's artifacts may land partially overwritten. The CI run stats in the prior audit show 15/39 PHP failures — some of these may be races.
**Fix:** Add
```yaml
concurrency:
  group: release-${{ github.ref }}
  cancel-in-progress: false
```

### [MEDIUM] M3 — `actions/checkout` missing entirely
**Where:** none of the 7 workflows use `actions/checkout`. The workflows are self-contained shell pipelines that `curl` sources directly.
**Issue:** not a bug per se — intentional minimal surface — but it does mean workflow file changes get committed and applied immediately to the `main`-triggered tag without any in-repo lint/CI gate on the YAML.
**Fix:** Optional `validate-workflow` job that runs `actionlint` on every push. Cheap, catches typos and unsafe expression interpolations (see next finding).

### [LOW] L1 — `${{ github.event.inputs.* }}` pasted directly into `run:` blocks
**Where:** `build-apache.yml:43`, `build-binaries.yml:68`, `build-mirror.yml:53`, `build-mkcert.yml:43`, `build-nginx.yml:43`, `build-redis.yml:46` — all use `if [ -n "${{ github.event.inputs.version }}" ]; then VERSION="${{ github.event.inputs.version }}"`.
**Issue:** Standard GitHub-Actions script-injection pattern. `workflow_dispatch` inputs come from authorized maintainers so current risk is low, but the idiomatic defense is to pass them as `env:` and reference `$VERSION`:
```yaml
env:
  INPUT_VERSION: ${{ github.event.inputs.version }}
run: |
  VERSION="${INPUT_VERSION}"
```
This also blocks accidental backtick / `$()` expansion if a future input contains `;` / `` ` ``. `pull_request_target` is not used anywhere (good) and the repo has no `pull_request:`-triggered workflows that consume PR titles, so the injection surface is currently just the dispatch form.

### [LOW] L2 — Repo security toggles off
**Where:** `gh api repos/nks-hub/webdev-console-binaries` — `secret_scanning: disabled`, `secret_scanning_push_protection: disabled`, `dependabot_security_updates: disabled`.
**Issue:** Any accidental PAT / AWS key / upstream credential pushed to the repo will not be flagged. This is a public repo — the cost of enabling all three is zero and they're basic hygiene.
**Fix:** Enable in Repo → Settings → Security. Dependabot *version* updates for GH Actions is already on (good), `security_updates` is the separate toggle for known-CVE bumps.

### [INFO] I1 — `actions/checkout@v4` observation
**Where:** n/a — no workflow checks out the repo. When this does get added (e.g. for the SHA256SUMS signing step), pin to commit SHA + v4.x.

## 2. Release asset hygiene

### [HIGH] H4 — Zero checksums / signatures on any release asset
**Where:** all 22 tags — `gh release view` asset lists show only the primary archive/exe per platform, no `.sha256`, no `.sig`, no `.intoto.jsonl`.
**Fix:** Covered by H1. Simplest increment: one-line addition per workflow.

### [HIGH] H5 — Catalog-vs-release asset-naming drift
**Where:**
- MariaDB 11.8.3 / 12.3.1: catalog advertises only Windows x64 (`mariadb-X.Y.Z-windows-x64.zip` directly from `archive.mariadb.org`), but release `binaries-mariadb-12.3.1` ships Linux tar.gz + Windows **MSI**. The MSI extension (`-windows-x64.msi`) does not match the catalog's advertised `.zip` filename, so daemon URL lookup would fail even if the catalog did list it. `build-mariadb.yml:56-60` downloads `winx64-packages/mariadb-${VER}-winx64.msi`, never a zip.
- Redis 7.4.2: release ships `redis-7.4.2-linux-x64.tar.xz`, `redis-7.4.2-macos-arm64.tar.xz`, `redis-7.4.2-windows-x64.zip`. Catalog only references `redis-windows` community fork ZIP. Linux/macOS assets are orphaned (prior audit flagged).
- mkcert 1.4.4: release has proper `mkcert-1.4.4-*` assets; catalog bypasses them entirely and points at `github.com/FiloSottile/mkcert/releases/...` (prior audit flagged).
**Fix:** Decide authoritative source per app: either (a) drop orphan workflow outputs to save CI minutes, or (b) re-point catalog JSON to `nks-hub` assets. The MariaDB MSI naming specifically is broken — either convert MSI to extracted ZIP in CI, or stop producing MSI.

### [MEDIUM] M4 — Asset-naming inconsistencies across the 10 apps
**Where:**
- Apache release filename prefix is `httpd-` but catalog `app` key is `apache`: `httpd-2.4.66-linux-x64.tar.xz` vs expected `apache-2.4.66-linux-x64.tar.xz`. The daemon's filename stem heuristic (`BinaryDownloader.cs:98-106`) special-cases "cloudflared" only; `httpd` slips through as `guessedName` which happens to work for ZIP-extracted binaries but breaks any future "bin" single-file handling.
- MariaDB release uses `mariadb-*-linux-x64.tar.gz` (gz, not xz) while Apache/Nginx/PHP/Redis use `tar.xz`. The `BinaryDownloader.ExtractAsync` only handles `.zip` + single-file `.exe/.bin/empty`. `tar.gz` / `tar.xz` extraction is not implemented in the daemon at all (see `BinaryDownloader.cs:111`: `throw new NotSupportedException($"Archive format not supported: ext='{ext}' hint='{hint}'")`) — so on Linux/macOS the daemon cannot actually install any of the `tar.xz` / `tar.gz` assets it downloads. This is a latent bug that the prior audit did not surface because on Windows everything is zip.
- mailpit/caddy/cloudflared: mixed `.tar.gz` (mailpit, caddy) and bare binary (cloudflared). `BinaryDownloader` won't extract `.tar.gz` for mailpit or caddy either.
**Fix:** Either (a) unify all Linux/macOS artifacts to `.zip` to match the daemon extractor, or (b) implement `.tar.gz`/`.tar.xz` extraction in `BinaryDownloader.ExtractAsync` using `System.Formats.Tar` + `GZipStream`/`XZStream`. Option (b) is the right long-term move; option (a) is a 20-line workflow edit per build.

### [MEDIUM] M5 — Release bodies are copy-pasta templates
**Where:** every `Upload to release` step has a 1-2 line `body: |` with no version-of-build provenance, no "Built from commit" link, no workflow-run link, no upstream-source URL for the tarball.
**Fix:** Template a proper body with:
```
Built from: https://github.com/${{ github.repository }}/actions/runs/${{ github.run_id }}
Workflow source: https://github.com/${{ github.repository }}/blob/${{ github.sha }}/.github/workflows/build-apache.yml
Upstream tarball: https://archive.apache.org/dist/httpd/httpd-${{ steps.ver.outputs.version }}.tar.gz
SHA256: (see SHA256SUMS asset)
```
SECURITY.md already claims "audit-able from release page → 'Built from' link → workflow run" — this claim is not backed by the current release body content.

### [MEDIUM] M6 — No draft / manual-approval gate before release goes public
**Where:** every `softprops/action-gh-release@v2` call omits `draft: true`. Tags trigger automatic public release.
**Issue:** A broken or tampered build is publicly fetchable the moment the tag lands — the catalog-api can pick it up in its next refresh. No roll-back other than deleting the release.
**Fix:** Add `draft: true` + `prerelease: true` on first upload, then a small `finalize` job (or manual GitHub UI action) that flips it to published after smoke-test download. Alternative: add a curl-based smoke test on each uploaded asset inside the workflow before `softprops/action-gh-release` is called.

### [LOW] L3 — Author is `github-actions[bot]` for every release
Good — no humans force-publishing. Worth noting in a future "supply chain provenance" doc because it simplifies any SLSA provenance attestation (the GitHub-Actions OIDC identity is a single principal).

## 3. Coverage matrix (reference)

Coverage gaps already enumerated in `docs/binaries-audit-2026-04-18.md` (per-OS/arch matrix) and `docs/binaries-audit-log.md`. Summary, **not re-derived here**:
- PHP Linux 7.0/5.6 and macOS 7.x/8.0 skipped (intentional, documented in `build-binaries.yml:113-140`).
- Nginx & Apache: only latest version built for Linux/macOS (1.27.3, 2.4.66); older catalog versions are Windows-only via nginx.org / ApacheLounge.
- MariaDB: Linux/macOS only for 11.4.4; newer versions Windows-only.
- Mysql: no `build-mysql.yml` workflow exists — catalog always delegates to `dev.mysql.com` direct URLs.
- Redis / mkcert: orphan nks-hub assets (see M5 / H5 above).

## 4. Build reproducibility

### [MEDIUM] M7 — No dependency caching on any workflow
**Where:** no `actions/cache` anywhere. Every Apache/PHP/Nginx/Redis source build re-downloads tarballs, brew-installs the same formulae, apt-installs dozens of `-dev` packages per run.
**Issue:** (a) slow — PHP Linux 8.x build with PECL ext install can take 25-40 min per matrix slot, (b) wasted CI minutes (2026-04-17 had 39 PHP workflow runs), (c) re-hitting `brew install` / `apt-get install` repeatedly increases MITM / upstream-downtime exposure.
**Fix:** Cache `~/.cache/homebrew` and `/var/cache/apt/archives` (for legacy-docker legs, `~/docker` layer cache). For PHP PECL tarballs, cache `pecl.php.net` tgz files keyed on the version matrix map.

### [MEDIUM] M8 — Builds are not reproducible, and SECURITY.md says so
**Where:** SECURITY.md: "builds are not bit-for-bit reproducible. The same tag re-run will produce a functionally equivalent but not byte-identical binary".
**Issue:** Makes supply-chain auditing fundamentally harder — there is no way for a third party to re-run the pipeline and compare hashes. Blocks SLSA L4.
**Fix:** Roadmap item in SECURITY.md. Low priority until H1/H2 (signing) is done — signing unreproducible binaries is still strictly better than the current state.

### [LOW] L4 — Workflow re-runnability via tag-retry via `-rN` suffix is clever but undocumented
**Where:** `build-apache.yml:42` `VERSION="${RAW%-r*}"` strips `-r2` etc. from `binaries-apache-2.4.66-r2`. Same pattern in all workflows.
**Issue:** Nice escape hatch for retries without incrementing the version, but there is no README / CONTRIBUTING doc that explains it; new maintainers will push `-v2`, `-retry1`, etc. and get confusing failures.
**Fix:** One-liner in `README.md` or `CONTRIBUTING.md`: "To rebuild an existing version, tag as `binaries-<app>-<ver>-rN`."

### [INFO] I2 — Build logs preserved via default GitHub retention (90 days)
Default retention is fine for the current cadence. If any provenance attestation is added later, the run logs become load-bearing and retention should be raised to 400 days explicitly in repo settings.

## 5. Mirror workflow (caddy, cloudflared, mailpit)

### [HIGH] H6 — No upstream drift detection; upstream URL-schema changes silently break tag pushes
**Where:** `build-mirror.yml:55-95` — per-app case statements hard-code upstream URL templates (`https://github.com/caddyserver/caddy/releases/download/v${VER}/caddy_${VER}_linux_amd64.tar.gz` etc.).
**Issue:** (a) A version must be manually tagged by the maintainer — there's no cron or `workflow_dispatch` scheduled job polling upstream GitHub API for new releases, (b) if upstream ever restructures filenames (happened to mailpit in 2024, renamed from `-x86_64` to `-amd64`), the curl call fails on the next tag and needs a code edit. (c) cloudflared specifically has a weird compound curl chain that mirrors 5 variants (linux-x64/arm64, macos-x64/arm64, windows) with `|| true` on some — silent partial failures.
**Fix:**
- Add a scheduled `drift-check.yml` workflow (daily cron) that hits each upstream's GitHub `/releases/latest` API, compares to the latest catalog entry, and opens an issue if out of sync.
- Replace hard-coded URL templates with a small matrix + `url_template` string per app in one YAML block.
- Remove `|| true` from cloudflared darwin-arm64 and let it fail loudly.

### [MEDIUM] M9 — Mirror workflow does not verify upstream checksums
Same root cause as C2 (see §1). caddy, cloudflared, and mailpit all publish `*.sha256` alongside each asset; the workflow curls the asset but ignores the hash file.

### [INFO] I3 — Mirror naming convention is enforced by workflow, not daemon
**Where:** `build-mirror.yml` body comment: "File naming follows the catalog's `${app}-${version}-${os}-${arch}` convention". The convention is actually implicit — only the workflow is enforcing it. `BinaryDownloader` doesn't parse filenames for app/version; it uses the catalog URL as the single source of truth. If a workflow typo produces `caddy_2.10.2_linux-x64.tar.gz` (underscore) instead of `-`, the catalog just needs its URL updated; daemon is agnostic.
**Fix:** Document the convention in `README.md`. Optionally add a tiny post-upload step that lists uploaded asset names and `grep -E '^[a-z]+-[0-9.]+-[a-z]+-[a-z0-9]+(\\.[a-z.]+)?$'` to fail on drift.

## 6. Catalog consumption (daemon side)

### [CRITICAL-LITE → elevated to HIGH] H7 — Daemon does no integrity check on downloaded assets
**Where:** `src/daemon/NKS.WebDevConsole.Daemon/Binaries/BinaryDownloader.cs:50-72` — `EnsureSuccessStatusCode()` then straight to `FileStream.Write` → `File.Move`. No hash comparison, no signature verification, no certificate pinning beyond default TLS, no TOFU on first-install.
**Issue:** Complements C2 on the other side of the wire. Even if the catalog later adds `sha_256` to the schema (`CatalogClient.DownloadDoc` currently doesn't even have the field — `CatalogClient.cs:246-254`), the daemon would still need plumbing to verify it. Zip-slip defense is present and solid (`BinaryDownloader.cs:114-160`), but a malicious-zip-in-a-valid-archive isn't the threat model — tampered-archive is.
**Fix:** Add `sha_256` to `DownloadDoc`, thread it through `BinaryRelease`, and in `DownloadAsync` after `File.Move` compute SHA256 of archive and compare. Fail loudly + delete the downloaded file on mismatch. Catalog JSON schema needs updating first; the prior audit shows all existing catalog entries have `sha_256: null`.

### [HIGH] H8 — Daemon extractor only supports `.zip` and bare binaries
**Where:** `BinaryDownloader.cs:111` — `throw new NotSupportedException(...)` if ext not in `.zip`, `.exe`, `.bin`, or empty. The catalog lists `.tar.xz` and `.tar.gz` for most Linux/macOS assets.
**Issue:** On non-Windows, `wdc binaries install apache/nginx/mariadb/redis/php/caddy/mailpit` cannot succeed even if the URL resolves and the download completes — extraction throws.
**Fix:** Add tar.gz / tar.xz handling. `.tar.gz` via `System.IO.Compression.GZipStream` + `System.Formats.Tar` (net9). `.tar.xz` needs a NuGet dep (`SharpZipLib.XZ` or `joveler.compress.xz`). Same zip-slip defense should be applied to tar entries.

### [MEDIUM] M10 — CatalogClient has no ETag / If-Modified-Since support
**Where:** `CatalogClient.cs:102` — `_http.GetAsync($"{baseUrl}/api/v1/catalog", ct)` unconditionally.
**Issue:** Every `RefreshAsync` fetches the full JSON (currently ~6 KB but growing with every release). Catalog API supports standard HTTP caching; daemon could send `If-None-Match` on the cached ETag.
**Fix:** Record `ETag` header on success, send `If-None-Match` on subsequent refresh, short-circuit on `304 Not Modified`.

### [LOW] L5 — `BuiltInFallback` duplicates cloudflared metadata
**Where:** `CatalogClient.cs:164-197` — hard-coded cloudflared 2026.3.0 URLs pointing directly at `github.com/cloudflare/cloudflared/releases/...` with `amd64` filenames, **not** the nks-hub mirror.
**Issue:** Inconsistent with the catalog which does use the nks-hub mirror asset names (`cloudflared-2026.3.0-linux-x64`, no `amd64` suffix). Offline users via fallback get upstream URLs; online users via catalog get mirror URLs. Two code paths, two naming conventions.
**Fix:** Either remove `BuiltInFallback` (the catalog is reliable enough) or populate it from the nks-hub mirror asset URLs for consistency.

---

## Recommended next tickets

Priority-ordered, one PR each unless noted:

1. **[C1] Pin all GitHub Actions to commit SHAs.** Ten-minute mechanical change, Dependabot keeps them fresh. Enable `secret_scanning` + `secret_scanning_push_protection` in repo settings while you're there.
2. **[C2+H1] Ship SHA256SUMS per release, verify upstream where possible.** Start with the mirror workflow (upstream checksums exist), then Apache/Nginx/PHP GPG, then nks-hub-published SHA256SUMS. One PR per workflow, not one mega-PR.
3. **[H3] Stop scraping ApacheLounge HTML index.** Either capture-and-verify the SHA1 listed on the index, or switch Windows Apache to an in-repo mirrored zip that's updated manually per release.
4. **[H5+M4+M5] Unify asset-naming + decide orphan policy.** Pick one extension per OS (zip on Win, tar.xz on Unix), fix MariaDB MSI mismatch, drop orphan Redis/mkcert Linux assets OR update catalog to use them.
5. **[H7+H8] Daemon: add tar.gz/tar.xz extraction AND SHA256 verification.** Both are blockers for first-class Linux/macOS support — today only Windows actually works end-to-end.
6. **[H6] Drift-check cron.** Daily poll of upstream GitHub APIs for caddy/cloudflared/mailpit, auto-open issue when catalog lags upstream.
7. **[H2] Split build + publish jobs.** Separate `contents: read` build job uploading artifacts from a minimal `contents: write` publish job. Also adds a natural place to insert a pre-publish smoke test.
8. **[M6] Publish releases as draft first, flip to published after smoke test.** One-line YAML addition + one CI step.
9. **[M7] Cache brew / apt / PECL downloads.** Halves CI run time.
10. **[H3] Catalog → binaries-repo sync script.** `scripts/sync-catalog-from-releases.py` that polls the repo's releases and updates `wdc-catalog-api/app/data/apps/*.json`. Run either on a cron or on the catalog-api side as an admin endpoint.

**Out of scope (intentional):** SLSA L3 provenance, cosign signing, reproducible builds — all listed in SECURITY.md roadmap, worth doing only after items 1-3 above land.
