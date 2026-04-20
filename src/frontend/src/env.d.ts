/// <reference types="vite/client" />

/**
 * Vite-injected env vars available at `import.meta.env`. Kept in sync with
 * the `define` block in `electron.vite.config.ts` and the build-time
 * substitutions the renderer depends on. Adding entries here lets call
 * sites drop `(import.meta as any).env?.X` casts.
 */
interface ImportMetaEnv {
  /** Package version baked in at build time — surfaced in About + Settings. */
  readonly VITE_APP_VERSION?: string
}

// Global injected by the Electron main process into the renderer window
// at runtime (see electron/main.ts). Exists only after the main process
// has a chance to eval scripts on the BrowserWindow; consumers must
// null-check. Declared here so the useUpdatesStore readCurrent() helper
// doesn't need a `(window as any)` cast.
interface Window {
  __APP_VERSION__?: string

  /**
   * Context-bridge surface exposed by electron/preload.ts. Optional
   * because the renderer also loads in browser dev mode where the
   * preload never runs — consumers must guard access. Keep this in
   * sync with the `exposeInMainWorld('electronAPI', …)` block in
   * preload.ts.
   */
  electronAPI?: {
    /** Returns true if the URL was allowlisted + opened, false on rejection. */
    openExternal: (url: string) => Promise<boolean>
    showOpenDialog: (options?: {
      title?: string
      defaultPath?: string
      properties?: Array<'openFile' | 'openDirectory' | 'multiSelections' | 'showHiddenFiles'>
    }) => Promise<{ canceled: boolean; filePaths: string[] }>
    onSsoCallback: (
      handler: (payload: { token: string; error: string }) => void,
    ) => () => void
    /** Reveal file/directory in the native file manager. */
    revealInFolder: (targetPath: string) => Promise<boolean>
  }
}
