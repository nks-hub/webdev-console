/**
 * Truncation of oversized tool results. Lists are trimmed item by item
 * (never mid-item, never JSON-in-a-string), from the end the tool asks for.
 */

import { describe, expect, it } from 'vitest'

import { CHARACTER_LIMIT } from './constants.js'
import { safe, toolResponse } from './formatting.js'

const entries = (n: number) =>
  Array.from({ length: n }, (_, i) => ({ seq: i, path: `/page/${i}`, agent: 'x'.repeat(200) }))

const parse = (r: { content: { text: string }[] }) => JSON.parse(r.content[0]!.text)

describe('toolResponse', () => {
  it('passes small results through unchanged', () => {
    expect(toolResponse({ a: 1 }).content[0]!.text).toBe(JSON.stringify({ a: 1 }, null, 2))
    expect(toolResponse('plain').content[0]!.text).toBe('plain')
    expect(toolResponse(null).content[0]!.text).toBe('null')
  })

  it('keeps the newest entries of an oldest → newest log with keep: tail', () => {
    const result = { domain: 'shop.loc', count: 500, entries: entries(500) }
    const r = toolResponse(result, { keep: 'tail' })
    expect(r.content[0]!.text.length).toBeLessThanOrEqual(CHARACTER_LIMIT)
    const body = parse(r)
    expect(body.domain).toBe('shop.loc')
    expect(body.truncated).toBe(true)
    expect(body.truncation.field).toBe('entries')
    expect(body.truncation.totalItems).toBe(500)
    expect(body.truncation.kept).toMatch(/last/)
    expect(body.entries.at(-1).seq).toBe(499)
    expect(body.entries[0].seq).toBe(500 - body.entries.length)
    expect(body.truncation.omittedItems).toBe(500 - body.entries.length)
  })

  it('keeps the first entries by default', () => {
    const body = parse(toolResponse({ entries: entries(500) }))
    expect(body.entries[0].seq).toBe(0)
    expect(body.truncation.kept).toBe('first')
  })

  it('trims the largest array when there are several', () => {
    const body = parse(toolResponse({ topClients: [{ ip: '1.2.3.4' }], entries: entries(500) }, { keep: 'tail' }))
    expect(body.topClients).toEqual([{ ip: '1.2.3.4' }])
    expect(body.truncation.field).toBe('entries')
  })

  it('wraps a top-level array in an items envelope', () => {
    const body = parse(toolResponse(entries(500), { keep: 'tail' }))
    expect(body.truncated).toBe(true)
    expect(body.items.at(-1).seq).toBe(499)
    expect(body.totalItems).toBe(500)
  })

  it('does not escape JSON inside a string when trimming a list', () => {
    const text = toolResponse({ entries: entries(500) }).content[0]!.text
    expect(text).not.toContain('\\"seq\\"')
  })

  it('keeps the end of a long text with keep: tail', () => {
    const lines = Array.from({ length: 3000 }, (_, i) => `line ${i} ${'y'.repeat(20)}`).join('\n')
    const text = toolResponse(lines, { keep: 'tail' }).content[0]!.text
    expect(text.length).toBeLessThanOrEqual(CHARACTER_LIMIT)
    expect(text.startsWith('[truncated:')).toBe(true)
    expect(text.trimEnd().endsWith(`line 2999 ${'y'.repeat(20)}`)).toBe(true)
    expect(text.split('\n')[1]).toMatch(/^line \d+ y+$/)
  })

  it('keeps the start of a long text by default', () => {
    const text = toolResponse('z'.repeat(CHARACTER_LIMIT * 2)).content[0]!.text
    expect(text.startsWith('zzz')).toBe(true)
    expect(text).toMatch(/\[truncated: \d+ later characters omitted\]$/)
  })

  it('falls back to a parseable preview envelope when there is no array', () => {
    const body = parse(toolResponse({ blob: 'q'.repeat(CHARACTER_LIMIT * 2) }, { keep: 'tail' }))
    expect(body.truncated).toBe(true)
    expect(body.kept).toBe('end')
    expect(typeof body.preview).toBe('string')
  })

  it('falls back to the preview envelope when a single item is too large', () => {
    const body = parse(toolResponse({ entries: [{ big: 'q'.repeat(CHARACTER_LIMIT * 2) }] }))
    expect(body.truncated).toBe(true)
    expect(body.preview).toBeDefined()
  })
})

describe('safe', () => {
  it('forwards the keep option', async () => {
    const r = await safe(async () => ({ entries: entries(500) }), { keep: 'tail' })
    expect(parse(r).entries.at(-1).seq).toBe(499)
  })

  it('turns thrown errors into isError results', async () => {
    const r = await safe(async () => {
      throw new Error('nope')
    })
    expect(r.isError).toBe(true)
    expect(r.content[0]!.text).toBe('Error: nope')
  })
})
