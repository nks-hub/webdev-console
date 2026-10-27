<template>
  <section class="attention" aria-labelledby="ov-attention">
    <h2 id="ov-attention" class="ov-h2">{{ t('overview.attention.title') }}</h2>
    <ol v-if="items.length" class="att-list">
      <li v-for="item in items" :key="item.key" class="att-item">
        <el-icon class="att-icon" :class="`att-${item.tone}`" :size="16" aria-hidden="true">
          <CircleCloseFilled v-if="item.tone === 'err'" />
          <WarningFilled v-else-if="item.tone === 'warn'" />
          <InfoFilled v-else />
        </el-icon>
        <div class="att-body">
          <p class="att-text">
            <template v-if="item.subject"><strong>{{ item.subject }}</strong>{{ item.sep ?? ' ' }}</template>{{ item.text }}
          </p>
          <p v-if="item.detail" class="att-detail mono">{{ item.detail }}</p>
          <button
            type="button"
            class="link-act"
            :disabled="item.busy"
            @click="item.run()"
          >{{ item.busy ? t('common.working') : item.action }}</button>
        </div>
      </li>
    </ol>
    <p v-else class="att-none">
      <el-icon class="att-ok" :size="16" aria-hidden="true"><CircleCheckFilled /></el-icon>
      {{ t('overview.attention.none') }}
    </p>
  </section>
</template>

<script setup lang="ts">
import { computed, onMounted, ref } from 'vue'
import { useRouter } from 'vue-router'
import { useI18n } from 'vue-i18n'
import { CircleCloseFilled, WarningFilled, InfoFilled, CircleCheckFilled } from '@element-plus/icons-vue'
import { ElMessage } from 'element-plus'
import { useDaemonStore } from '../../stores/daemon'
import { useServicesStore } from '../../stores/services'
import { useSitesStore } from '../../stores/sites'
import { useSiteApplyStore } from '../../stores/siteApply'
import { useUpdatesStore } from '../../stores/updates'
import { usePluginsStore } from '../../stores/plugins'
import { fetchBackups } from '../../api/daemon'

/**
 * One list of "what to do now": every entry is a sentence plus one action.
 * Replaces the scattered banners (Apache stopped, backup stale, update)
 * and the colour-only LEDs that used to carry service failures.
 */
interface AttentionItem {
  key: string
  tone: 'err' | 'warn' | 'info'
  subject?: string
  /** Between subject and text: a space ("MySQL crashed") or ": " ("blog.loc: …"). */
  sep?: string
  text: string
  detail?: string | null
  action: string
  busy?: boolean
  run: () => void
}

const { t, te } = useI18n()
const router = useRouter()
const daemonStore = useDaemonStore()
const servicesStore = useServicesStore()
const sitesStore = useSitesStore()
const siteApplyStore = useSiteApplyStore()
const updatesStore = useUpdatesStore()
const pluginsStore = usePluginsStore()

const BACKUP_STALE_DAYS = 7
// null = unknown (not loaded or endpoint failed), so no false alarm.
const lastBackupAgeMs = ref<number | null>(null)
const backupsLoaded = ref(false)
const startingApache = ref(false)

onMounted(async () => {
  try {
    const data = await fetchBackups()
    lastBackupAgeMs.value = data.backups.length > 0
      ? Date.now() - new Date(data.backups[0].createdUtc).getTime()
      : Number.POSITIVE_INFINITY
    backupsLoaded.value = true
  } catch { /* offline or no backup endpoint: say nothing */ }
})

function stepLabel(name: string): string {
  const key = `siteApply.steps.${name}`
  return te(key) ? t(key) : name
}

const services = computed(() => daemonStore.services.filter(s => pluginsStore.isServiceVisible(s.id)))
const apache = computed(() => services.value.find(s => s.id === 'apache' || s.id === 'httpd'))

async function startApache() {
  if (!apache.value) return
  startingApache.value = true
  try {
    await servicesStore.start(apache.value.id)
  } catch (err) {
    ElMessage.error(t('toast.prefixedError', { name: 'Apache', err: err instanceof Error ? err.message : String(err) }))
  } finally {
    startingApache.value = false
  }
}

