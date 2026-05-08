<template>
  <!-- F91.6 + 2026-05-07 rewrite: per-site Cloudflare Tunnel tab.
       Previously a 30-line stub that only deep-linked to /cloudflare and
       relied on the page-level Save & Apply to persist anything. That
       made the toggle feel broken: flip on → no fields → no immediate
       feedback → user had to remember to click Save & Apply at the very
       top of the page. This version owns the per-site CF config inline:
       subdomain, zone picker, local service, protocol — plus a
       standalone "Apply" button that PUTs only this site and syncs the
       Cloudflare CNAME + ingress rules without dragging the rest of the
       SiteEdit form along. -->
  <el-tab-pane :name="name">
    <template #label>
      <span class="tab-label"><el-icon><Link /></el-icon> {{ label }}</span>
    </template>
    <div class="tab-content">
      <section class="edit-card">
        <header class="edit-card-header">
          <span class="edit-card-title">{{ $t('cloudflareSite.title') }}</span>
          <span class="edit-card-hint">{{ $t('cloudflareSite.subtitle') }}</span>
        </header>
        <div class="edit-card-body">
          <div class="ssl-toggle-row">
            <div class="ssl-toggle-meta">
              <div class="ssl-toggle-title">{{ $t('cloudflareSite.exposeTitle') }}</div>
              <div class="ssl-toggle-desc">{{ $t('cloudflareSite.exposeDesc') }}</div>
            </div>
            <el-switch
              :model-value="enabled"
              size="large"
              @update:model-value="onToggle"
            />
          </div>

          <!-- Inline config — only when the per-site toggle is ON. The
               full DNS / tunnel-ingress editor is still on /cloudflare;
               this is the "set the public host" minimum surface. -->
          <div v-if="enabled" class="cf-config">
            <div v-if="!hasCloudflareSetup" class="setup-warning">
              <el-alert type="warning" :closable="false" show-icon>
                <template #title>{{ $t('cloudflareSite.notConfiguredTitle') }}</template>
                <div>{{ $t('cloudflareSite.notConfiguredBody') }}</div>
                <el-button size="small" type="primary" @click="goToCloudflarePage">
                  {{ $t('cloudflareSite.openSetup') }}
                </el-button>
              </el-alert>
            </div>

            <template v-else>
              <el-form label-position="top" :model="form" class="cf-form">
                <el-form-item :label="$t('cloudflareSite.subdomainLabel')">
                  <div class="subdomain-row">
                    <el-input v-model="form.subdomain" placeholder="myapp" />
                    <span class="subdomain-zone">.{{ activeZoneName || '(zóna)' }}</span>
                    <el-button
                      v-if="props.site?.domain"
                      size="small"
                      :loading="suggesting"
                      @click="suggest"
                    >{{ $t('cloudflareSite.suggest') }}</el-button>
                  </div>
                  <div class="hint">{{ $t('cloudflareSite.subdomainHint') }}</div>
                </el-form-item>

                <el-form-item :label="$t('cloudflareSite.zoneLabel')">
                  <el-select
                    v-model="form.zoneId"
                    filterable
                    :placeholder="$t('cloudflareSite.zonePlaceholder')"
                    :loading="loadingZones"
                    style="width: 100%"
                  >
                    <el-option
                      v-for="z in zones"
                      :key="z.id"
                      :label="z.name"
                      :value="z.id"
                    />
                  </el-select>
                </el-form-item>

                <!-- "Local service" was a confusing power-user field —
                     the user shouldn't have to type localhost:80 to
                     expose THIS site. We derive it from the site's own
                     port + sslEnabled config and hide it under an
                     Advanced disclosure. -->
                <details class="advanced">
                  <summary>{{ $t('cloudflareSite.advancedLabel') }}</summary>
                  <el-form-item :label="$t('cloudflareSite.localServiceLabel')">
                    <div class="local-service-row">
                      <el-select v-model="form.protocol" style="width: 110px">
                        <el-option label="http" value="http" />
                        <el-option label="https" value="https" />
                      </el-select>
                      <el-input v-model="form.localService" :placeholder="derivedLocalService" />
                    </div>
                    <div class="hint">{{ $t('cloudflareSite.localServiceHint') }}</div>
                  </el-form-item>
                </details>
              </el-form>

              <div v-if="publicUrl" class="public-url">
                <span class="public-label">{{ $t('cloudflareSite.publicUrl') }}:</span>
                <!-- Plain <a target=_blank> is blocked by the Electron
                     setWindowOpenHandler('deny') guard. Route through
                     electronAPI.openExternal so the URL opens in the
                     user's default browser instead of being silently
                     swallowed (incident 2026-05-07). -->
                <a href="#" class="public-link mono" @click.prevent="openPublicUrl">
                  {{ publicUrl }}
                </a>
                <el-button size="small" text @click="copyPublicUrl">
                  <el-icon><DocumentCopy /></el-icon>
                </el-button>
              </div>
            </template>
          </div>

          <div class="cf-actions">
            <el-button
              :type="enabled && !applied ? 'primary' : 'default'"
              :loading="applying"
              :disabled="!enabled || !hasCloudflareSetup || !canApply"
              @click="applyNow"
            >
              {{ enabled
                  ? (applied ? $t('cloudflareSite.applyAgain') : $t('cloudflareSite.applyNow'))
                  : $t('cloudflareSite.applyNow') }}
            </el-button>
            <el-button size="default" @click="goToCloudflarePage">
              {{ $t('nav.tunnel') }}
            </el-button>
            <span v-if="applyStatus" class="apply-status" :class="applyStatus.kind">
              {{ applyStatus.message }}
            </span>
          </div>
          <div class="hint" style="margin-top: 8px">
            {{ $t('cloudflareSite.applyHint') }}
          </div>
        </div>
      </section>
    </div>
  </el-tab-pane>
