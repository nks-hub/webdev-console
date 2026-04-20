<template>
  <div class="login-page">
    <div class="login-card">
      <div class="login-brand">
        <div class="brand-mark">NKS WDC</div>
        <div class="brand-sub">Local development console</div>
      </div>

      <template v-if="authStore.isAuthenticated">
        <div class="login-state signed-in">
          <el-icon><UserFilled /></el-icon>
          <span>You are signed in to the catalog.</span>
        </div>
        <div class="login-actions">
          <el-button type="primary" @click="goHome">Continue to app</el-button>
          <el-button @click="authStore.logout()">Sign out</el-button>
        </div>
      </template>

      <template v-else>
        <div class="login-desc">
          Sign in to your NKS WDC catalog account to enable plugin auto-sync,
          SSO-gated admin actions, and per-device identity. You can use the
          app without signing in — the button below stays available at any
          time from the sidebar.
        </div>

        <div class="login-actions">
          <el-button
            type="primary"
            size="large"
            :loading="authStore.loginPending"
            :icon="User"
            @click="doLogin"
          >Sign in with SSO</el-button>
        </div>

        <div v-if="authStore.loginError" class="login-error">
          {{ authStore.loginError }}
        </div>

        <div class="login-catalog-hint">
          <span class="label">Catalog URL</span>
          <code class="url mono">{{ catalogUrl || 'https://wdc.nks-hub.cz' }}</code>
        </div>

        <div class="login-skip">
          <a href="#/dashboard" @click.prevent="goHome">Continue without signing in →</a>
        </div>
      </template>
    </div>
  </div>
</template>

<script setup lang="ts">
import { ref, onMounted } from 'vue'
import { useRouter } from 'vue-router'
import { ElMessage } from 'element-plus'
import { User, UserFilled } from '@element-plus/icons-vue'
import { useAuthStore } from '../../stores/auth'
import { daemonBaseUrl, daemonAuthHeaders as authHeaders } from '../../api/daemon'

const router = useRouter()
const authStore = useAuthStore()
const catalogUrl = ref('')

// Local inline parsers used to skip the 5199 fallback — when neither
// preload nor URL param was available, the URL ended up as
// `http://127.0.0.1:undefined` and the fetch rejected. Switched to the
// shared daemonBaseUrl() which has the same default as the typed API
// surface, so browser dev mode works consistently.
async function loadCatalogUrl() {
  try {
    const r = await fetch(`${daemonBaseUrl()}/api/settings`, { headers: authHeaders() })
    if (r.ok) {
      const data = await r.json()
      catalogUrl.value = (data?.['daemon.catalogUrl'] as string) || ''
    }
  } catch { /* offline — keep default */ }
}

async function doLogin() {
  try {
    await authStore.login(catalogUrl.value || 'https://wdc.nks-hub.cz')
    ElMessage.success('Signed in')
    goHome()
  } catch (err) {
    ElMessage.error(`Sign-in failed: ${err instanceof Error ? err.message : err}`)
  }
}

function goHome() {
  const redirect = (router.currentRoute.value.query.redirect as string | undefined) || '/dashboard'
  void router.replace(redirect)
}

onMounted(loadCatalogUrl)
</script>

<style scoped>
.login-page {
  display: flex;
  align-items: center;
  justify-content: center;
  min-height: 100%;
  padding: 40px 20px;
  box-sizing: border-box;
}
.login-card {
  width: 100%;
  max-width: 460px;
  background: var(--wdc-surface);
  border: 1px solid var(--wdc-border);
  border-radius: 14px;
  padding: 36px 32px;
  display: flex;
  flex-direction: column;
  gap: 20px;
  box-shadow: 0 10px 30px -10px rgba(0, 0, 0, 0.35);
}
.login-brand {
  display: flex;
  flex-direction: column;
  gap: 4px;
  align-items: flex-start;
}
.brand-mark {
  font-weight: 800;
  font-size: 1.5rem;
  letter-spacing: 0.06em;
  background: linear-gradient(135deg, #6366f1, #8b5cf6);
  -webkit-background-clip: text;
  -webkit-text-fill-color: transparent;
  background-clip: text;
}
.brand-sub { color: var(--el-text-color-secondary); font-size: 0.82rem; }
.login-desc { color: var(--el-text-color-secondary); font-size: 0.88rem; line-height: 1.55; }
.login-actions { display: flex; gap: 10px; flex-wrap: wrap; }
.login-error { color: var(--el-color-danger); font-size: 0.82rem; }
.login-state { display: flex; align-items: center; gap: 8px; color: var(--el-color-success); font-size: 0.95rem; }
.login-catalog-hint {
  display: flex;
  flex-direction: column;
  gap: 4px;
  font-size: 0.78rem;
  color: var(--el-text-color-secondary);
}
.login-catalog-hint .label { text-transform: uppercase; letter-spacing: 0.08em; font-size: 0.7rem; }
.mono { font-family: 'JetBrains Mono', monospace; }
.login-catalog-hint .url {
  background: var(--wdc-surface-2);
  padding: 4px 8px;
  border-radius: 4px;
  color: var(--wdc-text);
  font-size: 0.78rem;
  word-break: break-all;
}
.login-skip {
  font-size: 0.82rem;
  text-align: center;
  padding-top: 6px;
  border-top: 1px dashed var(--wdc-border);
}
.login-skip a { color: var(--wdc-accent); text-decoration: none; }
.login-skip a:hover { text-decoration: underline; }
</style>
