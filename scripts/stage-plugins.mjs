#!/usr/bin/env node
/*
 * Stage plugin DLLs into the packaged daemon layout.
 *
 * Copies plugin build artifacts into
 * <repo>/src/frontend/resources/daemon/plugins so the daemon (running
 * from the packaged app at runtime) finds them next to its own exe.
 *
 * The plugin source used to live under <repo>/src/plugins/ (F99). It has
 * since moved to nks-hub/webdev-console-plugins as the source of truth;
 * built DLLs are published per-plugin on that repo's GitHub Releases as
 * `<plugin-id>-v<version>` tags with a `<ProjectName>.zip` asset each.
 *
 * Resolution order, first hit wins:
 *
 *   1. <repo>/../webdev-console-plugins/NKS.WebDevConsole.Plugin.<X>/bin/Release/net9.0
 *      — sibling checkout of the plugins repo with a local
 *      `dotnet build -c Release`. This is the dev workflow: clone the
 *      plugins repo next to nks-ws, hack on it, build, run nks-ws's
 *      dist:mac/win/linux and the edits ride along.
 *
 *   2. GitHub Releases of nks-hub/webdev-console-plugins — the latest
 *      release of each plugin ID is downloaded and extracted. Used by
 *      CI (nks-ws/.github/workflows/build.yml) and by contributors who
 *      don't have the plugins repo cloned. Runs the first time on any
 *      fresh checkout. Skipped when WDC_SKIP_PLUGIN_DOWNLOAD=1 is set.
 *
 *   3. Empty (no plugins staged) — daemon's F95 runtime fallback will
 *      download from catalog-api into ~/.wdc/plugins on first run.
 *
 * Each plugin that ships a plugin.json lands in its own folder
 * (plugins/<ProjectName>/) because the daemon reads the manifest from the
 * DLL's folder; builds without a manifest stay at the top level.
 *
 * The target dir is wiped first so a removed plugin doesn't linger in
 * the packaged bundle. Safe to re-run.
 */
import { existsSync, mkdirSync, mkdtempSync, readdirSync, copyFileSync, rmSync, statSync, createWriteStream, writeFileSync } from 'node:fs'
import { basename, dirname, join, resolve } from 'node:path'
import { fileURLToPath } from 'node:url'
import { execSync } from 'node:child_process'
import { tmpdir } from 'node:os'
import { pipeline } from 'node:stream/promises'
import { Readable } from 'node:stream'

const __dirname = dirname(fileURLToPath(import.meta.url))
const repoRoot = resolve(__dirname, '..')

if (process.argv.includes('--help') || process.argv.includes('-h')) {
  console.log(`Usage: node scripts/stage-plugins.mjs

Stages plugin DLLs and nksdeploy.phar into the packaged daemon layout
at <repo>/src/frontend/resources/daemon/plugins/.

Resolution order (first hit wins):
  1. <repo>/build/plugins when WDC_PLUGIN_STAGE_SOURCE=build
  2. <repo>/../webdev-console-plugins (sibling clone with bin/Release builds)
  3. nks-hub/webdev-console-plugins GitHub Releases (downloaded fresh)

Environment variables:
  WDC_PLUGIN_STAGE_SOURCE=build force <repo>/build/plugins manual override
  WDC_SKIP_PLUGIN_DOWNLOAD=1   skip GitHub release download (offline)
  WDC_SKIP_PHAR_BUILD=1        skip the chained nksdeploy.phar build
  GITHUB_TOKEN=<pat>           bump GitHub API rate limits (5000/hr vs 60/hr)
  CI=true                      fail loudly if no plugins are staged`)
  process.exit(0)
}

if (process.argv.includes('--self-test')) {
  const { deepStrictEqual } = await import('node:assert/strict')
  const zip = name => ({ name: `NKS.WebDevConsole.Plugin.${name}.zip`, browser_download_url: name })
  const picked = pickPluginZips([
    { tag_name: 'nks.wdc.apache-v1.0.13', created_at: '2026-05-21', assets: [zip('Apache')] },
    { tag_name: 'nks.wdc.deploy.localrsync-v0.1.0', created_at: '2026-05-04', assets: [zip('LocalRsync')] },
    { tag_name: 'nks.wdc.next-v2.0.0', created_at: '2026-06-01', prerelease: true, assets: [zip('Apache')] },
    { tag_name: 'v0.1.0', created_at: '2026-04-20', assets: [zip('Apache'), zip('Cloudflare'), zip('Composer')] },
  ])
  deepStrictEqual(
    Object.fromEntries([...picked].map(([name, p]) => [name, p.release.tag_name])),
    {
      'NKS.WebDevConsole.Plugin.Apache.zip': 'nks.wdc.apache-v1.0.13',
      'NKS.WebDevConsole.Plugin.LocalRsync.zip': 'nks.wdc.deploy.localrsync-v0.1.0',
      'NKS.WebDevConsole.Plugin.Cloudflare.zip': 'v0.1.0',
      'NKS.WebDevConsole.Plugin.Composer.zip': 'v0.1.0',
    })
  deepStrictEqual(
    missingExpectedPlugins(expectedPluginDlls().filter(d => !d.includes('Composer'))),
    ['NKS.WebDevConsole.Plugin.Composer.dll'])
  console.log('[stage-plugins] self-test passed')
  process.exit(0)
}

