export type LogLevel = 'error' | 'warn' | 'info'
export type LogLevelFilter = 'all' | LogLevel

// Tag the service plugins put in front of every line: the stream (`[ERR]`
// for anything the binary wrote to stderr, which MySQL does for its whole
// log) or the log file it came from (`[error.log]`). Neither says anything
// about severity.
const PLUGIN_TAG = /^\[(?:ERR|OUT|[\w.-]+\.log)\]\s*/i

/** The line without the plugin's stream or file tag. */
export function stripLogTag(line: string): string {
  return line.replace(PLUGIN_TAG, '')
}

/** For display: drops `[ERR]`/`[OUT]`, keeps the log file name of mixed logs. */
export function stripStreamTag(line: string): string {
  return line.replace(/^\[(?:ERR|OUT)\]\s*/, '')
}

function levelOfToken(token: string): LogLevel {
  const t = token.toLowerCase()
  if (/^(emerg|alert|crit|critical|error|erro|fatal|panic|fatal error|parse error)$/.test(t)) return 'error'
  if (/^(warn|warning)$/.test(t)) return 'warn'
  return 'info'
}

/**
 * Severity of one log line as the server binaries print it: Apache
 * `[core:error]`, MySQL `[ERROR]`/`[Warning]`/`[System]`, PostgreSQL
 * `ERROR:`/`WARNING:`/`LOG:`, PHP `PHP Fatal error:`, Mailpit `level=error`,
 * Redis `#` (warning) and `*` (notice). Null when the line carries no level.
 */
export function logLevelOf(rawLine: string): LogLevel | null {
  const line = stripLogTag(rawLine)
  const tagged =
    line.match(/\[(?:[\w-]+:)?(emerg|alert|crit|error|warn|warning|notice|note|info|system|debug)\]/i)
    ?? line.match(/\b(FATAL|PANIC|ERROR|WARNING|LOG|INFO|NOTICE|DEBUG)\d?:/)
    ?? line.match(/\blevel=(\w+)/i)
    ?? line.match(/\bPHP (Fatal error|Parse error|Warning|Notice|Deprecated):/i)
  if (tagged) return levelOfToken(tagged[1])
  const redis = line.match(/^\d+:[A-Z] .+?\d\d:\d\d:\d\d\.\d+ ([.\-*#]) /)
  if (redis) return redis[1] === '#' ? 'warn' : 'info'
  if (/\b(fatal|panic|error|failed)\b/i.test(line)) return 'error'
  if (/\b(warn|warning)\b/i.test(line)) return 'warn'
  return null
}

const RANK: Record<LogLevel, number> = { info: 0, warn: 1, error: 2 }

/** "warn" keeps warnings and errors, "info" every line that has a level. */
export function matchesLogLevel(line: string, filter: LogLevelFilter): boolean {
  if (filter === 'all') return true
  const level = logLevelOf(line)
  return level !== null && RANK[level] >= RANK[filter]
}

/** The first error line of a log tail, without its plugin tag: the likely reason a service stopped. */
export function firstErrorLine(lines: readonly string[]): string | null {
  const hit = lines.find(l => logLevelOf(l) === 'error')
  return hit ? stripLogTag(hit) : null
}