</template>

<script setup lang="ts">
import { Link, DocumentCopy } from '@element-plus/icons-vue'
import { computed, onMounted, reactive, ref, watch } from 'vue'
import { useRouter } from 'vue-router'
import { ElMessage } from 'element-plus'
import {
  fetchCloudflareConfig,
  fetchCloudflareZones,
  suggestCloudflareSubdomain,
  cloudflareSync,
  updateSite,
} from '../api/daemon'
import type { SiteInfo } from '../api/types'

const props = defineProps<{
  name: string
  label: string
  site?: SiteInfo | null
}>()

const emit = defineEmits<{
  (e: 'update:site', site: SiteInfo): void
  (e: 'dirty'): void
}>()

const router = useRouter()

// ── State ────────────────────────────────────────────────────────────

const zones = ref<Array<{ id: string; name: string }>>([])
const loadingZones = ref(false)
const cfHasAccount = ref(false)
const cfHasTunnel = ref(false)

const form = reactive({
  subdomain: '',
  zoneId: '',
  zoneName: '',
  localService: 'localhost:80',
  protocol: 'http' as 'http' | 'https',
})

const enabled = computed(() => Boolean(props.site?.cloudflare?.enabled))

const hasCloudflareSetup = computed(() => cfHasAccount.value && cfHasTunnel.value)

const activeZoneName = computed(() => {
  const z = zones.value.find(x => x.id === form.zoneId)
  return z?.name ?? form.zoneName
})

// Derive "where the tunnel forwards to" from the site's own config so
// the user never has to type localhost:80 manually. SSL on → 443, off
// → 80. Ports come from site.{httpsPort,httpPort} when set, fall back
// to standard 443/80 otherwise.
const derivedLocalService = computed(() => {
  const s = props.site
  if (!s) return 'localhost:80'
  if (s.sslEnabled) {
    const port = s.httpsPort || 443
    return `localhost:${port}`
  }
  const port = s.httpPort || 80
  return `localhost:${port}`
})

const derivedProtocol = computed<'http' | 'https'>(() =>
  props.site?.sslEnabled ? 'https' : 'http'
)

// effective values used by Apply — form > derived. The Advanced
// disclosure lets power users override; everyone else gets sensible
// defaults without seeing the field.
const effectiveLocalService = computed(() => form.localService || derivedLocalService.value)
const effectiveProtocol = computed<'http' | 'https'>(() => form.protocol || derivedProtocol.value)

const canApply = computed(() =>
  Boolean(form.subdomain && form.zoneId)
)

const publicUrl = computed(() => {
  if (!form.subdomain || !activeZoneName.value) return ''
  return `https://${form.subdomain}.${activeZoneName.value}`
})

