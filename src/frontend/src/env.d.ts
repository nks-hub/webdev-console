/// <reference types="vite/client" />

/**
 * Ambient declarations for renderer-side globals injected by the Electron
 * main process (electron/main.ts) or the preload script
 * (electron/preload.ts). Keep this file in sync with those two sources —
 * each Window.* interface member below must mirror a real
 * contextBridge.exposeInMainWorld('<name>', …) block. Fields that exist
 * only in packaged Electron (not in `npm run dev` browser mode) are
 * marked optional so consumers are forced to guard access.
 *
 * ImportMetaEnv is synced against the `define` block in
 * electron.vite.config.ts — adding a new VITE_ env var means adding
 * both the define entry and the field here.
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
   * Port + token lookup exposed by electron/preload.ts so the renderer
   * can talk to the .NET daemon whose port is written to a rotating
   * file on disk. Declared here (not in api/daemon.ts) so pages can
   * access `window.daemonApi?.getPort()` without importing from the
   * API module just to pull in the type.
   */
  daemonApi: { getPort: () => number; getToken: () => string }

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
