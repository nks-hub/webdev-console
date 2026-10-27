import { i18n } from '../i18n'

/**
 * Operator-locale label for an MCP destructive-operation kind: the
 * `mcpKinds.labels.<id>` translation, else the label the daemon registry
 * supplied, else the bare wire id. Same lookup the confirmation banner,
 * Intents page and Kinds catalog use, for surfaces outside a component
 * (OS notifications, toasts).
 */
export function mcpKindLabel(item: { kind?: string | null; kindLabel?: string | null }): string {
  if (!item.kind) return item.kindLabel || ''
  const key = `mcpKinds.labels.${item.kind}`
  return i18n.global.te(key) ? i18n.global.t(key) : (item.kindLabel || item.kind)
}
