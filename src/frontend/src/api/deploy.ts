/**
 * Typed REST client for the NksDeploy plugin endpoints (registered under
 * /api/nks.wdc.deploy/ via the plugin's RegisterEndpoints override). Bearer
 * auth is shared with the rest of the daemon via daemonAuthHeaders().
 *
 * Schemas mirror the SDK DTOs (Plugin.SDK/Deploy/DeployDtos.cs) so any
 * round-trip drift is caught at compile time on the frontend side.
 */
import { daemonBaseUrl, daemonAuthHeaders } from './daemon'

export type DeployPhase =
  | 'Queued'
  | 'PreflightChecks'
  | 'Fetching'
  | 'Building'
  | 'Migrating'
  | 'AboutToSwitch'
  | 'Switched'
  | 'HealthCheck'
  | 'AwaitingSoak'
  | 'Done'
  | 'Failed'
  | 'Cancelled'
  | 'RollingBack'
  | 'RolledBack'

/** One progress event broadcast over SSE under event-type "deploy:event". */
export interface DeployEventDto {
  deployId: string
  phase: DeployPhase
  step: string
  message: string
  timestamp: string
  isTerminal: boolean
  isPastPonr: boolean
}

/** Snapshot of a deploy's current or final state (GET status response). */
export interface DeployResultDto {
  deployId: string
  success: boolean
  errorMessage: string | null
  startedAt: string
  completedAt: string | null
  releaseId: string | null
  commitSha: string | null
  finalPhase: DeployPhase
  /** Phase 6.19a — set when this run is part of a multi-host group. */
  groupId?: string | null
}

/** History entry returned by GET /sites/{domain}/history. */
export interface DeployHistoryEntryDto {
  deployId: string
  domain: string
  host: string
  branch: string
  finalPhase: DeployPhase
  startedAt: string
  completedAt: string | null
  commitSha: string | null
  releaseId: string | null
  error: string | null
  // Phase 7.5+++ — surface the trigger source ('gui' | 'mcp' | 'cli' | …).
  // Optional because older daemons / older history rows may not include it.
  triggeredBy?: string
}

/** Response envelope from POST .../deploy. deployId may be null in the 1s
 *  grace window — caller subscribes to SSE to receive the real id. */
export interface StartDeployResponseDto {
  deployId: string | null
  idempotencyKey: string
  note?: string
}

const PREFIX = '/api/nks.wdc.deploy'

async function request<T>(path: string, init: RequestInit = {}): Promise<T> {
  const headers: Record<string, string> = {
    ...(daemonAuthHeaders() as Record<string, string>),
    ...(init.body ? { 'Content-Type': 'application/json' } : {}),
    ...((init.headers as Record<string, string>) ?? {}),
  }
  const r = await fetch(`${daemonBaseUrl()}${path}`, { ...init, headers })
  if (!r.ok) {
    let detail: unknown = null
    try { detail = await r.json() } catch { /* swallow */ }
    throw new Error(`HTTP ${r.status} ${r.statusText} ${detail ? JSON.stringify(detail) : ''}`)
  }
  // 202 may have an empty body for some daemon builds — guard the parse
  const text = await r.text()
  return (text ? JSON.parse(text) : {}) as T
}

export function startDeploy(
  domain: string,
  host: string,
  options?: {
    idempotencyKey?: string
    backendOptions?: Record<string, unknown>
    /** Phase 6.12c — Pre-deploy DB snapshot config (DeploySnapshotOptions). */
    snapshot?: { include: boolean; retentionDays?: number }
    /** Phase 7.5+++ — explicit local-loopback paths override settings. */
    localPaths?: { source: string; target: string }
    /** Phase 7.5+++ — specific branch override (defaults to host setting). */
    branch?: string
  },
): Promise<StartDeployResponseDto> {
  // Daemon endpoint is /sites/{domain}/deploy — host travels in the body.
  // The 6.x-era /sites/{domain}/hosts/{host}/deploy URL was removed when
  // the real LocalDeployBackend landed; calling that 404'd silently.
  const body: Record<string, unknown> = {
    host,
    idempotencyKey: options?.idempotencyKey ?? crypto.randomUUID(),
    options: options?.backendOptions ?? {},
  }
  if (options?.snapshot !== undefined) body.snapshot = options.snapshot
  if (options?.branch) body.branch = options.branch
  if (options?.localPaths) body.localPaths = options.localPaths
  return request<StartDeployResponseDto>(
    `${PREFIX}/sites/${encodeURIComponent(domain)}/deploy`,
    { method: 'POST', body: JSON.stringify(body) },
  )
}

