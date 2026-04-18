# Binaries Audit Log

Rolling log of audit deltas against the live catalog at https://wdc.nks-hub.cz/api/v1/catalog and the GitHub releases on https://github.com/nks-hub/webdev-console-binaries. New sections appended at top.

For the full baseline audit with per-app coverage matrix, see [`binaries-audit-2026-04-18.md`](./binaries-audit-2026-04-18.md).

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

