import { defineStore } from 'pinia'
import { ref, computed } from 'vue'
import { catalogMe, type CatalogMeResponse } from '../api/daemon'

/**
 * F83 SSO store. Holds the catalog-api session token obtained via the
 * OIDC flow redirected through the `wdc://auth-callback` deep link.
 * Persists to `localStorage` so the desktop app survives restarts
 * without forcing a re-auth.
 *
 * The flow:
 *   1. `login(catalogUrl)` opens the system browser at
 *      `<catalogUrl>/auth/sso/login?redirect_uri=wdc://auth-callback`
 *   2. User signs in at the IdP, catalog-api 303s to `wdc://auth-callback?token=...`
 *   3. Electron main catches the deep-link, forwards `{token,error}` via
 *      the `sso-callback` IPC event.
 *   4. This store's registered `onSsoCallback` listener resolves the
 *      pending login Promise with the token (or rejects on error).
 *
 * Outside of Electron the API surface is a no-op — `electronAPI` is
 * undefined in a browser so callers fall back to cookie-based auth.
 */
const TOKEN_KEY = 'nks-wdc-sso-token'

/**
 * F91.6: best-effort JWT claim decode. JWTs are `header.payload.signature`
 * base64url-encoded, and the payload is plain JSON. We don't verify the
 * signature here — the daemon/catalog do that on every API call. This
 * decode exists purely so the UI can display who is signed in. Any parse
 * failure returns null and the UI falls back to the generic "Signed in".
 */
export interface SsoIdentity {
  email?: string
  name?: string
  sub?: string
  exp?: number
}
function decodeJwtClaims(jwt: string): SsoIdentity | null {
  try {
    const parts = jwt.split('.')
    if (parts.length < 2) return null
    const pad = parts[1].length % 4 === 0 ? '' : '='.repeat(4 - (parts[1].length % 4))
    const b64 = parts[1].replace(/-/g, '+').replace(/_/g, '/') + pad
    const json = atob(b64)
    const claims = JSON.parse(json) as Record<string, unknown>
    const s = (k: string) => typeof claims[k] === 'string' ? (claims[k] as string) : undefined
    const n = (k: string) => typeof claims[k] === 'number' ? (claims[k] as number) : undefined
    // F91.8: widen the fallback chain — different IdPs use different claim
    // keys (email / mail / upn / unique_name) and some only ship a short
    // username (preferred_username / nickname / username). Sub is always
    // present by JWT spec so it's our last-resort display value.
    return {
      email: s('email') ?? s('mail') ?? s('upn') ?? s('unique_name'),
      name: s('name') ?? s('preferred_username') ?? s('nickname') ?? s('username') ?? s('given_name'),
      sub: s('sub'),
      exp: n('exp'),
    }
  } catch { return null }
}

// F91.9: persist the catalog-fetched profile so a page reload can show
// "Signed in as …" before the network round-trip completes.
const PROFILE_KEY = 'nks-wdc-sso-profile'

export const useAuthStore = defineStore('auth', () => {
  const token = ref<string>(localStorage.getItem(TOKEN_KEY) ?? '')
  const loginError = ref<string>('')
  const loginPending = ref(false)
  // F91.9: authoritative profile fetched from catalog /api/v1/auth/me.
  // Falls back to JWT claim decode when the catalog is unreachable.
  const profile = ref<CatalogMeResponse | null>(
    (() => {
      try {
        const raw = localStorage.getItem(PROFILE_KEY)
        return raw ? JSON.parse(raw) as CatalogMeResponse : null
      } catch { return null }
    })()
  )

  const isAuthenticated = computed(() => token.value.length > 0)

  // F91.6: identity derived from JWT claims. Reactive on token change so
  // the UI re-renders "Signed in as …" the moment login completes.
  const identity = computed<SsoIdentity | null>(() =>
    token.value ? decodeJwtClaims(token.value) : null
  )
  // F91.9: displayName prefers the catalog-authoritative profile.email /
  // name over JWT claims — the catalog always has the richer data because
  // it inherits from its own SSO session, while the JWT in WDC's hands may
  // only carry sub + exp.
  const displayName = computed(() => {
    const p = profile.value
    if (p) return p.email || p.name || p.username || p.id || ''
    const i = identity.value
    if (!i) return ''
    return i.email || i.name || i.sub || ''
  })

  function setToken(newToken: string) {
    token.value = newToken
    if (newToken) localStorage.setItem(TOKEN_KEY, newToken)
    else localStorage.removeItem(TOKEN_KEY)
  }

  function setProfile(p: CatalogMeResponse | null) {
    profile.value = p
    if (p) localStorage.setItem(PROFILE_KEY, JSON.stringify(p))
    else localStorage.removeItem(PROFILE_KEY)
  }

  /**
   * F91.9: pull authoritative profile from the catalog using the stored
   * token. Called right after SSO completes AND on-demand from the
   * Settings page whenever the user views the Account tab. Silently
   * falls back to JWT claims when the catalog is offline — worst case
   * the UI shows JWT sub instead of the email.
   */
  async function refreshProfile(catalogUrl: string): Promise<void> {
    if (!token.value) { setProfile(null); return }
    try {
      const me = await catalogMe(catalogUrl, token.value)
      setProfile(me)
    } catch (err) {
      // F91.13: an HTTP 401 means the token is stale — JWT expired, catalog
      // restarted with a new signing key, or the Account was deleted. Clear
      // the token so the UI stops showing "Signed in" when the session is
      // actually dead. Other errors (network, 5xx) keep the cached profile.
      const msg = err instanceof Error ? err.message : String(err)
      if (/\b401\b|unauthori/i.test(msg)) {
        console.warn('[auth] token rejected by catalog — clearing:', msg)
        setToken('')
        setProfile(null)
      } else {
        console.debug('[auth] /me unavailable, falling back to JWT:', err)
      }
    }
  }

  function logout() {
    setToken('')
    setProfile(null)
    loginError.value = ''
  }

  /**
   * Kick off the SSO flow against `<catalogUrl>/auth/sso/login`. Resolves
   * once the deep-link callback arrives with a token, rejects if the
   * environment is not Electron or the IdP reports an error.
   */
  async function login(catalogUrl: string): Promise<string> {
    const api = window.electronAPI
    if (!api?.onSsoCallback || !api.openExternal) {
      const err = 'SSO requires the desktop app (electronAPI unavailable)'
      loginError.value = err
      throw new Error(err)
    }

    loginPending.value = true
    loginError.value = ''

    return await new Promise<string>((resolve, reject) => {
      const unsub = api.onSsoCallback!(payload => {
        try {
          if (payload.error) {
            loginError.value = payload.error
            reject(new Error(payload.error))
            return
          }
          if (!payload.token) {
            loginError.value = 'empty token'
            reject(new Error('empty token'))
            return
          }
          setToken(payload.token)
          resolve(payload.token)
        } finally {
          loginPending.value = false
          unsub()
        }
      })

      const base = catalogUrl.replace(/\/$/, '')
      const url = `${base}/auth/sso/login?redirect_uri=${encodeURIComponent('wdc://auth-callback')}`
      void api.openExternal!(url).catch(err => {
        loginPending.value = false
        loginError.value = String(err)
        unsub()
        reject(err instanceof Error ? err : new Error(String(err)))
      })
    })
  }

  function authHeader(): Record<string, string> {
    return token.value ? { Authorization: `Bearer ${token.value}` } : {}
  }

  return {
    token, loginError, loginPending, isAuthenticated, identity, displayName,
    profile,
    login, logout, setToken, setProfile, refreshProfile, authHeader,
  }
})
