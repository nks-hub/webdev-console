/**
 * Contract tests across the tool surface: every tool (deploy tools have their
 * own contract test) calls a route the daemon registers, the new tools send
 * the method, path, body and intent header the daemon reads, annotations and
 * read-only mode agree, and the README lists exactly the registered tools.
 */

import { mkdtempSync, readFileSync, rmSync, writeFileSync, existsSync } from 'node:fs'
import { tmpdir } from 'node:os'
import { dirname, join } from 'node:path'
import { fileURLToPath } from 'node:url'
import { afterEach, beforeEach, describe, expect, it, vi } from 'vitest'

vi.mock('../daemonClient.js', async (importOriginal) => ({
  ...(await importOriginal<object>()),
  daemonClient: (await import('../testing/recorder.js')).recorder.client,
}))

import { recorder, type RecordedCall } from '../testing/recorder.js'
import { resultBody } from '../testing/fakeServer.js'
import { allTools } from '../testing/allTools.js'
import { matchRoute } from '../testing/daemonRoutes.js'
import { intentFor } from './confirmation.js'
import { mergeSiteUpdate, stepStatus, summariseApply } from './sites.js'
import { backupFileName } from './backup.js'
import { MAX_IMPORT_BYTES, readImportSql } from './databases.js'

const here = dirname(fileURLToPath(import.meta.url))
const TOKEN = 'intent-id.nonce.signature'
const ZONE = 'a'.repeat(32)
const REC = 'b'.repeat(32)
const intent = { 'X-Intent-Token': TOKEN }

beforeEach(() => recorder.reset())

