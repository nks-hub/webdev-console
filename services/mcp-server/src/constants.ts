// Shared constants for the NKS WDC MCP server.

/**
 * Maximum response size in characters before truncation kicks in.
 * Long responses (full site lists, query results, large logs) are
 * cut at this boundary with a `truncated: true` marker so the AI
 * client doesn't see opaque "context length exceeded" errors.
 */
export const CHARACTER_LIMIT = 25000

/** Default page size for list operations. */
export const DEFAULT_LIMIT = 50

/** Maximum allowed page size for list operations. */
export const MAX_LIMIT = 200
