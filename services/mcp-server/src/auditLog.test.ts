/**
 * Unit tests for `wrapHandler()` — covers:
 *   1. Successful call propagates result + records ok audit row
 *   2. Failed call re-throws + records error audit row
 *   3. Audit POST failure does not break the wrapped handler
 *   4. Danger level is classified by static map (read default, override)
 *   5. Args are JSON-serialised + hashed
 */

import { describe, it, expect, vi, beforeEach } from 'vitest'

// daemonClient mock — wrapHandler imports it to fire the audit POST.
// Mock factory must be hoisted, so we capture the mock fn inside the
// factory closure and re-import it after vi.mock() resolves.
vi.mock('./daemonClient.js', () => ({
  daemonClient: {
    post: vi.fn(() => Promise.resolve({ id: 'mock-id' })),
  },
}))

// Import AFTER vi.mock so the wrap module sees the mocked client.
import { wrapHandler } from './auditLog.js'
import { daemonClient } from './daemonClient.js'

const mockPost = vi.mocked(daemonClient.post)

beforeEach(() => {
  mockPost.mockClear()
  mockPost.mockImplementation(() => Promise.resolve({ id: 'mock-id' }))
})

// Wait one microtask + a tick so the fire-and-forget POST scheduled in
// `finally` has a chance to run before assertions.
async function flush(): Promise<void> {
  await new Promise(resolve => setTimeout(resolve, 0))
}

describe('wrapHandler', () => {
  it('propagates the original handler result', async () => {
    const original = vi.fn(async () => ({ content: [{ type: 'text', text: 'hi' }] }))
    const wrapped = wrapHandler('wdc_get_status', original)

    const result = await wrapped({})
    expect(result).toEqual({ content: [{ type: 'text', text: 'hi' }] })
    expect(original).toHaveBeenCalledOnce()
  })

  it('records ok audit row after success', async () => {
    const original = vi.fn(async () => ({ content: [] }))
    const wrapped = wrapHandler('wdc_get_status', original)

    await wrapped({ foo: 'bar' })
    await flush()

    expect(mockPost).toHaveBeenCalledOnce()
    const [path, body] = mockPost.mock.calls[0]
    expect(path).toBe('/api/mcp/tool-calls')
    expect(body).toMatchObject({
      toolName: 'wdc_get_status',
      caller: 'mcp-server',
      dangerLevel: 'read',
      resultCode: 'ok',
      argsSummary: '{"foo":"bar"}',
    })
    // Hash should be 16-char hex (sha256 truncated).
    expect(body.argsHash).toMatch(/^[0-9a-f]{16}$/)
    // Duration is non-negative integer.
    expect(body.durationMs).toBeGreaterThanOrEqual(0)
    // Session id is a UUID generated at module load.
    expect(body.sessionId).toMatch(/^[0-9a-f-]{36}$/)
  })

  it('classifies destructive tools correctly', async () => {
    const original = vi.fn(async () => ({ content: [] }))
    const wrapped = wrapHandler('wdc_deploy_site', original)

    await wrapped({})
    await flush()

    expect(mockPost.mock.calls[0][1]).toMatchObject({
      toolName: 'wdc_deploy_site',
      dangerLevel: 'destructive',
    })
  })

  it('classifies mutate tools correctly', async () => {
    const original = vi.fn(async () => ({ content: [] }))
    const wrapped = wrapHandler('wdc_create_site', original)

    await wrapped({})
    await flush()

    expect(mockPost.mock.calls[0][1]).toMatchObject({
      dangerLevel: 'mutate',
    })
  })

  it('re-throws original errors and still records audit', async () => {
    const original = vi.fn(async () => {
      throw new Error('boom')
    })
    const wrapped = wrapHandler('wdc_smoke', original)

    await expect(wrapped({})).rejects.toThrow('boom')
    await flush()

    expect(mockPost).toHaveBeenCalledOnce()
    expect(mockPost.mock.calls[0][1]).toMatchObject({
      toolName: 'wdc_smoke',
      resultCode: 'error',
      errorMessage: 'boom',
    })
  })

  it('truncates args summary to 500 chars', async () => {
    const original = vi.fn(async () => ({ content: [] }))
    const wrapped = wrapHandler('wdc_smoke', original)

    const huge = { data: 'x'.repeat(2000) }
    await wrapped(huge)
    await flush()

    const summary = mockPost.mock.calls[0][1].argsSummary
    expect(summary.length).toBeLessThanOrEqual(500)
    expect(summary.endsWith('...')).toBe(true)
  })

  it('does not break when audit POST fails', async () => {
    mockPost.mockImplementation(() => Promise.reject(new Error('relay down')))
    const original = vi.fn(async () => ({ content: [{ type: 'text', text: 'ok' }] }))
    const wrapped = wrapHandler('wdc_smoke', original)

    // Should still resolve with the original result despite POST failure.
    const result = await wrapped({})
    expect(result).toEqual({ content: [{ type: 'text', text: 'ok' }] })
    await flush()
    expect(mockPost).toHaveBeenCalledOnce()
  })
})
