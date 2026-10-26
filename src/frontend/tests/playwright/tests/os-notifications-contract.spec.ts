import { test, expect } from './_fixtures'
import { readFileSync } from 'node:fs'
import { join, resolve } from 'node:path'
import {
  NOTIFICATION_CHANNELS,
  isSafeAppRoute,
  mergeNotificationPrefs,
  defaultNotificationPrefs,
} from '../../../src/services/osNotificationShared'

// Source-level contract for native OS notifications (#147). Locks the
// platform identity (Windows AppUserModelID), the click-route validation in
// main, the click → focus → `navigate` IPC → router.push wiring, the i18n
// coverage of every channel and the Settings → Notifications section.

const root = process.cwd()
const srcRoot = resolve(root, 'src')
const read = (...parts: string[]) => readFileSync(join(root, ...parts), 'utf-8')

type Locale = {
  settings?: { tabs?: Record<string, string> }
  osNotifications?: {
    channels?: Record<string, { label?: string; description?: string }>
    settings?: Record<string, string>
    events?: Record<string, string>
  }
}
const en = JSON.parse(readFileSync(join(srcRoot, 'locales', 'en.json'), 'utf-8')) as Locale
const cs = JSON.parse(readFileSync(join(srcRoot, 'locales', 'cs.json'), 'utf-8')) as Locale

