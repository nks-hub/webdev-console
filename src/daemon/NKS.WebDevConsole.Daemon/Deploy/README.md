# Deploy Lifecycle (Phase 7.5+++)

Operator reference for the local-loopback deploy backend in NKS WebDevConsole.
Mirrors the [nksdeploy](https://github.com/nks-hub/nksdeploy) folder convention
without requiring SSH or the PHAR — every operation runs against a configured
local target directory.

## Folder layout

After the first deploy, the target directory looks like:

```
{localTargetPath}/
├── releases/
│   ├── 20260427_010500/   ← timestamped release
│   ├── 20260427_011200/
│   └── 20260427_011900/
├── current → releases/20260427_011900/   (symlink, atomic swap)
├── shared/
│   ├── log/                (configurable per host.sharedDirs)
│   ├── temp/
│   └── .env                (configurable per host.sharedFiles)
└── .dep/
    ├── current_release    (path of active release)
    └── previous_release   (path of prior current — for rollback)
```

## Settings

Edit per-host config under **Sites → {domain} → Deploy → Settings → Hosts**:

| Field | Purpose |
|-------|---------|
| `localSourcePath` | Directory copied into each new release |
| `localTargetPath` | Where `releases/`, `current`, `shared/` live |
| `sharedDirs` | Symlinked from `shared/{name}` into each release |
| `sharedFiles` | Same, for files (e.g. `.env`) |
| `healthCheckUrl` | HTTP GET probed during the AwaitingSoak phase |
| `soakSeconds` | Soak window — health probe retries until 2xx or this expires |

Site-wide settings (Advanced tab):

| Field | Purpose |
|-------|---------|
| `keepReleases` | Retention — older release dirs are pruned |
| `lockTimeoutSeconds` | Stale lock cleanup threshold |
| `allowConcurrentHosts` | Group deploys: parallel (true) or sequential (false) |
| `envVars` | Key/value pairs merged into shell + php hook process env |

## Deploy flow

```
deploy POST → resolve settings + body localPaths
          ↓
     [Building]   validate source + create target tree
          ↓
   [pre_deploy hooks]  ← run shell/http/php in declared order
          ↓
     [Fetching]   recursive copy source → releases/{releaseId}/
          ↓
   [post_fetch hooks]
          ↓
     [Building]   apply shared symlinks (log, temp, .env, …)
          ↓
   [pre_switch hooks]  ← LAST cancel checkpoint
          ↓
     [Switching]  atomic symlink swap current → releases/{releaseId}
          ↓
   [post_switch hooks]
          ↓
   [AwaitingSoak] probe healthCheckUrl (when configured) for soakSeconds
          ↓
     [Building]   prune older releases per keepReleases
          ↓
       [Done]
```

Each phase fires a `deploy:phase` SSE event. Each hook fires a `deploy:hook`
SSE event with `{evt, type, label, ok, durationMs, error?}`. The deploy
drawer subscribes to both.

## Snapshot / restore

* **`snapshot:true` in deploy body** → ZIP of `target/current/` written to
  `~/.wdc/backups/pre-deploy/{domain}/{deployId}.zip` BEFORE the deploy
  starts (best-effort; failure logs `.zip.failed` placeholder).
* **`POST /sites/{domain}/snapshot-now`** → manual ZIP of `target/current/`
  to `~/.wdc/backups/manual/{domain}/{snapshotId}.zip`. Returns the path
  + size; respects `snapshot.retentionDays`.
* **`POST /sites/{domain}/snapshots/{snapshotId}/restore`** → extract the
  ZIP into a fresh `releases/{ts}-restored-{shortId}/` and atomic-swap
  `current` to it. Audit row inserted with `BackendId="local-restore"`.

Retention pruning runs at the moment a new snapshot is created (no
separate scheduler needed): files older than `snapshot.retentionDays`
in the same subfolder are deleted.

## Rollback

Two flavors, both back the symlink up by reading `.dep/previous_release`:

* `POST /sites/{domain}/deploys/{deployId}/rollback` — go back to N-1
* `POST /sites/{domain}/rollback-to` body `{host, releaseId}` — go back
  to a specific historical release (Releases sub-tab → "Roll back to this")

Group rollback (`POST /groups/{groupId}/rollback`) cascades the same
swap per host with localPaths configured; hosts without localPaths fall
back to a DB-only flip and show up in `noopHosts[]` of the response.

## MCP intent gates

Destructive endpoints accept an optional `X-Intent-Token` header that the
validator checks against `mcp_session_grants` (kind/scope match):

| Endpoint | Intent kind |
|----------|-------------|
| `POST /sites/{domain}/deploy` | `deploy` |
| `POST /groups` | `deploy` |
| `POST /deploys/{id}/rollback` | `rollback` |
| `POST /rollback-to` | `rollback` |
| `POST /restore` | `restore` |

Without a token: endpoint is open (back-compat). With a bogus token:
403 `intent_rejected`. Persistent grants (session / instance / api_key
/ always) auto-approve matching intents — the McpGrants page manages
them. Header `X-Allow-Unconfirmed: true` bypasses the operator
confirmation gate (CI flows).

## Cancel

`DELETE /sites/{domain}/deploys/{deployId}` trips a `CancellationToken`
inside the running backend task. Checkpoints at: after `running` status,
after each hook phase. Last checkpoint is BEFORE the symlink swap (after
that the deploy is committed; use rollback instead).

Response includes `interrupted: true` when the backend task was
actually interrupted, `false` when the row was already terminal (race
or never-existed).

## Notifications

Slack webhook URL configured in **Settings → Notifications → Slack**
fires on `deploy:complete` for outcomes listed in `notifyOn`
(default: success + failure). Test via **Test Slack** button — direct
POST that surfaces transport errors instead of swallowing them.

Email channel is configure-only at the moment (needs SMTP server config
— planned for a future iteration).

## Troubleshooting

* **Deploy stuck at Queued** → daemon hasn't picked it up; check
  `~/.wdc/logs/daemon/daemon-*.log` for exceptions
* **Symlink fails on Windows** → backend falls back to recursive copy.
  Enable Developer Mode or run elevated for symlinks.
* **Cancel doesn't interrupt** → response includes `"interrupted":false`
  when the backend already finished racing the cancel
* **Hook writes to current/log goes to wrong release** → `log` directory
  in the release IS a symlink to `shared/log` — that's by design (logs
  survive across deploys)

