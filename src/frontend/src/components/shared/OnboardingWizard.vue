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
        <div class="ob-final-actions">
          <el-button type="primary" size="large" :loading="creatingSample" @click="createSampleSite">
            {{ t('onboarding.createSampleSite') }}
          </el-button>
          <el-button size="large" @click="goToSites">
            {{ t('onboarding.createFirstSite') }}
          </el-button>
        </div>
      </div>
    </div>

    <template #footer>
      <div class="ob-footer">
        <div class="ob-footer-left">
          <el-button text @click="skip">{{ t('onboarding.skip') }}</el-button>
          <el-checkbox v-model="dontShowAgain" size="small" class="ob-dontshow">
            {{ t('onboarding.dontShowAgain') }}
          </el-checkbox>
        </div>
        <div class="ob-nav">
          <el-button v-if="activeStep > 0" @click="activeStep--">{{ t('onboarding.back') }}</el-button>
          <el-button
            v-if="activeStep < 2"
            type="primary"
            @click="goNext"
          >
            {{ t('onboarding.next') }}
          </el-button>
          <el-button
            v-else
            type="primary"
            :loading="finishing"
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
import { ref, onMounted, watch } from 'vue'
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
import { useSitesStore } from '../../stores/sites'

const sitesStore = useSitesStore()

const { t } = useI18n()
const router = useRouter()

// Belt-and-suspenders: a local flag backs up the server-side flag at
// ~/.wdc/data/onboarding-complete.flag. If the daemon's DataRoot is wiped
// (dev resets, portable-mode dir moved, etc.) we still remember that the
// user dismissed the wizard and don't re-prompt on every boot.
const LS_COMPLETED = 'wdc.onboarding.completed'
const LS_DONTSHOW = 'wdc.onboarding.dontShowAgain'

const visible = ref(false)
const activeStep = ref(0)
const state = ref<OnboardingState | null>(null)
const installing = ref<Set<string>>(new Set())
const installingCa = ref(false)
const phpVersions = ref<string[]>([])
const phpVersionChoice = ref<string>('')
const loadingVersions = ref(false)
const finishing = ref(false)
const creatingSample = ref(false)
const dontShowAgain = ref<boolean>(readLocalFlag(LS_DONTSHOW))

function readLocalFlag(key: string): boolean {
  try { return localStorage.getItem(key) === '1' } catch { return false }
}
function writeLocalFlag(key: string, value: boolean) {
  try {
    if (value) localStorage.setItem(key, '1')
    else localStorage.removeItem(key)
  } catch { /* quota/private mode — best-effort only */ }
}

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

// Idempotent completion: always write the local LS flag first (so we don't
// re-open even if the daemon POST fails), then try the server-side flag with
// retries. Returns true when both layers are persisted.
async function markCompleted(): Promise<boolean> {
  writeLocalFlag(LS_COMPLETED, true)
  if (dontShowAgain.value) writeLocalFlag(LS_DONTSHOW, true)

  // Two quick retries — the POST can race with daemon boot / token rotation.
  for (let attempt = 0; attempt < 3; attempt++) {
    try {
      await completeOnboarding()
      return true
    } catch (e) {
      if (attempt === 2) {
        console.warn('[onboarding] server complete failed after retries', e)
        return false
      }
      await new Promise(r => setTimeout(r, 400 * (attempt + 1)))
    }
  }
  return false
}

async function finish() {
  if (finishing.value) return
  finishing.value = true
  try {
    await markCompleted()
    // The `/api/onboarding/complete` endpoint seeds a `localhost` site as a
    // side-effect. sitesStore keeps a reactive cache — if we don't refresh
    // it the Sites page shows an empty list until the user manually
    // navigates away and back. Load() is idempotent + fast, so firing it
    // unconditionally on wizard close is cheaper than tracking "did we
    // actually seed" state.
    try { await sitesStore.load() } catch { /* harmless — Sites page re-fetches on mount too */ }
    visible.value = false
  } finally {
    finishing.value = false
  }
}

async function skip() {
  // Mark complete so the wizard never auto-reopens. User can relaunch from
  // Settings. We await the full write chain (LS + server) instead of the
  // previous fire-and-forget `try { await } catch {}` — that swallowed a
  // transient daemon error and left the server flag missing, which is how
  // the wizard re-appeared on every launch.
  await markCompleted()
  try { await sitesStore.load() } catch { /* same rationale as finish() */ }
  visible.value = false
}

async function createSampleSite() {
  creatingSample.value = true
  try {
    // The daemon's /api/onboarding/complete endpoint seeds a localhost site
    // as a side-effect (idempotent — skips if "localhost" already exists),
    // so we reuse it here. User lands on /sites so they can inspect.
    const ok = await markCompleted()
    if (!ok) {
      ElMessage.warning(t('onboarding.sampleSiteWarn'))
    } else {
      ElMessage.success(t('onboarding.sampleSiteOk'))
    }
    visible.value = false
    void router.push({ path: '/sites' })
  } finally {
    creatingSample.value = false
  }
}

function goToSites() {
  // Await completion BEFORE route-change so the wizard doesn't lose the POST
  // if the user navigates away mid-flight. Previous code used `void finish()`
  // which was racy with router.push().
  void (async () => {
    await markCompleted()
    visible.value = false
    void router.push({ path: '/sites', query: { create: '1' } })
  })()
}

// "Next" button advances; if we land on step 2 (mkcert) and the CA is
// already trusted, auto-skip to step 3 — both sub-steps are green so no
// reason to pause there. Keeps the step clickable via the Back button.
function goNext() {
  const next = activeStep.value + 1
  if (next === 1 && state.value?.prerequisites.mkcertCaInstalled) {
    activeStep.value = 2
    return
  }
  activeStep.value = next
}

// Watch the checkbox so toggling persists immediately — user doesn't have
// to click finish/skip for "don't show again" to stick. Idempotent writes.
watch(dontShowAgain, (val) => writeLocalFlag(LS_DONTSHOW, val))

onMounted(async () => {
  // Client-side early-out: if either local flag is set, never open.
  // Protects against the server flag being wiped (dev reset, portable dir
  // move, permission issue on write) — we always have the LS fallback.
  if (readLocalFlag(LS_COMPLETED) || readLocalFlag(LS_DONTSHOW)) {
    return
  }
  await refreshState()
  // Only auto-open if the daemon reports the wizard hasn't been completed yet.
  if (state.value && !state.value.completed) {
    visible.value = true
    void loadPhpVersions()
  } else if (state.value?.completed) {
    // Back-fill the LS flag so future launches skip the fetch entirely.
    writeLocalFlag(LS_COMPLETED, true)
  }
})

defineExpose({
  open: async () => {
    // Explicit reopen from Settings — clear local opt-out so the user
    // actually sees the wizard (otherwise the LS gate would block us).
    writeLocalFlag(LS_COMPLETED, false)
    writeLocalFlag(LS_DONTSHOW, false)
    dontShowAgain.value = false
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
.ob-final-actions {
  display: flex;
  gap: 12px;
  justify-content: center;
  flex-wrap: wrap;
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
.ob-footer-left {
  display: flex;
  align-items: center;
  gap: 12px;
}
.ob-dontshow {
  color: var(--wdc-text-3);
}
.ob-nav {
  display: flex;
  gap: 8px;
}
</style>
