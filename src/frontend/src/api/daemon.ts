import type {
  StatusResponse,
  SiteInfo,
  PhpVersion,
  DatabaseInfo,
  CertInfo,
  PluginManifest,
  ProgressUpdate,
  MetricsUpdate,
  LogEntry,
  BinaryRelease,
  InstalledBinary,
  SiteErrorLogEntry,
  HistoricalMetrics,
  ComposerStatus,
  ComposerCommandResult,
  AccessLogEntry,
  ServiceInfo,
} from './types'

// Window.daemonApi is declared in src/env.d.ts alongside the other
// preload surfaces (electronAPI, __APP_VERSION__). Moved there so files
// that don't import from this module still see the global type.

// Exported so pages hitting ad-hoc daemon endpoints via raw fetch() (10+
// pages used to ship their own slightly-different copy of this) share
// the exact same port/token resolution logic as the typed API surface.
export function daemonBaseUrl(): string {
  // Priority: (1) preload getPort() because it RE-READS the port file on every
  // call and survives daemon restarts (new token/port are picked up live),
  // (2) URL query param as fallback for browser/dev mode where preload doesn't exist.
  // Preload returns 0 if the file doesn't exist yet — treat 0 as "no value".
  const preloadPort = window.daemonApi?.getPort?.()
  if (typeof preloadPort === 'number' && preloadPort > 0) {
    return `http://localhost:${preloadPort}`
  }
  const urlPort = new URLSearchParams(window.location.search).get('port')
  if (urlPort && /^\d+$/.test(urlPort)) {
    return `http://localhost:${parseInt(urlPort, 10)}`
  }
  return 'http://localhost:5199'
}

// Exported alongside daemonBaseUrl so pages can build their own raw
// fetch() calls without re-implementing the token resolution. Each page
// previously shipped its own version — some forgot to include
// Content-Type, some forgot the Bearer token prefix, some used
// `getToken()` without optional-chaining.
export function daemonAuthHeaders(extra?: HeadersInit): Record<string, string> {
  // Prefer preload token (live-refreshed from port file), fallback to URL query.
  const preloadToken = window.daemonApi?.getToken?.() || ''
  const urlToken = new URLSearchParams(window.location.search).get('token') || ''
  const token = preloadToken || urlToken
  const headers: Record<string, string> = { 'Content-Type': 'application/json' }
  if (token) headers['Authorization'] = `Bearer ${token}`
  if (extra) {
    // Normalise HeadersInit shapes to a [string, string][] list. The
    // Headers DOM iterator is typed as Iterator<[string, string]> in
    // lib.dom.d.ts but TypeScript's NoImplicitAny on `entries()` over
    // the iterator confuses tsc without an explicit cast.
    const entries: [string, string][] = extra instanceof Headers
      ? Array.from(extra as unknown as Iterable<[string, string]>)
      : Array.isArray(extra)
        ? extra as [string, string][]
        : Object.entries(extra) as [string, string][]
    for (const [k, v] of entries) headers[k] = v
  }
  return headers
}

/**
 * Raw token lookup — preload value first, then URL query fallback.
 * Exported for call-sites that need the token as a URL query parameter
 * (EventSource doesn't accept Authorization headers, image <img> src
 * needs ?token=… in the URL). For fetch() calls prefer
 * daemonAuthHeaders which builds the Authorization header directly.
 */
export function daemonToken(): string {
  return (
    window.daemonApi?.getToken?.()
    || new URLSearchParams(window.location.search).get('token')
    || ''
  )
}

async function json<T>(path: string, init?: RequestInit): Promise<T> {
  const r = await fetch(`${daemonBaseUrl()}${path}`, {
    ...init,
    headers: daemonAuthHeaders(init?.headers),
  })
  if (!r.ok) {
    // Extract the real error from the response body when available.
    // Daemon endpoints consistently return { error: "..." } or plain JSON
    // with a message/detail field — try all common shapes before falling
    // back to the generic HTTP status. Without this, any 400/500 from the
    // daemon renders as "HTTP 400 Bad Request" in the UI with zero context.
    let detail = ''
    try {
      const txt = await r.text()
      if (txt) {
        try {
          const body = JSON.parse(txt)
          detail = body?.error ?? body?.message ?? body?.detail ?? body?.title ?? ''
          if (!detail && typeof body === 'string') detail = body
          if (!detail) detail = txt.length < 300 ? txt : ''
        } catch {
          // non-JSON plain-text body
          if (txt.length < 300) detail = txt
        }
      }
    } catch { /* couldn't read body — fall through */ }
    const prefix = `HTTP ${r.status} ${r.statusText}`
    throw new Error(detail ? `${prefix}: ${detail}` : prefix)
  }
  return r.json() as Promise<T>
}