/** Arguments that pass each tool's schema. Deploy tools are covered by deploy.contract.test.ts. */
const SAMPLE_ARGS: Record<string, Record<string, unknown>> = {
  // sites
  wdc_list_sites: {},
  wdc_get_site: { domain: 'shop.loc' },
  wdc_get_site_access_log: { domain: 'shop.loc', limit: 10 },
  wdc_get_site_metrics: { domain: 'shop.loc' },
  wdc_get_site_error_log: { domain: 'shop.loc' },
  wdc_site_history: { domain: 'shop.loc' },
  wdc_create_site: { domain: 'shop.loc', documentRoot: '/srv/shop' },
  wdc_delete_site: { domain: 'shop.loc', confirm: 'YES', intentToken: TOKEN },
  wdc_update_site: { domain: 'shop.loc', phpVersion: '8.3' },
  wdc_apply_site: { domain: 'shop.loc' },
  wdc_reapply_all_sites: {},
  wdc_detect_framework: { domain: 'shop.loc' },
  wdc_duplicate_site: { domain: 'shop.loc', newDomain: 'shop2.loc' },
  wdc_site_rollback: { domain: 'shop.loc', timestamp: '20260101-120000', confirm: 'YES' },
  // services
  wdc_list_services: {},
  wdc_get_service_logs: { serviceId: 'apache' },
  wdc_get_service_config: { serviceId: 'apache' },
  wdc_start_service: { serviceId: 'apache' },
  wdc_stop_service: { serviceId: 'apache', intentToken: TOKEN },
  wdc_restart_service: { serviceId: 'apache', intentToken: TOKEN },
  wdc_validate_config: { serviceId: 'apache', configPath: '/wdc/apache/conf/httpd.conf' },
  wdc_update_service_config: { serviceId: 'php', path: '/wdc/php/8.3/php.ini', content: 'memory_limit=1G', confirm: 'YES', intentToken: TOKEN },
  // system, hosts
  wdc_get_status: {},
  wdc_get_system_info: {},
  wdc_get_recent_activity: {},
  wdc_list_hosts: {},
  wdc_flush_dns: {},
  // databases
  wdc_list_databases: {},
  wdc_database_tables: { database: 'shop' },
  wdc_query: { database: 'shop', sql: 'SELECT 1' },
  wdc_execute: { database: 'shop', sql: 'DELETE FROM t', confirm: 'YES', intentToken: TOKEN },
  wdc_create_database: { name: 'shop' },
  wdc_drop_database: { name: 'shop', confirm: 'YES', intentToken: TOKEN },
  wdc_database_size: { database: 'shop' },
  wdc_table_columns: { database: 'shop', table: 'orders' },
  wdc_table_indexes: { database: 'shop', table: 'orders' },
  wdc_browse_table: { database: 'shop', table: 'orders' },
  wdc_export_database: { database: 'shop' },
  wdc_import_database: { database: 'shop', sql: 'CREATE TABLE t (id INT);', confirm: 'YES', intentToken: TOKEN },
  // mysql users
  wdc_list_mysql_users: {},
  wdc_create_mysql_user: { userName: 'app', host: 'localhost', password: 'pw' },
  wdc_set_mysql_user_password: { userName: 'app', host: 'localhost', password: 'pw2' },
  wdc_grant_mysql_user_database: { userName: 'app', host: 'localhost', database: 'shop', privileges: 'read' },
  wdc_drop_mysql_user: { userName: 'app', host: 'localhost', confirm: 'YES' },
  wdc_change_mysql_root_password: { currentPwd: '', newPwd: 'root2' },
  wdc_reset_mysql_root_password: { newPwd: 'root2', confirm: 'YES' },
  // ssl
  wdc_list_certs: {},
  wdc_install_ca: {},
  wdc_generate_cert: { domain: 'shop.loc' },
  wdc_revoke_cert: { domain: 'shop.loc', confirm: 'YES', intentToken: TOKEN },
  // php
  wdc_list_php_versions: {},
  wdc_list_php_extensions: { version: '8.3' },
  wdc_toggle_php_extension: { majorMinor: '8.3', extensionName: 'redis', enabled: true },
  // binaries
  wdc_list_catalog: {},
  wdc_list_installed_binaries: {},
  wdc_refresh_catalog: {},
  wdc_install_binary: { app: 'php', version: '8.3.10', intentToken: TOKEN },
  wdc_uninstall_binary: { app: 'php', version: '8.3.10', confirm: 'YES' },
  // plugins
  wdc_list_plugins: {},
  wdc_get_plugin_marketplace: {},
  wdc_enable_plugin: { id: 'nks.wdc.redis' },
  wdc_disable_plugin: { id: 'nks.wdc.redis' },
  wdc_install_plugin_from_marketplace: { id: 'nks.wdc.redis', downloadUrl: 'https://example.com/p.zip', intentToken: TOKEN },
  wdc_uninstall_plugin: { id: 'nks.wdc.redis', confirm: 'YES', intentToken: TOKEN },
  // backup
  wdc_list_backups: {},
  wdc_backup_stats: {},
  wdc_create_backup: {},
  wdc_restore_backup: { path: '/home/u/.wdc/backups/wdc-backup-1.zip', confirm: 'YES', intentToken: TOKEN },
  wdc_restore_backup_by_id: { id: 'wdc-backup-1.zip', confirm: 'YES', intentToken: TOKEN },
  wdc_delete_backup: { name: '/home/u/.wdc/backups/wdc-backup-1.zip', confirm: 'YES', intentToken: TOKEN },
  // settings
  wdc_get_settings: {},
  wdc_update_settings: { settings: { 'ports.http': 8080 } },
  // cloudflare
  wdc_get_cloudflare_config: {},
  wdc_verify_cloudflare_token: {},
  wdc_list_cloudflare_zones: {},
  wdc_list_cloudflare_dns: { zoneId: ZONE },
  wdc_list_cloudflare_tunnels: {},
  wdc_get_cloudflare_subdomain_suggestion: { domain: 'shop.loc' },
  wdc_save_cloudflare_config: { defaultZoneId: ZONE },
  wdc_create_cloudflare_dns: { zoneId: ZONE, name: 'shop', content: 'tunnel.example.com' },
  wdc_delete_cloudflare_dns: { zoneId: ZONE, recordId: REC, confirm: 'YES', intentToken: TOKEN },
  // confirmation
  wdc_request_confirmation: { operation: 'service_restart', target: 'apache', reason: 'apply new php.ini' },
  wdc_get_confirmation_status: { intentId: 'iid' },
  // composer
  wdc_composer_status: { domain: 'shop.loc' },
  wdc_composer_outdated: { domain: 'shop.loc' },
  wdc_composer_install: { domain: 'shop.loc' },
  wdc_composer_require: { domain: 'shop.loc', package: 'monolog/monolog:^3.0' },
  wdc_composer_remove: { domain: 'shop.loc', package: 'monolog/monolog' },
  // node
  wdc_list_node_sites: {},
  wdc_start_node_site: { domain: 'app.loc', intentToken: TOKEN },
  wdc_stop_node_site: { domain: 'app.loc' },
  wdc_restart_node_site: { domain: 'app.loc', intentToken: TOKEN },
}

