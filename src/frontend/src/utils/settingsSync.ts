/**
 * Which daemon settings travel to another machine through settings export /
 * import and cloud push / pull. An allowlist, so a new key stays local until
 * someone decides it is portable: ports, paths, the catalog login token, this
 * device's identity, MCP and telemetry choices never leave the machine.
 * Mirrored by SyncClassification in the CLI (src/daemon/NKS.WebDevConsole.Cli).
 */
const PORTABLE_SETTINGS = [
  'backup.scheduleHours',
  'backup.retainCount',
  'backup.content.',
  'daemon.catalogUrl',
  'plugins.autoSyncEnabled',
  'deploy.enabled',
]

export function isSettingSyncable(key: string): boolean {
  return PORTABLE_SETTINGS.some(p => (p.endsWith('.') ? key.startsWith(p) : key === p))
}

export function pickSyncableSettings<T>(settings: Record<string, T>): Record<string, T> {
  return Object.fromEntries(Object.entries(settings).filter(([key]) => isSettingSyncable(key)))
}
