import { defineStore } from 'pinia'
import { ref, computed } from 'vue'

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

export const useAuthStore = defineStore('auth', () => {
  const token = ref<string>(localStorage.getItem(TOKEN_KEY) ?? '')
  const loginError = ref<string>('')
  const loginPending = ref(false)

  const isAuthenticated = computed(() => token.value.length > 0)

  function setToken(newToken: string) {
    token.value = newToken
    if (newToken) localStorage.setItem(TOKEN_KEY, newToken)
    else localStorage.removeItem(TOKEN_KEY)
  }

  function logout() {
    setToken('')
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

  return { token, loginError, loginPending, isAuthenticated, login, logout, setToken, authHeader }
})
