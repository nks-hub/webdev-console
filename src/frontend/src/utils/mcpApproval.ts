import { h } from 'vue'
import { ElMessageBox } from 'element-plus'
import { i18n } from '../i18n'
import { mcpKindLabel } from './mcpKindLabel'

/** What the banner, the toast and the Requests page know about an AI request. */
export interface McpApprovalItem {
  kind?: string | null
  kindLabel?: string | null
  kindDanger?: string | null
  domain?: string | null
  host?: string | null
  prompt?: string | null
}

// The MCP server fills domain/host with markers such as '*db*' or
// '*backup*' where an operation has no real value; they mean nothing to
// the operator.
export function isMcpTargetMarker(value: string | null | undefined): boolean {
  return !value || value === '*' || /^\*.*\*$/.test(value)
}

/** The real target of a request ("blog.loc", "blog.loc → prod1"), '' when there is none. */
export function mcpTargetLabel(item: McpApprovalItem): string {
  const parts = [item.domain, item.host].filter((v): v is string => !isMcpTargetMarker(v))
  return [...new Set(parts)].join(' → ')
}

/**
 * Asks the operator to approve one AI request. Anything not known to be
 * reversible needs the target typed in (or a confirmation word when there
 * is no target), so neither Enter nor a stray click can approve it.
 * `extra` adds a sentence, e.g. the rule an approve-and-trust button also
 * creates. Resolves false when the operator cancels.
 */
export async function confirmMcpApproval(item: McpApprovalItem, extra = ''): Promise<boolean> {
  const t = i18n.global.t
  const target = mcpTargetLabel(item)
  const lines = [
    t('mcpApproval.what', {
      kind: mcpKindLabel(item) || t('mcpApproval.unknownKind'),
      target: target || t('mcpApproval.noTarget'),
    }),
    item.prompt ? t('mcpApproval.aiSays', { prompt: item.prompt }) : t('mcpApproval.aiSaysNothing'),
  ]
  if (extra) lines.push(extra)
  const message = (rows: string[]) => h('div', { class: 'mcp-approval-message' }, rows.map((r) => h('p', r)))
  try {
    if (item.kindDanger !== 'reversible') {
      const expected = isMcpTargetMarker(item.domain) ? t('mcpApproval.word') : item.domain!
      await ElMessageBox.prompt(message([...lines, t('mcpApproval.typeToConfirm', { expected })]), t('mcpApproval.titleIrreversible'), {
        type: 'warning',
        confirmButtonText: t('mcpApproval.approve'),
        cancelButtonText: t('common.cancel'),
        inputPlaceholder: expected,
        inputValidator: (value: string) => value.trim() === expected || t('mcpApproval.mismatch', { expected }),
      })
    } else {
      await ElMessageBox.confirm(message(lines), t('mcpApproval.title'), {
        type: 'info',
        confirmButtonText: t('mcpApproval.approve'),
        cancelButtonText: t('common.cancel'),
      })
    }
    return true
  } catch {
    return false
  }
}
