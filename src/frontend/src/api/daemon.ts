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
} from './types'

declare global {
  interface Window {
    daemonApi: { getPort: () => number; getToken: () => string }
  }
}

function base(): string {
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

function authHeaders(extra?: HeadersInit): Record<string, string> {
  // Prefer preload token (live-refreshed from port file), fallback to URL query.
  const preloadToken = window.daemonApi?.getToken?.() || ''
  const urlToken = new URLSearchParams(window.location.search).get('token') || ''
  const token = preloadToken || urlToken
  const headers: Record<string, string> = { 'Content-Type': 'application/json' }
  if (token) headers['Authorization'] = `Bearer ${token}`
  if (extra) {
    const entries = extra instanceof Headers
      ? Array.from(extra.entries())
      : Array.isArray(extra)
        ? extra
        : Object.entries(extra)
    for (const [k, v] of entries) headers[k] = v
  }
  return headers
}

async function json<T>(path: string, init?: RequestInit): Promise<T> {
  const r = await fetch(`${base()}${path}`, {
    ...init,
    headers: authHeaders(init?.headers),
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

// Services
export const fetchServices = (): Promise<any[]> =>
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
}

export const fetchCloudflareConfig = (): Promise<CloudflareConfig> =>
  json('/api/cloudflare/config')

export const saveCloudflareConfig = (cfg: Partial<CloudflareConfig>): Promise<CloudflareConfig> =>
  json('/api/cloudflare/config', { method: 'PUT', body: JSON.stringify(cfg) })

export const verifyCloudflareToken = (): Promise<any> =>
  json('/api/cloudflare/verify')

export const fetchCloudflareZones = (): Promise<any> =>
  json('/api/cloudflare/zones')

export const fetchCloudflareDns = (zoneId: string): Promise<any> =>
  json(`/api/cloudflare/zones/${zoneId}/dns`)

export interface CfDnsRecordCreate {
  type: string
  name: string
  content: string
  proxied?: boolean
  ttl?: number
}
export const createCloudflareDns = (zoneId: string, body: CfDnsRecordCreate): Promise<any> =>
  json(`/api/cloudflare/zones/${zoneId}/dns`, { method: 'POST', body: JSON.stringify(body) })

export const deleteCloudflareDns = (zoneId: string, recordId: string): Promise<any> =>
  json(`/api/cloudflare/zones/${zoneId}/dns/${recordId}`, { method: 'DELETE' })

export const fetchCloudflareTunnels = (): Promise<any> =>
  json('/api/cloudflare/tunnels')

export const fetchCloudflareTunnelConfig = (tunnelId: string): Promise<any> =>
  json(`/api/cloudflare/tunnels/${tunnelId}/configuration`)

export interface CfIngressRule {
  hostname: string
  service: string
}
export const updateCloudflareTunnelIngress = (
  tunnelId: string,
  rules: CfIngressRule[],
): Promise<any> =>
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

export const cloudflareSync = (): Promise<{ ok: boolean; synced: number; sites: any[] }> =>
  json('/api/cloudflare/sync', { method: 'POST' })

// Databases
export const fetchDatabases = (): Promise<DatabaseInfo[]> =>
  json('/api/databases')

// SSL
export const fetchCerts = (): Promise<CertInfo[]> =>
  json('/api/ssl/certs')

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

// Binaries
// Daemon returns a FLAT BinaryRelease[] (each entry has .app) rather than the
// Record<app, BinaryRelease[]> the frontend used to assume. Group on the client
// so Binaries.vue keeps its existing shape without needing a daemon API change.
export const fetchBinaryCatalog = async (): Promise<Record<string, BinaryRelease[]>> => {
  const flat = await json<BinaryRelease[] | Record<string, BinaryRelease[]>>('/api/binaries/catalog')
  // If the daemon ever starts returning the grouped shape directly, pass it
  // through untouched (arrays have length, objects have keys).
  if (!Array.isArray(flat)) return flat as Record<string, BinaryRelease[]>
  const grouped: Record<string, BinaryRelease[]> = {}
  for (const entry of flat) {
    const app = (entry as any).app ?? ''
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
      ? `${base()}/api/events?token=${encodeURIComponent(token)}`
      : `${base()}/api/events`
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
