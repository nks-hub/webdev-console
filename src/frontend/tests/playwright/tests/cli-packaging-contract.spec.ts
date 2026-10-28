import { test, expect } from './_fixtures'
import { readFileSync } from 'node:fs'
import { join } from 'node:path'

// Source-level contract for shipping the `wdc` CLI with the installers:
//  - stage:cli:<os> publishes it self-contained into resources/cli and every
//    dist script runs it before electron-builder;
//  - electron-builder copies resources/cli to <resources>/cli;
//  - the NSIS installer adds that folder to PATH and the uninstaller removes it.

const frontendRoot = process.cwd()
const read = (...parts: string[]): string => readFileSync(join(frontendRoot, ...parts), 'utf-8')

test.describe('CLI packaging contract', () => {
  test('stage:cli scripts publish a self-contained CLI and feed every dist script', () => {
    const pkg = JSON.parse(read('package.json')) as { scripts: Record<string, string> }
    for (const [os, rid] of [['win', 'win-x64'], ['mac', 'osx-arm64'], ['linux', 'linux-x64']]) {
      const stage = pkg.scripts[`stage:cli:${os}`]
      expect(stage, `stage:cli:${os}`).toContain('NKS.WebDevConsole.Cli.csproj')
      expect(stage).toContain(`-r ${rid}`)
      expect(stage).toContain('--self-contained')
      expect(stage).toContain('-o resources/cli')
    }
    expect(pkg.scripts.dist).toContain('npm run stage:daemon:win && npm run stage:cli:win && npm run stage:mcp')
    expect(pkg.scripts['dist:mac']).toContain('npm run stage:daemon:mac && npm run stage:cli:mac && npm run stage:mcp')
    expect(pkg.scripts['dist:linux']).toContain('npm run stage:daemon:linux && npm run stage:cli:linux && npm run stage:mcp')
  })

  test('electron-builder ships resources/cli and includes the PATH script', () => {
    const builder = read('electron-builder.yml')
    expect(builder).toMatch(/- from: resources\/cli\r?\n\s+to: cli/)
    expect(builder).toMatch(/^nsis:[\s\S]*?\n {2}include: build\/installer\.nsh\r?$/m)
  })

  test('installer.nsh edits the user or machine PATH by install mode and cleans up on uninstall', () => {
    const nsh = read('build', 'installer.nsh')
    expect(nsh).toMatch(/!macro customInstall[\s\S]*?\$installMode == "all"[\s\S]*?wdcPathAddMachine[\s\S]*?wdcPathAddUser/)
    expect(nsh).toMatch(/!macro customUnInstall[\s\S]*?\$installMode == "all"[\s\S]*?un\.wdcPathRemoveMachine[\s\S]*?un\.wdcPathRemoveUser/)
    expect(nsh).toContain('HKLM "SYSTEM\\CurrentControlSet\\Control\\Session Manager\\Environment"')
    expect(nsh).toContain('HKCU "Environment"')
    expect(nsh).toContain('${WM_WININICHANGE}')
    expect(nsh).toContain('$INSTDIR\\resources\\cli')
  })
})
