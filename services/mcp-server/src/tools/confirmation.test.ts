/**
 * Unit tests for the intent mapping used by wdc_request_confirmation. The
 * (kind, domain, host) triple must match what the daemon route checks, or the
 * approved intent is rejected with kind/domain/host_mismatch.
 */

import { describe, it, expect, vi } from 'vitest'

vi.mock('../daemonClient.js', () => ({ daemonClient: {} }))

import { intentFor, intentHeaders } from './confirmation.js'

describe('intentFor', () => {
  it.each([
    ['database_drop', 'shop_db', { kind: 'database_drop', domain: 'shop_db', host: '*db*' }],
    ['database_execute', 'shop_db', { kind: 'database_query', domain: 'shop_db', host: '*db*' }],
    ['site_delete', 'blog.test', { kind: 'site_delete', domain: 'blog.test', host: '*site*' }],
    ['service_stop', 'apache', { kind: 'service_restart', domain: 'apache', host: '*svc*' }],
    ['ssl_cert_delete', 'blog.test', { kind: 'ssl_cert_delete', domain: 'blog.test', host: '*ssl*' }],
    ['dns_record_delete', 'zone123', { kind: 'dns_record_delete', domain: 'zone123', host: '*dns*' }],
    ['binary_install', 'php', { kind: 'binary_install', domain: 'php', host: '*binary*' }],
    ['plugin_install', 'nks.wdc.redis', { kind: 'plugin_install', domain: 'nks.wdc.redis', host: '*plugin*' }],
    ['backup_restore', 'ignored', { kind: 'backup_restore', domain: '*backup*', host: '*backup*' }],
    ['mysql_user_drop', 'app@localhost', { kind: 'mysql_user_drop', domain: 'app@localhost', host: '*mysql*' }],
    ['mysql_root_reset', 'ignored', { kind: 'mysql_root_reset', domain: 'root', host: '*mysql*' }],
  ] as const)('%s on %s', (operation, target, expected) => {
    expect(intentFor(operation, target)).toEqual(expected)
  })
})

describe('intentHeaders', () => {
  it('sends the token as X-Intent-Token', () => {
    expect(intentHeaders('a.b.c')).toEqual({ 'X-Intent-Token': 'a.b.c' })
  })
  it('sends nothing without a token', () => {
    expect(intentHeaders(undefined)).toBeUndefined()
  })
})