export function getDeployStatus(domain: string, deployId: string): Promise<DeployResultDto> {
  return request<DeployResultDto>(
    `${PREFIX}/sites/${encodeURIComponent(domain)}/deploys/${encodeURIComponent(deployId)}`,
  )
}

export function getDeployHistory(domain: string, limit = 50): Promise<{ domain: string; count: number; entries: DeployHistoryEntryDto[] }> {
  return request(
    `${PREFIX}/sites/${encodeURIComponent(domain)}/history?limit=${limit}`,
  )
}

export function rollbackDeploy(domain: string, deployId: string): Promise<{ sourceDeployId: string; status: string }> {
  return request(
    `${PREFIX}/sites/${encodeURIComponent(domain)}/deploys/${encodeURIComponent(deployId)}/rollback`,
    { method: 'POST' },
  )
}

/**
 * Phase 7.5+++ — rollback to an arbitrary historical release. Used by the
 * Releases sub-tab "Roll back to this" action when N-1 isn't enough (e.g.
 * the previous release was also broken). Daemon validates that
 * releases/{releaseId} exists before swapping the symlink.
 */
export function rollbackToRelease(
  domain: string,
  host: string,
  releaseId: string,
): Promise<{ status: string; host: string; releaseId: string; swappedTo: string | null; error: string | null }> {
  return request(
    `${PREFIX}/sites/${encodeURIComponent(domain)}/rollback-to`,
    { method: 'POST', body: JSON.stringify({ host, releaseId }) },
  )
}

export function cancelDeploy(domain: string, deployId: string): Promise<{ deployId: string; status: string }> {
  return request(
    `${PREFIX}/sites/${encodeURIComponent(domain)}/deploys/${encodeURIComponent(deployId)}`,
    { method: 'DELETE' },
  )
}

// ─── Deploy Settings types ────────────────────────────────────────────────────

export interface DeployHostConfig {
  name: string
  sshHost: string
  sshUser: string
  sshPort: number
  remotePath: string
  branch: string
  phpBinaryPath?: string
  composerInstall: boolean
  runMigrations: boolean
  soakSeconds: number
  healthCheckUrl?: string
  // Phase 7.5+++ — local-loopback backend paths. When set, the GUI
  // deploy button doesn't have to send `localPaths` in the body — the
  // daemon falls back to these stored values for that host.
  localSourcePath?: string
  localTargetPath?: string
  // Phase 7.5+++ nksdeploy compat — shared dirs/files symlinked from
  // shared/ into each release. Defaults (log/, temp/) apply when empty.
  sharedDirs?: string[]
  sharedFiles?: string[]
}

export interface DeployHookConfig {
  event: 'pre_deploy' | 'post_fetch' | 'pre_switch' | 'post_switch' | 'on_failure' | 'on_rollback'
  type: 'shell' | 'http' | 'php'
  command: string
  timeoutSeconds: number
  // Phase 7.5+++ — per-hook on/off toggle. Optional + treated as TRUE
  // when undefined so existing configs stay enabled without migration.
  enabled?: boolean
  // Phase 7.5+++ — free-form name for the hook ("Notify Slack on prod
  // deploy"). Optional; row falls back to the command/URL when empty.
  description?: string
}

export interface DeploySnapshotConfig {
  enabled: boolean
  retentionDays: number
}

export interface DeployNotificationsConfig {
  slackWebhook?: string
  emailRecipients: string[]
  notifyOn: string[]
}

export interface DeployAdvancedConfig {
  keepReleases: number
  lockTimeoutSeconds: number
  allowConcurrentHosts: boolean
  envVars: Record<string, string>
}

