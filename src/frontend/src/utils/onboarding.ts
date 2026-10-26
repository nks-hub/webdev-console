// The onboarding wizard is mounted once in App.vue; other pages ask it to
// reopen through this window event instead of holding a component ref.
export const OPEN_ONBOARDING_EVENT = 'wdc:open-onboarding'

export function requestOnboarding(): void {
  window.dispatchEvent(new CustomEvent(OPEN_ONBOARDING_EVENT))
}
