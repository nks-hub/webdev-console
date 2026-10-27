// The command palette is mounted once in App.vue; the header search field
// opens it through this window event instead of holding a component ref.
export const OPEN_COMMAND_PALETTE_EVENT = 'wdc:open-command-palette'

export function requestCommandPalette(): void {
  window.dispatchEvent(new CustomEvent(OPEN_COMMAND_PALETTE_EVENT))
}