// "applied" = the form values match what's currently stored on the site,
// so the apply button can show "Apply again" instead of pretending the
// last apply didn't happen. Recomputed on every site update.
const applied = computed(() => {
  const cf = props.site?.cloudflare
  if (!cf) return false
  return (
    cf.enabled === true &&
    (cf.subdomain ?? '') === form.subdomain &&
    (cf.zoneId ?? '') === form.zoneId &&
    (cf.localService ?? 'localhost:80') === form.localService &&
    ((cf.protocol ?? 'http') as 'http' | 'https') === form.protocol
  )
})

const applying = ref(false)
const suggesting = ref(false)
const applyStatus = ref<{ kind: 'success' | 'error'; message: string } | null>(null)

// ── Load + sync form ────────────────────────────────────────────────

async function loadCfMeta() {
  try {
    const cfg = await fetchCloudflareConfig()
    cfHasAccount.value = Boolean(cfg.accountId && cfg.apiToken)
    cfHasTunnel.value = Boolean(cfg.tunnelId && cfg.tunnelToken)
  } catch {
    cfHasAccount.value = false
    cfHasTunnel.value = false
  }
}

async function loadZones() {
  loadingZones.value = true
  try {
    const r = await fetchCloudflareZones()
    if (r.success && Array.isArray(r.result)) {
      zones.value = r.result.map(z => ({ id: z.id, name: z.name }))
      // Default zone if none selected and only one zone exists.
      if (!form.zoneId && zones.value.length === 1) {
        form.zoneId = zones.value[0]!.id
        form.zoneName = zones.value[0]!.name
      }
    }
  } catch {
    /* daemon offline / no token — surfaced via hasCloudflareSetup */
  } finally {
    loadingZones.value = false
  }
}

function syncFormFromSite() {
  const cf = props.site?.cloudflare
  if (!cf) return
  form.subdomain = cf.subdomain ?? ''
  form.zoneId = cf.zoneId ?? ''
  form.zoneName = cf.zoneName ?? ''
  form.localService = cf.localService ?? 'localhost:80'
  form.protocol = (cf.protocol ?? 'http') as 'http' | 'https'
}

watch(() => props.site, syncFormFromSite, { immediate: true, deep: true })

// Re-derive localService when protocol flips. We only overwrite when the
// current value matches the OPPOSITE protocol's default — this means a
// user who explicitly typed `localhost:8080` keeps their override across
// http↔https flips, but the common case (just toggling protocol) auto-
// matches the conventional port. Incident 2026-05-07: switching to https
// kept "localhost:80" stale.
watch(() => form.protocol, (proto) => {
  const httpPort = props.site?.httpPort || 80
  const httpsPort = props.site?.httpsPort || 443
  const httpDefault = `localhost:${httpPort}`
  const httpsDefault = `localhost:${httpsPort}`
  if (proto === 'https' && (form.localService === '' || form.localService === httpDefault)) {
    form.localService = httpsDefault
  } else if (proto === 'http' && (form.localService === '' || form.localService === httpsDefault)) {
    form.localService = httpDefault
  }
})

onMounted(() => {
  void loadCfMeta()
  void loadZones()
})

// ── Actions ──────────────────────────────────────────────────────────

async function suggest() {
  if (!props.site?.domain) return
  suggesting.value = true
  try {
    const r = await suggestCloudflareSubdomain(props.site.domain)
    if (r?.suggestion) {
      form.subdomain = r.suggestion
      emit('dirty')
    }
  } catch (e) {
    ElMessage.warning('Nepodařilo se navrhnout subdoménu: ' + (e instanceof Error ? e.message : ''))
  } finally {
    suggesting.value = false
  }
}

function onToggle(v: unknown) {
  if (!props.site) return
  const enabledNext = Boolean(v)
  const next: SiteInfo = {
    ...props.site,
    cloudflare: { ...(props.site.cloudflare ?? {}), enabled: enabledNext },
  }
  emit('update:site', next)
  emit('dirty')
  // Auto-suggest subdomain on first enable when none stored.
  if (enabledNext && !form.subdomain) {
    void suggest()
  }
}

