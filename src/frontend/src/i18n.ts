import { createI18n } from 'vue-i18n'
import en from './locales/en.json'
import cs from './locales/cs.json'

const STORAGE_KEY = 'wdc-locale'
const DEFAULT_LOCALE = 'en'
const SUPPORTED_LOCALES = ['en', 'cs'] as const
export type Locale = (typeof SUPPORTED_LOCALES)[number]

function detectInitialLocale(): Locale {
  // 1. Stored user preference wins
  try {
    const stored = localStorage.getItem(STORAGE_KEY)
    if (stored && (SUPPORTED_LOCALES as readonly string[]).includes(stored)) {
      return stored as Locale
    }
  } catch { /* localStorage may be unavailable */ }

  // 2. Browser language hint
  const browser = (navigator.language || '').toLowerCase()
  if (browser.startsWith('cs')) return 'cs'
  return DEFAULT_LOCALE
}

export const i18n = createI18n({
  legacy: false,
  locale: detectInitialLocale(),
  fallbackLocale: DEFAULT_LOCALE,
  messages: { en, cs },
})

export function setLocale(locale: Locale) {
  if (!(SUPPORTED_LOCALES as readonly string[]).includes(locale)) return
  ;(i18n.global.locale as unknown as { value: string }).value = locale
  try { localStorage.setItem(STORAGE_KEY, locale) } catch { /* ignore */ }
  document.documentElement.lang = locale
}

export const supportedLocales = SUPPORTED_LOCALES