// Status
export const fetchStatus = (): Promise<StatusResponse> =>
  json('/api/status')

// System info (os tag, arch tag, daemon version, counts, catalog status)
export interface SystemInfo {
  daemon: { version: string; uptime: number; pid: number }
  services: { running: number; total: number }
  sites: number
  plugins: number
  binaries: number
  os: {
    platform: string
    version: string
    machine: string
    tag: 'windows' | 'linux' | 'macos' | 'unknown'
    arch: 'x64' | 'x86' | 'arm64' | 'arm' | 'unknown'
  }
  runtime: { dotnet: string; arch: string }
  /** Catalog health snapshot — populated after CatalogClient.RefreshAsync. */
  catalog: {
    url: string
    cachedCount: number
    lastFetch: string | null
    reachable: boolean
  }
}
export const fetchSystem = (): Promise<SystemInfo> => json('/api/system')

// Services
export const fetchServices = (): Promise<ServiceInfo[]> =>
  json('/api/services')

export const startService = (id: string) =>
  json<{ ok: boolean; message: string; pid?: number }>(`/api/services/${id}/start`, { method: 'POST' })

export const stopService = (id: string) =>
  json<{ ok: boolean; message: string }>(`/api/services/${id}/stop`, { method: 'POST' })

export const restartService = (id: string) =>
  json<{ ok: boolean }>(`/api/services/${id}/restart`, { method: 'POST' })

// Sites
export const fetchSites = (): Promise<SiteInfo[]> =>
  json('/api/sites')

export const createSite = (data: Partial<SiteInfo>) =>
  json<SiteInfo>('/api/sites', { method: 'POST', body: JSON.stringify(data) })

export const deleteSite = (id: string) =>
  json<void>(`/api/sites/${id}`, { method: 'DELETE' })

export const updateSite = (id: string, data: Partial<SiteInfo>) =>
  json<SiteInfo>(`/api/sites/${id}`, { method: 'PUT', body: JSON.stringify(data) })

// Docker Compose detection — returns whether the site's document root
// contains a compose file. Phase 11 foothold (commit 2a92687).
export interface DockerComposeStatus {
  hasCompose: boolean
  composeFile: string | null
  fileName: string | null
}
export const fetchDockerComposeStatus = (domain: string): Promise<DockerComposeStatus> =>
  json(`/api/sites/${encodeURIComponent(domain)}/docker-compose`)

export const composeUp = (domain: string) =>
  json<{ ok: boolean; output: string }>(`/api/sites/${encodeURIComponent(domain)}/docker-compose/up`, { method: 'POST' })

export const composeDown = (domain: string) =>
  json<{ ok: boolean; output: string }>(`/api/sites/${encodeURIComponent(domain)}/docker-compose/down`, { method: 'POST' })

export const composeRestart = (domain: string) =>
  json<{ ok: boolean; output: string }>(`/api/sites/${encodeURIComponent(domain)}/docker-compose/restart`, { method: 'POST' })

export const composePs = (domain: string) =>
  json<{ ok: boolean; output: string }>(`/api/sites/${encodeURIComponent(domain)}/docker-compose/ps`)

// Backup management
export interface BackupEntry {
  path: string
  size: number
  createdUtc: string
}
export const fetchBackups = () =>
  json<{ count: number; backups: BackupEntry[] }>('/api/backup/list')

export const createBackup = () =>
  json<{ path: string; files: number; size: number }>('/api/backup', { method: 'POST' })

export const downloadBackup = (path?: string) => {
  const token = window.daemonApi?.getToken?.() || new URLSearchParams(window.location.search).get('token') || ''
  const params = new URLSearchParams()
  if (path) params.set('path', path)
  if (token) params.set('token', token)
  const qs = params.toString()
  window.open(`${daemonBaseUrl()}/api/backup/download${qs ? '?' + qs : ''}`, '_blank')
}

