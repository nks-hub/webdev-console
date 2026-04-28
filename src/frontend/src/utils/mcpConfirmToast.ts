import { h, type VNode } from 'vue'
import { ElNotification, ElButton, ElMessage } from 'element-plus'
import { i18n } from '../i18n'
import { confirmIntent, revokeIntent } from '../api/daemon'

/**
 * Phase 8 (3a) — inline approve toast for MCP confirm-requests.
 *
 * Complements the full McpConfirmBanner stack — when an SSE
 * `mcp:confirm-request` arrives, in addition to mounting a banner row
 * we also pop a corner toast with one-click Approve / Reject so the
 * operator doesn't need to scroll to the banner if they were focused
 * elsewhere.
 *
 * The banner is still authoritative — it carries the queue, expiry
 * countdown, trust shortcuts. The toast is the "right here, right
 * now" surface.
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

  const onApprove = async (): Promise<void> => {
    try {
      await confirmIntent(evt.intentId)
      ElMessage.success(t('mcp.toast.approveSuccess'))
    } catch (err) {
      // Surface the failure visibly — silent close was leaving operator
      // wondering whether the request actually went through. Most common
      // case: intent expired between SSE arrival and click.
      const msg = err instanceof Error ? err.message : String(err)
      ElMessage.error(t('mcp.toast.approveFailed', { error: msg }))
    } finally {
      close()
    }
  }
  const onReject = async (): Promise<void> => {
    try {
      await revokeIntent(evt.intentId)
      ElMessage.info(t('mcp.toast.rejectSuccess'))
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
        { type: 'primary', size: 'small', onClick: onApprove },
        () => t('mcp.toast.approve'),
      ),
      h(
        ElButton,
        { type: 'danger', size: 'small', plain: true, onClick: onReject },
        () => t('mcp.toast.reject'),
      ),
      h(
        ElButton,
        { size: 'small', link: true, onClick: onDetail },
        () => t('mcp.toast.detail'),
      ),
    ])

  const titleText = evt.kindLabel
    ? t('mcp.toast.kindRequest', { kind: evt.kindLabel })
    : evt.kind
      ? t('mcp.toast.kindRequest', { kind: evt.kind })
      : t('mcp.toast.genericRequest')
  const targetText = evt.domain && evt.host ? `${evt.domain}/${evt.host}` : ''

  const message = (): VNode =>
    h('div', [
      targetText
        ? h('div', { style: 'font-family:ui-monospace,Consolas,monospace;font-size:12px;opacity:0.85' }, targetText)
        : null,
      evt.prompt
        ? h('div', { style: 'margin-top:4px;font-size:13px' }, evt.prompt)
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

