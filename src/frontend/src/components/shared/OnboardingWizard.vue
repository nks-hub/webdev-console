<template>
  <el-dialog
    v-model="visible"
    :title="t('onboarding.title')"
    width="640px"
    align-center
    :close-on-click-modal="false"
    :close-on-press-escape="false"
    :show-close="false"
  >
    <div class="ob-wrap">
      <p class="ob-subtitle">{{ t('onboarding.subtitle') }}</p>

      <el-steps :active="activeStep" finish-status="success" align-center>
        <el-step :title="t('onboarding.step1')" />
        <el-step :title="t('onboarding.step2')" />
        <el-step :title="t('onboarding.step3')" />
      </el-steps>

      <!-- Step 1: binaries -->
      <div v-if="activeStep === 0" class="ob-step">
        <p class="ob-step-desc">{{ t('onboarding.step1Desc') }}</p>
        <div class="ob-actions-grid">
          <div class="ob-row">
            <span class="ob-label">Apache</span>
            <el-tag v-if="state?.prerequisites.apacheInstalled" size="small" type="success">{{ t('onboarding.installed') }}</el-tag>
            <el-button v-else size="small" :loading="installing.has('apache')" @click="doInstall('apache')">
              {{ t('onboarding.installApache') }}
            </el-button>
          </div>
          <div class="ob-row">
            <span class="ob-label">PHP</span>
            <el-tag v-if="state?.prerequisites.phpInstalled" size="small" type="success">{{ t('onboarding.installed') }}</el-tag>
            <template v-else>
              <el-select
                v-model="phpVersionChoice"
                size="small"
                class="ob-version-select"
                :placeholder="t('onboarding.pickVersion')"
                :loading="loadingVersions"
                :disabled="installing.has('php')"
              >
                <el-option
                  v-for="v in phpVersions"
                  :key="v"
                  :label="v"
                  :value="v"
                />
              </el-select>
              <el-button size="small" :loading="installing.has('php')" @click="doInstall('php', phpVersionChoice)">
                {{ t('onboarding.installPhp') }}
              </el-button>
            </template>
          </div>
          <div class="ob-row">
            <span class="ob-label">MySQL</span>
            <el-tag v-if="state?.prerequisites.mysqlInstalled" size="small" type="success">{{ t('onboarding.installed') }}</el-tag>
            <el-button v-else size="small" :loading="installing.has('mysql')" @click="doInstall('mysql')">
              {{ t('onboarding.installMysql') }}
            </el-button>
          </div>
        </div>
      </div>

      <!-- Step 2: mkcert -->
      <div v-else-if="activeStep === 1" class="ob-step">
        <p class="ob-step-desc">{{ t('onboarding.step2Desc') }}</p>
        <div class="ob-actions-grid">
          <div class="ob-row">
            <span class="ob-label">mkcert binary</span>
            <el-tag v-if="state?.prerequisites.mkcertBinaryInstalled" size="small" type="success">{{ t('onboarding.installed') }}</el-tag>
            <el-button v-else size="small" :loading="installing.has('mkcert')" @click="doInstall('mkcert')">
              {{ t('onboarding.installMkcert') }}
            </el-button>
          </div>
          <div class="ob-row">
            <span class="ob-label">Local CA trust</span>
            <el-tag v-if="state?.prerequisites.mkcertCaInstalled" size="small" type="success">{{ t('onboarding.installed') }}</el-tag>
            <el-button
              v-else
              size="small"
              :disabled="!state?.prerequisites.mkcertBinaryInstalled"
              :loading="installingCa"
              @click="installCa"
            >
              {{ t('onboarding.installMkcertCa') }}
            </el-button>
          </div>
        </div>
      </div>

      <!-- Step 3: ready -->
      <div v-else class="ob-step ob-step-final">
        <p class="ob-step-desc">{{ t('onboarding.step3Desc') }}</p>
        <el-button type="primary" size="large" @click="goToSites">
          {{ t('onboarding.createFirstSite') }}
        </el-button>
      </div>
    </div>

    <template #footer>
      <div class="ob-footer">
        <el-button text @click="skip">{{ t('onboarding.skip') }}</el-button>
        <div class="ob-nav">
          <el-button v-if="activeStep > 0" @click="activeStep--">{{ t('onboarding.back') }}</el-button>
          <el-button
            v-if="activeStep < 2"
            type="primary"
            @click="activeStep++"
          >
            {{ t('onboarding.next') }}
          </el-button>
          <el-button
            v-else
            type="primary"
            @click="finish"
          >
            {{ t('onboarding.finish') }}
          </el-button>
        </div>
      </div>
    </template>
  </el-dialog>
</template>

<script setup lang="ts">
import { ref, onMounted } from 'vue'
import { useI18n } from 'vue-i18n'
import { useRouter } from 'vue-router'
import { ElMessage } from 'element-plus'
import {
  fetchOnboardingState,
  completeOnboarding,
  installBinary,
  fetchLatestBinary,
  fetchBinaryCatalog,
  fetchSystem,
  installSslCa,
  type OnboardingState,
} from '../../api/daemon'
import { errorMessage } from '../../utils/errors'
import { compareSemver } from '../../utils/semver'

