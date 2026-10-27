import type { ServiceInfo } from '../api/types'
import { errorMessage } from './errors'

/**
 * Shared vocabulary for service rows: the Services page, the service plugin
 * pages and the Overview stack all read the same daemon fields
 * (GET /api/services → ServiceStatusDto) through these helpers.
 */
export type ServiceState = 'stopped' | 'starting' | 'running' | 'stopping' | 'crashed' | 'disabled' | 'notInstalled'

const BY_NUMBER: ServiceState[] = ['stopped', 'starting', 'running', 'stopping', 'crashed', 'disabled']

/** Lifecycle state in words; a service whose binary is missing is 'notInstalled'. */
export function stateOf(svc: ServiceInfo | null | undefined): ServiceState {
  if (!svc) return 'stopped'
  if (svc.installed === false) return 'notInstalled'
  return BY_NUMBER[svc.state ?? -1]
    ?? ((BY_NUMBER as string[]).includes(svc.status ?? '') ? svc.status as ServiceState : 'stopped')
}

/** Crashed, or switched off by the daemon after too many crashes in a row. */
export const isProblemState = (s: ServiceState) => s === 'crashed' || s === 'disabled'

const SHORT_NAMES: Record<string, string> = {
  'Apache HTTP Server': 'Apache',
  'PHP (Multi-version)': 'PHP',
}

export function shortName(svc: Pick<ServiceInfo, 'id' | 'displayName' | 'name'>): string {
  const name = svc.displayName || svc.name || ''
  return SHORT_NAMES[name] || name || svc.id
}

/** Binary folder on the Binaries page; mirrors ServiceStatusDto.BinaryApps in the daemon. */
export function binaryAppOf(serviceId: string): string {
  if (serviceId === 'cloudflare') return 'cloudflared'
  if (serviceId === 'httpd') return 'apache'
  return serviceId
}

/** Dedicated management page for a service, when one exists. */
const MANAGE_ROUTES: Record<string, string> = {
  apache: '/plugins/apache',
  php: '/plugins/php-custom',
  mysql: '/plugins/mysql',
  postgresql: '/plugins/postgresql',
  mailpit: '/plugins/mailpit',
  redis: '/plugins/redis',
  cloudflare: '/cloudflare',
}
export const manageRouteOf = (serviceId: string): string | null => MANAGE_ROUTES[serviceId] ?? null

export function formatUptime(seconds: number): string {
  const s = Math.max(0, Math.floor(seconds))
  if (s < 60) return `${s} s`
  if (s < 3600) return `${Math.floor(s / 60)} min`
  const h = Math.floor(s / 3600)
  if (h < 48) return `${h} h ${Math.floor((s % 3600) / 60)} min`
  return `${Math.floor(h / 24)} d ${h % 24} h`
}

export function formatMem(bytes: number): string {
  if (bytes < 1024 * 1024) return `${(bytes / 1024).toFixed(0)} KB`
  if (bytes < 1024 * 1024 * 1024) return `${(bytes / 1024 / 1024).toFixed(0)} MB`
  return `${(bytes / 1024 / 1024 / 1024).toFixed(1)} GB`
}

const PORT_LABELS: Record<string, string> = { http: 'HTTP', https: 'HTTPS', smtp: 'SMTP' }

/** "80" for one port, "SMTP 1025 · HTTP 8025" when the service listens on several. */
export function portsLabel(svc: ServiceInfo | null | undefined): string | null {
  const entries = Object.entries(svc?.ports ?? {})
  if (entries.length === 0) return svc?.port ? String(svc.port) : null
  if (entries.length === 1) return String(entries[0][1])
  return entries.map(([key, port]) => `${PORT_LABELS[key] ?? key} ${port}`).join(' · ')
}

/** Daemon error text without the "HTTP 500 Internal Server Error:" prefix the API client adds. */
export function cleanError(err: unknown): string {
  return errorMessage(err).replace(/^HTTP \d{3}[^:]*:\s*/, '').trim()
}

/**
 * Known cause of a failed start, so the panel can point at the fix. Only
 * messages the OS or the server binaries themselves print are matched.
 */
export function errorCause(message: string): 'port' | 'config' | null {
  if (/address already in use|only one usage of each socket address|EADDRINUSE|WSAEADDRINUSE|\b10048\b|port \d+ is (already )?in use/i.test(message)) return 'port'
  if (/syntax error|AH00526|unknown variable|unknown option|invalid configuration/i.test(message)) return 'config'
  return null
}
