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
})