// Per-site metrics — Phase 11 performance monitoring foothold
export interface SiteMetrics {
  domain: string
  hasMetrics: boolean
  accessLog: {
    path: string
    sizeBytes: number
    requestCount: number
    lastWriteUtc: string
  } | null
}
export const fetchSiteMetrics = (domain: string): Promise<SiteMetrics> =>
  json(`/api/sites/${encodeURIComponent(domain)}/metrics`)

// Phase 11 server-side historical metrics — backed by MetricsHistoryService
// background poller (60s cadence, 7-day retention). Returns time-series
// samples with a pre-computed `requestsPerMin` delta-from-previous so the
// frontend can render hour/day/week windows beyond the 5-minute client-side
// ring buffer in SiteEdit Metrics tab.
export interface SiteMetricsHistorySample {
  sampledAt: string         // ISO-8601 UTC
  requestCount: number      // cumulative line count at sample time
  sizeBytes: number         // cumulative log size at sample time
  lastWriteUtc: string | null
  requestsPerMin: number    // computed delta-from-previous, normalized to per-minute
}
export interface SiteMetricsHistory {
  domain: string
  minutes: number           // window size echoed back from server
  samples: SiteMetricsHistorySample[]
}
export const fetchSiteMetricsHistory = (
  domain: string,
  minutes: number = 60,
  limit: number = 200,
): Promise<SiteMetricsHistory> =>
  json(`/api/sites/${encodeURIComponent(domain)}/metrics/history?minutes=${minutes}&limit=${limit}`)

export const getHistoricalMetrics = (
  domain: string,
  opts?: { date?: string; granularity?: string },
): Promise<HistoricalMetrics> => {
  const params = new URLSearchParams()
  if (opts?.date) params.set('date', opts.date)
  if (opts?.granularity) params.set('granularity', opts.granularity)
  const qs = params.toString()
  return json(`/api/sites/${encodeURIComponent(domain)}/metrics/historical${qs ? '?' + qs : ''}`)
}

export const getErrorLogs = (
  domain: string,
  opts?: { lines?: number; since?: string },
): Promise<SiteErrorLogEntry[]> => {
  const params = new URLSearchParams()
  if (opts?.lines !== undefined) params.set('lines', String(opts.lines))
  if (opts?.since) params.set('since', opts.since)
  const qs = params.toString()
  return json(`/api/sites/${encodeURIComponent(domain)}/logs/errors${qs ? '?' + qs : ''}`)
}

export const getAccessLogs = (
  domain: string,
  opts?: { lines?: number; since?: string },
): Promise<AccessLogEntry[]> => {
  const params = new URLSearchParams()
  if (opts?.lines !== undefined) params.set('lines', String(opts.lines))
  if (opts?.since) params.set('since', opts.since)
  const qs = params.toString()
  return json(`/api/sites/${encodeURIComponent(domain)}/logs/access${qs ? '?' + qs : ''}`)
}

// PHP
export const fetchPhpVersions = (): Promise<PhpVersion[]> =>
  json('/api/php/versions')

// Filesystem browse (folder picker dialog)
export interface FsEntry {
  name: string
  path: string
  isDir: boolean
  isFile: boolean
  size: number
}
export interface FsBrowseResponse {
  path: string
  parent: string | null
  entries: FsEntry[]
}
export const browseFolder = (path?: string): Promise<FsBrowseResponse> =>
  json(`/api/fs/browse${path ? `?path=${encodeURIComponent(path)}` : ''}`)

// ─── Cloudflare Tunnel plugin ─────────────────────────────────────────
export interface CloudflareConfig {
  cloudflaredPath?: string | null
  tunnelToken?: string | null
  tunnelName?: string | null
  tunnelId?: string | null
  apiToken?: string | null
  accountId?: string | null
  defaultZoneId?: string | null
  startupTimeoutSecs?: number
  subdomainTemplate?: string | null
}

export const fetchCloudflareConfig = (): Promise<CloudflareConfig> =>
  json('/api/cloudflare/config')

export const saveCloudflareConfig = (cfg: Partial<CloudflareConfig>): Promise<CloudflareConfig> =>
  json('/api/cloudflare/config', { method: 'PUT', body: JSON.stringify(cfg) })