const destDir = join(repoRoot, 'src', 'frontend', 'resources', 'daemon', 'plugins')
const PLUGINS_REPO = 'nks-hub/webdev-console-plugins'

if (existsSync(destDir)) {
  rmSync(destDir, { recursive: true, force: true })
}
mkdirSync(destDir, { recursive: true })

function copyRecursive(from, to) {
  mkdirSync(to, { recursive: true })
  for (const name of readdirSync(from)) {
    const src = join(from, name)
    const dst = join(to, name)
    const s = statSync(src)
    if (s.isDirectory()) copyRecursive(src, dst)
    else copyFileSync(src, dst)
  }
}

function listPluginDlls(dir) {
  if (!existsSync(dir)) return []
  try {
    return readdirSync(dir).filter(n =>
      n.startsWith('NKS.WebDevConsole.Plugin.') && n.endsWith('.dll'))
  } catch {
    return []
  }
}

/**
 * Copies one plugin build into the bundle. A build with its own plugin.json
 * gets its own folder: the daemon reads plugin.json from the DLL's folder,
 * so in one flat folder every plugin got whichever plugin.json was copied
 * last (wrong id, description, and uninstall blacklist match). Builds
 * without plugin.json stay at the top level, where the loader finds them.
 */
function stagePluginBuild(fromDir, projectName) {
  const to = existsSync(join(fromDir, 'plugin.json')) ? join(destDir, projectName) : destDir
  copyRecursive(fromDir, to)
  return to
}

/** Staged plugin DLLs at the top level and one folder down: [{ name, dir }]. */
function stagedPluginDlls() {
  const dirs = [destDir, ...readdirSync(destDir)
    .map(n => join(destDir, n))
    .filter(p => statSync(p).isDirectory())]
  return dirs.flatMap(dir => listPluginDlls(dir)
    .filter(n => n !== 'NKS.WebDevConsole.Plugin.SDK.dll')
    .map(name => ({ name, dir })))
}

let sourceUsed = null

// Source 0: monorepo dotnet build output. Kept as an escape hatch in
// case a developer or CI job drops plugin DLLs here manually, but no
// longer produced by the monorepo build — src/plugins/ was removed in
// favour of the nks-hub/webdev-console-plugins repo. Expected to be
// empty on normal checkouts. It is opt-in because stale files here can
// otherwise mask a freshly-built sibling plugin checkout.
const monorepoBuildDir = join(repoRoot, 'build', 'plugins')
if (process.env.WDC_PLUGIN_STAGE_SOURCE === 'build' && listPluginDlls(monorepoBuildDir).length > 0) {
  copyRecursive(monorepoBuildDir, destDir)
  sourceUsed = monorepoBuildDir
}

// Source 1: sibling checkout of webdev-console-plugins repo.
const siblingRoot = resolve(repoRoot, '..', 'webdev-console-plugins')
let siblingEntries = []
try {
  if (!sourceUsed && existsSync(siblingRoot)) siblingEntries = readdirSync(siblingRoot)
} catch (err) {
  // An unreadable sibling (EPERM, locked by another process) must not stop the
  // build; fall through to the GitHub release download below.
  console.warn(`[stage-plugins] cannot read ${siblingRoot} (${err.code ?? err.message}), using GitHub releases`)
}
if (!sourceUsed && siblingEntries.length > 0) {
  const pluginProjectDirs = siblingEntries
    .filter(n => n.startsWith('NKS.WebDevConsole.Plugin.'))
    .map(n => join(siblingRoot, n, 'bin', 'Release', 'net9.0'))
    .filter(p => listPluginDlls(p).length > 0)
  if (pluginProjectDirs.length > 0) {
    for (const p of pluginProjectDirs) stagePluginBuild(p, basename(resolve(p, '..', '..', '..')))
    sourceUsed = siblingRoot
  }
}

