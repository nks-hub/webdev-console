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

  // Backups can hold MySQL dumps and site files; restoring them overwrites
  // live data, so they are opt-in per restore and start unticked.
  test('restore offers dumps and site files, both off by default', () => {
    const s = page()
    expect(s).toMatch(/function restoreBackup\(b: BackupEntry\) \{\s*restoreTarget\.value = b\s*restoreDatabases\.value = false\s*restoreSiteFiles\.value = false/)
    expect(s).toMatch(/<div v-if="restoreDbNames\.length" class="restore-option">\s*<el-checkbox v-model="restoreDatabases">/)
    expect(s).toMatch(/<div v-if="restoreSites\.length" class="restore-option">\s*<el-checkbox v-model="restoreSiteFiles">/)
    expect(s).toMatch(/databases: restoreDatabases\.value && restoreDbNames\.value\.length > 0,/)
    expect(s).toMatch(/siteFiles: restoreSiteFiles\.value && restoreSites\.value\.length > 0,/)
    // What was left out is shown before the window reloads.
    expect(s).toMatch(/if \(data\.skipped\?\.length\) \{[\s\S]*?await ElMessageBox\.alert\([\s\S]*?restartDaemon\(\)/)
    const endpoint = read('..', 'daemon', 'NKS.WebDevConsole.Daemon', 'Backup', 'BackupEndpoints.cs')
    expect(endpoint).toMatch(/record RestoreRequest\(string\? Path = null, bool Databases = false, bool SiteFiles = false\)/)
    expect(endpoint).toMatch(/new\s*\{\s*path = b\.Path, size = b\.Size, createdUtc = b\.Created, contentFlags = b\.ContentFlags\.ToString\(\),\s*databases = b\.Databases,/)
  })

  // The backup_restore intent does not name the content, so an approved
  // "restore the configuration" must not turn into a database overwrite.
  test('an MCP token cannot restore databases or site files', () => {
    const endpoint = read('..', 'daemon', 'NKS.WebDevConsole.Daemon', 'Backup', 'BackupEndpoints.cs')
    expect(endpoint).toMatch(/McpCallerContext\.IsMcpToken && \(options\.Databases \|\| options\.SiteFiles\)/)
    expect(endpoint.match(/if \(McpDataRestoreForbidden\(options\) is \{ \} forbidden\) return forbidden;/g)).toHaveLength(2)
    const byId = endpoint.slice(endpoint.indexOf('"/api/backup/{id}/restore"'))
    expect(byId.indexOf('McpDataRestoreForbidden(options)')).toBeLessThan(byId.indexOf('ValidateAndConsumeAsync'))
  })

  test('restore dialog strings exist in both languages', () => {
    const cs = JSON.parse(read('src', 'locales', 'cs.json'))
    expect(cs.backupsPage.restore.databases).toBe('Obnovit databáze (přepíše jejich obsah)')
    expect(cs.backupsPage.restore.siteFiles).toBe('Obnovit soubory webů (přepíše složky webů)')
    for (const lang of ['cs', 'en']) {
      const r = JSON.parse(read('src', 'locales', `${lang}.json`)).backupsPage.restore
      for (const key of ['noData', 'optional', 'databases', 'siteFiles', 'siteFilesHint', 'incompleteTitle', 'continue', 'doneDatabases', 'doneSites'])
        expect(r[key], `${lang} ${key}`).toBeTruthy()
      for (const key of ['databases', 'docroots']) expect(r.skipped[key]).toBeTruthy()
    }
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
