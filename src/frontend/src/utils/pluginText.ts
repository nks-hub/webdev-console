import { useI18n } from 'vue-i18n'

/** Localised plugin display name and author, shared by the list and detail pages. */
export function usePluginText() {
  const { t, te } = useI18n()

  // First-party plugins ship English plugin.json names; show the translated
  // one for known ids and the manifest text for anything else.
  function pluginName(p: { id: string; name: string }): string {
    const key = `plugins.names.${p.id.replace(/^nks\.wdc\./, '').replace(/\./g, '_')}`
    return te(key) ? t(key) : p.name
  }

  // The built-in catalogue says "NKS", plugin manifests say "NKS Hub".
  const authorName = (a: string): string => (a === 'NKS' ? 'NKS Hub' : a)

  const PLATFORM_LABELS: Record<string, string> = { windows: 'Windows', macos: 'macOS', linux: 'Linux' }

  /** Warning text when the daemon reports the plugin cannot run on this OS, else ''. */
  function platformWarning(p: { platformSupported?: boolean; supportedPlatforms?: string[] }): string {
    if (p.platformSupported !== false) return ''
    const platforms = (p.supportedPlatforms ?? []).map(x => PLATFORM_LABELS[x.toLowerCase()] ?? x).join(', ')
    return t('plugins.platformUnsupported', { platforms })
  }

  return { pluginName, authorName, platformWarning }
}
