import { test, expect } from './_fixtures'
import { readFileSync, readdirSync } from 'node:fs'
import { join, resolve } from 'node:path'
import { cleanError, errorCause, installRouteOf, portsLabel, stateKeyOf, stateOf } from '../../../src/utils/services'
import { firstErrorLine, logLevelOf, matchesLogLevel } from '../../../src/utils/logLevel'
import type { ServiceInfo } from '../../../src/api/types'

// Services page, service plugin pages and the shared LogViewer: the daemon
// status contract (installed / ports / version), no invented data on the
// plugin pages, and nothing that keeps polling from a hidden kept-alive page.

const srcRoot = resolve(process.cwd(), 'src')
const read = (...parts: string[]) => readFileSync(join(srcRoot, ...parts), 'utf-8')
const locale = (lang: string) => JSON.parse(read('locales', `${lang}.json`)) as Record<string, Record<string, unknown>>

const svc = (over: Partial<ServiceInfo>): ServiceInfo =>
  ({ id: 'apache', pid: null, cpuPercent: 0, memoryBytes: 0, ...over })

test.describe('Service status helpers', () => {
  test('a service whose binary is missing is notInstalled, whatever its state', () => {
    expect(stateOf(svc({ state: 0, installed: false }))).toBe('notInstalled')
    expect(stateOf(svc({ state: 4, installed: false }))).toBe('notInstalled')
    expect(stateOf(svc({ state: 0, installed: true }))).toBe('stopped')
    // Older daemons send no `installed`: treat as installed.
    expect(stateOf(svc({ state: 2 }))).toBe('running')
    expect(stateOf(svc({ status: 'crashed' }))).toBe('crashed')
    expect(stateOf(null)).toBe('stopped')
  })

  test('a crash before the service ever ran reads as a failed start', () => {
    expect(stateKeyOf(svc({ state: 4, crashKind: 'startFailed' }))).toBe('startFailed')
    expect(stateKeyOf(svc({ state: 4, crashKind: 'exited' }))).toBe('crashed')
    // A start that threw (also auto-start) leaves the service stopped with the reason.
    expect(stateKeyOf(svc({ state: 0, startError: 'mailpit.exe not found' }))).toBe('startFailed')
  })

  test('Install opens the binary the daemon names for the service', () => {
    expect(installRouteOf(svc({ id: 'cloudflare', binaryApp: 'cloudflared' }))).toEqual({ path: '/binaries', query: { app: 'cloudflared' } })
    expect(installRouteOf(svc({ id: 'redis' }))).toEqual({ path: '/binaries', query: { app: 'redis' } })
  })

  test('ports come from the daemon, never from a built-in table', () => {
    expect(portsLabel(svc({}))).toBeNull()
    expect(portsLabel(svc({ port: 3306, ports: { port: 3306 } }))).toBe('3306')
    expect(portsLabel(svc({ port: 8025, ports: { smtp: 1025, http: 8025 } }))).toBe('SMTP 1025 · HTTP 8025')
  })

  test('daemon errors lose the HTTP prefix and known causes are named', () => {
    expect(cleanError(new Error('HTTP 500 Internal Server Error: httpd.exe not found'))).toBe('httpd.exe not found')
    expect(errorCause('(OS 10048)Only one usage of each socket address is normally permitted.')).toBe('port')
    expect(errorCause('AH00526: Syntax error on line 12 of httpd.conf')).toBe('config')
    expect(errorCause('something else')).toBeNull()
  })
})

test.describe('Log level filter', () => {
  test('levels as the server binaries print them', () => {
    expect(logLevelOf('[Mon Oct 27 10:00:00 2026] [core:error] [pid 1] AH00015: Unable to open logs')).toBe('error')
    expect(logLevelOf('2026-10-27T10:00:00Z 0 [Warning] [MY-010068] CA certificate is self signed.')).toBe('warn')
    expect(logLevelOf('2026-10-27 10:00:00 CET LOG:  database system is ready')).toBe('info')
    expect(logLevelOf('PHP Fatal error:  Uncaught Error')).toBe('error')
    expect(logLevelOf('plain line without a level')).toBeNull()
    // The plugin's stream and file tags say nothing about severity.
    expect(logLevelOf('[ERR] 2026-10-27T17:41:04Z 0 [System] [MY-015015] [Server] MySQL Server - start.')).toBe('info')
    expect(logLevelOf('[error.log] [Tue Oct 27 18:42:06 2026] [mpm_winnt:notice] [pid 6296] AH00418: Parent: Created child')).toBe('info')
    expect(logLevelOf('[error.log] [Tue Oct 27 18:42:06 2026] [mpm_winnt:crit] [pid 6296] AH02538: Child: Parent process exited')).toBe('error')
  })

  test('"warn" keeps warnings and errors, "all" keeps everything', () => {
    expect(matchesLogLevel('[ssl:warn] AH01909', 'warn')).toBe(true)
    expect(matchesLogLevel('[core:error] boom', 'warn')).toBe(true)
    expect(matchesLogLevel('[mpm_winnt:notice] started', 'warn')).toBe(false)
    expect(matchesLogLevel('plain line', 'all')).toBe(true)
    expect(matchesLogLevel('plain line', 'info')).toBe(false)
  })

  test('the first error line of a crashed service is its likely reason', () => {
    const tail = [
      '[ERR] 2026-10-27T17:41:05Z 1 [System] [MY-013576] [InnoDB] InnoDB initialization has started.',
      '[ERR] 2026-10-27T17:41:05Z 1 [ERROR] [MY-012960] [InnoDB] Cannot create redo log files.',
      '[ERR] 2026-10-27T17:41:05Z 0 [ERROR] [MY-010119] [Server] Aborting',
    ]
    expect(firstErrorLine(tail)).toBe('2026-10-27T17:41:05Z 1 [ERROR] [MY-012960] [InnoDB] Cannot create redo log files.')
    expect(firstErrorLine(['[ERR] 2026-10-27 0 [System] start'])).toBeNull()
  })
})

