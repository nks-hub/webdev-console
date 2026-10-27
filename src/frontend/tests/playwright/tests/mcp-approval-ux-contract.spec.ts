import { test, expect } from './_fixtures'
import { readFileSync } from 'node:fs'
import { join, resolve } from 'node:path'

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

  test('toast approves and rejects through the banner store', () => {
    const toast = read('utils', 'mcpConfirmToast.ts')
    expect(toast).toContain('useMcpConfirmStore().approve(evt.intentId)')
    expect(toast).toContain('useMcpConfirmStore().reject(evt.intentId)')
    expect(toast).not.toMatch(/\bconfirmIntent\(/)
    expect(toast).not.toMatch(/\brevokeIntent\(/)
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
    expect(page).toMatch(/async function onApprove[\s\S]*?ElMessageBox\.confirm[\s\S]*?confirmStore\.approve\(row\.intentId\)/)
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
    const panel = read('components', 'mcp', 'McpOnboardingPanel.vue')
    expect(panel).toMatch(/const DEFAULT_PROFILE: ProfileDefinition\['id'\] = 'minimal'/)
    expect(panel).toMatch(/id: 'localDev'[\s\S]*?kindPattern: 'service_restart'/)
    expect(panel).toContain("const DEV_DATABASE_PATTERN = '*_dev'")
    for (const kind of ['database_drop', 'database_query', 'database_import']) {
      expect(panel).toContain(`localDevDatabaseGrant('${kind}'`)
    }
    expect(panel).not.toMatch(/profile\.id === 'balanced'/)
  })

  test('new strings exist in en and cs', () => {
    for (const locale of ['en', 'cs']) {
      const j = JSON.parse(read('locales', `${locale}.json`))
      expect(j.mcp.notify.title, `${locale} mcp.notify.title`).toBeTruthy()
      expect(j.mcp.notify.body).toContain('{kind}')
      expect(j.mcp.notify.bodyGeneric).toBeTruthy()
      expect(j.mcpIntents.approve).toBeTruthy()
      expect(j.mcpIntents.reject).toBeTruthy()
      expect(j.mcpIntents.approveDialog.message).toContain('{prompt}')
      expect(j.mcpOnboarding.default).toBeTruthy()
      expect(j.mcpOnboarding.profiles.localDev.bullets).toHaveLength(3)
    }
    const cs = JSON.parse(read('locales', 'cs.json'))
    expect(cs.mcpOnboarding.profiles.localDev.name).toBe('Lokální vývoj')
  })
})
