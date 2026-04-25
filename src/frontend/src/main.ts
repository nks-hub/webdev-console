import { createApp, defineAsyncComponent } from 'vue'
import { createPinia } from 'pinia'
import ElementPlus from 'element-plus'
import 'element-plus/dist/index.css'
import 'element-plus/theme-chalk/dark/css-vars.css'
import './assets/tokens.css'
import * as ElementPlusIcons from '@element-plus/icons-vue'
import * as Sentry from '@sentry/electron/renderer'

import { router } from './router/index'
import { i18n } from './i18n'
import App from './App.vue'

// Register panel components in the plugin registry. Wrapped in
// defineAsyncComponent so their heavy deps (echarts for MetricsChart,
// xterm for LogViewer) get code-split into their own chunks instead of
// being pulled into the main bundle through this eager import barrel.
import { registerPanelComponent } from './plugins/PluginRegistry'

registerPanelComponent('service-status-card', defineAsyncComponent(() => import('./components/shared/ServiceCard.vue')))
registerPanelComponent('version-switcher', defineAsyncComponent(() => import('./components/shared/VersionSwitcher.vue')))
registerPanelComponent('config-editor', defineAsyncComponent(() => import('./components/shared/ConfigEditor.vue')))
registerPanelComponent('log-viewer', defineAsyncComponent(() => import('./components/shared/LogViewer.vue')))
registerPanelComponent('metrics-chart', defineAsyncComponent(() => import('./components/shared/MetricsChart.vue')))

const app = createApp(App)

// Register all Element Plus icons globally
for (const [name, component] of Object.entries(ElementPlusIcons)) {
  app.component(name, component)
}

app.use(createPinia())
app.use(ElementPlus)
app.use(router)
app.use(i18n)

// Sentry renderer init — the main process already sets up the transport +
// DSN via @sentry/electron/main; renderer init picks that up automatically
// through IPC. Extra config here limits itself to Vue-aware integrations
// (component name + propsType on errors) and router navigation
// breadcrumbs. No DSN repeat — it's embedded in the shared scope from
// main. Consent-gating is also centralised in main (it won't call
// Sentry.init() there if SENTRY_DSN env is empty, and without that init
// this renderer integration stays inert).
// Renderer has no process.env — VITE_SENTRY_DSN is baked in at build time
// from the GitHub Secret via electron.vite.config.ts `define`. The main
// process's @sentry/electron already initialised the transport, so the
// renderer init just needs to wire Vue integration — it shares scope via
// IPC. Skipping when no DSN keeps `Sentry.captureException` a no-op.
const VITE_SENTRY_DSN = (import.meta.env.VITE_SENTRY_DSN as string | undefined) || ''

// URL helper used by beforeSend below. Drops the entire query string so
// `?port=17280&token=abc...` never reaches Sentry. Falls back to the
// original input when the URL parser rejects it (relative paths, etc).
function stripQuery(raw: string): string {
  try {
    const u = new URL(raw, 'http://placeholder.invalid')
    u.search = ''
    return u.protocol === 'http:' && u.hostname === 'placeholder.invalid'
      ? u.pathname
      : u.toString()
  } catch {
    return raw.split('?')[0]
  }
}

