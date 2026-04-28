import { test, expect } from './_fixtures'

// Phase 7.4 #109-D1+ iter 35 — verify the iter 28 deploy:settings-changed
// SSE broadcast fires on deploy.* settings save. The bash AAA section
// already covers this end-to-end via curl -N; Playwright version uses
// fetch with a streaming body reader so the contract is locked from
// the same TS test infrastructure that consumes it (DeploySettingsPanel,
// Settings.vue both subscribe via subscribeEventsMap → /api/events).
//
// Pattern: open /api/events stream, fire PUT /api/settings, scan the
// chunked body for the expected event line, abort.

test.describe('SSE deploy:settings-changed broadcast (#109-D1+ iter 28)', () => {
  test('PUT /api/settings with deploy.* key fires deploy:settings-changed', async ({ authedRequest, daemonAuth }) => {
    // Spawn a streaming fetch — Playwright's request fixture exposes
    // body() but not chunked reading; we use the underlying URL +
    // bearer header with the global fetch instead.
    const controller = new AbortController()
    const eventsPromise = (async () => {
      const r = await fetch(`http://localhost:${daemonAuth.port}/api/events`, {
        headers: { Authorization: `Bearer ${daemonAuth.token}` },
        signal: controller.signal,
      })
      const reader = r.body!.getReader()
      const decoder = new TextDecoder()
      let buffer = ''
      const seen: string[] = []
      const deadline = Date.now() + 5000
      while (Date.now() < deadline) {
        const { done, value } = await reader.read().catch(() => ({ done: true, value: undefined } as ReadableStreamReadResult<Uint8Array>))
        if (done) break
        buffer += decoder.decode(value, { stream: true })
        const lines = buffer.split('\n')
        buffer = lines.pop() ?? ''
        for (const line of lines) {
          if (line.startsWith('event: ')) seen.push(line.slice('event: '.length).trim())
          // Bail early once the target event arrives.
          if (seen.includes('deploy:settings-changed')) {
            controller.abort()
            return seen
          }
        }
      }
      return seen
    })()

    // Tiny sleep so the SSE stream subscription is established before
    // the broadcast — without this, the event fires before the reader
    // attaches and we miss it.
    await new Promise((r) => setTimeout(r, 250))

    const flip = await authedRequest.put('/api/settings', {
      data: { 'deploy.useLegacyHostHandlers': 'true' },
    })
    expect(flip.status()).toBe(200)

    const seen = await eventsPromise
    expect(seen, `events seen on /api/events stream: ${seen.join(',')}`).toContain('deploy:settings-changed')
  })
})
