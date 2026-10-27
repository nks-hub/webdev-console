<template>
  <el-dialog
    v-model="visible"
    :title="t('onboarding.title')"
    width="640px"
    align-center
    @close="onDismiss"
  >
    <div class="ob-wrap" @keydown.capture="onKeydownCapture">
      <p class="ob-subtitle">{{ t('onboarding.subtitle') }}</p>

      <el-steps :active="activeStep" finish-status="success" align-center class="ob-steps">
        <el-step :title="t('onboarding.step1')" />
        <el-step :title="t('onboarding.step2')" />
        <el-step :title="t('onboarding.step3')" />
      </el-steps>

      <el-alert
        v-if="!state"
        type="warning"
        :closable="false"
        show-icon
        :title="t('onboarding.stateError')"
      />

      <!-- Step 1: binaries. Each row lets you pick the version; a failed
           install keeps its error right under the row. -->
      <div v-if="activeStep === 0" class="ob-step">
        <p class="ob-step-desc">{{ t('onboarding.step1Desc') }}</p>
        <div class="ob-actions-grid">
          <div v-for="row in BINARY_ROWS" :key="row.app" class="ob-item">
            <div class="ob-row">
              <span class="ob-label">{{ row.label }}</span>
              <el-tag v-if="state && row.installed(state)" size="small" type="success">{{ installedText(state.versions?.[row.app]) }}</el-tag>
              <template v-else>
                <el-select
                  :id="`ob-version-${row.app}`"
                  v-model="choice[row.app]"
                  size="small"
                  class="ob-version-select"
                  :placeholder="t('onboarding.pickVersion')"
                  :aria-label="t('onboarding.versionOf', { name: row.label })"
                  :loading="loadingVersions"
                  :disabled="installing.has(row.app)"
                >
                  <el-option
                    v-for="v in versionsByApp[row.app] ?? []"
                    :key="v"
                    :label="v"
                    :value="v"
                  />
                </el-select>
                <el-button size="small" :loading="installing.has(row.app)" @click="doInstall(row.app, row.label)">
                  {{ t(row.installKey) }}
                </el-button>
              </template>
            </div>
            <div v-if="failures[row.app] && !(state && row.installed(state))" class="ob-error" role="alert">
              <p class="ob-error-text">{{ failures[row.app].text }}</p>
              <details v-if="failures[row.app].detail" class="ob-error-detail">
                <summary>{{ t('onboarding.err.details') }}</summary>
                <code>{{ failures[row.app].detail }}</code>
              </details>
              <div class="ob-error-actions">
                <el-button v-if="(versionsByApp[row.app] ?? []).length > 1" size="small" @click="tryAnother(row.app)">
                  {{ t('onboarding.err.tryAnother') }}
                </el-button>
                <el-button size="small" text @click="openBinaries(row.app)">{{ t('onboarding.err.openBinaries') }}</el-button>
              </div>
            </div>
          </div>
        </div>
      </div>

      <!-- Step 2: mkcert -->
      <div v-else-if="activeStep === 1" class="ob-step">
        <p class="ob-step-desc">{{ t('onboarding.step2Desc') }}</p>
        <div class="ob-actions-grid">
          <div class="ob-item">
            <div class="ob-row">
              <span class="ob-label">{{ t('onboarding.labelMkcertBinary') }}</span>
              <el-tag v-if="state?.prerequisites.mkcertBinaryInstalled" size="small" type="success">{{ installedText(state.versions?.mkcert) }}</el-tag>
              <el-button v-else size="small" :loading="installing.has('mkcert')" @click="doInstall('mkcert', 'mkcert')">
                {{ t('onboarding.installMkcert') }}
              </el-button>
            </div>
            <div v-if="failures.mkcert && !state?.prerequisites.mkcertBinaryInstalled" class="ob-error" role="alert">
              <p class="ob-error-text">{{ failures.mkcert.text }}</p>
              <details v-if="failures.mkcert.detail" class="ob-error-detail">
                <summary>{{ t('onboarding.err.details') }}</summary>
                <code>{{ failures.mkcert.detail }}</code>
              </details>
              <div class="ob-error-actions">
                <el-button size="small" text @click="openBinaries('mkcert')">{{ t('onboarding.err.openBinaries') }}</el-button>
              </div>
            </div>
          </div>
          <div class="ob-row">
            <span class="ob-label">{{ t('onboarding.labelLocalCaTrust') }}</span>
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
          <el-tooltip :content="t('onboarding.laterHint')" placement="top">
            <el-button text @click="visible = false">{{ t('onboarding.later') }}</el-button>
          </el-tooltip>
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
import { ref, reactive, onMounted, onBeforeUnmount, watch, nextTick } from 'vue'
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
  subscribeEventsMap,
  type OnboardingState,
} from '../../api/daemon'
import { errorMessage } from '../../utils/errors'
import { compareSemver } from '../../utils/semver'
import { installErrorDetail, installErrorMessage } from '../../utils/binaries'
import { useSitesStore } from '../../stores/sites'
import { useDaemonStore } from '../../stores/daemon'
import { OPEN_ONBOARDING_EVENT } from '../../utils/onboarding'

