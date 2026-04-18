# WDC Binaries Repo — Deep Pipeline Review (2026-04-18)

**Target:** github.com/nks-hub/webdev-console-binaries
**Pass 3** — goes below the prior audits (`docs/binaries-audit-2026-04-18.md`, `docs/reviews/2026-04-18-binaries-repo-audit.md`). Focus: workflow YAML line-level scrutiny, artifact integrity validation by download, release body provenance, run-history forensics, and governance. Prior findings are **not** re-derived unless a deeper nuance was missed.

**Severity totals:** **3 CRITICAL · 7 HIGH · 9 MEDIUM · 4 LOW · 2 INFO** (25 findings, cap honored).
**Methodology:** 7 workflow YAMLs fetched via `gh api .../contents/...` and read in full; 22 release tags enumerated; 8 releases deep-inspected via `gh release view --json assets,body`; 2 archives downloaded and unpacked for on-disk sanity; catalog re-queried to confirm URL consumption; GitHub Actions run history paginated to 80 entries; repo security+branch-protection settings queried.

## Top 5 urgent items

1. **[CRITICAL NEW] ~~`httpd-2.4.66-windows-x64.zip` is a 2,451-byte HTML error page, not a ZIP~~ — ✅ PARTIALLY RESOLVED 2026-04-18.** Two-part fix applied:
   - **Part A (workflow hardening):** binaries-repo PR #2 (`fix/apache-asset-validation` → `main`, commit `da2b043`) adds Content-Type HEAD preflight, size ≥5 MB guard, ZIP/tar.xz magic-byte check, and `unzip -l ≥50 entries` post-build smoke test. **Awaits human merge** before Part B Option 1 (`gh release delete-asset` + `gh workflow run`) can run with the hardened path.
   - **Part B (interim catalog redirect):** wdc-catalog-api commit `6b63ff9` redirects `apache@2.4.66` Windows URL from the poisoned nks-hub mirror back to the live upstream `https://www.apachelounge.com/download/VS17/binaries/httpd-2.4.66-251206-win64-VS17.zip` (verified 200 OK, 11.5 MB real ZIP, 907 entries). Stale `sha_256` + `size_bytes` stripped so daemon doesn't mismatch-fail against the honest upstream. **Pushed to origin/main; NOT deployed** (PVE infra incident guardrail).
   - Residual: daemon-side fake-file detection (Content-Type, magic byte) would catch the same class of poisoning on other future mirrors. Filed as follow-up; not in this fix.
