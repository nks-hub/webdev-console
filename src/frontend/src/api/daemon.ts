import type {
  StatusResponse,
  SiteInfo,
  PhpVersion,
  DatabaseInfo,
  CertInfo,
  PluginManifest,
  ProgressUpdate,
} from './types'

declare global {
  interface Window {
    daemonApi: { getPort: () => number }
  }
}

function base(): string {
  const port = window.daemonApi?.getPort() ?? 50051
  return `http://localhost:${port}`
}

async function json<T>(path: string, init?: RequestInit): Promise<T> {
  const r = await fetch(`${base()}${path}`, init)
  if (!r.ok) throw new Error(`HTTP ${r.status} ${r.statusText}`)
  return r.json() as Promise<T>
}

// Status
export const fetchStatus = (): Promise<StatusResponse> =>
  json('/api/status')

// Services
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
  json<SiteInfo>('/api/sites', { method: 'POST', headers: { 'Content-Type': 'application/json' }, body: JSON.stringify(data) })

export const deleteSite = (id: string) =>
  json<void>(`/api/sites/${id}`, { method: 'DELETE' })

export const updateSite = (id: string, data: Partial<SiteInfo>) =>
  json<SiteInfo>(`/api/sites/${id}`, { method: 'PATCH', headers: { 'Content-Type': 'application/json' }, body: JSON.stringify(data) })

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

/**
 * Subscribe to SSE stream from daemon.
 * Returns a cleanup function — call it to close the EventSource.
 */
export function subscribeEvents(
  onService: (data: import('./types').ServiceInfo) => void,
  onProgress: (data: ProgressUpdate) => void,
): () => void {
  const es = new EventSource(`${base()}/api/events`)

  es.addEventListener('service', (e: MessageEvent) => {
    try { onService(JSON.parse(e.data) as import('./types').ServiceInfo) } catch { /* ignore */ }
  })

  es.addEventListener('progress', (e: MessageEvent) => {
    try { onProgress(JSON.parse(e.data) as ProgressUpdate) } catch { /* ignore */ }
  })

  es.onerror = () => {
    // reconnect is automatic for EventSource; stores handle disconnected state via polling
  }

  return () => es.close()
}