export interface DeploySettings {
  hosts: DeployHostConfig[]
  snapshot: DeploySnapshotConfig
  hooks: DeployHookConfig[]
  notifications: DeployNotificationsConfig
  advanced: DeployAdvancedConfig
}

export interface DeploySnapshotEntry {
  id: string
  createdAt: string
  sizeBytes: number
  path: string
}

export function defaultDeploySettings(): DeploySettings {
  return {
    hosts: [],
    snapshot: { enabled: false, retentionDays: 30 },
    hooks: [],
    notifications: { slackWebhook: undefined, emailRecipients: [], notifyOn: ['success', 'failure'] },
    advanced: {
      keepReleases: 5,
      lockTimeoutSeconds: 600,
      allowConcurrentHosts: true,
      envVars: {},
    },
  }
}

/**
 * GET /api/nks.wdc.deploy/sites/{domain}/settings
 * Returns 404 until Phase 6.3 backend lands — falls back to defaults so the
 * UI is always renderable.
 */
export async function fetchDeploySettings(domain: string): Promise<DeploySettings> {
  try {
    return await request<DeploySettings>(
      `${PREFIX}/sites/${encodeURIComponent(domain)}/settings`,
    )
  } catch {
    return defaultDeploySettings()
  }
}

/**
 * PUT /api/nks.wdc.deploy/sites/{domain}/settings
 * Will 404 until Phase 6.3. Caller checks the thrown error and surfaces a
 * "persistence pending" toast — store catches and re-throws with a friendly
 * message.
 */
export async function saveDeploySettings(domain: string, settings: DeploySettings): Promise<void> {
  await request<void>(
    `${PREFIX}/sites/${encodeURIComponent(domain)}/settings`,
    { method: 'PUT', body: JSON.stringify(settings) },
  )
}

/**
 * GET /api/nks.wdc.deploy/sites/{domain}/snapshots
 * Returns an empty list when the endpoint does not exist yet.
 */
export async function fetchDeploySnapshots(domain: string): Promise<DeploySnapshotEntry[]> {
  try {
    return await request<DeploySnapshotEntry[]>(
      `${PREFIX}/sites/${encodeURIComponent(domain)}/snapshots`,
    )
  } catch {
    return []
  }
}

/**
 * Phase 6.4 — request an MCP intent token from the daemon. Used by the
 * restore flow because the daemon's restore endpoint requires intent
 * gating (kind=restore + host=*restore*) even when called from the GUI.
 *
 * The GUI is trusted (bearer-auth on /api/*), so we mint an intent
 * with `confirm-request` + auto-approve via the GUI confirm modal flow.
 * Returns the raw token string for the X-Intent-Token header.
 */
export interface IntentTokenResponse {
  intentId: string
  intentToken: string
  expiresAt: string
}

export async function createDeployIntent(
  domain: string,
  host: string,
  kind: 'deploy' | 'rollback' | 'cancel' | 'restore',
): Promise<IntentTokenResponse> {
  return request<IntentTokenResponse>('/api/mcp/intents', {
    method: 'POST',
    body: JSON.stringify({ domain, host, kind, expiresIn: 120 }),
  })
}

/**
 * Stamp confirmed_at on an intent so destructive endpoints accept it
 * without the X-Allow-Unconfirmed header. The GUI is the trusted
 * confirmation surface — we approve immediately after minting.
 */
export async function confirmDeployIntent(intentId: string): Promise<void> {
  await request<void>(`/api/mcp/intents/${encodeURIComponent(intentId)}/confirm`, {
    method: 'POST',
  })
}

export interface RestoreSnapshotResult {
  // Legacy fields kept for backward compat with older daemons.
  deployId?: string
  domain?: string
  mode?: 'sqlite' | 'mysql' | 'pgsql'
  bytesProcessed?: number
  durationMs?: number
  // Phase 7.5+++ — real local-loopback restore response.
  // restored=true means the .zip was extracted + current symlink swapped.
  // extractedTo / swappedTo are the same Windows release dir path on success.
  // error is non-null when extraction or symlink swap failed (e.g. permission).
  restored?: boolean
  sourceDeployId?: string
  backupPath?: string
  backupSizeBytes?: number
  extractedTo?: string | null
  swappedTo?: string | null
  error?: string | null
}

