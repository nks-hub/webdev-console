import { defineConfig, externalizeDepsPlugin } from 'electron-vite'
import vue from '@vitejs/plugin-vue'
// Tailwind CSS removed — pure CSS design tokens + Element Plus components
import { resolve } from 'path'
import { readFileSync } from 'node:fs'
import { execSync } from 'node:child_process'
import { fileURLToPath } from 'node:url'

const pkg = JSON.parse(readFileSync(new URL('./package.json', import.meta.url), 'utf-8')) as { version: string }

/**
 * Resolve the current git short SHA so the displayed version becomes
 * "0.2.25-abc1234" — operators can tell at a glance which commit a
 * build came from. Falls back to "dev" when git is unavailable
 * (clean tarball install) and to "" if the env var GIT_SHORT_SHA is
 * explicitly set to empty (CI override for reproducible-build tests).
 *
 * NOTE: Use fileURLToPath() — `new URL('.', import.meta.url).pathname`
 * returns "/C:/path/..." on Windows (leading slash before drive letter)
 * which Node's child_process refuses with ENOENT, silently falling
 * through to the "dev" fallback even on a healthy git checkout.
 */
function resolveBuildSha(): string {
  // CI / reproducible-build override
  const fromEnv = process.env.GIT_SHORT_SHA
  if (fromEnv !== undefined) return fromEnv
  try {
    return execSync('git rev-parse --short HEAD', {
      cwd: fileURLToPath(new URL('.', import.meta.url)),
      stdio: ['ignore', 'pipe', 'ignore'],
    }).toString().trim()
  } catch {
    return 'dev'
  }
}

const buildSha = resolveBuildSha()
const fullVersion = buildSha ? `${pkg.version}-${buildSha}` : pkg.version

// Sentry DSNs flow from GitHub Secrets → GHA env → Vite define. Empty
// string at build time = no Sentry in shipped artifact; runtime env
// (SENTRY_DSN) still wins so self-hosters can always override.
const sentryDsnFrontend = process.env.SENTRY_DSN_FRONTEND || process.env.SENTRY_DSN || ''
const sentryDsnBackend = process.env.SENTRY_DSN_BACKEND || ''
const sentryEnv = process.env.SENTRY_ENVIRONMENT || 'production'

export default defineConfig({
  main: {
    // Externalize everything except @sentry/electron which must be
    // bundled into the main.js since we pack without node_modules
    // (asar-only layout) for local installs. electron-builder normally
    // handles this via app.asar.unpacked, but our hot-swap install
    // flow doesn't run through electron-builder.
    plugins: [externalizeDepsPlugin({
      // Every dep in this list gets bundled INTO dist-electron/main.js
      // instead of staying as a node_require('<pkg>'). Our packaged app
      // ships as an asar blob without node_modules (hot-swap install
      // flow skips electron-builder's app.asar.unpacked mechanism), so
      // an externalized dep throws `Cannot find module` at first load.
      // @sentry/* — exception reporting; electron-log — file logs with
      // rotation; electron-updater — auto-updater (was ERR_MODULE_NOT_FOUND
      // on every packaged launch → users stuck on first installed DMG
      // because `await import('electron-updater')` couldn't resolve
      // inside the asar-only layout).
      exclude: ['@sentry/electron', '@sentry/core', '@sentry/utils', '@sentry/types', '@sentry/node', 'electron-log', 'electron-updater'],
    })],
    build: {
      outDir: 'dist-electron',
      rollupOptions: {
        input: resolve(__dirname, 'electron/main.ts')
      }
    },
    // Main process reads Sentry DSN from process.env at runtime — this
    // define substitutes the build-time secret into the bundle as a
    // fallback when the env var isn't set by the user's shell.
    define: {
      'process.env.SENTRY_DSN_DEFAULT': JSON.stringify(sentryDsnFrontend),
      'process.env.SENTRY_DSN_BACKEND_DEFAULT': JSON.stringify(sentryDsnBackend),
      'process.env.SENTRY_ENVIRONMENT_DEFAULT': JSON.stringify(sentryEnv),
      // Main process needs gitSha for the app-get-version IPC handler.
      // Renderer reads VITE_APP_VERSION as a fallback only — primary path
      // is the IPC roundtrip so package.json bumps don't need a renderer
      // rebuild, just an Electron restart.
      'import.meta.env.VITE_APP_GIT_SHA': JSON.stringify(buildSha),
    },
  },
  preload: {
    plugins: [externalizeDepsPlugin()],
    build: {
      outDir: 'dist-electron',
      emptyOutDir: false,
      rollupOptions: {
        input: resolve(__dirname, 'electron/preload.ts')
      }
    }
  },
  renderer: {
    root: resolve(__dirname, 'src'),
    server: {
      // 5173 collides with sibling LiberShare dev on same workstation — use 5190 for wdc
      port: 5190,
      strictPort: false,
      host: '127.0.0.1',
    },
    define: {
      'import.meta.env.VITE_APP_VERSION': JSON.stringify(fullVersion),
      // Expose the bare semver + sha separately too so any UI surface
      // that wants to render them differently (e.g. tooltip on the
      // commit hash linking to the GitHub commit) can.
      'import.meta.env.VITE_APP_VERSION_BASE': JSON.stringify(pkg.version),
      'import.meta.env.VITE_APP_GIT_SHA': JSON.stringify(buildSha),
      'import.meta.env.VITE_SENTRY_DSN': JSON.stringify(sentryDsnFrontend),
      'import.meta.env.VITE_SENTRY_ENVIRONMENT': JSON.stringify(sentryEnv),
    },
    base: './',
    build: {
      // Keep the renderer output alongside main/preload under dist-electron/
      // so main.ts can resolve '../renderer/index.html' relative to its own
      // __dirname in the packaged app.asar. The default electron-vite layout
      // drops renderer into `out/renderer/`, which sits at a different path
      // in the packaged tree and produced a blank-window production build.
      outDir: 'dist-electron/renderer',
      emptyOutDir: true,
      rollupOptions: {
        input: resolve(__dirname, 'src/index.html')
      }
    },
    plugins: [vue()]
  }
})