/**
 * Cloudflare API v4 response envelope. The daemon proxies CF responses
 * through `/api/cloudflare/*` preserving this shape. Generic over the
 * payload so each endpoint can pin `result` to its own subtype instead
 * of stringly-typed `any` returns.
 */
export interface CfResponse<T> {
  success: boolean
  result?: T
  errors?: Array<{ code?: number; message?: string }>
  messages?: Array<{ code?: number; message?: string }>
}

export const verifyCloudflareToken = (): Promise<CfResponse<unknown>> =>
  json('/api/cloudflare/verify')

export const fetchCloudflareZones = (): Promise<CfResponse<Array<{ id: string; name: string }>>> =>
  json('/api/cloudflare/zones')

export const fetchCloudflareDns = (
  zoneId: string,
): Promise<CfResponse<Array<{ id: string; type: string; name: string; content: string; proxied: boolean }>>> =>
  json(`/api/cloudflare/zones/${zoneId}/dns`)

export interface CfDnsRecordCreate {
  type: string
  name: string
  content: string
  proxied?: boolean
  ttl?: number
}
export const createCloudflareDns = (zoneId: string, body: CfDnsRecordCreate): Promise<CfResponse<{ id: string }>> =>
  json(`/api/cloudflare/zones/${zoneId}/dns`, { method: 'POST', body: JSON.stringify(body) })

export const deleteCloudflareDns = (zoneId: string, recordId: string): Promise<CfResponse<{ id?: string }>> =>
  json(`/api/cloudflare/zones/${zoneId}/dns/${recordId}`, { method: 'DELETE' })

export const fetchCloudflareTunnels = (): Promise<CfResponse<Array<{ id: string; name: string }>>> =>
  json('/api/cloudflare/tunnels')

export interface CfIngressRule {
  hostname: string
  service: string
}

/**
 * Cloudflare v4 response envelope, trimmed to the fields we bind.
 * The full upstream shape has `success`, `errors`, `messages`, etc., but
 * pinning just `result.config.ingress` captures what the ingress tab
 * reads off the endpoint.
 */
export interface CfTunnelConfigResponse {
  result?: {
    config?: {
      ingress?: Array<Partial<CfIngressRule>>
    }
  }
}

export const fetchCloudflareTunnelConfig = (tunnelId: string): Promise<CfTunnelConfigResponse> =>
  json(`/api/cloudflare/tunnels/${tunnelId}/configuration`)

export const updateCloudflareTunnelIngress = (
  tunnelId: string,
  rules: CfIngressRule[],
): Promise<{ ok: boolean }> =>
  json(`/api/cloudflare/tunnels/${tunnelId}/configuration`, {
    method: 'PUT',
    body: JSON.stringify({ rules }),
  })

export interface CloudflareAutoSetupResult {
  ok: boolean
  account: { id: string; name: string }
  tunnel: { id: string; name: string }
  tokenFetched: boolean
}
export const cloudflareAutoSetup = (apiToken: string): Promise<CloudflareAutoSetupResult> =>
  json('/api/cloudflare/auto-setup', {
    method: 'POST',
    body: JSON.stringify({ apiToken }),
  })

export const cloudflareSync = (): Promise<{ ok: boolean; synced: number; sites: string[] }> =>
  json('/api/cloudflare/sync', { method: 'POST' })

// ─── Catalog API account + device management ──────────────────────────
// These call the catalog-api directly (not the daemon), using JWT auth.

function catalogBase(catalogUrl?: string): string {
  return (catalogUrl || 'https://wdc.nks-hub.cz').replace(/\/$/, '')
}

function jwtHeaders(token: string): Record<string, string> {
  return {
    'Content-Type': 'application/json',
    'Authorization': `Bearer ${token}`,
  }
}

export interface CatalogTokenResponse {
  token: string
  email: string
}

export async function catalogRegister(
  catalogUrl: string, email: string, password: string,
): Promise<CatalogTokenResponse> {
  const r = await fetch(`${catalogBase(catalogUrl)}/api/v1/auth/register`, {
    method: 'POST',
    headers: { 'Content-Type': 'application/json' },
    body: JSON.stringify({ email, password }),
  })
  if (!r.ok) {
    const body = await r.json().catch(() => ({}))
    throw new Error(body?.detail || `HTTP ${r.status}`)
  }
  return r.json()
}

