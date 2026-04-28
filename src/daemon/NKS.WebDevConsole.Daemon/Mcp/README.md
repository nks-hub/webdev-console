# MCP Module — Phase 8 Redesign

Operator reference for the redesigned Model Context Protocol module in
NKS WebDev Console (v0.3.0+). Consolidates the four pre-Phase-8 surfaces
(Hub / Intents / Grants / Kinds) into a single page with four tabs and a
brand-new **Activity** view that exposes 100 % of MCP traffic, not just
signed destructive intents.

## What changed at a glance

| Before (≤ v0.2.25)                                | After (v0.3.0+)                                            |
|---------------------------------------------------|------------------------------------------------------------|
| 4 separate routes (Hub, Intents, Grants, Kinds)   | 1 hub page, 4 tabs                                          |
| Audit only of *signed* destructive intents        | Full audit of every tool call (read + mutate + destructive) |
| Domain-heavy labels (intent / kind / grant)       | Plain language (Activity / Requests / Rules / Catalog)      |
| 30 s polling refresh                              | SSE `mcp:tool-call` real-time + 5 min safety net            |
| No actionable hints                               | Suggested grants from repeated manual approvals             |
| No first-run UX                                   | Onboarding panel with 4 trust profiles                      |
| No analytics                                      | 24 h timeline, top-tools panel, p50/p95/p99 latency, throughput, error rate |
| Approve-only via banner (queue, scroll-to-find)   | Inline corner toast + banner queue side-by-side             |
| No CSV export                                     | `Export CSV` (RFC 4180) with current filters                |
| Audit table grew unbounded                        | Hourly retention sweep (default 30 d, configurable 1–365)   |

## The four tabs

### 1. ⚡ Activity (default)
Every MCP tool call recorded by the MCP server is shown here as a
session-grouped feed. A "session" = one MCP client connection (UUID
generated at MCP server boot). Sessions break on a 5 min gap or a
sessionId change.

- **Stats banner** — total / reads / mutates / destructives / errors / sessions for the last 24 h.
- **Perf KPIs** — calls/min throughput, p50 / p95 / p99 latency, error rate. Yellow above 1 % errors or > 1 s p95; red above 5 % errors or > 5 s p99.
- **24 h timeline** — stacked-bar SVG, one bar per hour. Read = blue, mutate = yellow, destructive = red. Hover for hour totals.
- **Top tools 24 h** — top 10 most-called tools with horizontal bar, avg duration, error pill. Click → instant filter on the feed.
- **Session cards** — first expanded, rest collapsed. Click header to toggle. Header shows caller pill, session id, age, total + per-danger counts.
- **Read-collapse** — runs of ≥ 3 consecutive read calls within a session collapse to "N× read collapsed" (click to expand).
- **Per-call detail** — click any call row → expand inline with full id, calledAt, argsHash, intentId link, error, full args (copyable).
- **Filters** — danger level, tool name, session id (auto-set from pill click), sessions/flat view toggle.
- **Real-time** — `mcp:tool-call` SSE event refreshes feed within ~600 ms (throttled).

### 2. 🔒 Requests (formerly Intents)
Live queue of signed AI requests waiting for confirmation. Same as
before, plus the inline approve toast that pops in the lower-right
corner alongside the persistent banner.

### 3. 🔑 Rules (formerly Grants)
Auto-approve rules. Pattern: `kindPattern × targetPattern × scope (always
| session | api_key)` with optional cooldown. The hub header chip
"⚠️ N never matched" links here filtered by `usage=deadweight` so unused
rules can be cleaned up.

### 4. 📚 Catalog (formerly Kinds)
Read-only inventory of every destructive operation kind plugins have
registered. Each row shows the wire-format id, the human label, the
plugin id, and the danger level.

## Hub-level surfaces

Banners that appear above the tabs based on state:

### Onboarding panel
Shown on first visit (zero grants AND `localStorage["mcp.onboarding.dismissed.v1"]` not set).
Four preset profiles:

| Profile        | Grants created                              | Notes                                |
|----------------|---------------------------------------------|--------------------------------------|
| 🛡️ Minimal      | none                                        | Every destructive op asks            |
| ⚖️ Balanced ⭐ | `deploy/*`, `rollback/*`                    | Reversible auto, destructive asks    |
| 🚀 Full Trust  | `*/*`                                       | Test envs only — pair with Always-ask|
| 🎯 Manual      | none — opens Rules tab                      | Build your own                       |

### Suggested grants
Yellow banner. Polls `/api/mcp/grants/suggested` (last 7 d, ≥ 3 manual
approvals on identical kind+domain+host). Click "Auto-approve" to mint
an `always` grant matching `kind × domain/*`.

### Inline approve toast
Element Plus `ElNotification` in `bottom-right`, 60 s duration, three
buttons: **Approve** / **Reject** / **Detail**. Auto-dismisses on
`mcp:intent-changed` SSE so the toast disappears when the operator
resolves the intent through any other surface.

## Daemon REST endpoints

All require Bearer auth; all gated by `mcp.enabled` setting.

| Method | Path                                       | Purpose                                                     |
|--------|--------------------------------------------|-------------------------------------------------------------|
| POST   | `/api/mcp/tool-calls`                      | Append audit row (called by MCP server, fire-and-forget)    |
| GET    | `/api/mcp/tool-calls`                      | List with `?limit&offset&dangerLevel&toolName&sessionId`    |
| GET    | `/api/mcp/tool-calls/stats`                | Aggregate + percentiles + throughput (`?withinMinutes`)     |
| GET    | `/api/mcp/tool-calls/timeline`             | Hourly buckets (`?withinHours`, default 24)                 |
| GET    | `/api/mcp/tool-calls/by-tool`              | Top-N grouped by toolName (`?withinHours&limit`)            |
| GET    | `/api/mcp/tool-calls/export.csv`           | Stream RFC 4180 CSV, capped 10 k rows, respects filters     |
| GET    | `/api/mcp/grants/suggested`                | Aggregated cleanup hints (`?withinDays&minOccurrences`)     |

## Settings keys

Settings → MCP section (gated on `mcp.enabled`):

| Key                                  | Default | Range  | Effect                                                        |
|--------------------------------------|---------|--------|---------------------------------------------------------------|
| `mcp.enabled`                        | false   | bool   | Master switch — every `/api/mcp/*` returns 404 when off       |
| `mcp.strict_kinds`                   | false   | bool   | Validator rejects intents for un-registered kinds             |
| `mcp.always_confirm_kinds`           | (empty) | csv    | Kinds that ALWAYS prompt the GUI banner regardless of grants  |
| `mcp.grant_expired_retention_days`   | 1       | 0–365  | Janitor delay before deleting expired grants                  |
| `mcp.grant_revoked_retention_days`   | 30      | 0–365  | Audit window before purging revoked grants                    |
| `mcp.toolCallRetentionDays`          | 30      | 1–365  | Hourly sweep of `mcp_tool_calls` audit table                  |

## Schema

Migration **017** introduced `mcp_tool_calls`:

```sql
CREATE TABLE mcp_tool_calls (
    id           TEXT    PRIMARY KEY,
    called_at    TEXT    NOT NULL,        -- ISO 8601 UTC
    session_id   TEXT,                    -- MCP server-generated UUID
    caller       TEXT    NOT NULL DEFAULT 'unknown',  -- e.g. 'claude-code'
    tool_name    TEXT    NOT NULL,
    args_summary TEXT,                    -- first 500 chars of args JSON, secrets redacted
    args_hash    TEXT,                    -- SHA-256 / 16 chars
    duration_ms  INTEGER NOT NULL DEFAULT 0,
    result_code  TEXT    NOT NULL DEFAULT 'ok',  -- 'ok' | 'error' | 'denied'
    error_message TEXT,
    danger_level TEXT    NOT NULL DEFAULT 'read',  -- 'read' | 'mutate' | 'destructive'
    intent_id    TEXT                     -- cross-link to deploy_intents when applicable
);
```