try {
  // @sentry/electron/renderer re-exports from @sentry/browser which does
  // NOT ship vueIntegration — that one lives in @sentry/vue and we don't
  // pull it in to keep the renderer bundle lean. Vue component errors
  // are already funnelled via app.config.errorHandler below, so we only
  // need the base init to wire up the IPC scope + breadcrumbs.
  Sentry.init({
    ...(VITE_SENTRY_DSN ? { dsn: VITE_SENTRY_DSN } : {}),
    // Tag every renderer event with the same environment label as the
    // main process Sentry init (production for packaged builds, otherwise
    // development). Lets Sentry filter dashboards by deployment without
    // operators having to remember the URL/release-tag heuristic. Vite's
    // `import.meta.env.PROD` is true for `vite build` output, false for
    // `vite dev` — exactly the discriminator we want.
    environment: import.meta.env.PROD ? 'production' : 'development',
    release: typeof window.__APP_VERSION__ === 'string' ? `nks-wdc-electron@${window.__APP_VERSION__}` : undefined,
    // Drop benign daemon-disconnect noise. `Failed to fetch` from the
    // shared json() helper in api/daemon.ts fires every time the daemon
    // is restarting (factory reset, SSO callback racing the port file,
    // graceful shutdown on quit). It surfaces in the UI as the existing
    // toast/log line; Sentry agreggates each restart cycle into a
    // separate issue (one per port number that happened to be in flight)
    // and buries real bugs. Anything more interesting than a transient
    // network error keeps flowing through.
    //
    // ALSO scrub PII/credentials from breadcrumbs and request context.
    // The renderer URL frequently embeds `?port=...&token=<bearer>` —
    // earlier the entire URL flowed into Sentry, so any captured event
    // had the live daemon token in `event.request.url`, in navigation
    // breadcrumbs, in Vue route fullPath tags, etc. Mirror the main-
    // process scrub: drop user identity, strip query strings from URLs,
    // remove Authorization headers.
    beforeSend(event, hint) {
      const err = hint?.originalException
      const msg = err instanceof Error ? err.message : (typeof err === 'string' ? err : '')
      if (typeof msg === 'string' && msg.startsWith('Failed to fetch')) {
        const frames = event.exception?.values?.[0]?.stacktrace?.frames ?? []
        const fromDaemonHelper = frames.some(f =>
          (f.function === 'json' || f.function === 'fetch')
          && (f.filename ?? '').includes('daemon')
        )
        if (fromDaemonHelper) return null
      }
      // Identity wipe — packaged renderer never opts users in to PII
      if (event.user) {
        event.user = { id: undefined, email: undefined, username: undefined, ip_address: undefined }
      }
      // Strip query string from request URL (token + port live there)
      if (event.request?.url) {
        event.request.url = stripQuery(event.request.url)
      }
      // Drop Authorization header if Sentry's default integration captured it
      if (event.request?.headers && typeof event.request.headers === 'object') {
        const h = event.request.headers as Record<string, string>
        delete h.Authorization
        delete h.authorization
      }
      // Sanitize breadcrumbs — fetch/XHR/navigation breadcrumbs all carry URLs
      if (Array.isArray(event.breadcrumbs)) {
        for (const b of event.breadcrumbs) {
          if (b.data && typeof b.data === 'object') {
            const d = b.data as Record<string, unknown>
            for (const key of ['url', 'to', 'from']) {
              const val = d[key]
              if (typeof val === 'string') d[key] = stripQuery(val)
            }
          }
        }
      }
      return event
    },
  })
  // Tag every event with the active route so crash triage knows which
  // page the user was on when it blew up.
  router.afterEach((to) => {
    Sentry.setTag('route', to.name?.toString() || to.path)
    Sentry.addBreadcrumb({ category: 'navigation', message: `→ ${to.fullPath}`, level: 'info' })
  })
} catch (err) {
  // A broken Sentry init must never take down the UI.
  console.warn('[Sentry] renderer init skipped:', err)
}

// Renderer → main log bridge. preload.ts exposes `rendererLog` via
// contextBridge; we fan every console.warn/error + Vue error +
// unhandled rejection through it so the same events also land in
// ~/.wdc/logs/electron/main.log (support can read one file instead of
// chasing DevTools output that's gone after a reload). Local console
// output stays untouched.
type RendererLogFn = (level: 'info' | 'warn' | 'error' | 'debug', args: unknown[]) => Promise<boolean>
const rendererLog: RendererLogFn | undefined =
  (window as unknown as { electronAPI?: { rendererLog?: RendererLogFn } }).electronAPI?.rendererLog
function bridge(level: 'info' | 'warn' | 'error' | 'debug', ...args: unknown[]) {
  try { rendererLog?.(level, args) } catch { /* bridge unavailable — ignore */ }
}
if (rendererLog) {
  const origWarn = console.warn.bind(console)
  const origError = console.error.bind(console)
  console.warn = (...a: unknown[]) => { bridge('warn', ...a); origWarn(...a) }
  console.error = (...a: unknown[]) => { bridge('error', ...a); origError(...a) }
}

// Global error handler — prevents white screen on component errors.
// Also funnels to Sentry (wrapped so a Sentry failure doesn't rethrow).
app.config.errorHandler = (err, instance, info) => {
  console.error('[Vue Error]', info, err)
  try {
    Sentry.withScope((scope) => {
      scope.setContext('vue', { lifecycleHook: info, componentName: instance?.$?.type?.name ?? '(anonymous)' })
      Sentry.captureException(err)
    })
  } catch {
    /* Sentry unavailable — error already logged to console above */
  }
}

// Unhandled promise rejections — Vue.errorHandler doesn't catch these,
// but Sentry does if we wire the window event.
window.addEventListener('unhandledrejection', (e) => {
  bridge('error', '[unhandledrejection]', e.reason)
  try { Sentry.captureException(e.reason) } catch { /* no-op */ }
})

app.mount('#app')