export async function catalogLogin(
  catalogUrl: string, email: string, password: string,
): Promise<CatalogTokenResponse> {
  const r = await fetch(`${catalogBase(catalogUrl)}/api/v1/auth/login`, {
    method: 'POST',
    headers: { 'Content-Type': 'application/json' },
    body: JSON.stringify({ email, password }),
  })
  if (!r.ok) {
    const body = await r.json().catch(() => ({}))
    throw new Error(body?.detail || `HTTP ${r.status}`)
  }
  return r.json()
}

export interface DeviceInfo {
  device_id: string
  name: string | null
  os: string | null
  arch: string | null
  site_count: number | null
  last_seen_at: string | null
  updated_at: string | null
  online: boolean
  is_current: boolean
}

// Shared helper: extract { detail: "..." } or raw text from a failed
// catalog-api response so thrown errors surface the actual cause (auth
// expired, device not found, validation error) instead of a bare status.
async function catalogErrorMessage(r: Response): Promise<string> {
  const body = await r.json().catch(() => null)
  if (body && typeof body === 'object' && 'detail' in body && body.detail) {
    return String(body.detail)
  }
  const text = await r.text().catch(() => '')
  return text || `HTTP ${r.status}`
}

export async function fetchDevices(
  catalogUrl: string,
  token: string,
  currentDeviceId?: string,
): Promise<DeviceInfo[]> {
  // Pass current device id so the server can flag `is_current=true` on
  // the row representing the caller. The UI uses this for the "this
  // device" badge in the Devices tab.
  const qs = currentDeviceId ? `?current_device_id=${encodeURIComponent(currentDeviceId)}` : ''
  const r = await fetch(`${catalogBase(catalogUrl)}/api/v1/devices${qs}`, {
    headers: jwtHeaders(token),
  })
  if (!r.ok) throw new Error(await catalogErrorMessage(r))
  return r.json()
}

export async function pushConfigToDevice(
  catalogUrl: string, token: string, targetDeviceId: string, sourceDeviceId: string,
): Promise<any> {
  const r = await fetch(`${catalogBase(catalogUrl)}/api/v1/devices/${targetDeviceId}/push-config`, {
    method: 'POST',
    headers: jwtHeaders(token),
    body: JSON.stringify({ source_device_id: sourceDeviceId }),
  })
  if (!r.ok) throw new Error(await catalogErrorMessage(r))
  return r.json()
}

export async function deleteDevice(
  catalogUrl: string, token: string, deviceId: string,
): Promise<any> {
  const r = await fetch(`${catalogBase(catalogUrl)}/api/v1/devices/${deviceId}`, {
    method: 'DELETE',
    headers: jwtHeaders(token),
  })
  if (!r.ok) throw new Error(await catalogErrorMessage(r))
  return r.json()
}

export const suggestCloudflareSubdomain = (domain: string): Promise<{ suggestion: string; domain: string }> =>
  json(`/api/cloudflare/suggest-subdomain?domain=${encodeURIComponent(domain)}`)

// Databases
export const fetchDatabases = (): Promise<DatabaseInfo[]> =>
  json('/api/databases')

// SSL
export const fetchCerts = (): Promise<CertInfo[]> =>
  json('/api/ssl/certs')

// Install the local mkcert CA into the system trust store so self-signed
// site certificates become trusted in browsers + system HTTP clients.
// Used by both SslManager.vue (as an explicit action) and OnboardingWizard
// (as part of the first-run flow).
export const installSslCa = (): Promise<{ ok: boolean }> =>
  json('/api/ssl/install-ca', { method: 'POST' })

// Plugins
export const fetchPlugins = (): Promise<PluginManifest[]> =>
  json('/api/plugins')

export interface MarketplacePlugin {
  id: string
  name: string
  version: string
  description: string
  downloadUrl: string
  author: string
  license: string
  installed: boolean
}
export interface MarketplaceResponse {
  source: string
  reachable: boolean
  plugins: MarketplacePlugin[]
  count?: number
  error?: string
}
export const fetchMarketplace = (): Promise<MarketplaceResponse> =>
  json('/api/plugins/marketplace')

