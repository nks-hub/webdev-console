<template>
  <div class="login-page">
    <div class="login-card">
      <div class="login-brand">
        <h1 class="brand-mark">{{ $t('app.name') }}</h1>
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

        <!-- SSO is the one primary action -->
        <div class="login-actions">
          <el-button
            type="primary"
            size="large"
            :loading="authStore.loginPending"
            :icon="User"
            @click="doSsoLogin"
          >{{ $t('login.signInSso') }}</el-button>
        </div>

        <div v-if="authStore.loginPending" class="login-wait" role="status">
          <span>{{ $t('login.waiting') }}</span>
          <el-button text size="small" @click="authStore.cancelLogin()">{{ $t('login.cancel') }}</el-button>
        </div>

        <div v-if="authStore.loginError" class="login-error" role="alert">
          {{ errorText(authStore.loginError) }}
        </div>

        <!-- Divider -->
        <div class="login-divider">
          <span>{{ $t('login.or') }}</span>
        </div>

        <!-- E-mail and password: sign in, or create an account -->
        <el-form label-position="top" size="default" @submit.prevent="submitForm">
          <el-form-item :label="$t('login.email')">
            <el-input
              v-model="authEmail"
              :placeholder="$t('login.emailPlaceholder')"
              autocomplete="email"
              :disabled="authLoading"
            />
          </el-form-item>
          <el-form-item :label="$t('login.password')">
            <el-input
              v-model="authPassword"
              type="password"
              show-password
              :autocomplete="registering ? 'new-password' : 'current-password'"
              :disabled="authLoading"
              @keydown.enter.prevent="submitForm"
            />
          </el-form-item>
          <el-form-item v-if="registering" :label="$t('login.passwordAgain')">
            <el-input
              v-model="authPasswordAgain"
              type="password"
              show-password
              autocomplete="new-password"
              :disabled="authLoading"
              @keydown.enter.prevent="submitForm"
            />
          </el-form-item>
          <div class="login-actions">
            <el-button
              :loading="authLoading"
              :disabled="!authEmail || !authPassword"
              @click="submitForm"
            >{{ registering ? $t('login.createAccount') : $t('common.login') }}</el-button>
            <el-button text @click="toggleRegistering">
              {{ registering ? $t('login.haveAccount') : $t('login.noAccount') }}
            </el-button>
          </div>
          <div v-if="authError" class="login-error" role="alert">{{ authError }}</div>
        </el-form>

        <div class="login-skip">
          <a href="#/" @click.prevent="goHome">{{ $t('login.continueWithout') }}</a>
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
import { errorMessage } from '../../utils/errors'

const router = useRouter()
const authStore = useAuthStore()
const { t } = useI18n()
// The catalog address is edited in Settings > Account; this page only uses it.
const catalogUrl = ref('')

const authEmail = ref('')
const authPassword = ref('')
const authPasswordAgain = ref('')
const registering = ref(false)
const authLoading = ref(false)
const authError = ref('')

// The auth store and the catalog hand back raw (English) messages; show a
// sentence in the UI language and keep the raw text only as a last resort.
function errorText(raw: unknown): string {
  const msg = errorMessage(raw)
  if (/desktop app|electronAPI/i.test(msg)) return t('login.errors.desktopOnly')
  if (/empty token/i.test(msg)) return t('login.errors.emptyToken')
  if (/^timed out$/i.test(msg)) return t('login.errors.timedOut')
  if (/\b401\b|unauthori|invalid (credentials|password)|incorrect/i.test(msg)) return t('login.errors.badCredentials')
  if (/\b409\b|already (exists|registered|in use)/i.test(msg)) return t('login.errors.exists')
  if (/failed to fetch|network|ECONNREFUSED|timed? ?out/i.test(msg)) return t('login.errors.network')
  return t('login.errors.generic', { detail: msg })
}

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
    if (errorMessage(err) === 'cancelled') return
    ElMessage.error(`${t('login.failed')}: ${errorText(err)}`)
  }
}

function toggleRegistering() {
  registering.value = !registering.value
  authError.value = ''
  authPasswordAgain.value = ''
}

async function submitForm() {
  if (!authEmail.value || !authPassword.value) return
  if (registering.value && authPassword.value !== authPasswordAgain.value) {
    authError.value = t('login.errors.mismatch')
    return
  }
  authLoading.value = true
  authError.value = ''
  try {
    const call = registering.value ? catalogRegister : catalogLogin
    const result = await call(getCatalogUrl(), authEmail.value, authPassword.value)
    authStore.setToken(result.token)
    localStorage.setItem('nks-wdc-catalog-email', result.email)
    await authStore.refreshProfile(getCatalogUrl())
    authPassword.value = ''
    authPasswordAgain.value = ''
    ElMessage.success(`${t(registering.value ? 'login.registered' : 'login.success')}: ${result.email}`)
    goHome()
  } catch (e) {
    authError.value = errorText(e)
  } finally {
    authLoading.value = false
  }
}

function goHome() {
  const redirect = (router.currentRoute.value.query.redirect as string | undefined) || '/'
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
  padding: var(--wdc-space-6-5) var(--wdc-space-4-5);
  box-sizing: border-box;
}
.login-card {
  width: 100%;
  max-width: 460px;
  background: var(--wdc-surface);
  border: 1px solid var(--wdc-border);
  border-radius: var(--wdc-radius-xl);
  padding: var(--wdc-space-6-5) var(--wdc-space-6-5);
  display: flex;
  flex-direction: column;
  gap: var(--wdc-space-4-5);
  box-shadow: var(--wdc-shadow-pop);
}
.login-brand {
  display: flex;
  flex-direction: column;
  gap: var(--wdc-space-1);
  align-items: flex-start;
}
.brand-mark {
  margin: 0;
  font-weight: 800;
  font-size: 1.5rem;
  letter-spacing: 0.06em;
  background: var(--wdc-brand-gradient);
  -webkit-background-clip: text;
  -webkit-text-fill-color: transparent;
  background-clip: text;
}
.brand-sub { color: var(--el-text-color-secondary); font-size: 0.82rem; }
.login-desc { color: var(--el-text-color-secondary); font-size: 0.88rem; line-height: 1.55; }
.login-actions { display: flex; gap: 10px; flex-wrap: wrap; }
.login-wait {
  display: flex;
  align-items: center;
  gap: var(--wdc-space-2);
  color: var(--wdc-text-2);
  font-size: 0.87rem;
}
.login-error { color: var(--el-color-danger); font-size: 0.82rem; margin-top: 4px; }
.login-state { display: flex; align-items: center; gap: 8px; color: var(--el-color-success); font-size: 0.95rem; }
.login-divider {
  display: flex;
  align-items: center;
  gap: var(--wdc-space-3);
  color: var(--el-text-color-secondary);
  font-size: 0.82rem;
  margin: var(--wdc-space-1) 0;
  font-family: var(--wdc-font-display);
}
.login-divider::before,
.login-divider::after {
  content: '';
  flex: 1 1 auto;
  height: 1px;
  background: var(--wdc-border);
}
.login-skip {
  font-size: 0.82rem;
  text-align: center;
  padding-top: var(--wdc-space-1-5);
  border-top: 1px dashed var(--wdc-border);
}
.login-skip a { color: var(--wdc-accent); text-decoration: none; }
.login-skip a:hover { text-decoration: underline; }
</style>
