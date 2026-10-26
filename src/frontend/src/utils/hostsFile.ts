/** Absolute path of the system hosts file for the OS the renderer runs on. */
export function hostsFilePath(): string {
  return /Windows/i.test(navigator.userAgent)
    ? 'C:\\Windows\\System32\\drivers\\etc\\hosts'
    : '/etc/hosts'
}

/**
 * Opens the hosts file in VS Code when it is installed (vscode://file/…),
 * otherwise reveals it in the OS file manager. window.open(…, '_self') was
 * cancelled by the renderer's will-navigate lock, so the button did nothing.
 */
export async function openHostsFile(): Promise<void> {
  const path = hostsFilePath()
  const api = window.electronAPI
  if (!api) return
  let opened = false
  try {
    opened = (await api.openExternal?.(`vscode://file/${path.replace(/\\/g, '/')}`)) === true
  } catch {
    opened = false // no vscode:// handler registered
  }
  if (!opened) await api.revealInFolder?.(path)
}