export interface InstallPluginResponse {
  installed: boolean
  id: string
  path: string
  restartRequired: boolean
  message: string
}
export const installPluginFromMarketplace = (id: string, downloadUrl: string): Promise<InstallPluginResponse> =>
  json<InstallPluginResponse>('/api/plugins/install', {
    method: 'POST',
    body: JSON.stringify({ id, downloadUrl }),
  })

// Onboarding
export interface OnboardingState {
  completed: boolean
  prerequisites: {
    apacheInstalled: boolean
    phpInstalled: boolean
    mysqlInstalled: boolean
    mkcertBinaryInstalled: boolean
    mkcertCaInstalled: boolean
  }
}
export const fetchOnboardingState = (): Promise<OnboardingState> =>
  json('/api/onboarding/state')

export const completeOnboarding = (): Promise<{ completed: boolean }> =>
  json('/api/onboarding/complete', { method: 'POST' })

export const enablePlugin = (id: string) =>
  json<void>(`/api/plugins/${id}/enable`, { method: 'POST' })

export const disablePlugin = (id: string) =>
  json<void>(`/api/plugins/${id}/disable`, { method: 'POST' })

export const fetchPluginUi = (id: string) =>
  json<import('./types').PluginUiDefinition>(`/api/plugins/${id}/ui`)

export interface PluginNavEntry {
  pluginId: string
  category: string
  id: string
  label: string
  icon: string
  route: string
  order: number
}

export const fetchPluginNavEntries = () =>
  json<{ entries: PluginNavEntry[] }>('/api/plugins/ui')

// Binaries
// Daemon returns a FLAT BinaryRelease[] (each entry has .app) rather than the
// Record<app, BinaryRelease[]> the frontend used to assume. Group on the client
// so Binaries.vue keeps its existing shape without needing a daemon API change.
export const fetchBinaryCatalog = async (): Promise<Record<string, BinaryRelease[]>> => {
  const flat = await json<BinaryRelease[] | Record<string, BinaryRelease[]>>('/api/binaries/catalog')
  // If the daemon ever starts returning the grouped shape directly, pass it
  // through untouched (arrays have length, objects have keys).
  if (!Array.isArray(flat)) return flat
  const grouped: Record<string, BinaryRelease[]> = {}
  for (const entry of flat) {
    const app = entry.app ?? ''
    if (!app) continue
    if (!grouped[app]) grouped[app] = []
    grouped[app].push(entry)
  }
  return grouped
}

export const fetchBinaryCatalogForApp = (app: string): Promise<BinaryRelease[]> =>
  json(`/api/binaries/catalog/${app}`)

export const fetchInstalledBinaries = async (): Promise<InstalledBinary[]> => {
  // Daemon uses `installPath` + `executable` in its DTO while the frontend
  // type exposes `path`. Normalise at the API boundary so Binaries.vue can
  // keep using InstalledBinary.path without knowing about the naming clash.
  const raw = await json<any[]>('/api/binaries/installed')
  if (!Array.isArray(raw)) return []
  return raw.map(r => ({
    app: r.app,
    version: r.version,
    path: r.path ?? r.installPath ?? r.executable ?? '',
    isDefault: r.isDefault ?? false,
  }))
}

export const installBinary = (app: string, version: string) =>
  json<{ ok: boolean; path?: string; message?: string }>('/api/binaries/install', {
    method: 'POST',
    body: JSON.stringify({ app, version }),
  })

export const uninstallBinary = (app: string, version: string) =>
  json<{ ok: boolean }>(`/api/binaries/${app}/${version}`, { method: 'DELETE' })

// Service logs
export const fetchServiceLogs = (id: string, lines = 200): Promise<string[]> =>
  json(`/api/services/${id}/logs?lines=${lines}`)

// Service config
export interface ConfigFile {
  name: string
  path: string
  content: string
}
export const fetchServiceConfig = (id: string): Promise<{ serviceId: string; files: ConfigFile[] }> =>
  json(`/api/services/${id}/config`)

export interface ConfigValidationResult {
  isValid: boolean
  output: string
}

