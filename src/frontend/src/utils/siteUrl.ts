import type { SiteInfo } from '../api/types'

/** Full address of a site, e.g. `https://shop.loc` or `http://shop.loc:8080`. */
export function siteUrl(site: Pick<SiteInfo, 'domain' | 'sslEnabled' | 'httpPort' | 'httpsPort'>): string {
  const https = !!site.sslEnabled
  const port = https ? (site.httpsPort || 443) : (site.httpPort || 80)
  const suffix = port === (https ? 443 : 80) ? '' : `:${port}`
  return `${https ? 'https' : 'http'}://${site.domain}${suffix}`
}

/** Opens a URL in the system browser (Electron) or a new tab (browser preview). */
export function openUrl(url: string): void {
  const api = window.electronAPI
  if (api?.openExternal) void api.openExternal(url)
  else window.open(url, '_blank', 'noopener')
}

/** Shows a folder in the system file manager; false outside Electron. */
export async function revealFolder(path: string): Promise<boolean> {
  const api = window.electronAPI
  if (!api?.revealInFolder) return false
  return api.revealInFolder(path)
}

/** Path as the OS writes it: backslashes for drive paths (the daemon may return forward slashes). */
export function displayPath(path: string): string {
  return /^[A-Za-z]:[\\/]/.test(path) ? path.replace(/\//g, '\\') : path
}

/** Folder separator used by a given path. */
export function pathSeparator(path: string): '\\' | '/' {
  return path.includes('\\') || /^[A-Za-z]:/.test(path) ? '\\' : '/'
}

/** "8.5.12" or "8.5" → "PHP 8.5". */
export function phpLabel(version: string): string {
  return `PHP ${version.split('.').slice(0, 2).join('.')}`
}
