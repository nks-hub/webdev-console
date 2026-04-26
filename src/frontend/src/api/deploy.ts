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