/** Responses some tools need to get past their first call. */
function respond(call: RecordedCall): unknown {
  if (call.method === 'POST' && call.path === '/api/mcp/intents') return { intentId: 'iid', intentToken: TOKEN, expiresAt: 'x' }
  if (call.method === 'GET' && call.path === '/api/sites/shop.loc') return { domain: 'shop.loc', documentRoot: '/srv/shop', phpVersion: '8.2' }
  if (call.path === '/api/sites/reapply-all') return []
  if (call.path.split('?')[0]!.endsWith('/export')) return '-- dump'
  return {}
}

const nonDeployTools = () => [...allTools().tools.keys()].filter(t => !t.startsWith('wdc_deploy_'))

describe('every tool calls a daemon route', () => {
  it('has sample arguments for every non-deploy tool', () => {
    expect(nonDeployTools().filter(t => !(t in SAMPLE_ARGS))).toEqual([])
    expect(Object.keys(SAMPLE_ARGS).filter(t => !nonDeployTools().includes(t))).toEqual([])
  })

  it.each(Object.entries(SAMPLE_ARGS))('%s', async (tool, args) => {
    recorder.reset(respond)
    const r = await allTools().call(tool, args)
    expect(r.isError, r.content[0]?.text).toBeUndefined()
    expect(recorder.calls.length).toBeGreaterThan(0)
    for (const c of recorder.calls) {
      expect(matchRoute(c.method, c.path), `${tool}: ${c.method} ${c.path} is not a daemon route`).toBeDefined()
    }
  })
})