/**
 * Phase 6.6 — on-demand DB snapshot WITHOUT a deploy. Useful before
 * manual schema migrations or ad-hoc DB ops. Creates a synthetic
 * deploy_runs row tagged backend_id=manual-snapshot so the result
 * shows up in the per-site snapshot inventory.
 */
export interface SnapshotNowResult {
  snapshotId: string
  domain: string
  path: string
  sizeBytes: number
  durationMs: number
}

export async function snapshotNow(domain: string): Promise<SnapshotNowResult> {
  return request<SnapshotNowResult>(
    `${PREFIX}/sites/${encodeURIComponent(domain)}/snapshot-now`,
    { method: 'POST' },
  )
}

/**
 * Phase 6.7 — DeployGroup history. Lists multi-host deploy fan-outs for
 * the site, newest first.
 */
export type DeployGroupPhase =
  | 'initializing'
  | 'preflight'
  | 'deploying'
  | 'awaiting_all_soak'
  | 'all_succeeded'
  | 'partial_failure'
  | 'rolling_back_all'
  | 'rolled_back'
  | 'group_failed'

export interface DeployGroupEntry {
  id: string
  domain: string
  hosts: string[]
  hostDeployIds: Record<string, string>
  /**
   * Phase 6.15b — per-host terminal status from deploy_runs.status.
   * Keyed by host name. Empty/missing keys = pre-fan-out or status not
   * recorded. Used for "replay only failed hosts" subset offer.
   */
  hostStatuses?: Record<string, string>
  phase: DeployGroupPhase
  startedAt: string
  completedAt: string | null
  errorMessage: string | null
  triggeredBy: 'gui' | 'mcp' | 'cli'
}

export interface DeployGroupHistoryResponse {
  domain: string
  count: number
  entries: DeployGroupEntry[]
}

export async function fetchDeployGroups(
  domain: string,
  limit = 50,
): Promise<DeployGroupHistoryResponse> {
  try {
    return await request<DeployGroupHistoryResponse>(
      `${PREFIX}/sites/${encodeURIComponent(domain)}/groups?limit=${limit}`,
    )
  } catch {
    return { domain, count: 0, entries: [] }
  }
}

export interface StartGroupResult {
  groupId: string
  idempotencyKey: string
  hostCount: number
}

export interface RollbackGroupResult {
  groupId: string
  status: string
}

/**
 * Phase 6.13a — operator-driven group cascade rollback. Posts to the
 * plugin's POST /sites/{domain}/groups/{groupId}/rollback endpoint.
 * Like single-host rollback, GUI calls don't need an intent token —
 * the bearer-auth + GUI confirmation modal are sufficient gates.
 */
export async function rollbackDeployGroup(
  domain: string,
  groupId: string,
): Promise<RollbackGroupResult> {
  return request<RollbackGroupResult>(
    `${PREFIX}/sites/${encodeURIComponent(domain)}/groups/${encodeURIComponent(groupId)}/rollback`,
    { method: 'POST', body: '{}' },
  )
}

/**
 * Phase 6.10 — kick off a multi-host atomic deploy group from the GUI.
 * Posts to the plugin's POST /sites/{domain}/groups endpoint. Intent
 * gating is bypassed for GUI-originated requests (the bearer-auth on
 * /api/* + the trusted GUI surface are sufficient — the AI/MCP path
 * is what needs the X-Intent-Token gate).
 */
export async function startDeployGroup(
  domain: string,
  hosts: string[],
  options?: Record<string, unknown>,
): Promise<StartGroupResult> {
  return request<StartGroupResult>(
    `${PREFIX}/sites/${encodeURIComponent(domain)}/groups`,
    {
      method: 'POST',
      body: JSON.stringify({ hosts, options: options ?? {} }),
    },
  )
}

/**
 * Phase 7.5+++ — POST /api/nks.wdc.deploy/test-host-connection.
 * Pure TCP probe (5s timeout) to verify the SSH host is reachable
 * from the daemon's network position. Used by DeploySettingsPanel's
 * host edit dialog so operators get fast feedback before saving a
 * config that turns out to be unroutable. Always returns 200; the
 * `ok` flag in the body indicates success/failure.
 */
