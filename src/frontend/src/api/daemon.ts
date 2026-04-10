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
  // Electron: preload exposes getPort(). Browser dev: use ?port= query param or default 5199.
  const urlPort = new URLSearchParams(window.location.search).get('port')
  const port = window.daemonApi?.getPort() ?? (urlPort ? parseInt(urlPort) : 5199)
  return `http://localhost:${port}`
}

function authHeaders(extra?: HeadersInit): Record<string, string> {
  const urlToken = new URLSearchParams(window.location.search).get('token')
  const token = window.daemonApi?.getToken?.() || urlToken || ''
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

/**
 * Subscribe to SSE stream from daemon.
 * Returns a cleanup function — call it to close the EventSource.
 */
export function subscribeEvents(
  onService: (data: import('./types').ServiceInfo) => void,
  onProgress: (data: ProgressUpdate) => void,
  onMetrics?: (data: MetricsUpdate) => void,
  onLog?: (data: LogEntry) => void,
): () => void {
  const token = window.daemonApi?.getToken?.() || ''
  const url = token
    ? `${base()}/api/events?token=${encodeURIComponent(token)}`
    : `${base()}/api/events`
  const es = new EventSource(url)

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
    // reconnect is automatic for EventSource; stores handle disconnected state via polling
  }

  return () => es.close()
}