describe('new tools send what the daemon reads', () => {
  const cases: Array<[string, Record<string, unknown>, Partial<RecordedCall>]> = [
    ['wdc_get_site_error_log', { domain: 'shop.loc', lines: 50, since: '2026-01-01T00:00:00Z' },
      { method: 'GET', path: '/api/sites/shop.loc/logs/errors?lines=50&since=2026-01-01T00%3A00%3A00Z' }],
    ['wdc_site_history', { domain: 'shop.loc' }, { method: 'GET', path: '/api/sites/shop.loc/history' }],
    ['wdc_apply_site', { domain: 'shop.loc' }, { method: 'POST', path: '/api/sites/shop.loc/apply' }],
    ['wdc_reapply_all_sites', {}, { method: 'POST', path: '/api/sites/reapply-all' }],
    ['wdc_detect_framework', { domain: 'shop.loc' }, { method: 'POST', path: '/api/sites/shop.loc/detect-framework' }],
    ['wdc_duplicate_site', { domain: 'shop.loc', newDomain: 'copy.loc', copyFiles: 'top' },
      { method: 'POST', path: '/api/sites/shop.loc/duplicate', body: { newDomain: 'copy.loc', copyFiles: 'top' } }],
    ['wdc_site_rollback', { domain: 'shop.loc', timestamp: '20260101-120000', confirm: 'YES' },
      { method: 'POST', path: '/api/sites/shop.loc/rollback', body: { timestamp: '20260101-120000' } }],
    ['wdc_restart_service', { serviceId: 'apache', intentToken: TOKEN },
      { method: 'POST', path: '/api/services/apache/restart', headers: intent }],
    ['wdc_get_service_logs', { serviceId: 'mysql', lines: 20 }, { method: 'GET', path: '/api/services/mysql/logs?lines=20' }],
    ['wdc_get_service_config', { serviceId: 'php' }, { method: 'GET', path: '/api/services/php/config' }],
    ['wdc_update_service_config', { serviceId: 'php', path: '/p/php.ini', content: 'x=1', confirm: 'YES', intentToken: TOKEN },
      { method: 'POST', path: '/api/services/php/config', body: { path: '/p/php.ini', content: 'x=1' }, headers: intent }],
    ['wdc_validate_config', { serviceId: 'apache', configPath: '/a/httpd.conf', content: 'Listen 80' },
      { method: 'POST', path: '/api/config/validate', body: { serviceId: 'apache', configPath: '/a/httpd.conf', content: 'Listen 80' } }],
    ['wdc_list_php_extensions', { version: '8.3' }, { method: 'GET', path: '/api/php/8.3/extensions' }],
    ['wdc_composer_status', { domain: 'shop.loc' }, { method: 'GET', path: '/api/sites/shop.loc/composer/status' }],
    ['wdc_composer_outdated', { domain: 'shop.loc' }, { method: 'GET', path: '/api/sites/shop.loc/composer/outdated' }],
    ['wdc_composer_install', { domain: 'shop.loc' }, { method: 'POST', path: '/api/sites/shop.loc/composer/install' }],
    ['wdc_composer_require', { domain: 'shop.loc', package: 'a/b:^1' },
      { method: 'POST', path: '/api/sites/shop.loc/composer/require', body: { package: 'a/b:^1' } }],
    ['wdc_composer_remove', { domain: 'shop.loc', package: 'a/b' },
      { method: 'POST', path: '/api/sites/shop.loc/composer/remove', body: { package: 'a/b' } }],
    ['wdc_list_node_sites', {}, { method: 'GET', path: '/api/node/sites' }],
    ['wdc_start_node_site', { domain: 'app.loc', intentToken: TOKEN },
      { method: 'POST', path: '/api/node/sites/app.loc/start', headers: intent }],
    ['wdc_stop_node_site', { domain: 'app.loc' }, { method: 'POST', path: '/api/node/sites/app.loc/stop' }],
    ['wdc_restart_node_site', { domain: 'app.loc', intentToken: TOKEN },
      { method: 'POST', path: '/api/node/sites/app.loc/restart', headers: intent }],
    ['wdc_database_size', { database: 'shop' }, { method: 'GET', path: '/api/databases/shop/size' }],
    ['wdc_table_columns', { database: 'shop', table: 'orders' }, { method: 'GET', path: '/api/databases/v2/shop/tables/orders/columns' }],
    ['wdc_table_indexes', { database: 'shop', table: 'orders' }, { method: 'GET', path: '/api/databases/v2/shop/tables/orders/indexes' }],
    ['wdc_browse_table', { database: 'shop', table: 'orders', page: 2, pageSize: 10, orderBy: 'id', orderDir: 'desc' },
      { method: 'GET', path: '/api/databases/v2/shop/tables/orders/data?page=2&pageSize=10&orderDir=desc&orderBy=id' }],
    ['wdc_import_database', { database: 'shop', sql: 'SELECT 1;', confirm: 'YES', intentToken: TOKEN },
      { method: 'POST', path: '/api/databases/shop/import', body: 'SELECT 1;', contentType: 'text/plain; charset=utf-8', headers: intent }],
    ['wdc_list_hosts', {}, { method: 'GET', path: '/api/hosts' }],
    ['wdc_flush_dns', {}, { method: 'POST', path: '/api/dns/flush' }],
    ['wdc_uninstall_plugin', { id: 'nks.wdc.redis', confirm: 'YES', intentToken: TOKEN },
      { method: 'DELETE', path: '/api/plugins/nks.wdc.redis', headers: intent }],
    ['wdc_backup_stats', {}, { method: 'GET', path: '/api/backup/stats' }],
    ['wdc_delete_backup', { name: 'C:\\Users\\u\\.wdc\\backups\\wdc-backup-1.zip', confirm: 'YES', intentToken: TOKEN },
      { method: 'DELETE', path: '/api/backup/wdc-backup-1.zip', headers: intent }],
    ['wdc_export_database', { database: 'shop' }, { method: 'GET', path: '/api/databases/shop/export?' }],
    ['wdc_restore_backup_by_id', { id: 'wdc-backup-1.zip', confirm: 'YES', intentToken: TOKEN },
      { method: 'POST', path: '/api/backup/wdc-backup-1.zip/restore', headers: intent }],
  ]

  it.each(cases)('%s', async (tool, args, expected) => {
    recorder.reset(respond)
    const r = await allTools().call(tool, args)
    expect(r.isError, r.content[0]?.text).toBeUndefined()
    expect(recorder.calls).toHaveLength(1)
    expect(recorder.calls[0]).toMatchObject(expected)
    if (!expected.headers) expect(recorder.calls[0]!.headers).toBeUndefined()
  })

  it('wdc_create_site forwards ports, Node settings, environment and phpSettings', async () => {
    recorder.reset(respond)
    const args = {
      domain: 'app.loc',
      documentRoot: '/srv/app',
      httpPort: 8080,
      httpsPort: 8443,
      nodeUpstreamPort: 3000,
      nodeStartCommand: 'npm run start',
      environment: { NODE_ENV: 'development' },
      phpSettings: { memoryLimit: '512M', xdebugEnabled: true },
    }
    await allTools().call('wdc_create_site', { ...args, intentToken: TOKEN })
    expect(recorder.calls[0]).toMatchObject({
      method: 'POST',
      path: '/api/sites',
      body: { ...args, sslEnabled: true, phpVersion: '', framework: '' },
      headers: intent,
    })
    expect(recorder.calls[0]!.body).not.toHaveProperty('intentToken')
  })

  it('wdc_create_site rejects unknown phpSettings keys and bad env names', async () => {
    const t = allTools()
    await expect(t.call('wdc_create_site', { domain: 'a.loc', documentRoot: '/a', phpSettings: { evil: 1 } })).rejects.toThrow()
    await expect(t.call('wdc_create_site', { domain: 'a.loc', documentRoot: '/a', environment: { 'A=B': 'x' } })).rejects.toThrow()
  })

  it('wdc_update_site reads the site and PUTs it with only the given fields changed', async () => {
    recorder.reset(respond)
    await allTools().call('wdc_update_site', { domain: 'shop.loc', phpVersion: '8.4', aliases: ['www.shop.loc'] })
    expect(recorder.calls.map(c => `${c.method} ${c.path}`)).toEqual(['GET /api/sites/shop.loc', 'PUT /api/sites/shop.loc'])
    expect(recorder.calls[1]!.body).toEqual({ domain: 'shop.loc', documentRoot: '/srv/shop', phpVersion: '8.4', aliases: ['www.shop.loc'] })
  })

  it('wdc_update_site refuses an empty change', async () => {
    const r = await allTools().call('wdc_update_site', { domain: 'shop.loc' })
    expect(r.isError).toBe(true)
    expect(recorder.calls).toHaveLength(0)
  })

  it('wdc_apply_site reports step statuses as words with a summary', async () => {
    recorder.reset(() => ({ domain: 'shop.loc', success: false, steps: [
      { name: 'ssl', status: 2, detail: 'mkcert missing' },
      { name: 'vhost', status: 'ok', detail: null },
      { name: 'hosts', status: 1, detail: 'already present' },
    ] }))
    const body = resultBody(await allTools().call('wdc_apply_site', { domain: 'shop.loc' })) as ReturnType<typeof summariseApply>
    expect(body.steps.map(s => s.status)).toEqual(['failed', 'ok', 'skipped'])
    expect(body.summary).toEqual({ ok: 1, skipped: 1, failed: 1 })
    expect(body.failedSteps).toEqual(['ssl: mkcert missing'])
  })

  it('wdc_browse_table offers no free-form WHERE', () => {
    expect(Object.keys(allTools().tools.get('wdc_browse_table')!.config.inputSchema!)).not.toContain('where')
  })
})