test.describe('Services UI contract', () => {
  test('a page that fails to render gets a recoverable message, the cache is bounded', () => {
    const app = read('App.vue')
    expect(app).toMatch(/<PageErrorBoundary>\s*<keep-alive[^>]*:max="\d+"/)
    expect(read('components', 'shared', 'PageErrorBoundary.vue')).toMatch(/onErrorCaptured/)
  })

  test('LogViewer stops its watch lease and SSE while its page is hidden', () => {
    const viewer = read('components', 'shared', 'LogViewer.vue')
    expect(viewer).toMatch(/onDeactivated\(stop\)/)
    expect(viewer).toMatch(/onActivated\(/)
    expect(viewer).toMatch(/matchesLogLevel\(line, logLevel\.value\)/)
  })

  test('not installed means Install, linked to that app on Binaries', () => {
    for (const file of [['pages', 'ServicesPage.vue'], ['shared', 'ServicePageHead.vue']]) {
      const source = read('components', ...file)
      expect(source).toMatch(/notInstalled/)
      expect(source).toMatch(/installRouteOf\(/)
    }
  })

  test('the summary sentence counts the same services as the "All" filter', () => {
    const page = read('components', 'pages', 'ServicesPage.vue')
    expect(page).toMatch(/const summary = computed\(\(\) => \{\s*const total = services\.value\.length/)
    expect(page).toMatch(/id: 'all' as const, label: t\('servicesPage\.filter\.all'\), count: services\.value\.length/)
  })

  test('MySQL system accounts offer no password, grant or delete actions', () => {
    const page = read('components', 'pages', 'MySqlPluginPage.vue')
    expect(page).toMatch(/v-if="isSystemUser\(row\)"[\s\S]*?v-else class="table-actions"/)
    expect(page).toMatch(/\^\(mysql\|mariadb\)\\./)
  })

  test('a missing binary is shown as "not installed", not as a failed start', () => {
    expect(read('components', 'shared', 'ServiceErrorNote.vue')).toMatch(/installed === false \? '' : service\.value\?\.startError/)
  })

  test('the PHP page reads the version for new sites from isActive, like PhpManager', () => {
    const page = read('components', 'pages', 'PhpPluginPage.vue')
    expect(page).toContain('ver.isActive ?? ver.isDefault')
    expect(page).not.toMatch(/v\.isDefault\)|ver\.isDefault"/)
  })

  test('/service/:id opens the Services page with that service selected', () => {
    expect(read('router', 'index.ts')).toMatch(/path: '\/service\/:id', redirect: to => \(\{ path: '\/services', query: \{ service:/)
  })

  test('plugin pages show no invented values or developer placeholders', () => {
    const pages = readdirSync(join(srcRoot, 'components', 'pages')).filter(f => f.endsWith('PluginPage.vue'))
    expect(pages.length).toBeGreaterThanOrEqual(6)
    for (const page of pages) {
      const source = read('components', 'pages', page)
      expect(source, page).not.toMatch(/tuningPending|configPending|fpmUnknown|query_cache_size|maxRequestWorkers|startService|stopService/)
      expect(source, page).not.toMatch(/\?\? (80|443|1025|3306|5432|6379|8025)\b/)
    }
    for (const lang of ['cs', 'en']) {
      expect(read('locales', `${lang}.json`), lang).not.toMatch(/Backend endpoint/)
    }
  })

  test('every service state has a word and a sentence in both locales', () => {
    const states = ['stopped', 'starting', 'running', 'stopping', 'crashed', 'disabled', 'notInstalled', 'startFailed']
    for (const lang of ['cs', 'en']) {
      const page = locale(lang).servicesPage as Record<string, Record<string, string>>
      for (const s of states) {
        expect(page.status[s], `${lang} status.${s}`).toBeTruthy()
        expect(page.explain[s], `${lang} explain.${s}`).toBeTruthy()
      }
    }
  })
})
