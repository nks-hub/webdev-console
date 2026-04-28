import { ref, type Ref } from 'vue'

/**
 * Runtime app version probe.
 *
 * Primary path: IPC `electronAPI.getAppVersion()` — main process resolves
 * `app.getVersion()` (= bundled package.json) at process start, so a version
 * bump there is reflected after a single Electron restart with NO renderer
 * rebuild. The build-time gitShortSha is still baked into the renderer
 * bundle (it identifies the artifact) and main returns it alongside.
 *
 * Fallback: `import.meta.env.VITE_APP_VERSION` baked at build time. Only
 * used when `electronAPI` isn't bridged (vite dev opened in plain browser
 * for component testing, etc.).
 */
const cached: Ref<{ version: string; gitSha: string; full: string }> = ref({
  version: (import.meta.env.VITE_APP_VERSION_BASE as string | undefined) ?? '0.0.0',
  gitSha: (import.meta.env.VITE_APP_GIT_SHA as string | undefined) ?? 'dev',
  full: (import.meta.env.VITE_APP_VERSION as string | undefined) ?? '0.0.0-dev',
})

let probed = false

// `electronAPI` shape now lives in env.d.ts (single source of truth) so
// every consumer sees the same getAppVersion/osNotify/rendererLog
// signatures.

export function useAppVersion() {
  if (!probed) {
    probed = true
    const api = (typeof window !== 'undefined' ? window.electronAPI : undefined)
    if (api?.getAppVersion) {
      api.getAppVersion()
        .then((v: { version: string; gitSha: string; full: string }) => {
          if (v && typeof v.version === 'string') cached.value = v
        })
        .catch((err: unknown) => {
          // Keep fallback values; surface the error in main log.
          console.warn('[useAppVersion] IPC probe failed, using build-time fallback:', err)
        })
    }
  }
  return cached
}