describe('intents for the new gated tools', () => {
  it.each([
    ['service_restart', 'apache', { kind: 'service_restart', domain: 'apache', host: '*svc*' }],
    ['service_config_write', 'php', { kind: 'service_config_write', domain: 'php', host: '*svc*' }],
    ['database_import', 'shop', { kind: 'database_import', domain: 'shop', host: '*db*' }],
    ['plugin_uninstall', 'nks.wdc.redis', { kind: 'plugin_uninstall', domain: 'nks.wdc.redis', host: '*plugin*' }],
    ['backup_restore', 'x', { kind: 'backup_restore', domain: '*backup*', host: '*backup*' }],
    ['backup_delete', 'wdc-backup-1.zip', { kind: 'backup_delete', domain: 'wdc-backup-1.zip', host: '*backup*' }],
    ['node_process', 'app.loc', { kind: 'node_process', domain: 'app.loc', host: '*mcp*' }],
  ] as const)('%s', (op, target, expected) => {
    expect(intentFor(op, target)).toEqual(expected)
  })

  it.each([
    ['Services/ServiceEndpoints.cs', '"service_restart", domain: id, host: "*svc*"'],
    ['Services/ServiceConfigEndpoints.cs', '"service_config_write", domain: id, host: "*svc*"'],
    ['Data/DatabaseEndpoints.cs', '"database_import", domain: name, host: "*db*"'],
    ['Plugin/PluginEndpoints.cs', '"plugin_uninstall", domain: id, host: "*plugin*"'],
    ['Backup/BackupEndpoints.cs', '"backup_restore", domain: "*backup*", host: "*backup*"'],
  ])('%s checks %s', (file, gate) => {
    const src = readFileSync(join(here, '..', '..', '..', '..', 'src', 'daemon', 'NKS.WebDevConsole.Daemon', file), 'utf8')
    expect(src).toContain(gate)
  })
})

