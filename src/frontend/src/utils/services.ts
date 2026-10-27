import type { ServiceInfo } from '../api/types'
import { errorMessage } from './errors'
import { serviceViewState, type ServiceViewState } from './serviceState'

/**
 * Helpers for the Services page and the service plugin pages. The state
 * itself comes from utils/serviceState.ts (shared with the shell).
 */

/** serviceViewState for a service that may not be in the list (yet). */
export const stateOf = (svc: ServiceInfo | null | undefined): ServiceViewState =>
  svc ? serviceViewState(svc) : 'stopped'

/** Crashed, or switched off by the daemon after too many crashes in a row. */
export const isProblemState = (s: ServiceViewState) => s === 'crashed' || s === 'disabled'

/** Key under servicesPage.status / .explain: a crash before the service ever ran reads as a failed start. */
export function stateKeyOf(svc: ServiceInfo | null | undefined): ServiceViewState | 'startFailed' {
  const state = stateOf(svc)
  return state === 'crashed' && svc?.crashKind === 'startFailed' ? 'startFailed' : state
}

/** Binaries page link for a service that is not installed. */
export const installRouteOf = (svc: ServiceInfo) =>
  ({ path: '/binaries', query: { app: svc.binaryApp ?? svc.id } })

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
