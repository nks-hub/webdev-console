import { servicePort } from './servicePorts'

/** Address of the Mailpit web UI, using the port from the settings. */
export function mailpitUrl(): string {
  return `http://127.0.0.1:${servicePort('mailpit') ?? 8025}`
}

export function openMailpit(): void {
  const url = mailpitUrl()
  if (window.electronAPI?.openExternal) void window.electronAPI.openExternal(url)
  else window.open(url, '_blank')
}