describe('Returns line matches the daemon response', () => {
  it('wdc_create_backup names every field POST /api/backup returns', () => {
    const src = readFileSync(join(here, '..', '..', '..', '..', 'src', 'daemon', 'NKS.WebDevConsole.Daemon', 'Backup', 'BackupEndpoints.cs'), 'utf8')
    const ok = /MapPost\("\/api\/backup"[\s\S]*?return Results\.Ok\(new \{ ([^}]*) \}\);/.exec(src)![1]!
    const fields = ok.split(',').map(f => f.split('=')[0]!.trim())
    expect(fields).toEqual(['path', 'files', 'size', 'contentFlags', 'skipped'])
    const description = allTools().tools.get('wdc_create_backup')!.config.description!
    const returns = description.slice(description.indexOf('Returns:'))
    for (const f of fields) expect(returns).toMatch(new RegExp(`\\b${f}\\b`))
  })
})

describe('annotations and read-only mode', () => {
  it('read-only mode registers exactly the tools marked readOnlyHint', () => {
    const all = allTools().tools
    const readOnly = [...all.values()].filter(t => t.config.annotations?.readOnlyHint).map(t => t.name).sort()
    const registered = [...allTools({ readonly: true, deployScopes: ['*'] }).tools.keys()].sort()
    expect(registered).toEqual(readOnly)
  })

  it('no tool is both read-only and destructive', () => {
    const bad = [...allTools().tools.values()].filter(t => t.config.annotations?.readOnlyHint && t.config.annotations?.destructiveHint)
    expect(bad.map(t => t.name)).toEqual([])
  })

  it('every tool declares all four hints', () => {
    const missing = [...allTools().tools.values()].filter(t => {
      const a = t.config.annotations ?? {}
      return ['readOnlyHint', 'destructiveHint', 'idempotentHint', 'openWorldHint'].some(k => typeof (a as Record<string, unknown>)[k] !== 'boolean')
    })
    expect(missing.map(t => t.name)).toEqual([])
  })

  it.each(['wdc_delete_site', 'wdc_drop_database', 'wdc_revoke_cert', 'wdc_delete_cloudflare_dns'])(
    '%s is not idempotent (its intent token is single-use)',
    (tool) => {
      expect(allTools().tools.get(tool)!.config.annotations).toMatchObject({ destructiveHint: true, idempotentHint: false })
    },
  )

  it('every tool that takes an intentToken says how to get one', () => {
    const silent = [...allTools().tools.values()].filter(t =>
      'intentToken' in (t.config.inputSchema ?? {}) && !/wdc_(request_confirmation|deploy_create_intent)/.test(t.config.description ?? ''))
    expect(silent.map(t => t.name)).toEqual([])
  })

  it('wdc_refresh_catalog is a mutation and is hidden in read-only mode', () => {
    expect(allTools().tools.get('wdc_refresh_catalog')!.config.annotations?.readOnlyHint).toBe(false)
    expect(allTools({ readonly: true, deployScopes: ['*'] }).tools.has('wdc_refresh_catalog')).toBe(false)
  })
})

describe('registration', () => {
  it('allTools registers the same modules as index.ts', () => {
    const index = readFileSync(join(here, '..', 'index.ts'), 'utf8')
    const helper = readFileSync(join(here, '..', 'testing', 'allTools.ts'), 'utf8')
    const calls = (src: string) => [...src.matchAll(/\b(register\w+Tools)\b(?=\(server, opts\)|,)/g)].map(m => m[1]).sort()
    expect(calls(helper)).toEqual(calls(index))
  })
})

