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
}
