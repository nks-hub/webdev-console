import { getErrorLogs, getHistoricalMetrics } from '../api/daemon'
import type { SiteErrorLogEntry } from '../api/types'

const DAY_MS = 24 * 60 * 60 * 1000

// What Apache, PHP and PHP-FPM log as a real failure; notices, warnings and deprecations are not errors.
const ERROR_SEVERITIES = new Set(['emerg', 'alert', 'crit', 'error', 'err', 'fatal'])

/** Errors in a site's logs from the last 24 hours, newest first. */
export async function fetchRecentErrors(domain: string): Promise<SiteErrorLogEntry[]> {
  const entries = await getErrorLogs(domain, { lines: 1000, since: new Date(Date.now() - DAY_MS).toISOString() })
  return entries.filter(e => ERROR_SEVERITIES.has(e.severity.toLowerCase()))
}

const dayKey = (d: Date) => `${d.getFullYear()}-${String(d.getMonth() + 1).padStart(2, '0')}-${String(d.getDate()).padStart(2, '0')}`

/**
 * Requests per hour over the last 24 hours, oldest first. Read from the same access logs as the
 * Metrics tab, so both show the same numbers; the daemon buckets by calendar day, hence two days.
 */
export async function fetchHourlyRequests(domain: string): Promise<number[]> {
  const now = Date.now()
  const days = await Promise.all(
    [new Date(now - DAY_MS), new Date(now)].map(d => getHistoricalMetrics(domain, { date: dayKey(d), granularity: '1h' })),
  )
  return days
    .flatMap(day => day.series.find(s => s.name === 'requests')?.data ?? [])
    .filter(p => { const t = new Date(p.ts).getTime(); return t > now - DAY_MS && t <= now })
    .map(p => p.value)
}