const sitesStore = useSitesStore()
const daemonStore = useDaemonStore()

const { t } = useI18n()
const router = useRouter()

// Belt-and-suspenders: a local flag backs up the server-side flag at
// ~/.wdc/data/onboarding-complete.flag. If the daemon's DataRoot is wiped
// (dev resets, portable-mode dir moved, etc.) we still remember that the
// user dismissed the wizard and don't re-prompt on every boot.
const LS_COMPLETED = 'wdc.onboarding.completed'
const LS_DONTSHOW = 'wdc.onboarding.dontShowAgain'

// Programs offered in step 1, each with a version picker.
type PickableApp = 'apache' | 'php' | 'mysql'
interface BinaryRow {
  app: PickableApp
  label: string
  installKey: string
  installed: (s: OnboardingState) => boolean
}
const BINARY_ROWS: BinaryRow[] = [
  { app: 'apache', label: 'Apache', installKey: 'onboarding.installApache', installed: s => s.prerequisites.apacheInstalled },
  { app: 'php', label: 'PHP', installKey: 'onboarding.installPhp', installed: s => s.prerequisites.phpInstalled },
  { app: 'mysql', label: 'MySQL', installKey: 'onboarding.installMysql', installed: s => s.prerequisites.mysqlInstalled },
]

interface Failure { version: string; text: string; detail: string }

const visible = ref(false)
const activeStep = ref(0)
const state = ref<OnboardingState | null>(null)
const installing = ref<Set<string>>(new Set())
const installingCa = ref(false)
const versionsByApp = ref<Partial<Record<PickableApp, string[]>>>({})
const choice = reactive<Record<string, string>>({})
const failures = ref<Record<string, Failure>>({})
// Versions already tried per app, so "Try another version" walks the list
// instead of offering the broken one again.
const tried: Record<string, string[]> = {}
const loadingVersions = ref(false)
const finishing = ref(false)
const creatingSample = ref(false)
const dontShowAgain = ref<boolean>(readLocalFlag(LS_DONTSHOW))
// Closing the dialog only means "later": it comes back on the next launch,
// but not again during this session.
let dismissedThisSession = false

function readLocalFlag(key: string): boolean {
  try { return localStorage.getItem(key) === '1' } catch { return false }
}
function writeLocalFlag(key: string, value: boolean) {
  try {
    if (value) localStorage.setItem(key, '1')
    else localStorage.removeItem(key)
  } catch { /* quota/private mode — best-effort only */ }
}

function installedText(version?: string | null): string {
  return version ? t('onboarding.installedVersion', { version }) : t('onboarding.installed')
}

async function refreshState() {
  try {
    state.value = await fetchOnboardingState()
    // Something installed elsewhere (Binaries page, CLI) clears its old error.
    for (const row of BINARY_ROWS) {
      if (row.installed(state.value)) delete failures.value[row.app]
    }
  } catch (e) {
    console.warn('[onboarding] state fetch failed', e)
    state.value = null
  }
}

// While the dialog is open the state is re-read when the daemon announces an
// installed binary, when the window regains focus and on step changes, with a
// slow timer as a safety net.
const REFRESH_MS = 15000
let refreshTimer: ReturnType<typeof setInterval> | null = null
let unsubscribeSse: (() => void) | null = null
const onFocus = () => { void refreshState() }

