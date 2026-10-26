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

// Czech has three plural forms: 1 | 2–4 | 0 and 5+ ("1 host | 2 hosti |
// 5 hostů"). vue-i18n's default rule maps three-form messages to
// zero | one | many, which rendered "1 hosti" and "2 hostů".
function czechPluralRule(choice: number, choicesLength: number): number {
  const n = Math.abs(choice)
  if (choicesLength === 3) return n === 1 ? 0 : n >= 2 && n <= 4 ? 1 : 2
  if (choicesLength === 2) return n === 1 ? 0 : 1
  return Math.min(n, choicesLength - 1)
}

export const i18n = createI18n({
  legacy: false,
  locale: detectInitialLocale(),
  fallbackLocale: DEFAULT_LOCALE,
  warnHtmlMessage: false,
  messages: { en, cs },
  pluralRules: { cs: czechPluralRule },
})

export function setLocale(locale: Locale) {
  if (!(SUPPORTED_LOCALES as readonly string[]).includes(locale)) return
  ;(i18n.global.locale as unknown as { value: string }).value = locale
  try { localStorage.setItem(STORAGE_KEY, locale) } catch { /* ignore */ }
  document.documentElement.lang = locale
}
