import type { Directive } from 'vue'

/**
 * Makes a clickable non-button element (sidebar rows, tiles) reachable and
 * operable from the keyboard: focusable, announced with a role, and
 * activated by Enter/Space like a native control.
 *
 *   <div v-activate @click="…">        → role="button"
 *   <div v-activate:link @click="…">   → role="link" (navigation)
 *
 * Key presses that originate inside a nested interactive control (a switch
 * in the row) are left to that control.
 */
const handlers = new WeakMap<HTMLElement, (e: KeyboardEvent) => void>()

export const vActivate: Directive<HTMLElement> = {
  mounted(el, binding) {
    if (!el.hasAttribute('tabindex')) el.setAttribute('tabindex', '0')
    if (!el.hasAttribute('role')) el.setAttribute('role', binding.arg === 'link' ? 'link' : 'button')
    const onKey = (e: KeyboardEvent) => {
      if (e.target !== el) return
      if (e.key === 'Enter' || (e.key === ' ' && binding.arg !== 'link')) {
        e.preventDefault()
        el.click()
      }
    }
    handlers.set(el, onKey)
    el.addEventListener('keydown', onKey)
  },
  beforeUnmount(el) {
    const onKey = handlers.get(el)
    if (onKey) el.removeEventListener('keydown', onKey)
    handlers.delete(el)
  },
}