export interface TestHostConnectionResult {
  ok: boolean
  latencyMs?: number
  error?: string
  /** Stable enum: "timeout" | "socket_error" | "unexpected" */
  code?: string
}

/**
 * Phase 7.5+++ — fire one configured hook against the daemon's test
 * endpoint without doing a full deploy. Returns the same shape as the
 * `deploy:hook` SSE event so the GUI can show a consistent toast.
 */
export interface TestHookResult {
  ok: boolean
  durationMs: number
  error?: string | null
  workingDir?: string
}

export async function testHook(
  domain: string,
  hook: { type: string; command: string; timeoutSeconds?: number; description?: string },
): Promise<TestHookResult> {
  return request<TestHookResult>(
    `${PREFIX}/sites/${encodeURIComponent(domain)}/hooks/test`,
    { method: 'POST', body: JSON.stringify(hook) },
  )
}

/**
 * Phase 7.5+++ — fire one Slack test message via the daemon. Body's
 * slackWebhook overrides settings if present. Returns {ok, durationMs, error?}.
 */
export interface TestNotificationResult {
  ok: boolean
  durationMs: number
  error?: string | null
}

export async function testNotification(
  domain: string,
  body: { slackWebhook?: string; host?: string } = {},
): Promise<TestNotificationResult> {
  return request<TestNotificationResult>(
    `${PREFIX}/sites/${encodeURIComponent(domain)}/notifications/test`,
    { method: 'POST', body: JSON.stringify(body) },
  )
}

/**
 * Phase 7.5+++ — dry-run preview of a deploy. Returns the resolved
 * plan (release id, hooks that would fire, retention impact, etc.)
 * WITHOUT executing anything. No DB write, no SSE, no copy.
 */
export interface DryRunDeployResult {
  dryRun: true
  deployId: null
  wouldRelease: string
  wouldExtractTo: string
  wouldCopyFrom: string | null
  wouldSwapCurrentFrom: string | null
  currentRelease: string | null
  sourceLastModified: string | null
  branch: string | null
  sharedDirs: string[]
  sharedFiles: string[]
  keepReleases: number
  existingReleaseCount: number
  wouldPruneCount: number
  hooksWillFire: Record<string, number>
  totalHooksEnabled: number
  healthCheckUrl: string | null
  soakSeconds: number
  slackEnabled: boolean
}

export async function dryRunDeploy(
  domain: string,
  host: string,
  options?: { branch?: string; localPaths?: { source: string; target: string } },
): Promise<DryRunDeployResult> {
  const body: Record<string, unknown> = { host, dryRun: true }
  if (options?.branch) body.branch = options.branch
  if (options?.localPaths) body.localPaths = options.localPaths
  return request<DryRunDeployResult>(
    `${PREFIX}/sites/${encodeURIComponent(domain)}/deploy`,
    { method: 'POST', body: JSON.stringify(body) },
  )
}

export async function testHostConnection(
  host: string,
  port: number,
): Promise<TestHostConnectionResult> {
  return request<TestHostConnectionResult>(
    `${PREFIX}/test-host-connection`,
    {
      method: 'POST',
      body: JSON.stringify({ host, port }),
    },
  )
}

/**
 * POST /api/nks.wdc.deploy/sites/{domain}/snapshots/{deployId}/restore.
 * Caller must mint+confirm an intent first; this helper attaches the
 * X-Intent-Token header. Body always includes `confirm: true` per the
 * daemon's two-gate destructive contract.
 */
export async function restoreSnapshot(
  domain: string,
  deployId: string,
  intentToken: string,
): Promise<RestoreSnapshotResult> {
  return request<RestoreSnapshotResult>(
    `${PREFIX}/sites/${encodeURIComponent(domain)}/snapshots/${encodeURIComponent(deployId)}/restore`,
    {
      method: 'POST',
      headers: { 'X-Intent-Token': intentToken },
      body: JSON.stringify({ confirm: true }),
    },
  )
}
