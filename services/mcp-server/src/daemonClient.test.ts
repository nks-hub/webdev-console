/**
 * Daemon errors reach the agent as text. For MCP capability and intent
 * refusals the text must say what to do next (which WDC setting the user has
 * to enable, which intent to request).
 */

import { describe, it, expect } from 'vitest'

import { describeDaemonError } from './daemonClient.js'

describe('describeDaemonError', () => {
  it('surfaces the capability and the hint for mcp_capability_disabled', () => {
    const text = describeDaemonError(403, JSON.stringify({
      error: 'mcp_capability_disabled',
      capability: 'database_admin',
      settingKey: 'mcp.capabilities.database_admin',
      detail: 'POST /api/plugins/mysql/reset-password',
      hint: 'Enable it in WDC → Settings → MCP (mcp.capabilities.database_admin).',
    }))
    expect(text).toContain('mcp_capability_disabled')
    expect(text).toContain('capability: database_admin (mcp.capabilities.database_admin)')
    expect(text).toContain('Enable it in WDC → Settings → MCP')
  })

  it('surfaces the intent kind and target for intent_rejected', () => {
    const text = describeDaemonError(425, JSON.stringify({
      error: 'intent_rejected',
      reason: 'pending_confirmation',
      intentKind: 'node_process',
      intentDomain: 'app.loc',
      hint: 'Ask the user to approve it',
    }))
    expect(text).toContain('reason: pending_confirmation')
    expect(text).toContain('intent: kind node_process on app.loc')
    expect(text).toContain('Ask the user to approve it')
  })

  it('keeps plain errors short', () => {
    expect(describeDaemonError(400, JSON.stringify({ error: 'Invalid database name' }))).toBe('Invalid database name')
    expect(describeDaemonError(500, '')).toBe('HTTP 500')
    expect(describeDaemonError(502, 'x'.repeat(400))).toHaveLength(301)
  })
})
