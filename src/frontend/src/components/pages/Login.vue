<template>
  <div class="login-page">
    <div class="login-card">
      <div class="login-brand">
        <div class="brand-mark">NKS WDC</div>
        <div class="brand-sub">{{ $t('login.subtitle') }}</div>
      </div>

      <template v-if="authStore.isAuthenticated">
        <div class="login-state signed-in">
          <el-icon><UserFilled /></el-icon>
          <span>{{ $t('login.signedInHint') }}</span>
        </div>
        <div class="login-actions">
          <el-button type="primary" @click="goHome">{{ $t('login.continue') }}</el-button>
          <el-button @click="authStore.logout()">{{ $t('login.signOut') }}</el-button>
        </div>
      </template>

      <template v-else>
        <div class="login-desc">{{ $t('login.description') }}</div>

        <!-- SSO -->
        <div class="login-actions">
          <el-button
            type="primary"
            size="large"
            :loading="authStore.loginPending"
            :icon="User"
            @click="doSsoLogin"
          >{{ $t('login.signInSso') }}</el-button>
        </div>

        <div v-if="authStore.loginError" class="login-error">
          {{ authStore.loginError }}
        </div>

        <!-- Divider -->
        <div class="login-divider">
          <span>{{ $t('login.or') }}</span>
        </div>

        <!-- Password form — F91.15 parity with Settings > Account -->
        <el-form label-position="top" size="default" @submit.prevent="doPasswordLogin">
          <el-form-item :label="$t('login.email')">
            <el-input
              v-model="authEmail"
              placeholder="you@example.com"
              autocomplete="email"
              :disabled="authLoading"
            />
          </el-form-item>
          <el-form-item :label="$t('login.password')">
            <el-input
              v-model="authPassword"
              type="password"
              show-password
              autocomplete="current-password"
              :disabled="authLoading"
              @keydown.enter.prevent="doPasswordLogin"
            />
          </el-form-item>
          <div class="login-actions">
            <el-button
              type="primary"
              :loading="authLoading"
              :disabled="!authEmail || !authPassword"
              @click="doPasswordLogin"
            >{{ $t('common.login') }}</el-button>
            <el-button
              :loading="authLoading"
              :disabled="!authEmail || !authPassword"
              @click="doPasswordRegister"
            >{{ $t('common.register') }}</el-button>
          </div>
          <div v-if="authError" class="login-error">{{ authError }}</div>
        </el-form>

        <div class="login-catalog-hint">
          <span class="label">{{ $t('login.catalogUrl') }}</span>
          <code class="url mono">{{ catalogUrl || 'https://wdc.nks-hub.cz' }}</code>
        </div>

        <div class="login-skip">
          <a href="#/dashboard" @click.prevent="goHome">{{ $t('login.continueWithout') }}</a>
        </div>
      </template>
    </div>
  </div>
</template>

<script setup lang="ts">
import { ref, onMounted } from 'vue'
import { useRouter } from 'vue-router'
import { useI18n } from 'vue-i18n'
import { ElMessage } from 'element-plus'
import { User, UserFilled } from '@element-plus/icons-vue'
import { useAuthStore } from '../../stores/auth'
import { fetchSettings, catalogLogin, catalogRegister } from '../../api/daemon'

const router = useRouter()
const authStore = useAuthStore()
const { t } = useI18n()
const catalogUrl = ref('')

// Password-login local state (matches Settings > Account pattern)
const authEmail = ref('')
const authPassword = ref('')
const authLoading = ref(false)
const authError = ref('')

async function loadCatalogUrl() {
  try {
    const data = await fetchSettings()
    catalogUrl.value = data['daemon.catalogUrl'] || ''
  } catch { /* offline — keep default */ }
}

function getCatalogUrl(): string {
  return (catalogUrl.value || 'https://wdc.nks-hub.cz').replace(/\/$/, '')
}

async function doSsoLogin() {
  try {
    await authStore.login(getCatalogUrl())
    ElMessage.success(t('login.success'))
    goHome()
  } catch (err) {
    ElMessage.error(`${t('login.failed')}: ${err instanceof Error ? err.message : err}`)
  }
}

async function doPasswordLogin() {
  if (!authEmail.value || !authPassword.value) return
  authLoading.value = true
  authError.value = ''
  try {
    const result = await catalogLogin(getCatalogUrl(), authEmail.value, authPassword.value)
    authStore.setToken(result.token)
    localStorage.setItem('nks-wdc-catalog-email', result.email)
    await authStore.refreshProfile(getCatalogUrl())
    authPassword.value = ''
    ElMessage.success(`${t('login.success')}: ${result.email}`)
    goHome()
  } catch (e) {
    authError.value = e instanceof Error ? e.message : String(e)
  } finally {
    authLoading.value = false
  }
}

async function doPasswordRegister() {
  if (!authEmail.value || !authPassword.value) return
  authLoading.value = true
  authError.value = ''
  try {
    const result = await catalogRegister(getCatalogUrl(), authEmail.value, authPassword.value)
    authStore.setToken(result.token)
    localStorage.setItem('nks-wdc-catalog-email', result.email)
    await authStore.refreshProfile(getCatalogUrl())
    authPassword.value = ''
    ElMessage.success(`${t('login.registered')}: ${result.email}`)
    goHome()
  } catch (e) {
    authError.value = e instanceof Error ? e.message : String(e)
  } finally {
    authLoading.value = false
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
  gap: 18px;
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
.login-error { color: var(--el-color-danger); font-size: 0.82rem; margin-top: 4px; }
.login-state { display: flex; align-items: center; gap: 8px; color: var(--el-color-success); font-size: 0.95rem; }
.login-divider {
  display: flex;
  align-items: center;
  gap: 12px;
  color: var(--el-text-color-secondary);
  font-size: 0.78rem;
  text-transform: uppercase;
  letter-spacing: 0.1em;
  margin: 4px 0;
}
.login-divider::before,
.login-divider::after {
  content: '';
  flex: 1 1 auto;
  height: 1px;
  background: var(--wdc-border);
}
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