function startLiveRefresh() {
  stopLiveRefresh()
  refreshTimer = setInterval(() => { void refreshState() }, REFRESH_MS)
  unsubscribeSse = subscribeEventsMap({ 'binary:installed': () => { void refreshState() } })
  window.addEventListener('focus', onFocus)
}
function stopLiveRefresh() {
  if (refreshTimer) { clearInterval(refreshTimer); refreshTimer = null }
  unsubscribeSse?.(); unsubscribeSse = null
  window.removeEventListener('focus', onFocus)
}
watch(visible, (open) => { if (open) startLiveRefresh(); else stopLiveRefresh() })
watch(activeStep, () => { void refreshState() })

async function doInstall(app: string, label: string) {
  installing.value.add(app)
  delete failures.value[app]
  let version = ''
  try {
    // Prefer a user-picked version, fall back to "latest compatible on this
    // OS/arch" which the daemon resolves. Never hardcode — apache 2.4.62 was
    // Windows-only so a pinned macOS version 404'd.
    version = choice[app]?.trim() || await resolveLatest(app) || ''
    if (!version) {
      ElMessage.warning(t('toast.noBinaryForPlatform', { app: label }))
      return
    }
    await installBinary(app, version)
    ElMessage.success(t('toast.installedVersion', { app: label, version }))
    await refreshState()
  } catch (e) {
    ;(tried[app] ??= []).push(version)
    failures.value[app] = {
      version,
      text: installErrorMessage(t, e, app, version),
      detail: installErrorDetail(e),
    }
  } finally {
    installing.value.delete(app)
  }
}

// Picks the next version that has not failed yet, wrapping around the list,
// and puts the cursor on the picker.
function tryAnother(app: string) {
  const list = versionsByApp.value[app as PickableApp] ?? []
  const next = list.find(v => !(tried[app] ?? []).includes(v)) ?? list.find(v => v !== failures.value[app]?.version)
  if (next) choice[app] = next
  delete failures.value[app]
  void nextTick(() => document.getElementById(`ob-version-${app}`)?.focus())
}

function openBinaries(app: string) {
  visible.value = false
  void router.push({ path: '/binaries', query: { app } })
}

// MySQL: the long-term 8.4 line is the safe default, not the newest innovation release.
function preferredVersion(app: PickableApp, versions: string[]): string {
  return (app === 'mysql' ? versions.find(v => v.startsWith('8.4.')) : undefined) ?? versions[0] ?? ''
}