// Standalone Apply: PUT only this site (with the current cloudflare
// form) and then run cloudflareSync() to push the CNAME + ingress
// upserts. No global "Save & Apply" of unrelated SiteEdit fields.
async function applyNow() {
  if (!props.site || !canApply.value) return
  applying.value = true
  applyStatus.value = null
  try {
    const z = zones.value.find(x => x.id === form.zoneId)
    const updated = await updateSite(props.site.domain, {
      ...props.site,
      cloudflare: {
        enabled: true,
        subdomain: form.subdomain,
        zoneId: form.zoneId,
        zoneName: z?.name ?? form.zoneName,
        localService: effectiveLocalService.value,
        protocol: effectiveProtocol.value,
      },
    })
    // Reflect in parent (SiteEdit) so its dirty state clears too.
    emit('update:site', updated as SiteInfo)
    // Push CNAME + ingress to Cloudflare immediately.
    const sync = await cloudflareSync()
    applyStatus.value = {
      kind: 'success',
      message: `Synced ${sync.synced ?? 0} site(s) — ${publicUrl.value}`,
    }
    ElMessage.success('Tunel aplikován: ' + publicUrl.value)
  } catch (e) {
    const msg = e instanceof Error ? e.message : String(e)
    applyStatus.value = { kind: 'error', message: msg }
    ElMessage.error('Apply failed: ' + msg)
  } finally {
    applying.value = false
  }
}

function goToCloudflarePage() {
  void router.push('/cloudflare')
}

function openPublicUrl() {
  if (!publicUrl.value) return
  const api = (window as unknown as {
    electronAPI?: { openExternal?: (url: string) => Promise<unknown> }
  }).electronAPI
  if (api?.openExternal) {
    void api.openExternal(publicUrl.value)
  } else {
    window.open(publicUrl.value, '_blank', 'noopener')
  }
}

async function copyPublicUrl() {
  if (!publicUrl.value) return
  try {
    await navigator.clipboard.writeText(publicUrl.value)
    ElMessage.success('Zkopírováno: ' + publicUrl.value)
  } catch {
    ElMessage.warning('Schránka nedostupná — URL: ' + publicUrl.value)
  }
}
</script>

<style scoped>
.tab-content { padding: 16px 0; }
.edit-card { background: var(--wdc-surface); border: 1px solid var(--wdc-border); border-radius: var(--wdc-radius-lg); margin-bottom: 16px; box-shadow: var(--wdc-shadow-sm); }
.edit-card-header { padding: 14px 18px; border-bottom: 1px solid var(--wdc-border); display: flex; justify-content: space-between; align-items: baseline; background: linear-gradient(180deg, var(--wdc-accent-dim), transparent); border-radius: var(--wdc-radius-lg) var(--wdc-radius-lg) 0 0; }
.edit-card-title { font-weight: 700; color: var(--wdc-text); font-size: 0.95rem; }
.edit-card-hint { font-size: 0.78rem; color: var(--wdc-text-3); }
.edit-card-body { padding: 14px 18px; }
.ssl-toggle-row { display: flex; align-items: center; justify-content: space-between; gap: 16px; padding: 10px 0; border-bottom: 1px solid var(--wdc-border); }
.ssl-toggle-title { font-weight: 600; color: var(--wdc-text); }
.ssl-toggle-desc { font-size: 0.82rem; color: var(--wdc-text-3); margin-top: 2px; }
.cf-config { padding: 14px 0; border-bottom: 1px solid var(--wdc-border); }
.cf-form { max-width: 640px; }
.subdomain-row { display: flex; align-items: center; gap: 8px; }
.subdomain-zone { color: var(--wdc-text-3); font-size: 0.85rem; white-space: nowrap; }
.local-service-row { display: flex; gap: 8px; }
.cf-actions { display: flex; align-items: center; gap: 12px; padding: 10px 0 0; }
.hint { font-size: 0.78rem; color: var(--wdc-text-3); margin-top: 4px; }
.tab-label { display: inline-flex; align-items: center; gap: 6px; }
.public-url { margin: 8px 0 0; font-size: 0.85rem; }
.public-label { color: var(--wdc-text-3); margin-right: 6px; }
.public-link { color: var(--wdc-accent, #5b8cff); text-decoration: none; }
.public-link:hover { text-decoration: underline; }
.mono { font-family: 'Cascadia Code', 'Consolas', monospace; }
.setup-warning { padding: 8px 0; }
.apply-status { font-size: 0.82rem; }
.apply-status.success { color: #36c290; }
.apply-status.error { color: #ef6e6e; }
</style>