export const validateServiceConfig = (
  serviceId: string,
  configPath: string,
  content?: string,
): Promise<ConfigValidationResult> =>
  json('/api/config/validate', {
    method: 'POST',
    body: JSON.stringify({ serviceId, configPath, content }),
  })

export interface SaveServiceConfigResult {
  saved: boolean
  applied: boolean
  restarted: boolean
  message: string
}

export const saveServiceConfig = (
  id: string,
  path: string,
  content: string,
): Promise<SaveServiceConfigResult> =>
  json(`/api/services/${id}/config`, {
    method: 'POST',
    body: JSON.stringify({ path, content }),
  })

// ── Node.js per-site process management ──────────────────────────────

export interface NodeSiteStatus {
  domain: string
  state: number // 0=Stopped, 1=Starting, 2=Running, 3=Stopping, 4=Crashed
  pid: number | null
  port: number
  startCommand: string
  cpuPercent: number
  memoryBytes: number
  uptime: string | null
}

export const fetchNodeSites = () =>
  json<NodeSiteStatus[]>('/api/node/sites')

export const startNodeSite = (domain: string) =>
  json<NodeSiteStatus>(`/api/node/sites/${encodeURIComponent(domain)}/start`, { method: 'POST' })

export const stopNodeSite = (domain: string) =>
  json<{ ok: boolean; domain: string }>(`/api/node/sites/${encodeURIComponent(domain)}/stop`, { method: 'POST' })

export const restartNodeSite = (domain: string) =>
  json<NodeSiteStatus>(`/api/node/sites/${encodeURIComponent(domain)}/restart`, { method: 'POST' })

// Composer
export const composerStatus = (domain: string): Promise<ComposerStatus> =>
  json(`/api/sites/${encodeURIComponent(domain)}/composer/status`)

export const composerInstall = (domain: string): Promise<ComposerCommandResult> =>
  json(`/api/sites/${encodeURIComponent(domain)}/composer/install`, { method: 'POST' })

export const composerRequire = (domain: string, pkg: string): Promise<ComposerCommandResult> =>
  json(`/api/sites/${encodeURIComponent(domain)}/composer/require`, {
    method: 'POST',
    body: JSON.stringify({ package: pkg }),
  })

export const composerRemove = (domain: string, pkg: string): Promise<ComposerCommandResult> =>
  json(`/api/sites/${encodeURIComponent(domain)}/composer/remove`, {
    method: 'POST',
    body: JSON.stringify({ package: pkg }),
  })

export interface ComposerOutdatedEntry {
  name: string
  version: string | null
  latest: string | null
  latestStatus: string | null
}

export const composerOutdated = (domain: string): Promise<{ installed: ComposerOutdatedEntry[] }> =>
  json(`/api/sites/${encodeURIComponent(domain)}/composer/outdated`)

export const composerInit = (domain: string, body: Record<string, string>): Promise<{ exitCode: number; stdout: string; stderr: string; composerRoot: string }> =>
  json(`/api/sites/${encodeURIComponent(domain)}/composer/init`, {
    method: 'POST',
    body: JSON.stringify(body),
  })

export const composerDiagnose = (domain: string): Promise<{ warnings: string[]; errors: string[] }> =>
  json(`/api/sites/${encodeURIComponent(domain)}/composer/diagnose`)

// ── Site duplication ──────────────────────────────────────────────────

export async function duplicateSite(
  domain: string,
  newDomain: string,
  copyFiles: 'all' | 'top' | 'empty' = 'all',
): Promise<{ domain: string; documentRoot: string; sourceDomain: string; copyFiles: string; warnings: string[] }> {
  const r = await fetch(`${daemonBaseUrl()}/api/sites/${encodeURIComponent(domain)}/duplicate`, {
    method: 'POST',
    headers: daemonAuthHeaders({ 'Content-Type': 'application/json' }),
    body: JSON.stringify({ newDomain, copyFiles }),
  })
  if (!r.ok) throw new Error(`HTTP ${r.status}: ${await r.text()}`)
  return r.json()
}

// ── Hosts file management ─────────────────────────────────────────────

export interface HostEntry {
  enabled: boolean
  ip: string
  hostname: string
  source: 'wdc' | 'custom' | 'external'
  comment?: string | null
  lineNumber: number
}

export interface HostApplyEntry {
  enabled: boolean
  ip: string
  hostname: string
  source: 'wdc' | 'custom' | 'external'
  comment?: string | null
}

