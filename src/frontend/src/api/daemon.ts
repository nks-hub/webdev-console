import type {
  StatusResponse,
  SiteInfo,
  PhpVersion,
  PluginManifest,
  PluginUiDefinition,
  ProgressUpdate,
  ValidationUpdate,
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
  // Priority: (1) preload getPort() — re-reads the port file on every call,
  // so daemon restarts (new port/token) are picked up live, and (2) URL query
  // param ONLY as a browser/dev fallback where preload isn't injected.
  // The URL param is intentionally NOT used in packaged Electron: the renderer
  // URL is frozen at window.loadURL time, so after a daemon restart it keeps
  // pointing at the old port and every fetch fails until the user reloads.
  // The preload value, by contrast, refreshes from disk on each call.
  const preloadPort = window.daemonApi?.getPort?.()
  if (typeof preloadPort === 'number' && preloadPort > 0) {
    return `http://localhost:${preloadPort}`
  }
  // Browser/dev fallback only — packaged renderer always has window.daemonApi
  // (preload runs before the page loads, so getPort()>0 for any live daemon).
  if (!window.daemonApi) {
    const urlPort = new URLSearchParams(window.location.search).get('port')
    if (urlPort && /^\d+$/.test(urlPort)) {
      return `http://localhost:${parseInt(urlPort, 10)}`
    }
  }
  // Final fallback. Matches the daemon's port-range base (Program.cs
  // PORT_BASE = 17280) so any early fetch before the port file lands
  // has a realistic chance of succeeding. Was `5199` historically — a
  // stale default from the pre-port-probing daemon config; every fetch
  // hitting it after daemon moved to 17280 lit Sentry FRONT-D ("Failed
  // to fetch localhost:5199"). If the fallback itself is unreachable
  // the fetch error becomes actionable ("daemon not running") instead
  // of silently chasing a dead port.
  return 'http://localhost:17280'
}

