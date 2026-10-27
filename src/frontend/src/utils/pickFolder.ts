/**
 * Opens the native folder dialog (Electron). Resolves to the chosen path,
 * `null` when the user cancelled, and `undefined` when there is no native
 * dialog (browser preview), so the caller can fall back to the in-app browser.
 */
export async function pickNativeFolder(title: string, defaultPath?: string): Promise<string | null | undefined> {
  const api = window.electronAPI
  if (!api?.showOpenDialog) return undefined
  const result = await api.showOpenDialog({ properties: ['openDirectory'], title, defaultPath })
  return result.canceled || !result.filePaths[0] ? null : result.filePaths[0]
}
