import type { ServiceInfo } from '../api/types'

/** What the UI shows for a service: the daemon state plus "binary missing". */
export type ServiceViewState =
  | 'running' | 'starting' | 'stopping' | 'crashed' | 'disabled' | 'stopped' | 'notInstalled'

const BY_NUMBER = ['stopped', 'starting', 'running', 'stopping', 'crashed', 'disabled'] as const
const NAMES: readonly string[] = BY_NUMBER

export function isRunning(svc: ServiceInfo): boolean {
  return svc.state === 2 || svc.status === 'running'
}

export function isCrashed(svc: ServiceInfo): boolean {
  return svc.state === 4 || svc.status === 'crashed'
}

export function serviceViewState(svc: ServiceInfo): ServiceViewState {
  const raw: (typeof BY_NUMBER)[number] = BY_NUMBER[svc.state ?? -1]
    ?? (NAMES.includes(svc.status ?? '') ? svc.status as (typeof BY_NUMBER)[number] : 'stopped')
  // A crash of something that was never installed is just "not installed".
  if (svc.installed === false && (raw === 'stopped' || raw === 'crashed' || raw === 'disabled')) {
    return 'notInstalled'
  }
  return raw
}

/** Display name without the plugin's long suffixes ("Apache HTTP Server" -> "Apache"). */
const SHORT_NAMES: Record<string, string> = {
  'Apache HTTP Server': 'Apache',
  'PHP (Multi-version)': 'PHP',
}
export function shortServiceName(svc: ServiceInfo): string {
  return SHORT_NAMES[svc.displayName ?? ''] || svc.displayName || svc.id
}
