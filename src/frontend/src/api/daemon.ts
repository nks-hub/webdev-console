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
  // Priority: (1) URL query param passed by Electron main at load time (authoritative, fresh),
  // (2) preload getPort() as fallback if URL param missing (browser dev mode).
  // Preload has a hardcoded default of 5199 which can be STALE — never trust it if URL has a value.
  const urlPort = new URLSearchParams(window.location.search).get('port')
  let port: number = 5199
  if (urlPort && /^\d+$/.test(urlPort)) {
    port = parseInt(urlPort, 10)
  } else {
    const preloadPort = window.daemonApi?.getPort?.()
    if (typeof preloadPort === 'number' && preloadPort > 0) port = preloadPort
  }
  return `http://localhost:${port}`
}

function authHeaders(extra?: HeadersInit): Record<string, string> {
  // Same priority: URL query param first (authoritative), preload fallback.
  const urlToken = new URLSearchParams(window.location.search).get('token')
  const token = urlToken || window.daemonApi?.getToken?.() || ''
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
  if (!r.ok) throw new Error(`HTTP ${r.status} ${r.statusText}`)
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

// Databases
export const fetchDatabases = (): Promise<DatabaseInfo[]> =>
  json('/api/databases')

// SSL
export const fetchCerts = (): Promise<CertInfo[]> =>
  json('/api/ssl/certs')

// Plugins
export const fetchPlugins = (): Promise<PluginManifest[]> =>
  json('/api/plugins')

export const enablePlugin = (id: string) =>
  json<void>(`/api/plugins/${id}/enable`, { method: 'POST' })

export const disablePlugin = (id: string) =>
  json<void>(`/api/plugins/${id}/disable`, { method: 'POST' })

export const fetchPluginUi = (id: string) =>
  json<import('./types').PluginUiDefinition>(`/api/plugins/${id}/ui`)

// Binaries
export const fetchBinaryCatalog = (): Promise<Record<string, BinaryRelease[]>> =>
  json('/api/binaries/catalog')

export const fetchBinaryCatalogForApp = (app: string): Promise<BinaryRelease[]> =>
  json(`/api/binaries/catalog/${app}`)

export const fetchInstalledBinaries = (): Promise<InstalledBinary[]> =>
  json('/api/binaries/installed')

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

/**
 * Subscribe to SSE stream from daemon.
 * Returns a cleanup function — call it to close the EventSource.
 *
 * Implements its own reconnect with exponential backoff because the built-in
 * EventSource reconnect uses the frozen initial URL. On daemon restart the
 * port/token change, so we must rebuild the URL from current location.search
 * (which Electron main refreshes on window reload) before each reconnect attempt.
 */
export function subscribeEvents(
  onService: (data: import('./types').ServiceInfo) => void,
  onProgress: (data: ProgressUpdate) => void,
  onMetrics?: (data: MetricsUpdate) => void,
  onLog?: (data: LogEntry) => void,
): () => void {
  let es: EventSource | null = null
  let closed = false
  let backoffMs = 1000
  const MAX_BACKOFF = 15000
  let reconnectTimer: ReturnType<typeof setTimeout> | null = null

  function buildUrl(): string {
    // Re-read URL params on every reconnect so we pick up fresh port/token
    const urlToken = new URLSearchParams(window.location.search).get('token')
    const token = urlToken || window.daemonApi?.getToken?.() || ''
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
