import { DaemonError } from '../api/daemon'
import { errorMessage } from './errors'

const DISPLAY_NAMES: Record<string, string> = {
  apache: 'Apache',
  caddy: 'Caddy',
  cloudflared: 'cloudflared',
  composer: 'Composer',
  mailpit: 'Mailpit',
  mariadb: 'MariaDB',
  mkcert: 'mkcert',
  mysql: 'MySQL',
  nginx: 'Nginx',
  node: 'Node.js',
  php: 'PHP',
  postgresql: 'PostgreSQL',
  redis: 'Redis',
}

/** Product name for a catalog app key ("mysql" → "MySQL"). */
export function appDisplayName(app: string): string {
  return DISPLAY_NAMES[app.toLowerCase()] ?? app
}

// Daemon `code` (BinaryEndpoints.ClassifyInstallError) → binaries.installError.* key.
const ERROR_KEYS: Record<string, string> = {
  download_unreachable: 'unreachable',
  not_an_archive: 'notAnArchive',
  sha_mismatch: 'shaMismatch',
  no_build_for_platform: 'noBuild',
  cancelled: 'cancelled',
}

type Translate = (key: string, named?: Record<string, unknown>) => string

/**
 * One plain sentence for a failed binary install, keyed by the `code` the
 * daemon puts in the error body. Unknown failures fall back to the raw text.
 */
export function installErrorMessage(t: Translate, err: unknown, app: string, version: string): string {
  const name = appDisplayName(app)
  if (err instanceof DOMException && err.name === 'AbortError') {
    return t('binaries.installError.cancelled', { name, version })
  }
  if (err instanceof DaemonError && err.code === 'download_failed') {
    const status = err.body?.upstreamStatus
    if (status === 403) return t('binaries.installError.forbidden', { name, version })
    if (status === 404) return t('binaries.installError.notFound', { name, version })
    return t('binaries.installError.httpStatus', { name, version, status: String(status ?? '?') })
  }
  if (err instanceof DaemonError && err.code && ERROR_KEYS[err.code]) {
    return t(`binaries.installError.${ERROR_KEYS[err.code]}`, { name, version })
  }
  return t('binaries.installError.generic', { name, version, err: installErrorDetail(err) })
}

/** Raw technical text worth showing in a collapsed "details" block. */
export function installErrorDetail(err: unknown): string {
  return err instanceof DaemonError ? err.detail || err.message : errorMessage(err)
}
