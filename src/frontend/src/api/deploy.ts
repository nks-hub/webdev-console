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
  options?: { idempotencyKey?: string; backendOptions?: Record<string, unknown> },
): Promise<StartDeployResponseDto> {
  return request<StartDeployResponseDto>(
    `${PREFIX}/sites/${encodeURIComponent(domain)}/hosts/${encodeURIComponent(host)}/deploy`,
    {
      method: 'POST',
      body: JSON.stringify({
        idempotencyKey: options?.idempotencyKey ?? crypto.randomUUID(),
        options: options?.backendOptions ?? {},
      }),
    },
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
}

export interface DeployHookConfig {
  event: 'pre_deploy' | 'post_fetch' | 'pre_switch' | 'post_switch' | 'on_failure' | 'on_rollback'
  type: 'shell' | 'http' | 'php'
  command: string
  timeoutSeconds: number
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
