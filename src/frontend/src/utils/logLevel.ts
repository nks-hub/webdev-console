export type LogLevel = 'error' | 'warn' | 'info'
export type LogLevelFilter = 'all' | LogLevel

/**
 * Severity of one log line as the server binaries print it: Apache
 * `[core:error]`, MySQL `[ERROR]`/`[Warning]`/`[Note]`, PostgreSQL
 * `ERROR:`/`WARNING:`/`LOG:`, PHP `PHP Fatal error:`, Mailpit `level=error`.
 * Null when the line carries no level.
 */
export function logLevelOf(line: string): LogLevel | null {
  if (/\b(fatal|panic|emerg|alert|crit|critical|error|err|erro)\b/i.test(line)) return 'error'
  if (/\b(warn|warning)\b/i.test(line)) return 'warn'
  if (/\b(info|notice|note|system)\b/i.test(line) || /\bLOG:/.test(line)) return 'info'
  return null
}

const RANK: Record<LogLevel, number> = { info: 0, warn: 1, error: 2 }

/** "warn" keeps warnings and errors, "info" every line that has a level. */
export function matchesLogLevel(line: string, filter: LogLevelFilter): boolean {
  if (filter === 'all') return true
  const level = logLevelOf(line)
  return level !== null && RANK[level] >= RANK[filter]
}