const items = computed<AttentionItem[]>(() => {
  const out: AttentionItem[] = []

  for (const svc of services.value.filter(s => s.state === 4)) {
    out.push({
      key: `crashed:${svc.id}`,
      tone: 'err',
      subject: svc.displayName || svc.id,
      text: t('overview.attention.crashed'),
      action: t('overview.attention.showWhy'),
      run: () => void router.push({ path: '/services', query: { filter: 'problems' } }),
    })
  }

  for (const rec of Object.values(siteApplyStore.results)) {
    const failed = rec.steps.filter(s => s.status === 'failed')
    // A deleted site's last apply result stays in the store; skip it.
    if (!failed.length || !sitesStore.sites.some(s => s.domain === rec.domain)) continue
    out.push({
      key: `apply:${rec.domain}`,
      tone: 'err',
      subject: rec.domain,
      sep: ': ',
      text: t('overview.attention.applyFailed', { step: failed.map(s => stepLabel(s.name)).join(', ') }, failed.length),
      detail: failed[0].detail,
      action: t('overview.attention.openSite'),
      run: () => void router.push(`/sites/${encodeURIComponent(rec.domain)}/edit`),
    })
  }

  if (sitesStore.sites.length > 0 && apache.value && apache.value.state === 0) {
    out.push({
      key: 'apache-stopped',
      tone: 'warn',
      text: t('overview.attention.apacheStopped'),
      action: t('overview.attention.startApache'),
      busy: startingApache.value,
      run: () => void startApache(),
    })
  }

  if (sitesStore.sites.length > 0 && backupsLoaded.value && lastBackupAgeMs.value !== null
    && lastBackupAgeMs.value > BACKUP_STALE_DAYS * 86_400_000) {
    out.push({
      key: 'backup',
      tone: 'info',
      text: Number.isFinite(lastBackupAgeMs.value)
        ? t('overview.attention.backupOld', { days: BACKUP_STALE_DAYS })
        : t('overview.attention.backupNever'),
      action: t('overview.attention.openBackups'),
      run: () => void router.push('/backups'),
    })
  }

  if (updatesStore.hasUpdate) {
    out.push({
      key: 'update',
      tone: 'info',
      text: t('overview.attention.update', { version: updatesStore.latestVersion }),
      action: t('overview.attention.openUpdate'),
      run: () => void router.push({ path: '/settings', query: { tab: 'update' } }),
    })
  }
  return out
})

defineExpose({ count: computed(() => items.value.length) })
</script>

<style scoped>
.ov-h2 {
  font-size: 1.2rem;
  margin-bottom: var(--wdc-space-3);
}
.att-list {
  list-style: none;
  margin: 0;
  padding: 0;
  border-top: 1px solid var(--wdc-border);
}
.att-item {
  display: grid;
  grid-template-columns: auto minmax(0, 1fr);
  gap: var(--wdc-space-3);
  padding: var(--wdc-space-4) 0;
  border-bottom: 1px solid var(--wdc-border);
}
.att-icon { margin-top: var(--wdc-space-0-5); }
.att-err { color: var(--wdc-status-error); }
.att-warn { color: var(--wdc-status-starting); }
.att-info { color: var(--wdc-accent); }
.att-body {
  display: grid;
  gap: var(--wdc-space-1-5);
  justify-items: start;
  min-width: 0;
}
.att-text { color: var(--wdc-text); line-height: 1.45; }
.att-text strong { font-weight: 600; }
.att-detail {
  font-size: 0.78rem;
  color: var(--wdc-text-2);
  overflow-wrap: anywhere;
}
.link-act {
  position: relative;
  padding: 0;
  border: 0;
  background: none;
  font-family: var(--wdc-font-display);
  font-weight: 500;
  font-size: 0.87rem;
  color: var(--wdc-text);
  text-decoration: underline;
  text-decoration-color: var(--wdc-accent);
  text-underline-offset: 0.2em;
  cursor: pointer;
}
/* Larger hit area without moving the layout. */
.link-act::before { content: ''; position: absolute; inset: -8px -4px; }
@media (hover: hover) {
  .link-act:hover:not(:disabled) { color: var(--wdc-accent-ink); }
}
.link-act:disabled { color: var(--wdc-text-3); cursor: progress; }
.link-act:focus-visible { outline: 2px solid var(--wdc-accent); outline-offset: 2px; }
.att-none {
  display: flex;
  align-items: center;
  gap: var(--wdc-space-2);
  padding: var(--wdc-space-4) 0;
  border-top: 1px solid var(--wdc-border);
  border-bottom: 1px solid var(--wdc-border);
  color: var(--wdc-text-2);
}
.att-ok { color: var(--wdc-status-running); }
</style>
