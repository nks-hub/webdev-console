import { test, expect } from './_fixtures'
import { readFileSync } from 'node:fs'
import { join, resolve } from 'node:path'

// Backups: a turned-off schedule (0) came back as 24 after a reload, and
// the opt-in MySQL / site-file content was silently left out of archives
// whose manifest still claimed it.

const read = (...parts: string[]) => readFileSync(join(resolve(process.cwd()), ...parts), 'utf-8')
const page = () => read('src', 'components', 'pages', 'BackupsPage.vue')

test.describe('Backups contract', () => {
  test('a schedule of 0 stays off after a reload', () => {
    const s = page()
    expect(s).not.toMatch(/parseInt\(s\['backup\.scheduleHours'\]\) \|\| 24/)
    expect(s).toMatch(/if \(Number\.isFinite\(hours\)\) scheduleHours\.value = /)
    // The overview shows the saved schedule, not unsaved edits.
    expect(s).toMatch(/if \(savedScheduleHours\.value <= 0\) return t\('backupsPage\.overview\.off'\)/)
    expect(s).toMatch(/if \(saved\) savedScheduleHours\.value = hours/)
  })

  test('content the daemon left out is reported, not shown as done', () => {
    expect(page()).toMatch(/:type="skippedGroups\.length \? 'warning' : 'success'"/)
    expect(page()).toMatch(/\$t\(`backupsPage\.skipped\.\$\{group\.key\}`\)/)
    const settings = read('src', 'components', 'pages', 'Settings.vue')
    expect(settings).toMatch(/if \(missing\.length\) ElMessage\.warning\(t\('settingsToast\.backupIncomplete'/)
    const events = read('src', 'services', 'osNotificationEvents.ts')
    expect(events).toMatch(/missing\.length \? 'backupIncompleteTitle' : 'backupCompletedTitle'/)
    const endpoint = read('..', 'daemon', 'NKS.WebDevConsole.Daemon', 'Backup', 'BackupEndpoints.cs')
    expect(endpoint).toMatch(/contentFlags = resultFlags\.ToString\(\), skipped = skippedJson/)
  })

  test('messages exist in both languages and Czech counts have three forms', () => {
    for (const lang of ['cs', 'en']) {
      const j = JSON.parse(read('src', 'locales', `${lang}.json`))
      for (const key of ['databases', 'docroots']) expect(j.backupsPage.skipped[key]).toBeTruthy()
      expect(j.backupsPage.overview.createdIncomplete).toBeTruthy()
      expect(j.settingsToast.backupIncomplete).toContain('{missing}')
      expect(j.osNotifications.events.backupIncompleteBody).toContain('{missing}')
    }
    const cs = JSON.parse(read('src', 'locales', 'cs.json'))
    for (const msg of [cs.backupsPage.overview.createdToast, cs.backupsPage.toast.restored, cs.settingsToast.backupCreated, cs.settingsToast.mampImported])
      expect(msg.split(' | ')).toHaveLength(3)
  })
})
