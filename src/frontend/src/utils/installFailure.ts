/**
 * Turns a failed binary install into one of a few known causes so the UI can
 * say it in plain words. The daemon puts a stable `code` (and for downloads
 * the upstream HTTP status) into the error body; when that is not available
 * the English message text is matched instead.
 */
export type InstallFailureKind =
  | 'forbidden' | 'notFound' | 'httpStatus' | 'notAnArchive' | 'shaMismatch'
  | 'noBuild' | 'unreachable' | 'cancelled' | 'generic'

export interface InstallFailure {
  kind: InstallFailureKind
  /** Upstream HTTP status for `httpStatus`. */
  status?: number
  /** Raw technical text for a collapsed "details" block. */
  detail: string
}

interface MaybeCoded {
  code?: unknown
  body?: { upstreamStatus?: unknown }
  message?: unknown
  name?: unknown
}

/** "HTTP 502 Bad Gateway: detail" reduced to the detail. */
function stripHttpPrefix(message: string): string {
  return message.replace(/^HTTP \d+ [^:]*: /, '')
}

function fromStatus(status: number | undefined): InstallFailure['kind'] {
  return status === 403 ? 'forbidden' : status === 404 ? 'notFound' : 'httpStatus'
}

export function classifyInstallFailure(err: unknown): InstallFailure {
  const e = (typeof err === 'object' && err !== null ? err : {}) as MaybeCoded
  const message = typeof e.message === 'string' ? e.message : String(err ?? '')
  const detail = stripHttpPrefix(message)

  if (e.name === 'AbortError') return { kind: 'cancelled', detail }

  switch (e.code) {
    case 'download_failed': {
      const status = typeof e.body?.upstreamStatus === 'number' ? e.body.upstreamStatus : undefined
      return { kind: fromStatus(status), status, detail }
    }
    case 'not_an_archive': return { kind: 'notAnArchive', detail }
    case 'sha_mismatch': return { kind: 'shaMismatch', detail }
    case 'no_build_for_platform': return { kind: 'noBuild', detail }
    case 'download_unreachable': return { kind: 'unreachable', detail }
    case 'cancelled': return { kind: 'cancelled', detail }
  }

  const text = detail.toLowerCase()
  if (/web page|not an archive|end of central directory|not a zip/.test(text)) return { kind: 'notAnArchive', detail }
  const statusMatch = /(?:answered|status code[^0-9]*|http)\s*(\d{3})/.exec(text)
  if (statusMatch) {
    const status = Number(statusMatch[1])
    return { kind: fromStatus(status), status, detail }
  }
  if (/sha-?256|checksum|hash/.test(text)) return { kind: 'shaMismatch', detail }
  if (/no build|no binary|not available for this platform|no release/.test(text)) return { kind: 'noBuild', detail }
  if (/timed? ?out|unreachable|failed to fetch|network|resolve|connection|refused/.test(text)) return { kind: 'unreachable', detail }
  return { kind: 'generic', detail }
}