// Source 2: download from GitHub releases.
// Skippable via WDC_SKIP_PLUGIN_DOWNLOAD so CI can run without network,
// or when the builder has already staged plugins by other means.
//
// Per plugin zip (asset `NKS.WebDevConsole.Plugin.<X>.zip`) the newest
// release that carries it wins. Plugins only get a per-plugin release
// (`<plugin-id>-v<ver>`) when their plugin.json version is bumped, so a
// plugin that was never bumped (Cloudflare, Composer) exists only in the
// all-plugins bundle release (`v0.1.0`) and has to come from there.
if (!sourceUsed && process.env.WDC_SKIP_PLUGIN_DOWNLOAD !== '1') {
  try {
    const releases = []
    for (let page = 1; ; page++) {
      const batch = await fetchGithubJson(`/repos/${PLUGINS_REPO}/releases?per_page=100&page=${page}`)
      if (!Array.isArray(batch)) break
      releases.push(...batch)
      if (batch.length < 100) break
    }
    const pick = pickPluginZips(releases)
    if (pick.size === 0) {
      throw new Error(`no plugin zips in ${PLUGINS_REPO} releases`)
    }
    // Oldest first, so shared dependency DLLs from newer builds overwrite
    // older copies at the top level rather than the other way round.
    const ordered = [...pick.values()].sort((a, b) => new Date(a.release.created_at) - new Date(b.release.created_at))
    const extractRoot = mkdtempSync(join(tmpdir(), 'wdc-plugins-'))
    for (const { asset, release } of ordered) {
      const zipPath = join(tmpdir(), `${release.tag_name}-${asset.name}`)
      await downloadToFile(asset.browser_download_url, zipPath)
      const projectName = asset.name.replace(/\.zip$/, '')
      const extracted = join(extractRoot, projectName)
      execSync(`unzip -o -q "${zipPath}" -d "${extracted}"`)
      // Most release zips carry no plugin.json; take it from the plugins repo
      // at the release tag so the manifest matches the build.
      const manifestPath = join(extracted, 'plugin.json')
      if (!existsSync(manifestPath)) {
        try {
          const file = await fetchGithubJson(`/repos/${PLUGINS_REPO}/contents/${projectName}/plugin.json?ref=${encodeURIComponent(release.tag_name)}`)
          writeFileSync(manifestPath, Buffer.from(file.content, 'base64'))
        } catch (err) {
          console.warn(`[stage-plugins] ${projectName}: no plugin.json at ${release.tag_name} (${err.message}), staging without manifest`)
        }
      }
      const stagedDir = stagePluginBuild(extracted, projectName)
      if (projectName === 'NKS.WebDevConsole.Plugin.NksDeploy') {
        const pharAsset = (release.assets ?? []).find(a => a.name === 'nksdeploy.phar')
        if (pharAsset) {
          await downloadToFile(pharAsset.browser_download_url, join(stagedDir, 'nksdeploy.phar'))
          console.log(`[stage-plugins] fetched bundled nksdeploy.phar from ${release.tag_name}`)
        }
      }
      console.log(`[stage-plugins] fetched ${asset.name} from release ${release.tag_name}`)
    }
    sourceUsed = `${PLUGINS_REPO} GitHub Releases`
  } catch (err) {
    console.warn(`[stage-plugins] release download failed: ${err.message}`)
  }
}

if (!sourceUsed) {
  console.warn('[stage-plugins] No plugin build output found — packaged daemon will ship with zero bundled plugins.')
  console.warn('[stage-plugins] Runtime fallback: daemon will download from catalog-api into ~/.wdc/plugins on first run.')
  console.warn(`[stage-plugins] Tried: ${siblingRoot} (sibling clone)`)
  console.warn(`[stage-plugins] Tried: ${PLUGINS_REPO} GitHub Releases`)
  // On CI, fail loudly: v0.2.5's macOS DMG shipped without any plugins
  // because the GitHub API call was rate-limited and this block silently
  // exit(0)'d. Users hit an empty app. The runtime catalog fallback works
  // in principle but leaves users with a broken install until they manually
  // install every plugin from the marketplace — not shippable. CI sets
  // `CI=true` by default, so this gate activates only on CI runs; local
  // devs without plugin sources keep the soft-fail behaviour.
  if (process.env.CI === 'true' && process.env.WDC_SKIP_PLUGIN_DOWNLOAD !== '1') {
    console.error('[stage-plugins] CI=true and no plugins were staged — refusing to ship a broken bundle.')
    process.exit(1)
  }
  process.exit(0)
}

const staged = stagedPluginDlls()
console.log(`[stage-plugins] Copied ${staged.length} plugin DLL(s) from ${sourceUsed} → ${destDir}`)