async function loadVersions() {
  loadingVersions.value = true
  try {
    const [catalog, system] = await Promise.all([
      fetchBinaryCatalog(),
      fetchSystem(),
    ])
    for (const { app } of BINARY_ROWS) {
      // Unique versions compatible with current OS/arch, newest first.
      const versions = Array.from(new Set(
        (catalog[app] ?? [])
          .filter(r => r.os === system.os.tag && r.arch === system.os.arch)
          .map(r => r.version)
      )).sort((a, b) => compareSemver(b, a))
      versionsByApp.value[app] = versions
      if (!choice[app]) choice[app] = preferredVersion(app, versions)
    }
  } catch (e) {
    console.warn('[onboarding] version list load failed', e)
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
    ElMessage.success(t('ssl.caTrusted'))
    await refreshState()
  } catch (e) {
    ElMessage.error(t('toast.caInstallFailed', { err: errorMessage(e) }))
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

// Every way of closing without finishing (×, Esc, click outside, "Later")
// lands here. That is "later", not "never": the wizard returns on the next
// launch unless "Don't show again" is ticked.
// ElSelect swallows Esc even while its list is closed, so the dialog never sees
// it; catch it first, but leave an open list to close on its own.
function onKeydownCapture(e: KeyboardEvent) {
  if (e.key !== 'Escape') return
  if ((e.target as HTMLElement | null)?.getAttribute('aria-expanded') === 'true') return
  visible.value = false
}

function onDismiss() {
  dismissedThisSession = true
  if (dontShowAgain.value) void markCompleted()
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

// The wizard mounts before the daemon is necessarily reachable (Electron
// splash, slow first start). A failed state fetch used to leave it closed
// for the whole session, so the auto-open check now runs on every connect
// until it gets a definitive answer from the daemon.
let stopConnectWatch: (() => void) | null = null

async function autoOpenCheck() {
  await refreshState()
  if (!state.value) return
  stopConnectWatch?.()
  stopConnectWatch = null
  // Only auto-open if the daemon reports the wizard hasn't been completed yet.
  if (!state.value.completed) {
    if (dismissedThisSession) return
    visible.value = true
    void loadVersions()
  } else {
    // Back-fill the LS flag so future launches skip the fetch entirely.
    writeLocalFlag(LS_COMPLETED, true)
  }
}

async function open() {
  // Explicit reopen (Help page) — clear local opt-out so the user
  // actually sees the wizard (otherwise the LS gate would block us).
  writeLocalFlag(LS_COMPLETED, false)
  writeLocalFlag(LS_DONTSHOW, false)
  dontShowAgain.value = false
  await refreshState()
  visible.value = true
  activeStep.value = 0
  void loadVersions()
}

function onOpenRequest() { void open() }

onMounted(() => {
  window.addEventListener(OPEN_ONBOARDING_EVENT, onOpenRequest)
  // Client-side early-out: if either local flag is set, never auto-open.
  // Protects against the server flag being wiped (dev reset, portable dir
  // move, permission issue on write) — we always have the LS fallback.
  if (readLocalFlag(LS_COMPLETED) || readLocalFlag(LS_DONTSHOW)) {
    return
  }
  stopConnectWatch = daemonStore.onConnect(autoOpenCheck)
})

onBeforeUnmount(() => {
  window.removeEventListener(OPEN_ONBOARDING_EVENT, onOpenRequest)
  stopConnectWatch?.()
  stopLiveRefresh()
})

defineExpose({ open })
</script>

<style scoped>
.ob-wrap {
  display: flex;
  flex-direction: column;
  gap: var(--wdc-space-4-5);
  padding: var(--wdc-space-1) 0 0;
}
.ob-subtitle {
  font-size: 0.92rem;
  color: var(--wdc-text-2);
  margin: 0 0 var(--wdc-space-1);
}
/* Element Plus gives step titles a 38px line height; with short titles one line is enough. */
.ob-steps :deep(.el-step__title) { line-height: 1.3; font-size: 0.9rem; }
.ob-step {
  min-height: 180px;
  padding: var(--wdc-space-4) var(--wdc-space-2) var(--wdc-space-1);
}
.ob-step-desc {
  font-size: 0.88rem;
  color: var(--wdc-text-2);
  margin: 0 0 var(--wdc-space-4);
}
.ob-step-final {
  padding-top: var(--wdc-space-4);
}
.ob-final-actions {
  display: flex;
  gap: var(--wdc-space-3);
  flex-wrap: wrap;
}
.ob-actions-grid {
  display: flex;
  flex-direction: column;
  gap: var(--wdc-space-2-5);
}
.ob-item { display: grid; gap: var(--wdc-space-1-5); }
.ob-error {
  display: grid;
  gap: var(--wdc-space-1-5);
  padding: var(--wdc-space-2-5) var(--wdc-space-3-5);
  border: 1px solid color-mix(in oklab, var(--wdc-status-error) 45%, transparent);
  border-radius: var(--wdc-radius-sm);
  background: color-mix(in oklab, var(--wdc-status-error) 8%, transparent);
}
.ob-error-text { margin: 0; font-size: 0.88rem; color: var(--wdc-text); }
.ob-error-detail { font-size: 0.78rem; color: var(--wdc-text-2); }
.ob-error-detail summary { cursor: pointer; }
.ob-error-detail code { display: block; margin-top: var(--wdc-space-1); overflow-wrap: anywhere; }
.ob-error-actions { display: flex; flex-wrap: wrap; gap: var(--wdc-space-2); }
.ob-error-actions :deep(.el-button + .el-button) { margin-left: 0; }
.ob-row {
  display: flex;
  align-items: center;
  justify-content: space-between;
  padding: var(--wdc-space-2-5) var(--wdc-space-3-5);
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
  margin-right: var(--wdc-space-2);
}
.ob-footer {
  display: flex;
  align-items: center;
  justify-content: space-between;
}
.ob-footer-left {
  display: flex;
  align-items: center;
  gap: var(--wdc-space-3);
}
.ob-dontshow {
  color: var(--wdc-text-3);
}
.ob-nav {
  display: flex;
  gap: var(--wdc-space-2);
}
</style>
