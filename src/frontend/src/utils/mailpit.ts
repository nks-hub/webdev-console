import { useDaemonStore } from '../stores/daemon'
import { servicePort } from './servicePorts'

/** Address of the Mailpit web UI, using the port the daemon reports, else the settings. */
export function mailpitUrl(): string {
  const reported = useDaemonStore().services.find(s => s.id === 'mailpit')?.ports?.http
  return `http://127.0.0.1:${reported ?? servicePort('mailpit') ?? 8025}`
}

export function openMailpit(): void {
  const url = mailpitUrl()
  if (window.electronAPI?.openExternal) void window.electronAPI.openExternal(url)
  else window.open(url, '_blank')
}