// Exported alongside daemonBaseUrl so pages can build their own raw
// fetch() calls without re-implementing the token resolution. Each page
// previously shipped its own version — some forgot to include
// Content-Type, some forgot the Bearer token prefix, some used
// `getToken()` without optional-chaining.
export function daemonAuthHeaders(extra?: HeadersInit): Record<string, string> {
  // daemonToken() handles the preload-first / query fallback — same logic.
  const token = daemonToken()
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
  // Same logic as daemonBaseUrl: preload value first (re-reads the port file
  // so a daemon restart's new token is picked up live), URL query string only
  // when preload is absent (browser/dev). In packaged Electron the URL token
  // goes stale across daemon restarts and any fetch using it 401s.
  const preloadToken = window.daemonApi?.getToken?.()
  if (preloadToken) return preloadToken
  if (!window.daemonApi) {
    return new URLSearchParams(window.location.search).get('token') || ''
  }
  return ''
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
          const body: unknown = JSON.parse(txt)
          if (typeof body === 'string') {
            detail = body
          } else if (body && typeof body === 'object') {
            const rec = body as Record<string, unknown>
            for (const key of ['error', 'message', 'detail', 'title']) {
              const v = rec[key]
              if (typeof v === 'string' && v) { detail = v; break }
            }
          }
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

// Settings store — flat "category.key" → string map the daemon persists
// in SQLite. Used by Settings.vue (read + write + sync compare) and by
// Login.vue (read-only, to pre-fill the catalog URL). Several pages and
// the sync flow re-fetch the same endpoint; keeping a single helper
// avoids drift between the raw fetch() call sites.
export const fetchSettings = (): Promise<Record<string, string>> =>
  json('/api/settings')

export const saveSettings = (patch: Record<string, string>): Promise<void> =>
  json('/api/settings', { method: 'PUT', body: JSON.stringify(patch) })

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
  const token = daemonToken()
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

/**
 * F91.9: authoritative identity lookup. The catalog-api answered the SSO
 * flow and knows exactly who's behind the token — it in turn inherits
 * that identity from its own NKS SSO session. We ask it back over
 * /api/v1/auth/me (with a couple of common fallbacks for older builds)
 * rather than decoding the JWT ourselves, because the token's claim set
 * may be minimal and the catalog's user profile is richer (display name,
 * org roles, avatar, etc.).
 */
export interface CatalogMeResponse {
  id?: string
  email?: string
  name?: string
  username?: string
  avatar_url?: string
  roles?: string[]
  // Keep extra fields — profile shape can grow without breaking callers.
  [k: string]: unknown
}

export async function catalogMe(
  catalogUrl: string, token: string,
): Promise<CatalogMeResponse> {
  const base = catalogBase(catalogUrl)
  // Try the canonical path first, fall back to older shapes. Any 2xx wins.
  const candidates = ['/api/v1/auth/me', '/api/v1/me', '/api/v1/user/me']
  let lastError = ''
  for (const path of candidates) {
    try {
      const r = await fetch(`${base}${path}`, { headers: jwtHeaders(token) })
      if (r.ok) return await r.json() as CatalogMeResponse
      if (r.status === 404) { lastError = `404 ${path}`; continue }
      throw new Error(await catalogErrorMessage(r))
    } catch (e) {
      lastError = e instanceof Error ? e.message : String(e)
      if (path === candidates[candidates.length - 1]) throw new Error(lastError)
    }
  }
  throw new Error(lastError || 'catalog /me unavailable')
}

export async function catalogRegister(
  catalogUrl: string, email: string, password: string,
): Promise<CatalogTokenResponse> {
  const r = await fetch(`${catalogBase(catalogUrl)}/api/v1/auth/register`, {
    method: 'POST',
    headers: { 'Content-Type': 'application/json' },
    body: JSON.stringify({ email, password }),
  })
  if (!r.ok) throw new Error(await catalogErrorMessage(r))
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
  if (!r.ok) throw new Error(await catalogErrorMessage(r))
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
    // FastAPI's HTTP 422 ships `detail` as an ARRAY of validation objects —
    // `{loc, msg, type, input, url}[]`. Naively calling String() on that
    // yields the infamous "[object Object],[object Object]" because Array
    // #toString delegates to each element's toString. We flatten each item
    // to its `msg` (or JSON-stringify as a last resort) so the UI surfaces
    // a readable error like "value is not a valid email address".
    const d: unknown = (body as Record<string, unknown>).detail
    if (Array.isArray(d)) {
      return d.map(item => {
        if (item && typeof item === 'object') {
          const rec = item as Record<string, unknown>
          if (typeof rec.msg === 'string') {
            const loc = Array.isArray(rec.loc) ? rec.loc.join('.') : null
            return loc ? `${loc}: ${rec.msg}` : rec.msg
          }
          try { return JSON.stringify(item) } catch { return String(item) }
        }
        return String(item)
      }).join('; ') || `HTTP ${r.status}`
    }
    if (typeof d === 'string') return d
    if (d && typeof d === 'object') {
      try { return JSON.stringify(d) } catch { /* fall through */ }
    }
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
): Promise<unknown> {
  const r = await fetch(`${catalogBase(catalogUrl)}/api/v1/devices/${targetDeviceId}/push-config`, {
    method: 'POST',
    headers: jwtHeaders(token),
    body: JSON.stringify({ source_device_id: sourceDeviceId }),
  })
  if (!r.ok) throw new Error(await catalogErrorMessage(r))
  return r.json()
}

export const suggestCloudflareSubdomain = (domain: string): Promise<{ suggestion: string; domain: string }> =>
  json(`/api/cloudflare/suggest-subdomain?domain=${encodeURIComponent(domain)}`)

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
  /** F91.12: true when the plugin was bundled with the daemon (vs.
   *  downloaded from the marketplace). Built-in plugins can be
   *  uninstalled (blacklisted) but never removed from disk, so the UI
   *  shows a "Restore" button instead of "Install" when builtIn. */
  builtIn?: boolean
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

// F91.4: removes the plugin files on disk. Requires the plugin be
// disabled first and no enabled plugin to declare a hard dependency on it.
export const uninstallPlugin = (id: string) =>
  json<{
    uninstalled: boolean
    id: string
    restartRequired: boolean
    message?: string
    deletedFiles?: number
    lockedFiles?: number
  }>(`/api/plugins/${id}`, { method: 'DELETE' })

// F91.7: graceful daemon restart. Daemon exits with code 99, Electron
// main respawns. Returns a Promise that resolves when the new daemon
// answers on the port file; callers can wait to know when reload is safe.
export const restartDaemon = () =>
  json<void>('/api/admin/restart', { method: 'POST' })

// F91.12: restore an uninstalled built-in plugin. Removes it from the
// uninstall blacklist and restarts the daemon. Returns rebuildRequired=true
// when the DLL has been purged from disk — caller must tell the user to
// rebuild the solution before retrying.
export const restorePlugin = (id: string) =>
  json<{
    restored: boolean
    id: string
    restartRequired: boolean
    rebuildRequired: boolean
    message: string
  }>(`/api/plugins/restore/${id}`, { method: 'POST' })

/**
 * F91.12: poll the daemon `/healthz` endpoint until it responds 200, or
 * until the timeout expires. Used after a restart so the UI waits for the
 * new daemon to be ready before loading fresh state.
 */
export async function waitForDaemon(timeoutMs = 15000): Promise<boolean> {
  const start = Date.now()
  while (Date.now() - start < timeoutMs) {
    try {
      const r = await fetch(`${daemonBaseUrl()}/healthz`)
      if (r.ok) return true
    } catch { /* ECONNREFUSED while daemon is booting — keep polling */ }
    await new Promise(res => setTimeout(res, 400))
  }
  return false
}

export const fetchPluginUi = (id: string) =>
  json<PluginUiDefinition>(`/api/plugins/${id}/ui`)

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

export interface LatestBinary {
  app: string
  version: string
  os: string
  arch: string
  url: string
}

/**
 * Ask the daemon for the latest release of {app} that's compatible with
 * the current OS+arch. Used by the onboarding wizard so we never hardcode
 * a version that doesn't exist on the user's platform. Returns null when
 * the catalog has no compatible binary (e.g. MySQL on macOS before the
 * catalog gains macOS arm64 builds).
 */
export const fetchLatestBinary = (app: string) =>
  json<LatestBinary>(`/api/binaries/catalog/${encodeURIComponent(app)}/latest`)

export const uninstallBinary = (app: string, version: string) =>
  json<{ ok: boolean }>(`/api/binaries/${app}/${version}`, { method: 'DELETE' })

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
 * Map-shaped SSE subscription. Pass any number of named event handlers as
 * key/value entries — added so `deploy:event` and other plugin-contributed
 * SSE channels can attach without growing the named-parameter overload of
 * <see cref="subscribeEvents"/> (per v2 audit fix #4: "subscribeEvents
 * refactor to Map"). The 5 named-param overload below stays as a thin
 * wrapper so existing callers keep compiling unchanged.
 *
 * Returns a cleanup function — call it to close the EventSource.
 */
export function subscribeEventsMap(
  handlers: Record<string, (data: unknown) => void>,
): () => void {
  let es: EventSource | null = null
  let closed = false
  let backoffMs = 1000
  const MAX_BACKOFF = 15000
  let reconnectTimer: ReturnType<typeof setTimeout> | null = null

  function buildUrl(): string {
    // daemonToken() handles the preload-first / query-string fallback order,
    // matching the original inline logic here + 9 other call sites.
    const token = daemonToken()
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

    // Iterate the handler map so every registered event type wires into the
    // single shared connection. Reusing the same EventSource for plugin
    // events (e.g. deploy:event) avoids the previous 3-EventSource problem
    // identified in the v2 audit.
    for (const [eventName, handler] of Object.entries(handlers)) {
      es.addEventListener(eventName, (e: MessageEvent) => {
        try { handler(JSON.parse(e.data)) } catch { /* ignore malformed payload */ }
      })
    }

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
    // Add ±20% jitter so multiple renderer windows reconnecting after a
    // daemon restart don't all hit /api/events at the exact same moment.
    // Without jitter an exponential-backoff herd can keep retriggering
    // thundering reconnects on every failed probe.
    const jitter = backoffMs * (0.8 + Math.random() * 0.4)
    reconnectTimer = setTimeout(() => {
      reconnectTimer = null
      backoffMs = Math.min(backoffMs * 2, MAX_BACKOFF)
      connect()
    }, jitter)
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

/**
 * Backwards-compatible 5-handler overload of <see cref="subscribeEventsMap"/>.
 * Existing call sites (Sites view, ServiceCard, LogViewer, etc.) continue
 * to use this signature; new plugins (deploy, future) attach via the Map
 * variant. Implementation just builds a handler map and delegates.
 */
export function subscribeEvents(
  onService: (data: ServiceInfo) => void,
  onProgress: (data: ProgressUpdate) => void,
  onMetrics?: (data: MetricsUpdate) => void,
  onLog?: (data: LogEntry) => void,
  onValidation?: (data: ValidationUpdate) => void,
): () => void {
  const handlers: Record<string, (data: unknown) => void> = {
    service: (d) => onService(d as ServiceInfo),
    progress: (d) => onProgress(d as ProgressUpdate),
  }
  if (onMetrics) handlers.metrics = (d) => onMetrics(d as MetricsUpdate)
  if (onLog) handlers.log = (d) => onLog(d as LogEntry)
  if (onValidation) handlers.validation = (d) => onValidation(d as ValidationUpdate)
  return subscribeEventsMap(handlers)
}