const { t } = useI18n()
const router = useRouter()

const visible = ref(false)
const activeStep = ref(0)
const state = ref<OnboardingState | null>(null)
const installing = ref<Set<string>>(new Set())
const installingCa = ref(false)
const phpVersions = ref<string[]>([])
const phpVersionChoice = ref<string>('')
const loadingVersions = ref(false)

async function refreshState() {
  try {
    state.value = await fetchOnboardingState()
  } catch (e) {
    console.warn('[onboarding] state fetch failed', e)
    state.value = null
  }
}

async function doInstall(app: string, explicitVersion?: string) {
  installing.value.add(app)
  try {
    // Prefer a user-picked version (PHP selector), fall back to "latest
    // compatible on this OS/arch" which the daemon resolves. Never hardcode
    // — apache 2.4.62 was Windows-only so a pinned macOS version 404'd.
    const version = explicitVersion?.trim() || await resolveLatest(app)
    if (!version) {
      ElMessage.warning(`${app}: no binary available for this platform`)
      return
    }
    await installBinary(app, version)
    ElMessage.success(`${app} ${version} installed`)
    await refreshState()
  } catch (e) {
    ElMessage.error(`${app}: ${errorMessage(e)}`)
  } finally {
    installing.value.delete(app)
  }
}

async function loadPhpVersions() {
  loadingVersions.value = true
  try {
    const [catalog, system] = await Promise.all([
      fetchBinaryCatalog(),
      fetchSystem(),
    ])
    const osTag = system.os.tag
    const archTag = system.os.arch
    const releases = catalog.php ?? []
    // Unique versions compatible with current OS/arch, newest first.
    const versions = Array.from(new Set(
      releases
        .filter(r => r.os === osTag && r.arch === archTag)
        .map(r => r.version)
    )).sort((a, b) => compareSemver(b, a))
    phpVersions.value = versions
    if (versions.length > 0 && !phpVersionChoice.value) {
      phpVersionChoice.value = versions[0]
    }
  } catch (e) {
    console.warn('[onboarding] php version load failed', e)
  } finally {
    loadingVersions.value = false
  }
}

async function resolveLatest(app: string): Promise<string | null> {
  try {
    const res = await fetchLatestBinary(app)
    return res?.version ?? null
  } catch (e) {
    console.warn(`[onboarding] latest ${app} lookup failed`, e)
    return null
  }
}

async function installCa() {
  installingCa.value = true
  try {
    // installSslCa() handles the daemon base URL and Bearer token via the
    // shared `json()` helper. The previous implementation fetched the
    // relative URL '/api/ssl/install-ca' with no headers, which 401'd
    // under the daemon's global auth middleware and also couldn't resolve
    // the base URL correctly in packaged Electron (file:// origin).
    await installSslCa()
    ElMessage.success('Local CA trusted')
    await refreshState()
  } catch (e) {
    ElMessage.error(`mkcert CA install failed: ${errorMessage(e)}`)
  } finally {
    installingCa.value = false
  }
}

async function finish() {
  try {
    await completeOnboarding()
    visible.value = false
  } catch (e) {
    ElMessage.error(`Failed to complete: ${errorMessage(e)}`)
  }
}

async function skip() {
  // Mark complete so the wizard never auto-reopens. User can relaunch from Settings.
  try { await completeOnboarding() } catch { /* ignore */ }
  visible.value = false
}

function goToSites() {
  void router.push({ path: '/sites', query: { create: '1' } })
  void finish()
}

onMounted(async () => {
  await refreshState()
  // Only auto-open if the daemon reports the wizard hasn't been completed yet.
  if (state.value && !state.value.completed) {
    visible.value = true
    void loadPhpVersions()
  }
})

defineExpose({
  open: async () => {
    await refreshState()
    visible.value = true
    activeStep.value = 0
    void loadPhpVersions()
  },
})
</script>

<style scoped>
.ob-wrap {
  display: flex;
  flex-direction: column;
  gap: 20px;
  padding: 4px 0 0;
}
.ob-subtitle {
  font-size: 0.92rem;
  color: var(--wdc-text-2);
  margin: 0 0 4px;
  text-align: center;
}
.ob-step {
  min-height: 180px;
  padding: 16px 8px 4px;
}
.ob-step-desc {
  font-size: 0.88rem;
  color: var(--wdc-text-2);
  margin: 0 0 16px;
}
.ob-step-final {
  text-align: center;
  padding-top: 36px;
}
.ob-actions-grid {
  display: flex;
  flex-direction: column;
  gap: 10px;
}
.ob-row {
  display: flex;
  align-items: center;
  justify-content: space-between;
  padding: 10px 14px;
  background: var(--wdc-surface);
  border: 1px solid var(--wdc-border);
  border-radius: var(--wdc-radius-sm);
}
.ob-label {
  font-size: 0.88rem;
  font-weight: 500;
  color: var(--wdc-text);
}
.ob-row :deep(.ob-version-select) {
  width: 140px;
  margin-left: auto;
  margin-right: 8px;
}
.ob-footer {
  display: flex;
  align-items: center;
  justify-content: space-between;
}
.ob-nav {
  display: flex;
  gap: 8px;
}
</style>