## SSE events at a glance

```
event: deploy:event       — phase transitions (phase, step, message, isPastPonr, isTerminal)
event: deploy:hook        — per-hook fire (deployId, evt, type, label, ok, durationMs, error?)
event: deploy:phase       — coarse phase update (deployId, phase, message)
event: deploy:complete    — terminal (deployId, success, durationMs?, error?, releaseDir?)
event: deploy:group-started   — group fan-out begins
event: deploy:group-complete  — group fan-out terminal
event: restore:complete   — restore endpoint terminal
event: mcp:confirm-request — operator approval needed before destructive op proceeds
```

EventSource `/api/events?token=<bearer>`. The daemon multiplex-broadcasts
all events to every subscriber.

## Plugin cutover (Phase D)

`deploy.useLegacyHostHandlers` is a startup-time toggle that swaps
authority for a subset of `/api/nks.wdc.deploy/*` routes from the
daemon's built-in `LocalDeployBackend` to the `nks.wdc.deploy` plugin
(loaded from `webdev-console-plugins/NksDeploy`).

**Toggle semantics:** restart-required. The daemon reads the value at
boot into `legacyHostHandlersAtBoot`. Runtime flip only takes effect on
the next daemon start. The readiness diagnostic surfaces drift via
`bootLegacyHostHandlers` + `restartPending` so the GUI can show
"restart to apply" hint.

**Gated endpoints** (currently 11 — see `gatedEndpoints[]` in
`/api/admin/plugin-readiness?explain=true` for the live list):

| Verb | Route | Plugin equivalent |
|------|-------|-------------------|
| POST | `/test-host-connection` | TCP probe utility |
| POST | `/sites/{domain}/hooks/test` | Direct C# `TestHookAsync` |
| POST | `/sites/{domain}/notifications/test` | Slack webhook smoke test |
| POST | `/sites/{domain}/snapshot-now` | FS-ZIP first, DB snapshotter fallback |
| POST | `/sites/{domain}/restore` | ZipFile.ExtractToDirectory + symlink swap |
| POST | `/sites/{domain}/snapshots/{snapshotId}/restore` | (alias of above) |
| GET  | `/sites/{domain}/history` | Pure read of shared `IDeployRunsRepository` |
| GET  | `/sites/{domain}/deploys/{deployId}` | Pure read |
| GET  | `/sites/{domain}/snapshots` | Pure read |
| GET  | `/sites/{domain}/settings` | Pure read of per-site JSON file |
| GET  | `/sites/{domain}/groups` | Pure read |

**Endpoints NOT gated** (still daemon-authoritative even in plugin
mode): real `deploy`, `rollback`, `rollback-to`, `groups` POST, `cancel`
(DELETE), `settings` PUT. These either depend on `nksdeploy.phar` (which
the plugin shells to via CliWrap and isn't built in the dev environment)
or share state (intent validator, in-flight deploy registry) that
hasn't been lifted into the plugin SDK.

### Validating the cutover

```bash
bash tools/validate-cutover.sh
```

Self-isolating: captures original setting, flips to `legacy=false`,
restarts daemon, asserts plugin handler authoritative (response shape
diff: `workingDir` field present in daemon mode, absent in plugin mode),
restores original setting via trap on exit. ~30s runtime with 2× daemon
restarts.

### Diagnostic envelope

`GET /api/admin/plugin-readiness?explain=true` exposes:
- `mode`: `built-in` | `plugin` (live setting view)
- `useLegacyHostHandlers`: current setting
- `bootLegacyHostHandlers`: value baked at daemon boot
- `restartPending`: `current != boot` (drift signal)
- `gatedEndpoints[]`: list of routes wrapped in
  `if (legacyHostHandlersAtBoot)` blocks
- `blockers[]` + `blockerDetails[]`: phase D2/D3/E reasons readyToFlip
  is still false today
- `recommendation`: human-readable status text (cs/en localized in
  popovers, English on the wire)
