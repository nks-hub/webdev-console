import { test, expect } from './_fixtures'
import { readFileSync } from 'node:fs'
import { join, resolve } from 'node:path'
import { compareSemver, versionCore } from '../../../src/utils/semver'

// Settings → Update shows what electron-updater is doing and installs
// through it. It used to compare 0.5.0 against the running 0.5.0-<sha>
// (a "new version" every time), had no link to the updater at all and
// "Download and install" only opened the setup .exe in a browser.

const root = process.cwd()
const read = (...parts: string[]) => readFileSync(join(resolve(root), ...parts), 'utf-8')

test.describe('Settings update contract', () => {
  test('a build of the latest release is not offered that release again', () => {
    expect(compareSemver(versionCore('0.5.0'), versionCore('0.5.0-6e25b70'))).toBe(0)
    expect(compareSemver(versionCore('0.5.1'), versionCore('0.5.0-6e25b70'))).toBe(1)
    const settings = read('src', 'components', 'pages', 'Settings.vue')
    expect(settings).toMatch(/compareSemver\(versionCore\(latestVersion\.value\), versionCore\(currentVersion\.value\)\)/)
  })

  test('main pushes updater state and installs on request', () => {
    const main = read('electron', 'main.ts')
    expect(main).toMatch(/win\.webContents\.send\('updater-status', updaterState\)/)
    expect(main).toMatch(/autoUpdater\.on\('download-progress'[\s\S]*?setUpdaterState\(\{/)
    expect(main).toMatch(/autoUpdater\.on\('error'[\s\S]*?setUpdaterState\(\{ status: 'error'/)
    expect(main).toMatch(/ipcMain\.handle\('updater-install'[\s\S]*?installUpdateAndRestart\(\)/)
    const preload = read('electron', 'preload.ts')
    expect(preload).toMatch(/ipcRenderer\.on\('updater-status', listener\)/)
    expect(preload).toMatch(/updaterInstall: \(\): Promise<boolean> => ipcRenderer\.invoke\('updater-install'\)/)
  })

  test('the update tab never opens a raw installer', () => {
    const settings = read('src', 'components', 'pages', 'Settings.vue')
    expect(settings).not.toMatch(/openExternal\(updateCheck\.downloadUrl\)/)
    const card = read('src', 'components', 'settings', 'easy', 'EasyUpdateSettings.vue')
    expect(card).toMatch(/updater\?\.status === 'downloaded'[\s\S]*?emit\('install'\)/)
    expect(card).not.toContain('downloadUrl')
  })

  // 0.5.0 and 0.5.1 shipped a publisherName their installers did not match,
  // so every download failed the signature check and the tab showed only
  // the raw error with no way forward.
  test('a failed updater offers the release page and says why', () => {
    const main = read('electron', 'main.ts')
    expect(main).toMatch(/autoUpdater\.on\('error'[\s\S]*?errorCode: \(error as \{ code\?: string \} \| null\)\?\.code \?\? null/)
    const card = read('src', 'components', 'settings', 'easy', 'EasyUpdateSettings.vue')
    expect(card).toMatch(/v-else-if="manualOnly && \(hasUpdate \|\| updater\?\.status === 'error'\)"[\s\S]*?emit\('openRelease'\)/)
    expect(card).toContain("updater.errorCode === 'ERR_UPDATER_INVALID_SIGNATURE'")
    const settings = read('src', 'components', 'pages', 'Settings.vue')
    expect(settings).toMatch(/u\.status === 'downloaded' \|\| u\.status === 'error'\) \? u\.version/)
    for (const lang of ['cs', 'en']) {
      const update = JSON.parse(read('src', 'locales', `${lang}.json`)).settings.update
      expect(update.errorHint.signature).toBeTruthy()
      expect(update.errorHint.generic).toBeTruthy()
      expect(update.errorDetail).toBeTruthy()
    }
  })

  // v0.5.4 test build: "current v0.5.4" next to "latest v0.5.3, up to date".
  test('a build ahead of the newest release says so instead of "up to date"', () => {
    expect(compareSemver(versionCore('0.5.3'), versionCore('0.5.4-a5b25f8c'))).toBe(-1)
    const card = read('src', 'components', 'settings', 'easy', 'EasyUpdateSettings.vue')
    expect(card).toMatch(/compareSemver\(versionCore\(props\.latestVersion\), versionCore\(props\.currentVersion\)\) < 0/)
    expect(card).toMatch(/<el-tag v-else-if="newerBuild" type="info"[\s\S]*?settings\.update\.newerBuild/)
    // The tag is words: it must not inherit the mono font of the version number.
    expect(card).toMatch(/<span class="mono">v\{\{ latestVersion \}\}<\/span>\s*<el-tag v-if="hasUpdate"/)
    for (const lang of ['cs', 'en'])
      expect(JSON.parse(read('src', 'locales', `${lang}.json`)).settings.update.newerBuild).toBeTruthy()
  })

  // Simple mode: a link to an Advanced-only section showed "These settings are
  // only in Advanced mode" above the General panel, as if General were meant.
  test('the advanced-only banner names the section and first-row panels do not scroll the title away', () => {
    const settings = read('src', 'components', 'pages', 'Settings.vue')
    expect(settings).toMatch(/\$t\('settings\.simple\.hiddenTab', \{ name: hiddenTabLabel \}\)/)
    expect(settings).toMatch(/if \(panel\) setTimeout\(\(\) => revealPanel\(panel\), 100\)/)
    // Measured from the page top, so a page left scrolled down by an earlier
    // link still goes back up for a first-row panel (round 8: 093, 096).
    expect(settings).toMatch(/el\.getBoundingClientRect\(\)\.top - scroller\.getBoundingClientRect\(\)\.top \+ scroller\.scrollTop/)
    expect(settings).toMatch(/if \(offset < window\.innerHeight \* 0\.6\) \(scroller \?\? window\)\.scrollTo\(\{ top: 0, behavior: 'smooth' \}\)\s*else el\.scrollIntoView/)
    for (const lang of ['cs', 'en'])
      expect(JSON.parse(read('src', 'locales', `${lang}.json`)).settings.simple.hiddenTab).toContain('{name}')
  })
})
