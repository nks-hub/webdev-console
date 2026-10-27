import { h, type VNode } from 'vue'
import { ElNotification, ElButton, ElMessage } from 'element-plus'
import { i18n } from '../i18n'
import { useMcpConfirmStore } from '../stores/mcpConfirm'
import { mcpKindLabel } from './mcpKindLabel'
import { mcpTargetLabel } from './mcpApproval'

/**
 * Phase 8 (3a) — corner toast for MCP confirm-requests.
 *
 * Complements the McpConfirmBanner stack: it says what the AI wants and
 * offers Reject and Show. Approving stays with the banner and the
 * Requests page, which confirm it first (and ask for the target to be
 * typed for anything irreversible); a one-click approve here would be the
 * weakest door to a destructive action.
 */
interface ToastEvent {
  intentId: string
  prompt?: string
  kind?: string
  kindLabel?: string
  domain?: string
  host?: string
}

const activeToasts = new Map<string, () => void>()

export function showMcpConfirmToast(evt: ToastEvent): void {
  // De-dupe: if a toast for this intent is already up, don't stack.
  if (activeToasts.has(evt.intentId)) return

  const t = i18n.global.t.bind(i18n.global)
  const close = (): void => {
    const closer = activeToasts.get(evt.intentId)
    if (closer) {
      closer()
      activeToasts.delete(evt.intentId)
    }
  }

  // Reject through the banner store so the matching banner goes away
  // together with the toast instead of lingering until expiry.
  const onReject = async (): Promise<void> => {
    try {
      if (await useMcpConfirmStore().reject(evt.intentId)) ElMessage.info(t('mcp.toast.rejectSuccess'))
      else ElMessage.info(t('mcp.banner.toastAlreadyConsumed'))
    } catch (err) {
      const msg = err instanceof Error ? err.message : String(err)
      ElMessage.error(t('mcp.toast.rejectFailed', { error: msg }))
    } finally {
      close()
    }
  }
  const onDetail = (): void => {
    // Hash-based router uses #/path — direct location.hash assignment
    // works without needing the router instance here.
    window.location.hash = '/mcp/intents'
    close()
  }

  const action = (): VNode =>
    h('div', { style: 'display:flex;gap:8px;flex-wrap:wrap;margin-top:8px' }, [
      h(
        ElButton,
        { type: 'primary', size: 'small', plain: true, onClick: onDetail },
        () => t('mcp.toast.detail'),
      ),
      h(
        ElButton,
        { type: 'danger', size: 'small', plain: true, onClick: onReject },
        () => t('mcp.toast.reject'),
      ),
    ])

  const kindText = mcpKindLabel(evt)
  const titleText = kindText
    ? t('mcp.toast.kindRequest', { kind: kindText })
    : t('mcp.toast.genericRequest')
  const targetText = mcpTargetLabel(evt)

  const message = (): VNode =>
    h('div', [
      targetText
        ? h('div', { style: 'font-size:12px' }, [
            t('mcp.banner.target') + ' ',
            h('code', { style: 'font-family:ui-monospace,Consolas,monospace' }, targetText),
          ])
        : null,
      evt.prompt
        ? h('div', { style: 'margin-top:4px;font-size:13px' }, [
            h('div', { style: 'font-size:11px;opacity:0.75' }, t('mcp.banner.aiSays')),
            evt.prompt,
          ])
        : null,
      action(),
    ].filter(Boolean) as VNode[])

  // 60s default — confirmation intents typically expire in 120s, so
  // even a busy operator gets to see the toast before timeout.
  const handle = ElNotification({
    title: titleText,
    message: message(),
    type: 'warning',
    position: 'bottom-right',
    duration: 60_000,
    showClose: true,
    customClass: 'mcp-confirm-toast',
    onClose: () => activeToasts.delete(evt.intentId),
  })
  activeToasts.set(evt.intentId, () => handle.close())
}

/** Close any toast for an intent that was resolved through the banner / GUI. */
export function closeMcpConfirmToast(intentId: string): void {
  const closer = activeToasts.get(intentId)
  if (closer) {
    closer()
    activeToasts.delete(intentId)
  }
}

