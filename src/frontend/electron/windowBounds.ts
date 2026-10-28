export interface WindowState { width: number; height: number; x?: number; y?: number; maximized?: boolean }

export interface Rect { x: number; y: number; width: number; height: number }

// Outer size, title bar included. 720x640 leaves about 700x610 of page: wide enough that
// the header keeps the Simple/Advanced switch (it hides at 640px) and tall enough for the
// shell's 580px minimum (App.vue .app-root), so the menu and status bar are never cut off.
export const MIN_WINDOW_WIDTH = 720
export const MIN_WINDOW_HEIGHT = 640

const DEFAULT_WIDTH = 1280
const DEFAULT_HEIGHT = 800

/**
 * Where the first window opens when there is no saved state: as much of 1280x800 as the
 * primary display's work area allows, centred in it. Never smaller than the minimum
 * size, and never starting left of or above the work area.
 */
export function defaultWindowState(workArea: Rect): WindowState {
  const width = Math.max(MIN_WINDOW_WIDTH, Math.min(DEFAULT_WIDTH, workArea.width))
  const height = Math.max(MIN_WINDOW_HEIGHT, Math.min(DEFAULT_HEIGHT, workArea.height))
  return {
    width,
    height,
    x: workArea.x + Math.max(0, Math.round((workArea.width - width) / 2)),
    y: workArea.y + Math.max(0, Math.round((workArea.height - height) / 2)),
  }
}