Indexes cover the four hot read paths: list (`called_at DESC`), session
group (`session_id, called_at`), danger filter (`danger_level, called_at DESC`),
per-tool history (`tool_name, called_at DESC`).

## MCP server side (services/mcp-server)

`src/auditLog.ts` exports `wrapHandler(toolName, originalHandler)` which:

1. Generates a per-process session UUID at module load time.
2. Classifies tools by static map (`DANGER_OVERRIDES`) — anything not
   listed defaults to `read`.
3. After each call, fires-and-forgets `POST /api/mcp/tool-calls` with
   `{ toolName, sessionId, caller='mcp-server', dangerLevel,
     durationMs, resultCode, errorMessage, argsSummary, argsHash }`.

`index.ts` monkey-patches `server.registerTool` once at boot so every
tool registered by the 12 module files transparently gets wrapped.

## SSE events

| Event                  | Payload                                                              | Fired when                                               |
|------------------------|----------------------------------------------------------------------|----------------------------------------------------------|
| `mcp:tool-call`        | `{ id, toolName, sessionId, caller, dangerLevel, resultCode, durationMs }` | After every successful audit insert                |
| `mcp:intent-changed`   | `{ intentId, change }`                                               | Intent confirmed / revoked / consumed                    |
| `mcp:confirm-request`  | `{ intentId, prompt, kind, kindLabel, domain, host }`                | Daemon needs GUI to approve a destructive intent         |
| `mcp:grant-changed`    | `{ change, id }`                                                     | Grant created / updated / revoked / swept                |
| `mcp:settings-changed` | (none)                                                               | `mcp.*` setting saved                                    |

Activity feed throttles its `mcp:tool-call` reaction to one refresh
per 600 ms because read-heavy AI sessions can fire hundreds of events
per second.

## Tests

`tests/NKS.WebDevConsole.Daemon.Tests/McpToolCallsRepositoryTests.cs`
covers 10 scenarios: insert round-trip, default fallbacks, danger /
tool / session filters, paging, ordering, count, stats with percentiles,
prune, by-tool aggregation. All hermetic — per-test SQLite file in
`%TEMP%`, deleted in finally.

## What's deferred

- **Trust tiers (Sandbox / Standard / Privileged)** — would simplify
  configuration drastically but requires auth-token scope refactor.
  Marked for a separate session.
- **Per-session detail dialog** — nice-to-have modal showing full call
  list per session with copy buttons; current click-to-expand on rows
  covers 90 % of the use case.
- **Error-state heatmap** — chart highlighting error spikes; today
  errors are surfaced through the KPI strip and per-row red tags.

## Migration / rollback

Migration 017 is additive and idempotent (`CREATE TABLE IF NOT EXISTS`).
Rolling back the daemon to a pre-Phase-8 build leaves the table in
place; nothing else references it. The MCP server `auditLog.ts` is
fire-and-forget so a rolled-back daemon (no `/api/mcp/tool-calls`
endpoint) just produces stderr warnings without breaking tool calls.

## Commit chain (Phase 8)

```
be84c04  Phase 1+2 — DB + endpoints + middleware + 4-tab hub
4401c99  3a — inline approve toast
74a353b  3b — suggested grants banner
e437d6d  3d — onboarding panel
c8acdc4  Polish 1 — SSE mcp:tool-call + session click filter
c6029ab  Polish 2 — retention sweeper + CSV export
1909006  Polish 3 — 24 h timeline chart
542976b  Polish 4 — top tools panel
08d7d86  Polish 5 — CommandPalette + per-call detail
44ddfcc  Polish 6 — configurable retention (settings)
4bdc1a1  Polish 7 — perf KPIs (p50/p95/p99 + throughput + error rate)
164c83a  Polish 8 — McpToolCallsRepository unit tests (10×)
```