2. **[CRITICAL] ~~GitHub's native per-asset `digest: sha256:...` is exposed by the REST API but neither catalog nor daemon consumes it~~ — ✅ PARTIALLY RESOLVED 2026-04-18 (wdc-catalog-api commit `57458e8`)** — `scripts/backfill-sha256-from-github.py` backfilled 38 of 38 nks-hub-hosted download rows (apache/caddy/cloudflared/mailpit/mariadb/nginx/php JSONs). Script is idempotent + re-runnable via cron. Remaining: 18 downloads at external mirrors (windows.php.net / archive.mariadb.org / archive.apache.org / nginx.org / dev.mysql.com) and 7 at FiloSottile/mkcert + redis-windows GitHub repos — outside this repo's digest reach; need separate upstream-hash ingestion OR workflow-side mirroring to nks-hub. **Daemon-side verification (CatalogClient.DownloadDoc sha_256 field + downloader check) still TODO** — doc-only today, enforcement is separate PR.
3. **[CRITICAL] ~~`main` branch has zero protection~~ — ✅ RESOLVED 2026-04-18 (binaries-repo commit `97b62664`)** — branch protection enforced on `main` (no force-push, no deletions, linear history, conversation resolution required), secret_scanning + push_protection + vulnerability_alerts + automated_security_fixes enabled repo-wide, `.github/CODEOWNERS` committed with `* @LuRy`. (Original finding: `gh api repos/.../branches/main/protection` → `HTTP 404`; repo security toggles all off; no CODEOWNERS.)
4. **[HIGH NEW] ~~`fail_on_unmatched_files` boolean-coerces a string expression and is effectively always-false for PHP~~ — ✅ RESOLVED 2026-04-18 (binaries-repo PR [#3](https://github.com/nks-hub/webdev-console-binaries/pull/3), commit `9382445`, awaits merge).** Step refactored (option 2): `fail_on_unmatched_files: true` is now a static literal, and the skip-when-no-archive decision moved to a step-level `if: matrix.target == 'windows-x64' || env.skip_source != 'true'` conditional. String coercion of GH Actions expression booleans eliminated. Behavior identical for windows-x64 + both skip_source paths; timebomb class closed.
5. **[HIGH NEW] ~~0-bit of upstream verification~~ — ✅ RESOLVED 2026-04-18 across 5 PRs, 3 workflows get real SHA256 (PHP, Apache, MariaDB), 2 workflows docs-only (nginx, Redis — no public hash manifest exists).**
   - PHP: PR [#4](https://github.com/nks-hub/webdev-console-binaries/pull/4) `0d94c39` — 3 verify blocks (Windows `sha256sum.txt`, Linux legacy host-side, Linux modern + macOS inline). Source: PHP.net JSON API `releases/?json&version=X.Y.Z`.
   - Apache: PR [#7](https://github.com/nks-hub/webdev-console-binaries/pull/7) `34a32dc` — Linux/macOS `archive.apache.org/dist/httpd-X.Y.Z.tar.gz.sha256`, Windows ApacheLounge `<url>.txt` sidecar parsed via awk.
   - MariaDB: PR [#9](https://github.com/nks-hub/webdev-console-binaries/pull/9) `2be65be` — per-directory `archive.mariadb.org/.../sha256sums.txt`, reusable `verify_sha256()` bash function, `curl || true` chain refactored to `if curl; then verify; fi` under `set -e`.
   - nginx: PR [#10](https://github.com/nks-hub/webdev-console-binaries/pull/10) `510b435` — no `.sha256`/`.sha512` on nginx.org; only `.asc` PGP with rotating release-manager keys. Documented inline as `TODO(SECURITY.md roadmap)`. HTML-as-ZIP guard from PR #2 still applies.
   - Redis: PR [#11](https://github.com/nks-hub/webdev-console-binaries/pull/11) `15bd832` — no integrity manifest ANYWHERE (download.redis.io 404s on sha/asc/hashes.json; GitHub release has no assets; forks publish nothing). Alternatives considered+rejected (inline-pinned hashes, TOFU). Documented inline.
   - Follow-up: SECURITY.md roadmap entry for PGP verification across all 5 workflows.
   - php: `https://www.php.net/distributions/php-X.Y.Z.tar.xz.asc` (PGP), `.sha256` (explicit file) — **both ignored**
   - apache: `https://archive.apache.org/dist/httpd/httpd-X.Y.Z.tar.gz.asc` + `.sha256` + `.sha512` — **all ignored**
   - nginx: `https://nginx.org/download/nginx-X.Y.Z.tar.gz.asc` (PGP) — **ignored**
   - redis: `download.redis.io` emits `*-hashes.txt` side-by-side since 7.2 — **ignored**
   - caddy/cloudflared/mailpit: each upstream GitHub release ships a `*-checksums.txt` — **ignored**
   - mkcert: `FiloSottile/mkcert` releases expose asset-level sha256 via GitHub API — **ignored**
   - mariadb: `archive.mariadb.org/mariadb-X.Y.Z/sha256sums.txt` per-release — **ignored**
   There is literally no upstream in this repo that doesn't publish *some* integrity artifact that is being ignored.

## Scope-area one-liners (per prompt sections 1-8)

1. **Workflow YAML deep read:** All 7 workflows use `workflow_dispatch + push:tags` triggers (good), `permissions: contents: write` at workflow scope (over-broad, prior audit), 100% floating major-tag pins on `softprops/action-gh-release@v2` (no SHA pins anywhere — confirmed), zero `concurrency:` blocks (race on `-rN` retries), zero `actions/checkout` (self-contained, but no lint gate), `${{ github.event.inputs.* }}` injection pattern in 6/7 workflows (low risk, idiomatic fix known), no cache, no self-test of built binary, no artifact signing, no matrix test/verify job.
2. **Build reproducibility:** Not reproducible by design (SECURITY.md confirms). No `SOURCE_DATE_EPOCH`. Toolchain versions not pinned (`ubuntu-24.04`/`macos-14`/`windows-2022` are rolling; Homebrew formula versions unpinned; `apt-get install` unpinned; `pecl.php.net` version map is pinned per-PHP-minor which is good but the tgzs themselves aren't hash-verified). Two re-runs of any tag would produce different sha256 — but the same tag *is* re-run for retries (`-r2` convention) and the released assets silently swap hashes with no changelog.
3. **Artifact integrity end-to-end:** All 22 releases return `HTTP 200` via `curl -sI`. Per-asset `digest: sha256:...` is **natively exposed by the GitHub REST API** (`gh api .../releases/tags/... --jq '.assets[] | .digest'`) — unused downstream. 8 releases asset-sampled; 1 release is provably broken (`httpd-2.4.66-windows-x64.zip` = 2451-byte HTML). No `SHA256SUMS`, no `.sig`, no `.intoto.jsonl` on any release. Asset-size anomalies: Apache Windows zip 2451 B (critical); nginx macOS-arm64 = 387 KB (plausibly small because the install layout only captures `sbin/nginx` + conf/html and nothing else — no bundled libs; cross-checked via tarball listing: it contains `sbin/nginx, conf/*, html/*` and dynamically links everything, which will fail to run on a random macOS without brew's openssl@3/pcre2 present — see finding M6 below).
4. **Upstream source verification:** Zero. See top-5 item 5.
5. **Test matrix coverage:** Zero. No workflow executes the produced binary. `php --version`, `apachectl -V`, `nginx -v`, `redis-cli --version`, `mariadb --version`, `mkcert -version`, `caddy version`, `cloudflared --version`, `mailpit version` — none are smoke-tested before the `softprops/action-gh-release` step. The 2451-byte Apache HTML bug survives precisely because of this omission.
6. **Release body quality:** Uniformly templated prose. **No `${{ github.run_id }}` link, no `${{ github.sha }}` commit pin, no upstream URL echoed, no SHA256 block.** SECURITY.md *claims* "audit-able from release page → 'Built from' link → workflow run" — this claim is false today; there is no such link.
7. **Dependabot + CODEOWNERS + security policy:** `dependabot.yml` covers only `github-actions` ecosystem (weekly, Monday, cap 5 — reasonable). **No `CODEOWNERS`**. `SECURITY.md` exists and is well-written but roadmap items (sha256sums, signing, SLSA, reproducibility) are all still aspirational. **No branch protection** on `main`. Security analysis toggles all off.
8. **Comparison table:** See §8.

---

## §1. Workflow-by-workflow deep findings

### A. `build-apache.yml` (172 lines)

- **[CRITICAL-A] Silent HTML-as-ZIP on Windows** (see Top-5 #1). Concrete fix sketch:
  ```yaml
  curl -fsSL -A "Mozilla/5.0 NKS-WDC/1.0" -o "httpd-${VER}-windows-x64.zip" "$URL"
  # NEW: content sanity — first 4 bytes must be "PK\x03\x04"
  head -c 4 "httpd-${VER}-windows-x64.zip" | xxd | grep -q "^00000000: 504b 0304" \
    || { echo "Downloaded file is not a ZIP (ApacheLounge rotated the build). Aborting."; file httpd-*.zip; exit 1; }
  # Cross-check against ApacheLounge-published SHA1 on the index page
  EXPECTED_SHA1=$(echo "$HTML" | grep -oE "[a-f0-9]{40}" | head -1)
  ```
- **[HIGH-A2] Static linking flags absent — `.so` modules assume host system libs.** `configure` uses `--with-apr=/usr --with-apr-util=/usr` on Linux and `$(brew --prefix ...)` on macOS. `bin/httpd` + `modules/*.so` therefore dynamically link against the CI runner's `/usr/lib/.../libapr-1.so.0`, `.../libssl.so.3`, `.../libxml2.so.16`, etc. A user downloading the tar.xz on a different distro (Debian, Alpine, Arch, RHEL, Fedora) will hit `ld.so: libapr-1.so.0 not found` or ABI mismatches. The archive is therefore "portable" only inside the Ubuntu-24.04 ABI bubble. Same applies to `build-nginx.yml`, `build-redis.yml`. No `ldd` verification step.
- **[HIGH-A3] ~~Package tarball includes runtime dirs like `logs`~~ — ✅ RESOLVED 2026-04-18 (binaries-repo PR [#14](https://github.com/nks-hub/webdev-console-binaries/pull/14), commit `deed0b9`, awaits merge).** `build-apache.yml` pre-tar scrub: `find "$STAGE/logs" -type f -delete` (catches accidental `access_log` from broken tests) + `rm -rf manual logs cgi-bin icons`. `htdocs` kept (default `httpd.conf DocumentRoot` points there, needed for first-start). ~1.0–1.5 MB xz saving per tarball (Linux + macOS). Added `tar -tf | head -30` post-tar audit line for CI log visibility. Windows (ApacheLounge) path untouched.
- **[MEDIUM-A4] Over-wide `--enable-mods-shared=reallyall`.** Pulls every module Apache ships, including `mod_example_hooks`, `mod_dialup`, etc. that are demo/test-only. Release size bloat and attack surface. WDC doesn't need them — `BinaryDownloader` can't disable at install time.

### B. `build-binaries.yml` (454 lines — PHP)

- **[HIGH-B1] icu-config heredoc is a parser-shenanigan.** `build-binaries.yml:175-188` uses `printf '%s\n' '...' '...' > /usr/local/bin/icu-config` inside a `docker run ... bash -c "..."` outer string. The double-quoted outer means `\"` is escaping-inside-escaping, and `\$` prevents the host shell from expanding docker-side vars. Any edit that drops a `\` before `$` interpolates the host's runner environment — including `GITHUB_TOKEN` if it's in env — into the guest container shim. Prior audit flagged this as MEDIUM (M1); reading the docker block start (`docker run --rm -v "${{ github.workspace }}:/work" -w /work ubuntu:20.04 bash -c "`) confirms the workspace is bind-mounted, which is the actual exfil vector: a tampered icu-config shim could copy `/proc/self/environ` (which the runner host's `GITHUB_TOKEN` is exported to) into `/work/...`. Moving the shim to a checked-in file (prior-audit fix) remains the right answer.
- **[HIGH-B2] ~~PECL tgz URLs fetched with zero integrity check~~ — ✅ PARTIALLY RESOLVED 2026-04-18 (binaries-repo PR [#12](https://github.com/nks-hub/webdev-console-binaries/pull/12), commit `fb8e7ed`, awaits merge after #6).** `verify_pecl_tgz` helper added with 4-layer verification: (1) size ≥ 1 KB, (2) magic bytes `1F 8B` (gzip), (3) actual bytes == REST `<f>` filesize from `https://pecl.php.net/rest/r/<pkg>/<ver>.xml`, (4) fallback to manifest `WARN … no_upstream_hash` on REST unreachable. Exit 17 = `hash_mismatch` → `FAIL` manifest line; linux-x64 hard-fails, macOS/Windows tolerant. **Cryptographic hash NOT available** — PECL publishes only filesize + per-extracted-file md5 (post-extract, non-trivial to validate without PEAR logic). Upgrade to sha256 needs upstream PECL change — SECURITY.md roadmap. Covers all 11 pinned extensions (apcu, redis, xdebug, igbinary, yaml, imagick, mongodb, swoole, memcached, oauth, imap). Defeats HTML-as-tgz + empty/truncated + swapped-size payload; residual risk = same-size swap (requires compromised TLS cert or PECL infra breach).
- **[HIGH-B3] ~~`install_pecl_ext` silently swallows PECL failures~~ — ✅ RESOLVED 2026-04-18 (binaries-repo PR [#6](https://github.com/nks-hub/webdev-console-binaries/pull/6), commit `08961dd`, awaits merge).** Helper now writes `PASS/FAIL/SKIP <ext> <ver> <reason>` lines to `pecl-build-manifest-<target>.txt` (uploaded as release asset alongside PHP archive) + **hard-fails on linux-x64** (reference build) via `::error::` + `return 1`. macOS/Windows still tolerant per existing matrix contract. Fine-grained exit codes 10-16 decode to download/extract/phpize/configure/make/install-stage reason.
- **[MEDIUM-B4] ~~No `concurrency:` block across 7 workflows~~ — ✅ RESOLVED 2026-04-18 (binaries-repo PR [#15](https://github.com/nks-hub/webdev-console-binaries/pull/15), commit `d0b6c26`, awaits merge).** All 7 workflows (`build-apache`, `build-binaries`/PHP, `build-mariadb`, `build-mirror`, `build-mkcert`, `build-nginx`, `build-redis`) gained top-level `concurrency:` block. Group key: `${{ github.workflow }}-${{ inputs.<version-input> || github.ref }}`. `build-mirror.yml` uses composite `...-${{ inputs.app || '' }}-${{ inputs.version || github.ref }}` so caddy/cloudflared/mailpit don't cross-serialize. `cancel-in-progress: false` — long-running compiles survive metadata re-tags. Different versions still build in parallel.
- **[MEDIUM-B5] `skip_source=true` for PHP 5.6 + 7.0 produces an empty Linux/macOS matrix slot with no marker.** The CI run summary shows green for the slot, the release exists, and it has only a `windows-x64` asset — but the catalog is the only thing that knows this is intentional. No `SKIPPED` file uploaded to the release; a user inspecting `gh release view binaries-php-7.0.33` sees only the Windows zip and has no idea whether Linux was tried-and-failed or intentionally-omitted.

### C. `build-mariadb.yml` (75 lines)

- **[HIGH-C1] ~~mariadb `|| true` + `fail_on_unmatched_files: false`~~ — ✅ RESOLVED 2026-04-18 across 2 PRs** (stacked fix because each half was independent):
  - **Integrity half** (`|| true` → verify): PR [#9](https://github.com/nks-hub/webdev-console-binaries/pull/9) `2be65be` — restructured `curl ... || true` chain into `if curl; then verify_sha256; fi`. SHA mismatch now fails the job.
  - **Completeness half** (silent missing asset): PR [#13](https://github.com/nks-hub/webdev-console-binaries/pull/13) `9d04d25` — `ASSETS+=()` enumeration output → upload uses `${{ steps.download.outputs.files }}` + `fail_on_unmatched_files: true`. linux-x64 mandatory (`::error::` + exit 1 if both bintar variants 404); macOS-arm64 + Windows MSI optional (`::warning::` + skip, but tracked in array so upload won't glob them back in). Base branch = PR #9 (no rebase needed).
- **[HIGH-C2] ~~MSI vs ZIP format mismatch~~ — ✅ RESOLVED 2026-04-18 (binaries-repo PR [#5](https://github.com/nks-hub/webdev-console-binaries/pull/5), commit `e73bb9c`, awaits merge).** `build-mariadb.yml` Windows step switched from `.msi` (no daemon handler) to upstream `mariadb-${V}-winx64.zip` from `archive.mariadb.org` (verified 200 OK, 86.7 MB, content-type application/zip). Aligns asset format with catalog expectation + daemon's ZIP extractor. Linux/macOS steps unchanged. HIGH-C1 (`|| true` + `fail_on_unmatched_files:false`) remains separate finding.
- **[MEDIUM-C3] `bintar-linux-systemd-x86_64` preferred, fallback to `bintar-linux-x86_64`.** Silent fallback: a systemd-dependent binary (from `-systemd-` tarball) runs fine under a systemd host but fails on musl / non-systemd. No logging of which path won. `||` chain: line 50-52. Logging + forcing one path deterministically would save future debugging.

### D. `build-mirror.yml` (106 lines — caddy/cloudflared/mailpit)

- **[HIGH-D1] ~~cloudflared redundant arm64 assets + workspace state leak~~ — ✅ RESOLVED 2026-04-18 (binaries-repo PR [#8](https://github.com/nks-hub/webdev-console-binaries/pull/8), commit `c44ffa5`, awaits merge).** `build-mirror.yml` refactored: explicit `rm -f *.tgz *.tar.gz *.zip` cleanup at step start, `extract_darwin()` helper replaces `|| true` on arm64 (both arches fail-fast), explicit `ASSETS+=()` array → `$GITHUB_OUTPUT` → upload `files:` list (no glob), `fail_on_unmatched_files: true`. Pre-existing orphaned `.tgz` assets on live releases (e.g. `binaries-cloudflared-2026.3.0`) require `gh release delete-asset` cleanup — flagged in PR body, needs explicit user approval post-workflow-validation. (PR renumbered from #6 to #8 due to parallel-agent PR collision — the branch `fix/mirror-no-workspace-leak` commit is canonical.)
- **[MEDIUM-D2] `chmod +x` inside `run:` is a no-op for GitHub release assets.** Release asset bits don't preserve Unix mode. Downloaders must `chmod +x` themselves post-download. This is a documentation/daemon concern, not a workflow concern, but the workflow line gives a false sense that the mode will be preserved.
- **[MEDIUM-D3] Zero upstream checksum consumption.** Caddy: `caddy_${VER}_checksums.txt` publish. Mailpit: same pattern. Cloudflared: has release-asset `*.sha256` per architecture. **All ignored.** This is the single lowest-effort fix across all 7 workflows — `curl -fsSL -O <base>/checksums.txt && sha256sum -c --ignore-missing checksums.txt`.

### E. `build-mkcert.yml` (62 lines)

- **[MEDIUM-E1] Mirror ships 3 triplets; catalog advertises 5.** Catalog expects `linux-arm64` and `macos-x64` (seen in `curl /api/v1/catalog`). Mirror only produces `linux-x64`, `macos-arm64`, `windows-x64`. Catalog bypasses the mirror entirely and points at FiloSottile upstream URLs for all 5 (prior audit); the nks-hub release is therefore completely unused. Add 2 curls to close the gap, then flip the catalog source.

### F. `build-nginx.yml` (129 lines)

- **[MEDIUM-F1] nginx macOS arm64 tar.xz is 387 KB and dynamically linked.** Install layout: `sbin/nginx + conf/* + html/*`. The binary is ~300 KB itself. All dependencies (pcre2, openssl@3, zlib) are runtime-resolved via `/opt/homebrew/...` paths baked into the binary. A target user without those brew formulae installed will get "dyld: Library not loaded: /opt/homebrew/opt/pcre2/lib/libpcre2-8.0.dylib". The tar.xz is effectively a developer preview, not a distributable binary. Either:
  - build statically (`--with-cc-opt="-static" --with-ld-opt="-static"`) — not trivial with OpenSSL but doable
  - bundle the dylibs into the tar.xz under `lib/` and add an `install_name_tool -change` fix-up pass
  - document the brew dependency in the release body
- **[LOW-F2] Windows build is nginx.org's stable ZIP — no HTTP/3, no ngx_brotli, no SSE4-optimized pcre.** Fine for WDC's local-dev use case, worth noting in the release body.

### G. `build-redis.yml` (124 lines)

- **[MEDIUM-G1] Windows redis uses `tporadowski/redis` unofficial Windows fork.** The fork last tagged v5.0.14.1 (2022) per the URL template; the workflow still tries the pattern for 7.4.2 — it falls through to the second URL (`redis-windows/redis-windows`) via the `for URL in ... do if curl -fsIL ...` guard. The guard is correct, but there's **no logging** of which URL was picked. For audit, we don't know whether the 16 MB `redis-7.4.2-windows-x64.zip` in the release came from tporadowski or redis-windows. Evidence trail is lost at build time.
- **[LOW-G2] Linux/macOS `tar xf redis-src.tar.gz` leaves no version pin.** The `make PREFIX=$PREFIX install` only ships `bin/redis-server, bin/redis-cli, bin/redis-benchmark, ...` — confirmed via `tar -tJf redis-7.4.2-macos-arm64.tar.xz` (6 binaries, no config templates, no `redis.conf`). Plugin-side `RedisPlugin` in the daemon is expected to generate `redis.conf`; verify that this is actually the case.

---

## §2. Build reproducibility (deeper)

- **[MEDIUM-RP1] No `SOURCE_DATE_EPOCH` export in any workflow.** Standard reproducible-build env var. Adding `export SOURCE_DATE_EPOCH=$(git log -1 --pretty=%ct 2>/dev/null || date +%s)` at the top of each build job is one line. Most GNU toolchain, autoconf-based builds (Apache, PHP, Nginx, Redis) honor it for mtime and archive deterministic ordering.
- **[MEDIUM-RP2] `tar -cJf` is non-deterministic by default.** Add `--sort=name --owner=0 --group=0 --mtime="@${SOURCE_DATE_EPOCH}" --numeric-owner --pax-option=exthdr.name=%d/PaxHeaders/%f,delete=atime,delete=ctime` for reproducibility. Specifically line `build-apache.yml:155`, `build-nginx.yml:114`, `build-redis.yml:111`, `build-binaries.yml:436`.
- **[INFO-RP3] `macos-14` runner image is rolling.** GitHub pins `macos-14.x.y` underneath but the workflow uses the short alias. Xcode / Homebrew versions move. Pinning to a digest is impractical on hosted runners; accept as-is, document in SECURITY.md.

## §3. Release body provenance (completely missing)

Every `Upload to release` step has a hand-written 1-2 sentence body (see §1). None include:

- `${{ github.run_id }}` / `${{ github.server_url }}/${{ github.repository }}/actions/runs/${{ github.run_id }}` — **the "Built from" link that SECURITY.md promises exists**
- `${{ github.sha }}` for workflow-source commit pin
- Upstream tarball URL actually used
- sha256 digests (GitHub already has them; the body could echo them from `sha256sum` output of the upload step)
- OS / Xcode / compiler version (`$RUNNER_OS`, `sw_vers`, `gcc --version`)

**[HIGH-RB1] ~~SECURITY.md makes a provenance claim the releases do not fulfill~~ — ✅ DISMISSED 2026-04-18 (false positive).** Re-read after top-5 #3 resolution: current SECURITY.md explicitly states "Source tarballs are not signature-verified in the workflow yet — see 'Future hardening' below" and lists "GPG-verify upstream PHP/Apache/Nginx source tarballs" as roadmap item #2. The "Built from" claim cited in the original review was not found in the actual file. Review-generation artifact; finding closed without code change.

## §4. Run-history forensics

`gh run list --limit 80`:

| Workflow | Success | Failure |
|---|---:|---:|
| Build PHP Binaries | 38 | 24 |
| Build Apache HTTPD Binaries | 1 | 5 |
| Build Nginx Binaries | 1 | 1 |
| Build Redis Binaries | 1 | 1 |
| Build MariaDB Binaries | 2 | 0 |
| Mirror Upstream Binaries | 4 | 0 |
| Dependabot Updates | 2 | 0 |

Observations:
- **[MEDIUM-RH1] Apache success rate is 1/6 (17%).** The single green run published the corrupt Windows ZIP (see top-5 #1). No human-facing signal distinguished the broken success from a fixed success.
- **[MEDIUM-RH2] PHP failure rate (24/62 = 39%) is high and correlated with retry-suffix tags.** Developers re-tagging `-r2`/`-r3` costs CI minutes, widens the window for a concurrent-run race (no `concurrency:` block), and doubles the attack surface of PECL curl-install steps.
- **[LOW-RH3] No failure notification.** No Slack/email hook. Catalog is manually synced (prior audit), so a failed run is silently ignored until a human notices the catalog didn't move.

## §5. Governance / supply chain

- **[CRITICAL-G1]** `main` branch: no protection (see top-5 #3).
- **[HIGH-G1] ~~No `CODEOWNERS`~~ — ✅ RESOLVED 2026-04-18 (binaries-repo commit `97b62664`)** — `.github/CODEOWNERS` committed with `* @LuRy`, covers `.github/workflows/**` via the wildcard.
- **[HIGH-G2] ~~Security toggles off~~ — ✅ RESOLVED 2026-04-18** — secret_scanning + secret_scanning_push_protection + vulnerability_alerts + automated_security_fixes all enabled (see top-5 #3).
- **[LOW-G3]** `dependabot.yml` is correct scope (`github-actions`, weekly), BUT because no workflow is SHA-pinned (prior audit C1), Dependabot has no concrete pins to bump — its only visible work so far is 2 successful runs that likely closed no-op PRs. After SHA-pinning the ~15 action refs across 7 workflows, Dependabot becomes active.
- **[INFO-G4]** SECURITY.md is better than most hobby-project repos: names an email contact, 5-day SLA, roadmap items are concrete. Out-of-date claim about "Built from" link (see RB1) is the only factual error.

## §6. Comparison table (per-app grid)

| App | Upstream source | Upstream hash/sig available? | Verified in CI? | Signed output? | Self-test? | Reproducible? | OS×arch triplets | Actions SHA-pinned? |
|---|---|:---:|:---:|:---:|:---:|:---:|---|:---:|
| php | php.net tarball (Linux/mac); windows.php.net zip | yes (.asc + .sha256) | **no** | no | no | no | 3 (skipped 5.6/7.0 Linux, 7.x/8.0 mac) | **no** |
| apache | apache.org tarball; ApacheLounge scrape Win | yes (.asc + .sha256 + .sha512); ApacheLounge SHA1 in page HTML | **no** | no | no | no | 3 (Win currently **BROKEN**) | **no** |
| nginx | nginx.org tarball; nginx.org Win zip | yes (.asc) | **no** | no | no | no | 3 (mac tarball is dyld-fragile) | **no** |
| redis | download.redis.io; tporadowski/redis-windows (Windows) | hashes.txt on Redis 7.2+ | **no** | no | no | no | 3 | **no** |
| mariadb | archive.mariadb.org (all triplets) | sha256sums.txt per release | **no** | no | no | no | 3 declared, typically 2 (macos silently-dropped) | **no** |
| mkcert | FiloSottile/mkcert GitHub release | GitHub API `digest` | **no** | no | no | yes (upstream is) | 3 (catalog expects 5) | **no** |
| caddy | caddyserver/caddy GitHub release | checksums.txt | **no** | no | no | upstream | 3 | **no** |
| cloudflared | cloudflare/cloudflared GitHub release | per-asset .sha256 | **no** | no | no | upstream | 5 (inconsistent `.tgz` on mac-arm64) | **no** |
| mailpit | axllent/mailpit GitHub release | checksums file | **no** | no | no | upstream | 3 | **no** |
| mysql | n/a (no workflow — dev.mysql.com direct) | upstream | n/a | n/a | n/a | n/a | 1 (Windows) | n/a |

Rows of "no" form a neat column — that's the shape of the hardening roadmap.

## §7. Unique findings NOT in the prior two audits

1. **[CRITICAL] Apache Windows ZIP is HTML** (Top-5 #1).
2. **[CRITICAL] GitHub-native `digest` field is ignored** (Top-5 #2). Neither audit noticed GitHub already exposes sha256.
3. **[HIGH] `fail_on_unmatched_files` expression evaluates to a string** (Top-5 #4).
4. **[HIGH] SECURITY.md "Built from" claim is a lie** (RB1).
5. **[HIGH] cloudflared release has two redundant macOS arm64 assets** (D1) — evidence of a workspace-state leak between matrix runs.
6. **[MEDIUM] macOS arm64 nginx tarball is not self-contained** (F1) — dyld-linked to Homebrew paths.
7. **[MEDIUM] MariaDB `|| true` fallback chain masks OS/arch drops** (C1).
8. **[MEDIUM] PHP PECL install warnings become silent feature-drops** (B3).
9. **[MEDIUM] No `SOURCE_DATE_EPOCH` export for autoconf reproducibility** (RP1).
10. **[MEDIUM] `tar -cJf` non-deterministic flags** (RP2).
11. **[LOW] No failure notification / Slack hook** (RH3).
12. **[INFO] main branch protection + CODEOWNERS + security toggles** (G1-G3).

## §8. Recommended hardening roadmap (top 10, one-PR scope each)

Ordered by impact × effort:

1. **Fix Apache Windows ZIP silent-HTML bug.** 3-line addition to `build-apache.yml` step (magic-byte check + abort). Prevents today's functional outage. ~15-min PR.
2. **Wire GitHub's native `digest` into catalog-api.** Change `wdc-catalog-api/app/data/...` JSON files to emit `sha_256` from `gh release view --json assets`. One generator PR. Downstream: daemon `BinaryDownloader` + `DownloadDoc` schema PR to verify on download. Closes the entire C2/H1/H7 triad from prior audit **without touching any workflow**.
3. **Enable branch protection on `main` + add `CODEOWNERS` covering `.github/workflows/**`.** 5-min settings change + single-file commit.
4. **Enable repo security toggles (`secret_scanning*`, `dependabot_security_updates`).** 3-click settings change.
5. **Add upstream verification to the 3 easiest workflows first.** Mirror workflow (caddy/cloudflared/mailpit all publish checksum files) → mkcert (use GitHub API digest) → mariadb (sha256sums.txt). These are the lowest-friction and cover 5 of 10 apps.
6. **Pin all `softprops/action-gh-release@v2` to immutable SHA.** Dependabot already configured; just mechanically replace tags. Covers the C1 finding from the prior audit.
7. **Add post-upload smoke test per OS.** `php --version`, `apachectl -V`, `nginx -v`, `redis-cli --version`, `mariadb --version`, `caddy version`, `cloudflared --version`, `mailpit version`, `mkcert -version`. Single step per matrix slot; fails-fast if the tarball is broken (would have caught the Apache-HTML bug).
8. **Fix `fail_on_unmatched_files` fragility in `build-binaries.yml`.** Replace the inline expression with a per-matrix `ext` override that simply omits the `files:` line for skipped slots. Pairs with adding an explicit `SKIPPED.txt` asset so downstream can tell intentional-skip from build-failure.
9. **Template release bodies with run_id / sha / upstream URL / sha256.** Single string replacement per workflow. Closes H1/M5 + aligns SECURITY.md claims with reality.
10. **Add `concurrency:` block per workflow.** `group: release-${{ github.ref }}`, `cancel-in-progress: false`. Prevents retry-tag races.

---

**Blocker checks:** none hit. `gh auth status` green, all `gh api` calls returned well-formed JSON, all 7 workflow YAMLs parsed clean. Review performed read-only; no workflow edits, no commits, no tag pushes in the binaries repo.
