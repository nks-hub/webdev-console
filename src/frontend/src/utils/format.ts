/** Locale-aware number formatting for the shell: Czech gets "0,0 %" and "26 MB". */

function unit(locale: string, unitName: string, value: number, fractionDigits = 0, fixed = false): string {
  return new Intl.NumberFormat(locale, {
    style: 'unit',
    unit: unitName,
    unitDisplay: 'short',
    maximumFractionDigits: fractionDigits,
    minimumFractionDigits: fixed ? fractionDigits : 0,
  }).format(value)
}

export function formatPercent(value: number, locale: string): string {
  return unit(locale, 'percent', value, 1, true)
}

export function formatBytes(bytes: number, locale: string): string {
  if (bytes < 1024 * 1024) return unit(locale, 'kilobyte', bytes / 1024)
  if (bytes < 1024 * 1024 * 1024) return unit(locale, 'megabyte', bytes / 1024 / 1024)
  return unit(locale, 'gigabyte', bytes / 1024 / 1024 / 1024, 1)
}

/** Megabytes with the unit, for chart axes and tooltips. */
export function formatMegabytes(mb: number, locale: string): string {
  return unit(locale, 'megabyte', mb, mb < 10 ? 1 : 0)
}

/** Two most significant parts of a duration: "50 s", "6 min", "1 h 5 min". */
export function formatDuration(totalSeconds: number, locale: string): string {
  const s = Math.max(0, Math.floor(totalSeconds))
  const d = Math.floor(s / 86400)
  const h = Math.floor((s % 86400) / 3600)
  const m = Math.floor((s % 3600) / 60)
  const parts: Array<[string, number]> = d > 0
    ? [['day', d], ['hour', h]]
    : h > 0 ? [['hour', h], ['minute', m]]
      : m > 0 ? [['minute', m]]
        : [['second', s]]
  return parts
    .filter(([, value], i) => i === 0 || value > 0)
    .map(([name, value]) => unit(locale, name, value))
    .join(' ')
}
