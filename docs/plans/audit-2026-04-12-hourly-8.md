# NKS WebDev Console — Hourly Strict Plan Audit #8 (2026-04-12)

## Summary

- **150 commits** on master, **441 tests** (285 daemon + 117 core + 39 catalog-api)
- **10 plugins**, **10 generators**, **11 doctor checks**
- Phase 0–10: 130/131 done (self-update open)
- Phase 11: 7/10 done + 1 partial (Nginx/PostgreSQL/RBAC not planned)

## New Since Audit-7 (18 commits)

- Backup UI, PluginPage error handling, MAMP migration UI
- SslManager/PhpManager/CloudflareTunnel/SiteEdit error alerts
- CLI: activity, backup list, cloudflare dns, binaries outdated, logs --follow, status --quiet, php extensions --toggle, sites/services/databases/plugins/binaries/doctor/logs pipe detection
- Security: tab+null byte blocked in Node command args
- Tests: BinaryManager validation, ConfigValidator PHP/MySQL/Redis, WdcPaths, NodeSiteStatus, DockerComposeRunner, SemverComparer, Node generator

## CLI Pipe Detection

All 7 list commands auto-detect `Console.IsOutputRedirected`:
sites, services, databases, plugins, binaries, doctor, logs

## No downgrades. No regressions.
