// Shared response formatting helpers. Every tool that returns data flows
// through `toolResponse()` so character-limit truncation and the
// markdown/json switch are applied consistently.

import { CHARACTER_LIMIT } from './constants.js'
import { ResponseFormat } from './schemas.js'

export interface ToolTextResult {
  [key: string]: unknown
  content: { type: 'text'; text: string }[]
  structuredContent?: Record<string, unknown>
  isError?: boolean
}

/**
 * Render a tool result. Always returns text content (the only content
 * type Claude Desktop currently displays inline). When `structured` is
 * provided AND the value is an object, also surface it as
 * `structuredContent` so newer MCP clients can read fields directly
 * without re-parsing the text.
 */
export function toolResponse(
  result: unknown,
  format: ResponseFormat = ResponseFormat.JSON,
  markdownRenderer?: (data: unknown) => string,
): ToolTextResult {
  let text: string
  if (format === ResponseFormat.MARKDOWN && markdownRenderer) {
    text = markdownRenderer(result)
  } else {
    text = typeof result === 'string' ? result : JSON.stringify(result, null, 2)
  }

  // Truncate oversize responses with a clear marker so the AI can ask
  // for a smaller window via filters or pagination.
  if (text.length > CHARACTER_LIMIT) {
    text =
      text.slice(0, CHARACTER_LIMIT) +
      `\n\n[truncated at ${CHARACTER_LIMIT} chars — ${text.length - CHARACTER_LIMIT} chars omitted. Use filters or pagination to narrow results.]`
  }

  const response: ToolTextResult = {
    content: [{ type: 'text', text }],
  }
  if (
    result !== null &&
    typeof result === 'object' &&
    !Array.isArray(result)
  ) {
    response.structuredContent = result as Record<string, unknown>
  }
  return response
}

/** Format a tool error. Always sets isError so the AI client can branch on it. */
export function toolError(message: string): ToolTextResult {
  return {
    isError: true,
    content: [{ type: 'text', text: `Error: ${message}` }],
  }
}
