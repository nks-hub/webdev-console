// Shared response formatting helpers. Every tool that returns data flows
// through `toolResponse()` so character-limit truncation and the
// markdown/json switch are applied consistently.

import { CHARACTER_LIMIT } from './constants.js'

export interface ToolTextResult {
  [key: string]: unknown
  content: { type: 'text'; text: string }[]
  structuredContent?: Record<string, unknown>
  isError?: boolean
}

/**
 * Which end of an oversized result to keep. Lists that run oldest → newest
 * (access logs, error logs, service logs) keep the `tail` so the agent sees
 * the newest entries; everything else keeps the `head`.
 */
export type KeepEnd = 'head' | 'tail'

export interface ResponseOptions {
  keep?: KeepEnd
}

function serialize(value: unknown): string {
  if (typeof value === 'string') return value
  if (value === undefined || value === null) return 'null'
  return JSON.stringify(value, null, 2)
}

function isPlainObject(v: unknown): v is Record<string, unknown> {
  return typeof v === 'object' && v !== null && !Array.isArray(v)
}

/**
 * Trim the result's main array (the result itself, or its largest top-level
 * array property) to as many items as fit, keeping the requested end. Trimming
 * the data rather than the serialised text keeps every kept item intact and
 * avoids returning JSON escaped inside a JSON string.
 */
function trimArray(result: unknown, keep: KeepEnd, limit: number): string | null {
  let items: unknown[]
  let rebuild: (kept: unknown[], note: Record<string, unknown>) => unknown
  if (Array.isArray(result)) {
    items = result
    rebuild = (kept, note) => ({ truncated: true, ...note, items: kept })
  } else if (isPlainObject(result)) {
    let field: string | undefined
    let largest = -1
    for (const [k, v] of Object.entries(result)) {
      if (!Array.isArray(v)) continue
      const size = JSON.stringify(v).length
      if (size > largest) {
        largest = size
        field = k
      }
    }
    if (field === undefined) return null
    const key = field
    items = result[key] as unknown[]
    rebuild = (kept, note) => ({ ...result, [key]: kept, truncated: true, truncation: { field: key, ...note } })
  } else {
    return null
  }

  const total = items.length
  const take = (n: number) => (keep === 'tail' ? items.slice(total - n) : items.slice(0, n))
  const render = (n: number) =>
    serialize(
      rebuild(take(n), {
        totalItems: total,
        returnedItems: n,
        omittedItems: total - n,
        kept: keep === 'tail' ? 'last (newest)' : 'first',
        hint:
          'Response exceeded the character limit, so only part of the list is returned. ' +
          'Narrow the query with filters, a smaller limit, or pagination.',
      }),
    )

  // Largest n whose rendering fits; the envelope grows monotonically with n.
  let lo = 0
  let hi = total
  while (lo < hi) {
    const mid = Math.ceil((lo + hi) / 2)
    if (render(mid).length <= limit) lo = mid
    else hi = mid - 1
  }
  if (lo === 0) return null // not even one item fits; fall back to a text preview
  return render(lo)
}

function trimText(text: string, keep: KeepEnd, limit: number): string {
  const budget = limit - 200
  if (keep === 'tail') {
    let tail = text.slice(text.length - budget)
    const nl = tail.indexOf('\n')
    if (nl >= 0 && nl < 1000) tail = tail.slice(nl + 1)
    return `[truncated: ${text.length - tail.length} earlier characters omitted]\n` + tail
  }
  let head = text.slice(0, budget)
  const nl = head.lastIndexOf('\n')
  if (nl >= 0 && head.length - nl < 1000) head = head.slice(0, nl)
  return head + `\n[truncated: ${text.length - head.length} later characters omitted]`
}

/**
 * Render a tool result as an MCP text response.
 *
 * Objects are serialized as pretty-printed JSON. An oversized result has its
 * main array trimmed (keeping the `head` or the `tail`, see {@link KeepEnd})
 * and is marked `truncated: true`; when there is no array to trim it falls
 * back to a JSON envelope with a text preview, so the text always stays
 * parseable.
 *
 * `structuredContent` is intentionally NOT attached here — per the MCP
 * spec, attaching `structuredContent` without declaring an `outputSchema`
 * on the tool leaves some clients in undefined territory. Keep the
 * surface area narrow: `content[].text` is always the source of truth.
 */
export function toolResponse(result: unknown, options: ResponseOptions = {}): ToolTextResult {
  const keep = options.keep ?? 'head'
  let text = serialize(result)

  if (text.length > CHARACTER_LIMIT) {
    if (typeof result === 'string') {
      text = trimText(text, keep, CHARACTER_LIMIT)
    } else {
      const trimmed = trimArray(result, keep, CHARACTER_LIMIT)
      if (trimmed !== null) {
        text = trimmed
      } else {
        const budget = CHARACTER_LIMIT - 400
        const preview = keep === 'tail' ? text.slice(text.length - budget) : text.slice(0, budget)
        text = JSON.stringify(
          {
            truncated: true,
            originalLength: text.length,
            omittedChars: text.length - preview.length,
            kept: keep === 'tail' ? 'end' : 'start',
            hint:
              'Response exceeded the character limit and was truncated. ' +
              'Narrow the query with filters or pagination.',
            preview,
          },
          null,
          2,
        )
      }
    }
  }

  return {
    content: [{ type: 'text', text }],
  }
}

/** Format a tool error. Always sets isError so the AI client can branch on it. */
export function toolError(message: string): ToolTextResult {
  return {
    isError: true,
    content: [{ type: 'text', text: `Error: ${message}` }],
  }
}

/**
 * Wrap a daemon call so any thrown Error is converted into a
 * well-formed MCP tool error response. Every tool handler should flow
 * through this — it's the single place error text is shaped.
 */
export async function safe(fn: () => Promise<unknown>, options?: ResponseOptions): Promise<ToolTextResult> {
  try {
    return toolResponse(await fn(), options)
  } catch (err) {
    return toolError(err instanceof Error ? err.message : String(err))
  }
}
