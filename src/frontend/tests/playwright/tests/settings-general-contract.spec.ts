import { test, expect } from './_fixtures'
import { readFileSync } from 'node:fs'
import { join, resolve } from 'node:path'

// General tab switches act on the real thing: the OS login item, the
// daemon's telemetry consent file, the default PHP setting. They used to
// write settings rows nothing read, while Sentry ran regardless of consent.

const root = process.cwd()
const read = (...parts: string[]) => readFileSync(join(resolve(root), ...parts), 'utf-8')

test.describe('Settings General contract', () => {
  test('run at startup is the Electron login item', () => {
    const main = read('electron', 'main.ts')
    expect(main).toMatch(/app\.setLoginItemSettings\(\{ openAtLogin: enabled === true, args: LOGIN_ITEM_ARGS \}\)/)
    expect(main).toMatch(/ipcMain\.handle\('autostart-get'/)
    expect(read('electron', 'preload.ts')).toMatch(/autostartSet: \(enabled: boolean\)[\s\S]*?ipcRenderer\.invoke\('autostart-set', enabled\)/)
    const settings = read('src', 'components', 'pages', 'Settings.vue')
    expect(settings).not.toContain("'general.runOnStartup'")
    expect(settings).toMatch(/@update:run-on-startup="setRunOnStartup"/)
  })

  test('crash reports need consent in main, daemon and renderer', () => {
    const main = read('electron', 'main.ts')
    expect(main).toMatch(/if \(sentryStarted \|\| !sentryDsn \|\| !hasCrashReportConsent\(\)\) return/)
    expect(main).toMatch(/beforeSend\(event\) \{\s*if \(!hasCrashReportConsent\(\)\) return null/)
    expect(main).toMatch(/daemonSentryDsn && hasCrashReportConsent\(\)/)
    const renderer = read('src', 'main.ts')
    expect(renderer).toMatch(/if \(!state\?\.active\) return/)
    const settings = read('src', 'components', 'pages', 'Settings.vue')
    expect(settings).toContain('/api/telemetry/consent')
    expect(settings).not.toContain("'telemetry.enabled'")
  })

  test('no dead path fields; databases are managed on their own page', () => {
    // Neither the daemon nor any plugin reads paths.* or backup.dir.
    const settings = read('src', 'components', 'pages', 'Settings.vue')
    for (const key of ['paths.apache', 'paths.hostsFile', 'backup.dir']) expect(settings).not.toContain(`'${key}'`)
    expect(settings).toContain('systemInfo?.dataDir')
    expect(settings).toMatch(/route\.query\.tab === 'databases'[\s\S]*?router\.replace\('\/databases'\)/)
    expect(settings).not.toMatch(/method: 'DELETE'[\s\S]{0,80}api\/databases/)
  })

  test('default PHP version is loaded and saved', () => {
    const settings = read('src', 'components', 'pages', 'Settings.vue')
    expect(settings).toMatch(/settings\['general\.defaultPhp'\]/)
    expect(settings).toMatch(/saveSettings\(\{ 'general\.defaultPhp': version \}\)/)
  })
})