test.describe('OS notifications contract', () => {
  test('Windows AppUserModelID matches the electron-builder appId and is set early', () => {
    const builder = read('electron-builder.yml')
    const appId = builder.match(/^appId:\s*(\S+)/m)?.[1]
    expect(appId, 'appId in electron-builder.yml').toBeTruthy()

    const notifications = read('electron', 'notifications.ts')
    expect(notifications).toContain(`export const APP_ID = '${appId}'`)
    // Packaged builds use the installer shortcut's AUMID; dev uses execPath.
    expect(notifications).toMatch(/app\.setAppUserModelId\(\s*app\.isPackaged \? APP_ID : process\.execPath\s*\)/)
    expect(notifications).toMatch(/process\.platform === 'win32'[\s\S]{0,80}setAppUserModelId/)

    const productName = builder.match(/^productName:\s*(.+)$/m)?.[1]?.trim()
    expect(notifications).toContain(`export const PRODUCT_NAME = '${productName}'`)

    const main = read('electron', 'main.ts')
    const identityCall = main.indexOf('configureNotificationIdentity()')
    expect(identityCall, 'main.ts calls configureNotificationIdentity()').toBeGreaterThan(-1)
    // Before the single-instance lock, the first window and the first toast.
    expect(identityCall).toBeLessThan(main.indexOf('app.requestSingleInstanceLock()'))
    expect(identityCall).toBeLessThan(main.indexOf('app.whenReady()'))
    // Main must not construct Notifications itself any more — everything goes
    // through showOsNotification so prefs/throttle/click routing apply.
    expect(main).not.toMatch(/new Notification\(/)
  })

  test('platform options: Linux icon + urgency, sticky critical toasts on Windows/Linux', () => {
    const notifications = read('electron', 'notifications.ts')
    expect(notifications).toMatch(/process\.platform === 'linux'\)\s*{\s*options\.urgency = urgency/)
    expect(notifications).toMatch(/options\.icon = icon/)
    expect(notifications).toMatch(/critical && \(process\.platform === 'win32' \|\| process\.platform === 'linux'\)\)\s*{\s*options\.timeoutType = 'never'/)
    expect(notifications).toContain('Notification.isSupported()')
    // Packaged Linux builds need icon.png shipped next to the tray icons.
    expect(read('electron-builder.yml')).toMatch(/- "icon\.png"/)
  })

  test('route validation accepts app routes and rejects external targets', () => {
    for (const ok of ['/services', '/services?filter=problems', '/sites/blog.loc/edit?tab=deploy', '/mcp/intents']) {
      expect(isSafeAppRoute(ok), ok).toBe(true)
    }
    for (const bad of [
      '', 'services', '//evil.example/x', '/a//b', 'https://evil.example', '/x?u=https://evil.example',
      'javascript:alert(1)', '/javascript:alert(1)', '\\\\server\\share', '/a\\b', '/a\nb', 42, null, undefined,
      `/${'a'.repeat(600)}`,
    ]) {
      expect(isSafeAppRoute(bad), String(bad)).toBe(false)
    }
  })

  test('main validates the click route before showing and before navigating', () => {
    const notifications = read('electron', 'notifications.ts')
    expect(notifications).toMatch(/import \{[\s\S]*isSafeAppRoute[\s\S]*\} from '\.\.\/src\/services\/osNotificationShared'/)
    expect(notifications).toMatch(/const route = isSafeAppRoute\(payload\.route\) \? payload\.route : undefined/)
    const focusFn = notifications.match(/export function focusMainWindowAndNavigate\([^)]*\)[^{]*{([\s\S]*?)\n}/)
    expect(focusFn, 'focusMainWindowAndNavigate present').not.toBeNull()
    const body = focusFn![1]
    expect(body).toMatch(/w\.restore\(\)/)
    expect(body).toMatch(/w\.show\(\)/)
    expect(body).toMatch(/w\.focus\(\)/)
    expect(body.indexOf('isSafeAppRoute(route)')).toBeLessThan(body.indexOf("webContents.send('navigate'"))
  })

  test('notification click → focus window → navigate IPC → router.push', () => {
    const notifications = read('electron', 'notifications.ts')
    // Strong references until close/click so the click handler isn't GC'd.
    expect(notifications).toMatch(/const liveNotifications = new Set<Notification>\(\)/)
    expect(notifications).toMatch(/liveNotifications\.add\(n\)/)
    expect(notifications).toMatch(/n\.on\('click', \(\) => {\s*release\(\)\s*focusMainWindowAndNavigate\(route\)/)
    expect(notifications).toMatch(/n\.on\('close', release\)/)

    const main = read('electron', 'main.ts')
    expect(main).toMatch(/ipcMain\.handle\('os-notify', [\s\S]*?showOsNotification\(payload\)/)
    expect(main).toMatch(/ipcMain\.handle\('notify-prefs-get'/)
    expect(main).toMatch(/ipcMain\.handle\('notify-prefs-set'/)

    const preload = read('electron', 'preload.ts')
    expect(preload).toMatch(/onNavigate: \(handler: \(route: string\) => void\) => {[\s\S]*?ipcRenderer\.on\('navigate', listener\)[\s\S]*?removeListener\('navigate', listener\)/)
    expect(preload).toMatch(/notifyPrefsGet: \(\) => ipcRenderer\.invoke\('notify-prefs-get'\)/)
    expect(preload).toMatch(/notifyPrefsSet: \(patch[^)]*\) => ipcRenderer\.invoke\('notify-prefs-set', patch\)/)

    const app = readFileSync(join(srcRoot, 'App.vue'), 'utf-8')
    expect(app).toMatch(/window\.electronAPI\.onNavigate\(\(target\) => {\s*if \(!isSafeAppRoute\(target\)\) return\s*router\.push\(target\)/)
  })

  test('every SSE event source is wired to its channel', () => {
    const app = readFileSync(join(srcRoot, 'App.vue'), 'utf-8')
    for (const [event, handler] of [
      ["'service:state'", 'notifyServiceState('],
      ["'site:applied'", 'notifySiteApplied('],
      ["'deploy:complete'", 'notifyDeployComplete('],
      ["'mcp:confirm-request'", 'notifyMcpConfirm('],
      ["'backup:completed'", 'notifyBackupCompleted('],
      ["'backup:failed'", 'notifyBackupFailed('],
    ]) {
      expect(app, event).toContain(event)
      expect(app, handler).toContain(handler)
    }
    expect(app).toContain('notifyUpdateAvailable(')

    const events = readFileSync(join(srcRoot, 'services', 'osNotificationEvents.ts'), 'utf-8')
    for (const ch of ['services', 'sites', 'deploy', 'mcp', 'backup', 'updates']) {
      expect(events, `channel ${ch}`).toContain(`channel: '${ch}'`)
    }
    expect(events).toContain("route: '/services'")
    expect(events).toContain('route: `/sites/${encodeURIComponent(result.domain)}/edit`')
    expect(events).toContain("route: '/mcp/intents'")
    expect(events).toMatch(/key: `update:\$\{version\}`,\s*once: true/)

    const main = read('electron', 'main.ts')
    expect(main).toMatch(/channel: 'daemon'/)
    expect(main).toMatch(/channel: 'updates'[\s\S]*?once: true/)

    // The daemon broadcasts backup outcomes from both the scheduler and the API.
    const daemonRoot = resolve(root, '..', 'daemon', 'NKS.WebDevConsole.Daemon', 'Backup')
    const scheduler = readFileSync(join(daemonRoot, 'BackupScheduler.cs'), 'utf-8')
    const endpoints = readFileSync(join(daemonRoot, 'BackupEndpoints.cs'), 'utf-8')
    for (const source of [scheduler, endpoints]) {
      expect(source).toContain('"backup:completed"')
      expect(source).toContain('"backup:failed"')
    }
  })

  test('every channel has en and cs label + description', () => {
    expect(NOTIFICATION_CHANNELS.length).toBeGreaterThanOrEqual(8)
    for (const ch of NOTIFICATION_CHANNELS) {
      for (const [name, locale] of [['en', en], ['cs', cs]] as const) {
        expect(locale.osNotifications?.channels?.[ch]?.label, `${name} ${ch}.label`).toBeTruthy()
        expect(locale.osNotifications?.channels?.[ch]?.description, `${name} ${ch}.description`).toBeTruthy()
      }
    }
    // Same keys in both locales for the settings and event texts.
    for (const group of ['settings', 'events'] as const) {
      const enKeys = Object.keys(en.osNotifications?.[group] ?? {}).sort()
      const csKeys = Object.keys(cs.osNotifications?.[group] ?? {}).sort()
      expect(enKeys.length, `${group} keys`).toBeGreaterThan(0)
      expect(csKeys).toEqual(enKeys)
    }
    // Czech copy is real Czech, not an untranslated copy.
    expect(cs.osNotifications!.settings!.backgroundOnlyLabel).toMatch(/[áčďéěíňóřšťúůýž]/i)
    expect(cs.osNotifications!.settings!.backgroundOnlyLabel).not.toBe(en.osNotifications!.settings!.backgroundOnlyLabel)
  })

  test('preferences: master switch, background-only default on, legacy migration', () => {
    const defaults = defaultNotificationPrefs()
    expect(defaults.enabled).toBe(true)
    expect(defaults.backgroundOnly).toBe(true)
    for (const ch of NOTIFICATION_CHANNELS) expect(defaults.channels[ch]).toBe(true)
    // Unknown keys and wrong types are dropped.
    const merged = mergeNotificationPrefs(defaults, { enabled: 'no', channels: { deploy: false, evil: true }, locale: 'de' })
    expect(merged.enabled).toBe(true)
    expect(merged.channels.deploy).toBe(false)
    expect((merged.channels as Record<string, boolean>).evil).toBeUndefined()
    expect(merged.locale).toBe('en')

    const notifications = read('electron', 'notifications.ts')
    expect(notifications).toContain("'notification-prefs.json'")
    expect(notifications).toMatch(/const THROTTLE_MS = 60_000/)
    expect(notifications).toMatch(/prefs\.backgroundOnly && !critical && isMainWindowFocused\(\)/)

    const service = readFileSync(join(srcRoot, 'services', 'osNotifications.ts'), 'utf-8')
    expect(service).toContain("const LEGACY_PREF_PREFIX = 'wdc.osnotify.'")
    expect(service).toMatch(/if \(!prefs\.legacyMigrated\)/)
    expect(service).toMatch(/clearLegacyToggles\(\)/)
  })

  test('Settings has a Notifications section in both Simple and Advanced modes', () => {
    const settings = readFileSync(join(srcRoot, 'components', 'pages', 'Settings.vue'), 'utf-8')
    // Advanced: registered with SettingsShell and rendered as a section.
    expect(settings).toMatch(/{ name: 'notifications', label: t\('settings\.tabs\.notifications'\), group: 'app' }/)
    expect(settings).toMatch(/<section v-show="activeTab === 'notifications'"[\s\S]*?<NotificationSettings :t="t" \/>/)
    // Not hidden in Simple mode.
    const advancedOnly = settings.match(/const ADVANCED_ONLY_TABS = new Set\(\[([^\]]*)\]\)/)?.[1] ?? ''
    expect(advancedOnly).not.toContain("'notifications'")
    // Simple: rendered inside the simple grid (before the SettingsShell v-else).
    const simpleGrid = settings.slice(0, settings.indexOf('<SettingsShell'))
    expect(simpleGrid).toContain('<NotificationSettings :t="t" />')
    expect(en.settings?.tabs?.notifications).toBeTruthy()
    expect(cs.settings?.tabs?.notifications).toBeTruthy()

    const component = readFileSync(join(srcRoot, 'components', 'settings', 'shared', 'NotificationSettings.vue'), 'utf-8')
    for (const id of ['notify-master', 'notify-background-only', 'notify-test']) {
      expect(component, id).toContain(`data-testid="${id}"`)
    }
    expect(component).toMatch(/v-for="ch in notificationChannels"/)
    expect(component).toContain("t(`osNotifications.channels.${ch}.description`)")
    expect(component).toContain('sendTestNotification(')
  })
})
