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


## 2026-04-18 09:08 UTC — MariaDB builds succeeded, catalog JSON updated (but live DB NOT updated)

**Builds completed:**
- `binaries-mariadb-11.8.3` published 08:50:56Z — linux-x64.tar.gz + windows-x64.msi
- `binaries-mariadb-12.3.1` published 08:51:39Z — linux-x64.tar.gz + windows-x64.msi

**Catalog source updated:** `wdc-catalog-api` commit `5dc6b7e` adds `linux/x64` downloads for both releases to `app/data/apps/mariadb.json`.

**BLOCKER for live rollout:** The JSON file is a first-boot seed only (`app/service.py:308 seed_from_json` short-circuits when `App` table has rows). The deployed DB at `wdc.nks-hub.cz` keeps the old windows-only entries. To publish the new URLs live, someone must apply them via the admin UI / API mutation endpoints (add_download per release), OR rebuild the prod DB from seed (data-loss risk).

**Next step:** Open a proper ticket for a "resync catalog JSON → DB" admin action, or accept that live updates flow only through the admin UI and not through git. Decision needed from owner.