export const fetchHosts = (): Promise<HostEntry[]> =>
  json('/api/hosts')

export const applyHosts = (entries: HostApplyEntry[]): Promise<{ applied: boolean; entryCount: number }> =>
  json('/api/hosts/apply', { method: 'POST', body: JSON.stringify({ entries }) })

export const backupHosts = (): Promise<{ path: string; timestamp: string }> =>
  json('/api/hosts/backup', { method: 'POST' })

export const restoreHosts = (pathOrContent: { path?: string; content?: string }): Promise<{ restored: boolean }> =>
  json('/api/hosts/restore', { method: 'POST', body: JSON.stringify(pathOrContent) })

/**
 * Subscribe to SSE stream from daemon.
 * Returns a cleanup function — call it to close the EventSource.
 *
 * Implements its own reconnect with exponential backoff because the built-in
 * EventSource reconnect uses the frozen initial URL. On daemon restart the
 * port/token change, so we must rebuild the URL from current location.search
 * (which Electron main refreshes on window reload) before each reconnect attempt.
 */
export interface ValidationUpdate {
  phase: 'started' | 'passed' | 'failed'
  serviceId: string
  configPath?: string
  output?: string
}

export function subscribeEvents(
  onService: (data: import('./types').ServiceInfo) => void,
  onProgress: (data: ProgressUpdate) => void,
  onMetrics?: (data: MetricsUpdate) => void,
  onLog?: (data: LogEntry) => void,
  onValidation?: (data: ValidationUpdate) => void,
): () => void {
  let es: EventSource | null = null
  let closed = false
  let backoffMs = 1000
  const MAX_BACKOFF = 15000
  let reconnectTimer: ReturnType<typeof setTimeout> | null = null

  function buildUrl(): string {
    // Prefer preload (live-refreshes port file) over URL query (frozen)
    const preloadToken = window.daemonApi?.getToken?.() || ''
    const urlToken = new URLSearchParams(window.location.search).get('token') || ''
    const token = preloadToken || urlToken
    return token
      ? `${daemonBaseUrl()}/api/events?token=${encodeURIComponent(token)}`
      : `${daemonBaseUrl()}/api/events`
  }

  function connect() {
    if (closed) return
    try {
      es = new EventSource(buildUrl())
    } catch {
      scheduleReconnect()
      return
    }

    es.addEventListener('open', () => {
      // Successful connection — reset backoff
      backoffMs = 1000
    })

    es.addEventListener('service', (e: MessageEvent) => {
      try { onService(JSON.parse(e.data) as import('./types').ServiceInfo) } catch { /* ignore */ }
    })

    es.addEventListener('progress', (e: MessageEvent) => {
      try { onProgress(JSON.parse(e.data) as ProgressUpdate) } catch { /* ignore */ }
    })

    es.addEventListener('metrics', (e: MessageEvent) => {
      try { onMetrics?.(JSON.parse(e.data) as MetricsUpdate) } catch { /* ignore */ }
    })

    es.addEventListener('log', (e: MessageEvent) => {
      try { onLog?.(JSON.parse(e.data) as LogEntry) } catch { /* ignore */ }
    })

    es.addEventListener('validation', (e: MessageEvent) => {
      try { onValidation?.(JSON.parse(e.data) as ValidationUpdate) } catch { /* ignore */ }
    })

    es.onerror = () => {
      // EventSource would auto-reconnect with stale URL — we close and rebuild
      // so the next attempt uses fresh port/token (daemon restart scenario)
      if (es) {
        try { es.close() } catch { /* ignore */ }
        es = null
      }
      scheduleReconnect()
    }
  }

  function scheduleReconnect() {
    if (closed) return
    if (reconnectTimer !== null) return
    reconnectTimer = setTimeout(() => {
      reconnectTimer = null
      backoffMs = Math.min(backoffMs * 2, MAX_BACKOFF)
      connect()
    }, backoffMs)
  }

  connect()

  return () => {
    closed = true
    if (reconnectTimer !== null) {
      clearTimeout(reconnectTimer)
      reconnectTimer = null
    }
    if (es) {
      try { es.close() } catch { /* ignore */ }
      es = null
    }
  }
}