// Every first-party plugin the UI has a page or catalogue entry for must be
// in the bundle; a missing one shows up as a dead page in the installed app.
const missing = missingExpectedPlugins(staged.map(d => d.name))
if (missing.length > 0) {
  console.warn(`[stage-plugins] missing expected plugin(s): ${missing.join(', ')}`)
  if (process.env.CI === 'true') {
    console.error('[stage-plugins] CI=true and expected plugins are missing — refusing to ship an incomplete bundle.')
    process.exit(1)
  }
}

// ─── companion artifacts: nksdeploy.phar ──────────────────────────────
//
// The NKS.WebDevConsole.Plugin.NksDeploy DLL shells out to nksdeploy.phar
// at runtime via CliWrap. ResolveNksDeployPhar() looks beside the plugin
// DLL first, so dropping the phar in destDir makes it bundle-portable
// (electron-builder copies the whole resources/daemon/plugins/ tree).
// Skipped when the NksDeploy plugin isn't present (other plugins don't
// need phar) or via WDC_SKIP_PHAR_BUILD=1.
const nksDeploy = staged.find(d => d.name === 'NKS.WebDevConsole.Plugin.NksDeploy.dll')
if (nksDeploy && process.env.WDC_SKIP_PHAR_BUILD !== '1') {
  const pharDest = join(nksDeploy.dir, 'nksdeploy.phar')
  const builderScript = join(__dirname, 'build-nksdeploy-phar.mjs')
  if (existsSync(pharDest)) {
    console.log(`[stage-plugins] using staged nksdeploy.phar → ${pharDest}`)
  } else {
    console.log(`[stage-plugins] building nksdeploy.phar → ${pharDest}`)
    try {
      execSync(`node "${builderScript}" "${pharDest}"`, { stdio: 'inherit' })
    } catch (err) {
      console.warn(`[stage-plugins] phar build failed: ${err.message}`)
      console.warn('[stage-plugins] plugin will fall back to PATH lookup at runtime; deploys may fail')
      if (process.env.CI === 'true') {
        console.error('[stage-plugins] CI=true and phar build failed — refusing to ship a broken plugin bundle.')
        process.exit(1)
      }
    }
  }
}

// ─── helpers ───────────────────────────────────────────────────────────

/** Plugin DLLs the installer must contain. */
function expectedPluginDlls() {
  return [
    'Apache', 'Caddy', 'Cloudflare', 'Composer', 'Hosts', 'LocalRsync', 'Mailpit', 'MariaDB',
    'MySQL', 'Nginx', 'Node', 'NksDeploy', 'PHP', 'PostgreSQL', 'Redis', 'SSL',
  ].map(name => `NKS.WebDevConsole.Plugin.${name}.dll`)
}

function missingExpectedPlugins(stagedDlls) {
  const staged = new Set(stagedDlls)
  return expectedPluginDlls().filter(dll => !staged.has(dll))
}

/**
 * For each plugin zip asset name, the newest published release carrying it.
 * Drafts and prereleases are ignored.
 */
function pickPluginZips(releases) {
  const pick = new Map()
  for (const release of releases) {
    if (release.draft || release.prerelease) continue
    for (const asset of release.assets ?? []) {
      if (!/^NKS\.WebDevConsole\.Plugin\.[A-Za-z0-9]+\.zip$/.test(asset.name)) continue
      const prev = pick.get(asset.name)
      if (!prev || new Date(release.created_at) > new Date(prev.release.created_at)) {
        pick.set(asset.name, { asset, release })
      }
    }
  }
  return pick
}

async function fetchGithubJson(path) {
  const url = `https://api.github.com${path}`
  const headers = {
    'Accept': 'application/vnd.github+json',
    'User-Agent': 'nks-wdc-stage-plugins',
    'X-GitHub-Api-Version': '2022-11-28',
  }
  if (process.env.GITHUB_TOKEN) {
    headers.Authorization = `Bearer ${process.env.GITHUB_TOKEN}`
  }
  const res = await fetch(url, { headers })
  if (!res.ok) throw new Error(`GitHub ${res.status}: ${await res.text().catch(() => '?')}`)
  return res.json()
}

async function downloadToFile(url, dest) {
  const headers = {
    'Accept': 'application/octet-stream',
    'User-Agent': 'nks-wdc-stage-plugins',
  }
  if (process.env.GITHUB_TOKEN) {
    headers.Authorization = `Bearer ${process.env.GITHUB_TOKEN}`
  }
  const res = await fetch(url, { headers, redirect: 'follow' })
  if (!res.ok) throw new Error(`download ${res.status}: ${url}`)
  await pipeline(Readable.fromWeb(res.body), createWriteStream(dest))
}
