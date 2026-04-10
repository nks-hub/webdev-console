# NKS WebDev Console — Autonomous Sub-Agent Workflow

Guide for parallel iteration using cron-driven loops and dispatched sub-agents against the NKS WDC codebase (Electron + Vue + C# daemon + plugin DLLs).

Target: Phase 7+ (post-MVP integration tests, benchmarks, docs, i18n). Main agent is Claude Opus in a persistent session. Sub-agents are dispatched via `Task` tool with `subagent_type`. State is shared via task list, MCP memory, git worktrees, and the daemon REST/SSE surface.

---

## 1. Orchestration Model

### When to dispatch a sub-agent

Dispatch when ANY of these apply:

- Task touches >5 files or >500 LOC.
- Task requires a different tool specialization (security audit, perf bench, test writing).
- Task is independent of current work (no shared files, no shared state).
- Main agent context window is >60% full and task would blow it past 80%.
- Task is long-running (>15 min inline) and blocks other progress.
- Task is exploratory and may return empty — offload to `Explore` to avoid polluting main context.

### When to stay inline

- Single-file edit or tight feedback loop (compile → test → fix).
- Task requires knowledge of prior decisions made in current turn.
- Task <5 minutes of tool calls.
- Task is sequential by nature (e.g. "read log, fix bug, rebuild").

### Partition heuristic

Split a large task iff: `(files_touched × avg_complexity) / max_agent_capacity > 1`.

Max agent capacity rule of thumb: 15 files or 2000 LOC per sub-agent before context degrades.

### Parallel vs serial

Parallelize when:
- Tasks touch disjoint file sets (verified via `git ls-files | grep` before dispatch).
- No task depends on another's output.
- Each task has a clear success criterion (build passes, test passes, no lint errors).

Serialize when:
- Output of A is input to B (e.g. security-auditor finds vulns → debugger confirms → test-automator writes regression).
- Shared mutable state (same config file, same DB schema).
- Rate-limited external resource (daemon can only run one integration suite at a time).

---

## 2. Task Partitioning for Phase 7+

### Phase 7.A — Integration Test Suite (parallel-friendly)

Split 15 e2e scenarios from `docs/e2e-scenarios.md` into 3 balanced packages:

| Package | Scenarios | Sub-agent | Files owned |
|---|---|---|---|
| P7.A.1 | Sites CRUD, vhost gen, SSL, hosts write | `full-stack-orchestration:test-automator` | `tests/e2e/sites/**` |
| P7.A.2 | Services lifecycle (Apache, MySQL, PHP-FPM, Redis, Mailpit) | `full-stack-orchestration:test-automator` | `tests/e2e/services/**` |
| P7.A.3 | Plugin load/unload/hot-reload, config rollback, import/export | `full-stack-orchestration:test-automator` | `tests/e2e/plugins/**`, `tests/e2e/config/**` |

Each sub-agent owns its subdirectory exclusively. Shared test harness (`tests/e2e/helpers/*`) is frozen — created inline by main agent BEFORE dispatch.

### Phase 7.B — Performance Benchmarks (serial then parallel)

Serial bootstrap (main agent inline):
1. Create `tests/perf/harness/` with SSE client, HTTP load runner, process timing probes.
2. Lock harness — no sub-agent may modify.

Then parallel dispatch:
- `full-stack-orchestration:performance-engineer` → daemon REST load (1000 req/s baseline, /api/services, /api/sites, /api/plugins).
- `full-stack-orchestration:performance-engineer` → SSE throughput (1000 events/s metrics stream sustained 60s).
- `full-stack-orchestration:performance-engineer` → UI cold start (Electron spawn → first paint → Dashboard connected, <3s target).
- `full-stack-orchestration:performance-engineer` → plugin load parallel vs serial measured (validate iteration 6 claim: 700ms → 250ms).

Each writes to `tests/perf/results/<bench-name>/` — disjoint directories.

### Phase 7.C — Documentation (fully parallel)

| Doc | Owner agent | Target file |
|---|---|---|
| User Guide (install, first site, service control) | `general-purpose` | `docs/user-guide.md` |
| Plugin SDK Guide (IServiceModule, IApiEndpointProvider, manifest schema) | `Plan` then `general-purpose` | `docs/plugin-sdk.md` |
| API Reference (OpenAPI generation + narrative) | `backend-api-security:backend-architect` | `docs/api-reference.md` |
| Architecture diagrams (C4 model, deployment, IPC flow) | `Plan` | `docs/architecture.md` |
| Troubleshooting expansion (error codes, recovery) | `error-detective` | `docs/troubleshooting.md` |

All five can run truly in parallel — disjoint files, no code changes.

### Phase 7.D — Localization Framework (serial)

1. `Plan` agent: design i18n strategy (Vue-i18n for frontend, .resx for daemon, key extraction workflow). Output: `docs/plans/i18n-strategy.md`.
2. `multi-platform-apps:frontend-developer`: wire Vue-i18n, extract all hardcoded strings in `src/frontend/src/**/*.vue` to `locales/en.json`. Baseline English locale only.
3. `csharp-pro`: add `Microsoft.Extensions.Localization` to daemon, move user-facing strings (error messages, log categories) to `.resx`.
4. `general-purpose`: translate `en.json` → `cs.json` (Czech baseline since primary user is Czech).
5. `full-stack-orchestration:test-automator`: snapshot tests that every locale file has same keys.

Serial because each step depends on the previous (strategy → extract → translate → test).

---

## 3. Concrete Workflow Examples

### Example 1 — Parallel bug squash (3 agents, independent)

Trigger: user reports "frontend sidebar icons broken, backend /api/services returns 500 sometimes, no tests for sites delete".

Main agent (inline):
1. `TaskCreate`: `frontend-sidebar-icons`, `backend-services-500`, `test-sites-delete`.
2. Dispatch three `Task` calls in ONE message (parallel):
   - Agent A (`multi-platform-apps:frontend-developer`) — claim files: `src/frontend/src/components/Sidebar.vue`, `src/frontend/src/assets/icons/**`. Task: fix icons.
   - Agent B (`debugger`) — claim files: `src/daemon/NKS.WebDevConsole.Daemon/Api/ServicesController.cs`, `src/daemon/NKS.WebDevConsole.Core/Services/**`. Task: reproduce and fix 500.
   - Agent C (`full-stack-orchestration:test-automator`) — claim files: `tests/e2e/sites/delete.spec.ts`. Task: write regression test for sites delete.
3. Wait for all three. Each returns structured summary (files changed, tests run, remaining gotchas).
4. Main agent validates: run `dotnet build` + `npm run build` + test suite. If all pass → single commit per agent task.

### Example 2 — Sequential chain (security → debug → test)

Trigger: quarterly security audit.

1. Dispatch `full-stack-orchestration:security-auditor` with scope `src/daemon/**` and `src/frontend/src/main/**`. Output: findings list in task list (`TaskCreate` per finding).
2. For each `critical` or `high` finding, main agent dispatches `debugger` inline or batch (one per finding, parallel if files disjoint).
3. After fix committed, dispatch `full-stack-orchestration:test-automator` per finding to write regression test referencing the CVE-style ID from the audit.
4. Finally dispatch `superpowers:code-reviewer` to review the whole chain (security fixes + tests).

Sub-agents report back to main via task updates (`TaskUpdate` with `status: done`, `result: <summary>`).

### Example 3 — Audit cycle (Explore → Plan → Review → Fix)

Hourly cron prompt: "Run audit cycle on changed areas since last audit."

1. `Explore` agent: git log since last audit tag, map changed files to subsystems (daemon, frontend, plugins, tests). Output: `audit-scope.json` in `/tmp/wdc-audit/`.
2. `Plan` agent: reads scope, produces risk-ranked task list. Writes via `TaskCreate`.
3. `superpowers:code-reviewer` agent: reviews top-5 risk files. Output: inline review comments attached to tasks.
4. Main agent dispatches fixers (debugger, csharp-pro, typescript-pro) in parallel per finding.
5. `full-stack-orchestration:test-automator` adds tests covering each fix.
6. Main agent commits atomic per-finding.

---

## 4. State Sharing Between Agents

### File ownership claims (task list protocol)

Before dispatching a sub-agent, main agent writes file claims into the task description:

```
TaskCreate(
  title: "Fix sidebar icons",
  description: "CLAIMS: src/frontend/src/components/Sidebar.vue, src/frontend/src/assets/icons/**\nNOT_ALLOWED: anything under src/daemon/ or tests/",
  assignee: "frontend-developer"
)
```

Sub-agent MUST refuse to edit files outside its claim. Violation = rollback and re-dispatch.

### Git worktrees for heavy isolation

For multi-hour autonomous runs where agents touch large surfaces, use git worktrees:

```bash
git worktree add ../nks-ws-agent-a feature/phase-7-sites-tests
git worktree add ../nks-ws-agent-b feature/phase-7-services-tests
```

Each sub-agent operates in its own worktree. Merge back via main agent after verification. Skill `superpowers:using-git-worktrees` handles setup.

Use worktrees when:
- Parallel work expected to last >30 min.
- Agents need to run builds concurrently (different `bin/` dirs).
- Branch per workstream desired for review.

Skip worktrees when:
- Quick parallel edits (<10 min).
- All agents touch disjoint files anyway.

### MCP memory as shared knowledge store

Before starting: every sub-agent MUST call `mcp__memory__search_by_tag` with tags `nks-ws,nks-wdc` + subsystem tag.

After finishing: sub-agent stores learnings via `mcp__memory__store_memory` with tags including agent role + phase number (e.g. `nks-ws,phase-7,test-automator,e2e-sites`).

Main agent periodically consolidates via `mcp__memory__trigger_consolidation`.

### Daemon port file (shared daemon access)

All sub-agents read `%TEMP%\nks-wdc\daemon.port` and `%TEMP%\nks-wdc\daemon.token` to authenticate against the same daemon instance. Format:

```
port=5199
token=<base64-32bytes>
```

Agents wanting isolated daemon state spawn a second daemon on a random port and set `WDC_DATA_DIR` env var to an isolated dir. Convention: `%TEMP%\nks-wdc-agent-<id>\`.

### Task list as shared todo board

`TaskCreate` / `TaskUpdate` / `TaskList` operates on a single queue visible to all agents in the same session. Protocol:

- `status: pending` → not yet claimed.
- `status: in_progress` + `assignee: <agent-name>` → someone is working on it.
- `status: blocked` + `reason` → needs main agent intervention.
- `status: done` + `result` → completed, main agent validates.

Sub-agent dispatched with the task ID it owns. Sub-agent updates task to `in_progress` on start, `done` on finish.

---

## 5. Reporting From Sub-Agent to Main

Sub-agent's final message is captured verbatim by main. Enforce structured report format in every dispatch prompt:

```
Return a final report with exactly these sections:
## Files Modified
<absolute paths, one per line>
## Commands Run
<command + exit code, one per line>
## Tests Passed
<test name + status>
## Remaining Issues
<blockers or deferred items>
## Memory Stored
<memory content hashes>
```

Main agent parses this, updates task list, decides next move. No free-form prose expected.

---

## 6. Error Handling & Rollback

### Sub-agent failure modes

| Failure | Detection | Action |
|---|---|---|
| Build broken after edit | Main runs `dotnet build` + `npm run build` post-dispatch | `git stash` agent's changes, re-dispatch with error log as context |
| Test regression | Main runs targeted test after edit | Bisect which file broke it, re-dispatch debugger |
| Agent edited files outside claim | `git diff --name-only` post-dispatch | Hard rollback: `git checkout -- <file>`, re-dispatch with stricter claim |
| Agent hit context window limit | Incomplete report | Split task further, re-dispatch smaller pieces |
| Daemon crashed during e2e test | Test agent reports daemon EPIPE | Restart daemon via Electron main or CLI, retry once, escalate to debugger if persists |

### Rollback primitives

- File-level: `git checkout -- <path>`.
- Commit-level: `git reset --soft HEAD~1` (keep changes staged for inspection), then re-dispatch.
- Branch-level (worktree): `git worktree remove --force <path>` + re-create clean.
- Daemon state: kill daemon, `rm -rf %TEMP%\nks-wdc-agent-<id>\`, respawn.

Never use `git reset --hard` without user confirmation — it destroys uncommitted work from other agents.

---

## 7. Daemon Access for Sub-Agents

All sub-agents share one daemon instance via the port file contract:

```
C:\Users\<user>\AppData\Local\Temp\nks-wdc\daemon.port
```

Contents:
```
port=5199
token=<bearer-token>
pid=<daemon-pid>
```

Agent code (pseudo):
```ts
const portFile = path.join(os.tmpdir(), 'nks-wdc', 'daemon.port');
const { port, token } = parsePortFile(portFile);
const headers = { Authorization: `Bearer ${token}` };
const base = `http://127.0.0.1:${port}`;
```

### REST endpoints sub-agents use

- `GET /api/services` — enumerate services, check state.
- `POST /api/services/{id}/start` — start a service for test setup.
- `GET /api/sites` — list sites for integration tests.
- `POST /api/sites` — create test sites with disposable names (prefix `e2e-agent-<id>-`).
- `DELETE /api/sites/{name}` — teardown.
- `GET /api/plugins` — verify plugin load state before test.
- `GET /api/databases` + `/export`, `/import` — state snapshots.

### SSE event stream

`GET /api/events` with `Accept: text/event-stream`. Events: `metrics`, `log`, `service-state`, `plugin-load`, `config-change`.

Sub-agents subscribe to verify async behavior (e.g. "assert metrics event received within 2s of service start").

### Isolated daemon per agent (when needed)

For tests that mutate global state (hosts file, SSL certs), spawn dedicated daemon:

```bash
wdc-daemon --urls http://127.0.0.1:0 \
  --data-dir %TEMP%\nks-wdc-agent-<id> \
  --port-file %TEMP%\nks-wdc-agent-<id>\daemon.port
```

Port 0 = OS assigns free port. Agent reads back from port file after 500ms.

Cleanup on exit: kill by PID from port file, remove data dir.

---

## 8. CDP Inspection (Electron Renderer)

Launch Electron with `--remote-debugging-port=9222`. Sub-agents connect to `http://localhost:9222/json` to list pages, then to the WebSocket for DOM/console inspection without Playwright.

Use cases:
- `debugger` agent reads renderer console errors without opening a browser.
- `full-stack-orchestration:test-automator` snapshots DOM tree for regression baselines.
- `multi-platform-apps:frontend-developer` verifies computed styles after a CSS change.

Minimal CDP flow (agent pseudo):
```js
const targets = await fetch('http://localhost:9222/json').then(r => r.json());
const renderer = targets.find(t => t.type === 'page' && t.url.includes('index.html'));
const ws = new WebSocket(renderer.webSocketDebuggerUrl);
ws.send(JSON.stringify({ id: 1, method: 'Runtime.evaluate',
  params: { expression: 'document.querySelectorAll("aside nav a").length' } }));
```

Enable via Electron main:
```ts
app.commandLine.appendSwitch('remote-debugging-port', '9222');
```

Guard behind `WDC_DEBUG=1` env var — never ship enabled in production builds.

Faster than Playwright for simple DOM/eval probes. Use Playwright (`mcp__playwright__*`) only for full interaction flows.

---

## 9. Cron-Driven Self-Renewing Loops

Current loops:
- 3-min: iteration plan refresh (read task list, pick next task, dispatch).
- 5-min: test/review (run test suite on changed files since last tick).
- 90-min: audit (Explore changed files, Plan risk list, code-reviewer top-5).
- Hourly: strict plan audit (re-read `docs/plans/revised-architecture-plan.md`, verify current work still aligns).

### Self-renewing prompt template

Each cron prompt MUST be idempotent and self-contained. Template:

```
You are the orchestrator for NKS WDC autonomous iteration.

STEP 1 — Memory lookup:
- mcp__memory__search_by_tag tags: "nks-ws,phase-7,current-iteration"
- mcp__memory__retrieve_memory query: "NKS WDC current blockers"

STEP 2 — State check:
- git status (verify clean or known dirty)
- TaskList (read pending + in_progress)
- curl http://127.0.0.1:<port>/api/health with bearer token

STEP 3 — Decide ONE action:
- If pending tasks exist and files disjoint: dispatch up to N=3 sub-agents in parallel.
- If in_progress tasks stale (>15 min): check sub-agent liveness, escalate or re-dispatch.
- If no pending tasks: run Explore on git log since last tick, generate new tasks.
- If build broken: stop, escalate to debugger sub-agent, do not dispatch others.

STEP 4 — Execute and report:
- Run the action.
- Store outcome via mcp__memory__store_memory with tags.
- Commit atomically if build passes.

STEP 5 — Schedule next tick:
- This prompt is cron-driven. Do NOT attempt to reschedule yourself.
- Leave task list in consistent state for next tick.
```

### Idempotency rules

- Never start a new sub-agent for a task already `in_progress` (check timestamp; stale = >15 min).
- Never commit if `git diff` shows files not owned by this tick's tasks.
- Never re-run a test suite already green in the last 5 min unless files changed since.

### Stale detection

`in_progress` task without updates for 15 min → mark `blocked`, require main agent intervention.

Sub-agent that never returns (session timeout, crash) → orphaned task detected by cron, reset to `pending`, re-dispatched with fresh context.

---

## 10. Limits & Safeguards

### Max parallel agents

Practical ceiling: **3 sub-agents in parallel** per main agent turn.

Reasons:
- Each sub-agent returns ~5-20 KB of structured report → 3 × 20 KB = 60 KB context cost per parallel batch.
- 4+ agents risks main agent context blowing past 70% before next tick.
- Daemon has limited concurrent request capacity for e2e tests.

### Context budget per sub-agent

Give each sub-agent a prompt ≤8 KB (task + claims + constraints + report format). Expect ≤30 KB in return including tool calls. Budget 40 KB per sub-agent round trip.

Main agent running at 1M context (Opus 1M mode) can handle ~50 sub-agent rounds before needing `/compact`. Plan compact checkpoints between phases.

### Deduplication

Before dispatch, hash the task title + file claims. Check in-memory set of active dispatches. If duplicate, skip.

After dispatch, store (hash, result) in `mcp__memory__store_memory` with tag `dispatch-ledger`. Next tick checks ledger to avoid re-running identical task within 1 hour.

### Cost awareness

Sub-agents via Task tool consume separate model contexts. Budget: ~10-20 sub-agent dispatches per hourly tick. Over that, consolidate more work inline or switch to cheaper models (`haiku` for trivial tasks like file listing, doc formatting).

---

## 11. Phase 7+ Recommended Strategies (not in current plan)

### 11.1 Integration test observability

Before writing 15 e2e scenarios, add a test-observability layer:

- Test harness emits SSE events to `http://127.0.0.1:<port>/api/test-events` (new dev-only endpoint).
- Each test step publishes `{step: "create-site", status: "start/end", duration_ms}`.
- Main agent subscribes and shows real-time progress in task list.
- Failing tests automatically attach last 50 events to task update for debugger handoff.

Benefit: parallel e2e runs become debuggable without re-running.

Dispatch: `full-stack-orchestration:test-automator` + `context-management:context-manager` together.

### 11.2 Perf benchmark regression gate

- Store baseline numbers in `tests/perf/baseline.json` (committed).
- Each perf run compares and fails if regression >10% on any metric.
- Gate wired into 5-min test/review cron loop — blocks commit if regression detected.
- Auto-bisect: dispatch `debugger` against last 10 commits when regression found.

### 11.3 Documentation as tested artifact

Plugin SDK guide and API reference should be generated from code:

- C# XML doc comments → DocFX → `docs/api-reference.md` fragments.
- Daemon OpenAPI (Swashbuckle) → `docs/api-reference.md` via `npx @redocly/cli build-docs`.
- Vue components → `vue-docgen-api` → `docs/frontend-components.md`.

Sub-agent `cicd-automation:deployment-engineer` wires a pre-commit hook that regenerates docs when signatures change. User-written narrative docs live in separate files and are untouched by generation.

### 11.4 Localization framework (i18n)

Phased rollout recommendation:

1. Frontend first (higher impact, faster). Vue-i18n with lazy locale loading. Keys in `kebab-case.dot.notation`.
2. Daemon error messages next. `.resx` per culture. CLI `--culture cs-CZ` flag.
3. Plugin-provided strings. Each plugin ships `plugin.<lang>.json` alongside `plugin.json`. PluginLoader merges into i18n store on load.
4. Locale switcher in settings page. Persists to `%APPDATA%\NKS\WebDevConsole\settings.json`.
5. CI lint: test that every locale has the same key set (`full-stack-orchestration:test-automator` writes this test).

Default locale: `en`. Primary secondary: `cs`.

### 11.5 Cross-agent learning ledger

Every sub-agent failure stores a `learning` memory with tags `nks-ws,lesson,<agent-type>`. Main agent on each tick retrieves `lesson` memories and injects top-5 into next sub-agent prompts as "Known Pitfalls".

Prevents repeated mistakes across iterations (e.g. "PluginLoadContext must share Core/SDK assemblies", "Use IServiceModule not empty ProcessManager", "preload path is `join(__dirname, 'preload.js')`").

---

## 12. Quick Reference — Dispatch Decision Tree

```
user request arrives
 ├─ single file, <5 min, tight loop? → inline
 ├─ multi-file, parallelizable, disjoint? → split + parallel dispatch (max 3)
 ├─ multi-file, sequential deps? → chain dispatch (A → B → C)
 ├─ exploratory? → Explore agent (offload, don't pollute main)
 ├─ architecture/design? → Plan agent
 ├─ bug? → debugger agent (or error-detective for log analysis)
 ├─ tests needed? → full-stack-orchestration:test-automator
 ├─ security concern? → full-stack-orchestration:security-auditor
 ├─ perf issue? → full-stack-orchestration:performance-engineer
 ├─ C# specific? → csharp-pro
 ├─ Vue/TS specific? → typescript-pro or multi-platform-apps:frontend-developer
 ├─ backend API? → backend-api-security:backend-architect
 ├─ CI/CD? → cicd-automation:deployment-engineer
 ├─ review? → superpowers:code-reviewer
 └─ unknown? → general-purpose
```

---

## 13. Checklist Before Every Cron Tick

Main agent MUST verify:

1. `mcp__memory__search_by_tag` returned fresh context.
2. Daemon port file exists and responds to `/api/health`.
3. `git status` shows only expected dirty files.
4. Task list is consistent (no orphaned `in_progress` >15 min).
5. Last commit built cleanly (check `build/` timestamp vs last commit).
6. No uncommitted sub-agent work from previous tick (else: commit or rollback first).
7. Context usage <70% (else: `/compact` or defer dispatch).
8. Dispatch ledger consulted for dedup.

If any check fails, tick aborts and logs reason via `mcp__memory__store_memory` tag `cron-abort`.

---

## Relevant files

- Orchestration target: `C:\work\sources\nks-ws\`
- Architecture plan: `C:\work\sources\nks-ws\docs\plans\revised-architecture-plan.md`
- Daemon source: `C:\work\sources\nks-ws\src\daemon\NKS.WebDevConsole.Daemon\`
- Frontend source: `C:\work\sources\nks-ws\src\frontend\src\`
- Plugins: `C:\work\sources\nks-ws\src\plugins\`
- Tests root (to be expanded in Phase 7): `C:\work\sources\nks-ws\tests\`
- Port file contract: `%TEMP%\nks-wdc\daemon.port`
- Agent isolated daemon dir: `%TEMP%\nks-wdc-agent-<id>\`
