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
 * The target dir is wiped first so a removed plugin doesn't linger in
 * the packaged bundle. Safe to re-run.
 */
import { existsSync, mkdirSync, readdirSync, copyFileSync, rmSync, statSync, createWriteStream, writeFileSync } from 'node:fs'
import { dirname, join, resolve } from 'node:path'
import { fileURLToPath } from 'node:url'
import { execSync } from 'node:child_process'
import { tmpdir } from 'node:os'
import { pipeline } from 'node:stream/promises'
import { Readable } from 'node:stream'

const __dirname = dirname(fileURLToPath(import.meta.url))
const repoRoot = resolve(__dirname, '..')

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

let sourceUsed = null

// Source 0: monorepo dotnet build output. Kept as an escape hatch in
// case a developer or CI job drops plugin DLLs here manually, but no
// longer produced by the monorepo build — src/plugins/ was removed in
// favour of the nks-hub/webdev-console-plugins repo. Expected to be
// empty on normal checkouts. Runs first so if you DO want to override
// the sibling/GH-release sources, dropping DLLs here still wins.
const monorepoBuildDir = join(repoRoot, 'build', 'plugins')
if (listPluginDlls(monorepoBuildDir).length > 0) {
  copyRecursive(monorepoBuildDir, destDir)
  sourceUsed = monorepoBuildDir
}

// Source 1: sibling checkout of webdev-console-plugins repo.
const siblingRoot = resolve(repoRoot, '..', 'webdev-console-plugins')
if (!sourceUsed && existsSync(siblingRoot)) {
  const pluginProjectDirs = readdirSync(siblingRoot)
    .filter(n => n.startsWith('NKS.WebDevConsole.Plugin.'))
    .map(n => join(siblingRoot, n, 'bin', 'Release', 'net9.0'))
    .filter(p => listPluginDlls(p).length > 0)
  if (pluginProjectDirs.length > 0) {
    for (const p of pluginProjectDirs) copyRecursive(p, destDir)
    sourceUsed = siblingRoot
  }
}

// Source 2: download from GitHub releases.
// Skippable via WDC_SKIP_PLUGIN_DOWNLOAD so CI can run without network,
// or when the builder has already staged plugins by other means.
if (!sourceUsed && process.env.WDC_SKIP_PLUGIN_DOWNLOAD !== '1') {
  try {
    const releases = await fetchGithubJson(`/repos/${PLUGINS_REPO}/releases?per_page=100`)
    if (!Array.isArray(releases) || releases.length === 0) {
      throw new Error(`no releases on ${PLUGINS_REPO}`)
    }
    // Group by plugin ID (tag prefix before -v<ver>), keep only the
    // release whose `created_at` is newest per plugin.
    const newestPerPlugin = new Map()
    for (const rel of releases) {
      const tag = rel.tag_name ?? ''
      const m = tag.match(/^(nks\.wdc\.[a-z]+)-v(.+)$/)
      if (!m) continue
      const [, pluginId, version] = m
      const prev = newestPerPlugin.get(pluginId)
      if (!prev || new Date(rel.created_at) > new Date(prev.created_at)) {
        newestPerPlugin.set(pluginId, { ...rel, pluginId, version })
      }
    }
    if (newestPerPlugin.size === 0) {
      throw new Error(`no <plugin-id>-v<ver> tags in ${PLUGINS_REPO}`)
    }
    for (const [pluginId, rel] of newestPerPlugin) {
      const asset = (rel.assets ?? []).find(a => a.name.endsWith('.zip'))
      if (!asset) {
        console.warn(`[stage-plugins] ${pluginId} ${rel.version}: no .zip asset, skipping`)
        continue
      }
      const zipPath = join(tmpdir(), `${pluginId}-${rel.version}.zip`)
      await downloadToFile(asset.browser_download_url, zipPath)
      execSync(`unzip -o -q "${zipPath}" -d "${destDir}"`)
      console.log(`[stage-plugins] fetched ${pluginId} ${rel.version} from release`)
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

const count = listPluginDlls(destDir).length
console.log(`[stage-plugins] Copied ${count} plugin DLL(s) from ${sourceUsed} → ${destDir}`)

// ─── companion artifacts: nksdeploy.phar ──────────────────────────────
//
// The NKS.WebDevConsole.Plugin.NksDeploy DLL shells out to nksdeploy.phar
// at runtime via CliWrap. ResolveNksDeployPhar() looks beside the plugin
// DLL first, so dropping the phar in destDir makes it bundle-portable
// (electron-builder copies the whole resources/daemon/plugins/ tree).
// Skipped when the NksDeploy plugin isn't present (other plugins don't
// need phar) or via WDC_SKIP_PHAR_BUILD=1.
const hasNksDeployPlugin = listPluginDlls(destDir).some(n => n === 'NKS.WebDevConsole.Plugin.NksDeploy.dll')
if (hasNksDeployPlugin && process.env.WDC_SKIP_PHAR_BUILD !== '1') {
  const pharDest = join(destDir, 'nksdeploy.phar')
  const builderScript = join(__dirname, 'build-nksdeploy-phar.mjs')
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

// ─── helpers ───────────────────────────────────────────────────────────

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
