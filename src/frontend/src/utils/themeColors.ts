import { computed } from 'vue'
import { useThemeStore } from '../stores/theme'

/**
 * Canvas libraries (ECharts) need concrete colours, not `var(--token)` and
 * not every CSS colour syntax (the tokens are oklch). Paint one pixel with
 * the token's computed value and read it back as rgb.
 */
let ctx: CanvasRenderingContext2D | null = null

export interface Rgb { r: number; g: number; b: number }

export function resolveToken(name: string, fallback: Rgb = { r: 128, g: 128, b: 128 }): Rgb {
  const value = getComputedStyle(document.documentElement).getPropertyValue(name).trim()
  if (!value) return fallback
  ctx ??= document.createElement('canvas').getContext('2d', { willReadFrequently: true })
  if (!ctx) return fallback
  ctx.clearRect(0, 0, 1, 1)
  ctx.fillStyle = '#000'
  ctx.fillStyle = value
  ctx.fillRect(0, 0, 1, 1)
  const [r, g, b] = ctx.getImageData(0, 0, 1, 1).data
  return { r, g, b }
}

export function rgba(c: Rgb, alpha = 1): string {
  return `rgba(${c.r}, ${c.g}, ${c.b}, ${alpha})`
}

/** Chart palette that re-resolves when the theme flips. */
export function useChartPalette() {
  const theme = useThemeStore()
  return computed(() => {
    void theme.isDark // dependency: the theme class is applied before consumers re-render
    return {
      accent: resolveToken('--wdc-accent'),
      success: resolveToken('--wdc-status-running'),
      text: resolveToken('--wdc-text'),
      text3: resolveToken('--wdc-text-3'),
      border: resolveToken('--wdc-border'),
      elevated: resolveToken('--wdc-elevated'),
      borderStrong: resolveToken('--wdc-border-strong'),
    }
  })
}
