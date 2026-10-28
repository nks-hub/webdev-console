import { i18n } from '../i18n'

// BackupContentFlags.ToString() on the daemon: "Vhosts, Ssl", "Default", "All"…
const FLAG_KEYS: Record<string, string[]> = {
  Vhosts: ['vhosts'],
  PluginConfigs: ['pluginConfigs'],
  Ssl: ['ssl'],
  Databases: ['databases'],
  Docroots: ['docroots'],
  Default: ['vhosts', 'pluginConfigs', 'ssl'],
  All: ['vhosts', 'pluginConfigs', 'ssl', 'databases', 'docroots'],
}

/** Content keys ("databases") of a daemon content-flags string. */
export function backupContentKeys(flags: string): string[] {
  return flags.split(',').flatMap(f => FLAG_KEYS[f.trim()] ?? [])
}

/** Short labels in the UI language: "Default" → "weby, pluginy, SSL". */
export function backupContentLabel(flags: string): string {
  return backupContentKeys(flags)
    .map(k => i18n.global.t(`backupsPage.content.short.${k}`))
    .join(', ')
}