describe('README', () => {
  const readme = readFileSync(join(here, '..', '..', 'README.md'), 'utf8')
  const catalog = readme.slice(readme.indexOf('## Tool catalog'), readme.indexOf('## Daemon discovery'))

  it('lists exactly the registered tools', () => {
    const listed = [...new Set([...catalog.matchAll(/`(wdc_[a-z_]+)`/g)].map(m => m[1]))].sort()
    expect(listed).toEqual([...allTools().tools.keys()].sort())
  })

  it('states the real tool count', () => {
    const n = allTools().tools.size
    expect(readme).toContain(`## Tool catalog (${n} tools)`)
    expect(readme).toContain(`**${n} tools`)
  })

  it('does not mention the nonexistent wdc_set_default_php', () => {
    expect(readme).not.toContain('wdc_set_default_php')
  })
})

describe('helpers', () => {
  it.each([[0, 'ok'], [1, 'skipped'], [2, 'failed'], [9, 'unknown'], ['ok', 'ok'], ['Failed', 'failed'], ['1', 'skipped'], [null, 'unknown']] as const)(
    'stepStatus(%s) = %s',
    (input, expected) => {
      expect(stepStatus(input)).toBe(expected)
    },
  )

  it('mergeSiteUpdate keeps unrelated fields and ignores fields it does not own', () => {
    expect(
      mergeSiteUpdate(
        { domain: 'a.loc', nodeStartCommand: 'npm start', environment: { A: '1' }, phpVersion: '8.2' },
        { phpVersion: '8.3', sslEnabled: undefined, nodeStartCommand: 'rm -rf /' },
      ),
    ).toEqual({ domain: 'a.loc', nodeStartCommand: 'npm start', environment: { A: '1' }, phpVersion: '8.3' })
  })

  it('backupFileName takes the last path segment of either separator', () => {
    expect(backupFileName('/home/u/.wdc/backups/b.zip')).toBe('b.zip')
    expect(backupFileName('C:\\Users\\u\\b.zip')).toBe('b.zip')
    expect(backupFileName('b.zip')).toBe('b.zip')
  })

  describe('files', () => {
    let dir: string
    beforeEach(() => {
      dir = mkdtempSync(join(tmpdir(), 'wdc-mcp-db-'))
    })
    afterEach(() => rmSync(dir, { recursive: true, force: true }))

    it('readImportSql needs exactly one source', () => {
      expect(() => readImportSql(undefined, undefined)).toThrow(/exactly one/)
      expect(() => readImportSql('x', '/a.sql')).toThrow(/exactly one/)
      expect(readImportSql('SELECT 1', undefined)).toBe('SELECT 1')
    })

    it('wdc_import_database reads filePath and posts it as text', async () => {
      const f = join(dir, 'dump.sql')
      writeFileSync(f, 'CREATE TABLE t (id INT);')
      recorder.reset(() => ({ ok: true }))
      await allTools().call('wdc_import_database', { database: 'shop', filePath: f, confirm: 'YES', intentToken: TOKEN })
      expect(recorder.calls[0]).toMatchObject({ body: 'CREATE TABLE t (id INT);', contentType: 'text/plain; charset=utf-8' })
      expect(MAX_IMPORT_BYTES).toBeGreaterThan(1024 * 1024)
    })

    it('wdc_export_database writes outputPath and refuses to overwrite', async () => {
      const f = join(dir, 'out.sql')
      recorder.reset(() => '-- dump\nCREATE TABLE t (id INT);\n')
      const body = resultBody(await allTools().call('wdc_export_database', { database: 'shop', outputPath: f }))
      // The daemon allows an export to a file only with the database_admin capability.
      expect(recorder.calls[0]!.path).toBe(`/api/databases/shop/export?outputPath=${encodeURIComponent(f)}`)
      expect(body).toEqual({ database: 'shop', path: f, bytes: Buffer.byteLength('-- dump\nCREATE TABLE t (id INT);\n') })
      expect(readFileSync(f, 'utf8')).toContain('CREATE TABLE t')
      const again = await allTools().call('wdc_export_database', { database: 'shop', outputPath: f })
      expect(again.isError).toBe(true)
      expect(again.content[0]!.text).toMatch(/already exists/)
      expect(existsSync(f)).toBe(true)
    })

    it('wdc_export_database rejects a relative outputPath', async () => {
      await expect(allTools().call('wdc_export_database', { database: 'shop', outputPath: 'out.sql' })).rejects.toThrow(/absolute/)
    })
  })
})
