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
 * Render a tool result as an MCP text response.
 *
 * Objects are serialized as pretty-printed JSON. Oversize payloads are
 * wrapped in a truncation envelope so the text remains parseable as JSON
 * (mid-string slicing would break clients that re-parse the `text` field).
 *
 * `structuredContent` is intentionally NOT attached here — per the MCP
 * spec, attaching `structuredContent` without declaring an `outputSchema`
 * on the tool leaves some clients in undefined territory. Keep the
 * surface area narrow: `content[].text` is always the source of truth.
 */
export function toolResponse(result: unknown): ToolTextResult {
  let text: string
  if (typeof result === 'string') {
    text = result
  } else if (result === undefined || result === null) {
    text = 'null'
  } else {
    text = JSON.stringify(result, null, 2)
  }

  if (text.length > CHARACTER_LIMIT) {
    const preview = text.slice(0, CHARACTER_LIMIT - 200)
    const omitted = text.length - preview.length
    // Re-wrap in a valid JSON envelope so the caller can still
    // `JSON.parse()` the payload even after truncation.
    text = JSON.stringify(
      {
        truncated: true,
        originalLength: text.length,
        omittedChars: omitted,
        hint:
          'Response exceeded the character limit and was truncated. ' +
          'Narrow the query with filters or pagination.',
        preview,
      },
      null,
      2,
    )
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
export async function safe(fn: () => Promise<unknown>): Promise<ToolTextResult> {
  try {
    return toolResponse(await fn())
  } catch (err) {
    return toolError(err instanceof Error ? err.message : String(err))
  }
}
