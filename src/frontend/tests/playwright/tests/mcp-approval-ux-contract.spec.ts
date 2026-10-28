import { test, expect } from './_fixtures'
import { readFileSync } from 'node:fs'
import { join, resolve } from 'node:path'
import { DEFAULT_PROFILE, MCP_PROFILES, detectProfile } from '../../../src/components/mcp/mcpProfiles'

// Source-level contract for the MCP approval flow in the GUI:
//  - a settled intent (approved anywhere, rejected, consumed, expired) must
//    drop its banner, not just the toast;
//  - pending requests are reloaded from the daemon on start and reconnect;
//  - the Intents page can approve and reject pending rows;
//  - the OS notification uses the human kind label;
//  - onboarding offers a local-dev preset while strict stays the default.

const srcRoot = resolve(process.cwd(), 'src')
const read = (...parts: string[]): string => readFileSync(join(srcRoot, ...parts), 'utf-8')

test.describe('MCP approval UX contract', () => {
  test('settled intents remove the banner store entry', () => {
    const store = read('stores', 'mcpConfirm.ts')
    expect(store).toMatch(/SETTLED_CHANGES[^=]*=\s*new Set\(\['confirmed', 'revoked', 'consumed', 'expired'\]\)/)
    expect(store).toMatch(/function handleIntentChanged[\s\S]*?SETTLED_CHANGES\.has\(evt\.change\)\) remove\(evt\.intentId\)/)

    const app = read('App.vue')
    const handler = app.match(/'mcp:intent-changed': \(data\) => \{([\s\S]*?)\n    \},/)
    expect(handler, 'App.vue mcp:intent-changed handler').not.toBeNull()
    expect(handler![1]).toContain('closeMcpConfirmToast(evt.intentId)')
    expect(handler![1]).toContain('mcpConfirmStore.handleIntentChanged(evt)')
  })

  test('toast rejects through the banner store and never approves', () => {
    const toast = read('utils', 'mcpConfirmToast.ts')
    expect(toast).toContain('useMcpConfirmStore().reject(evt.intentId)')
    expect(toast).not.toContain('.approve(')
    expect(toast).not.toMatch(/\bconfirmIntent\(/)
    expect(toast).not.toMatch(/\brevokeIntent\(/)
  })

  test('banner never focuses Approve and confirms before approving', () => {
    const banner = read('components', 'mcp', 'McpConfirmBanner.vue')
    // A request arriving while the operator types must not take Enter/Space.
    expect(banner).not.toMatch(/\.focus\(/)
    expect(banner).toMatch(/async function approve\(item: PendingConfirm\)[\s\S]*?confirmMcpApproval\(item\)[\s\S]*?store\.approve\(item\.intentId\)/)
    expect(banner).toMatch(/async function approveAndTrust[\s\S]*?confirmMcpApproval\(item, trustScopeText/)
    // Anything not known to be reversible needs the target typed in.
    const approval = read('utils', 'mcpApproval.ts')
    expect(approval).toMatch(/if \(item\.kindDanger !== 'reversible'\)[\s\S]*?ElMessageBox\.prompt[\s\S]*?inputValidator/)
  })

  test('trust shortcuts never create a wildcard grant and scope to the asking client', () => {
    const banner = read('components', 'mcp', 'McpConfirmBanner.vue')
    expect(banner).not.toMatch(/\?\? '\*'/)
    expect(banner).toMatch(/function canTrust[\s\S]*?!!item\.kind && !isMcpTargetMarker\(item\.domain\) && !item\.alwaysConfirm/)
    expect(banner).toMatch(/kindPattern: item\.kind!,\s*targetPattern: item\.domain!/)
    expect(banner).toMatch(/minutes !== null && item\.sessionId\) return \{ scopeType: 'session'/)
    expect(banner).toMatch(/item\.clientId\) return \{ scopeType: 'api_key'/)
    // The daemon reports who asked, live and after a reload.
    const store = read('stores', 'mcpConfirm.ts')
    expect(store).toContain('sessionId: e.sessionId ?? undefined')
    expect(store).toContain('clientId: e.clientId ?? undefined')
  })

  test('suggested grants target the domain the daemon matches', () => {
    const banner = read('components', 'mcp', 'SuggestedGrantsBanner.vue')
    expect(banner).toContain('targetPattern: s.domain,')
    expect(banner).not.toContain('/*`')
  })

  test('hub shows one off state instead of failing lists while MCP is off', () => {
    const hub = read('components', 'pages', 'McpHub.vue')
    expect(hub).toMatch(/<div v-if="!flags\.mcpEnabled" class="hub-off"[\s\S]*?<template v-else>[\s\S]*?<McpOnboardingPanel /)
    for (const pane of ['activity', 'intents', 'grants', 'kinds']) {
      expect(hub).toContain(`name="${pane}" lazy>`)
    }
    // Simple mode keeps Activity and Requests only.
    expect(hub).toContain('<el-tab-pane v-if="advanced" name="grants" lazy>')
    expect(hub).toContain('<el-tab-pane v-if="advanced" name="kinds" lazy>')
  })

  test('pending requests are reloaded on daemon connect', () => {
    const app = read('App.vue')
    const onConnect = app.match(/daemonStore\.onConnect\(\(\) => \{([\s\S]*?)\n  \}\)/)
    expect(onConnect, 'onConnect callback').not.toBeNull()
    expect(onConnect![1]).toContain('mcpConfirmStore.loadPending()')

    const store = read('stores', 'mcpConfirm.ts')
    expect(store).toMatch(/async function loadPending[\s\S]*?fetchPendingIntents\(\)/)
    const api = read('api', 'daemon.ts')
    expect(api).toMatch(/fetchPendingIntents[\s\S]*?pendingOnly: 'true'/)
  })

  test('Intents page approves and rejects pending rows via the store', () => {
    const page = read('components', 'pages', 'McpIntents.vue')
    expect(page).toMatch(/v-if="row\.state === 'pending_confirmation'" class="intent-decide"[\s\S]*?data-testid="intent-approve"[\s\S]*?data-testid="intent-reject"/)
    expect(page).toMatch(/async function onApprove[\s\S]*?confirmMcpApproval\(row\)[\s\S]*?confirmStore\.approve\(row\.intentId\)/)
    expect(page).toMatch(/async function onReject[\s\S]*?confirmStore\.reject\(row\.intentId\)/)
  })

  test('OS notification uses the human kind label', () => {
    const app = read('App.vue')
    const handler = app.match(/'mcp:confirm-request': \(data\) => \{([\s\S]*?)\n    \},/)
    expect(handler).not.toBeNull()
    expect(handler![1]).toContain('mcpKindLabel(evt)')
    // The OS notification goes through osNotificationEvents with the label, not the wire id.
    expect(handler![1]).toContain('notifyMcpConfirm({ ...evt, kindLabel: mcpKindLabel(evt) })')
    expect(handler![1]).not.toMatch(/perform \$\{evt\.kind\}/)
    const label = read('utils', 'mcpKindLabel.ts')
    expect(label).toContain('mcpKinds.labels.')
  })

  test('onboarding offers a local-dev preset and keeps strict as default', () => {
    expect(DEFAULT_PROFILE).toBe('minimal')
    const localDev = MCP_PROFILES.find((p) => p.id === 'localDev')!
    expect(localDev.grants.map((g) => `${g.kindPattern}|${g.targetPattern}`)).toEqual([
      'service_restart|*', 'database_drop|*_dev', 'database_query|*_dev', 'database_import|*_dev',
    ])
    expect(localDev.grants.every((g) => g.scopeType === 'always')).toBe(true)
    // The hub names the profile the active grants match, all five of them.
    expect(detectProfile([])).toBe('minimal')
    expect(detectProfile(localDev.grants.map((g) => ({ ...g })))).toBe('localDev')
    expect(detectProfile([{ kindPattern: '*', targetPattern: '*' }])).toBe('permissive')
    expect(detectProfile([{ kindPattern: 'deploy', targetPattern: 'blog.loc' }])).toBe('custom')

    const panel = read('components', 'mcp', 'McpOnboardingPanel.vue')
    expect(panel).not.toMatch(/profile\.id === 'balanced'/)
    // Only the button applies a profile, and Full trust has to be typed.
    expect(panel).not.toContain('role="button"')
    expect(panel).toMatch(/async function apply[\s\S]*?profile\.id === 'permissive' && !\(await confirmPermissive\(\)\)/)
    expect(panel).toMatch(/if \(okCount < profile\.grants\.length\)[\s\S]*?ElMessage\.error/)
  })

  test('new strings exist in en and cs', () => {
    for (const locale of ['en', 'cs']) {
      const j = JSON.parse(read('locales', `${locale}.json`))
      expect(j.mcp.notify.title, `${locale} mcp.notify.title`).toBeTruthy()
      expect(j.mcp.notify.body).toContain('{kind}')
      expect(j.mcp.notify.bodyGeneric).toBeTruthy()
      expect(j.mcpIntents.approve).toBeTruthy()
      expect(j.mcpIntents.reject).toBeTruthy()
      expect(j.mcpApproval.aiSays).toContain('{prompt}')
      expect(j.mcpApproval.typeToConfirm).toContain('{expected}')
      for (const state of ['pending_confirmation', 'ready', 'consumed', 'revoked', 'expired']) {
        expect(j.mcpIntents.state[state], `${locale} state ${state}`).toBeTruthy()
      }
      expect(j.mcpKinds.labels.backup_restore).toBeTruthy()
      expect(j.mcpOnboarding.default).toBeTruthy()
      expect(j.mcpOnboarding.profiles.localDev.bullets).toHaveLength(3)
    }
    const cs = JSON.parse(read('locales', 'cs.json'))
    expect(cs.mcpOnboarding.profiles.localDev.name).toBe('Lokální vývoj')
  })

  test('round 2: hub layout, readable banner, reachable decisions', () => {
    const hub = read('components', 'pages', 'McpHub.vue')
    // The global page rule pads the hub; a scoped padding: 0 glued the
    // title to the sidebar.
    expect(hub).not.toMatch(/\.page \{ padding: 0/)
    expect(hub).toContain('data-testid="mcp-hub-simple-note"')
    const card = read('components', 'mcp', 'McpConnectCard.vue')
    // On Requests, Rules or Actions the setup steps collapse, also when the
    // kept-alive hub only changes route (round 3). Simple mode shows Activity
    // on /mcp/grants and /mcp/kinds, so only Requests collapses there (round 6).
    // Round 7: the kept-alive hub decides again on every route or mode change,
    // so simple-mode Requests → Rules opens the steps above an empty Activity.
    expect(card).toContain('const expanded = ref(wantExpanded())')
    expect(card).toContain('watch([() => route.fullPath, () => uiMode.isAdvanced], () => { expanded.value = wantExpanded() })')
    expect(card).toContain('uiMode.isAdvanced ? /\\/mcp\\/(intents|grants|kinds)$/ : /\\/mcp\\/intents$/')
    // The profile panel sits on the Rules tab, not above the tabs (round 3).
    expect(hub).toMatch(/<el-tab-pane v-if="advanced" name="grants" lazy>[\s\S]*?<McpOnboardingPanel \/>\s*<McpGrants \/>/)
    expect(read('components', 'pages', 'McpActivity.vue')).toContain("caller.startsWith('client:')")
    const panel = read('components', 'mcp', 'McpOnboardingPanel.vue')
    expect(panel).toContain("MCP_PROFILES.filter((p) => p.id !== 'custom')")

    const banner = read('components', 'mcp', 'McpConfirmBanner.vue')
    // Opaque card below the app header; no rules from simple mode.
    expect(banner).toMatch(/\.mcp-confirm-stack \{[\s\S]*?top: 60px;/)
    expect(banner).toMatch(/background: linear-gradient\([^;]*\), var\(--wdc-surface\);/)
    expect(banner).toMatch(/function canTrust[\s\S]*?uiMode\.isAdvanced &&/)
    expect(read('utils', 'mcpApproval.ts')).not.toContain('inputPlaceholder')

    const intents = read('components', 'pages', 'McpIntents.vue')
    expect(intents).toContain('<el-table-column label="" width="200" align="right" fixed="right">')
    expect(intents).toContain('!e.intentId.toLowerCase().startsWith(q)')

    const activity = read('components', 'pages', 'McpActivity.vue')
    expect(activity).toContain("t('mcpActivity.emptyFiltered')")
    expect(activity).toMatch(/loadError\.value = err instanceof Error/)
    expect(activity).toMatch(/watch\(\[searchQuery, toolFilter\]/)
    const timeline = read('components', 'mcp', 'McpActivityTimeline.vue')
    expect(timeline).toContain('function localHour(hour: string)')
    expect(timeline).not.toMatch(/total: \$\{b\.total\}/)
  })

  test('round 4: throughput from the 24 h count, localized numbers, no empty chart', () => {
    const activity = read('components', 'pages', 'McpActivity.vue')
    expect(activity).toContain('formatThroughput(stats.total / STATS_WINDOW_MINUTES)')
    expect(activity).not.toContain('toFixed(')
    expect(activity).toContain('<div v-if="stats && stats.total > 0" class="analytics-row">')
  })
})
