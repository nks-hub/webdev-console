<template>
  <div class="site-overview">
    <section class="ov-card" :aria-labelledby="`${uid}-state`">
      <h2 :id="`${uid}-state`" class="ov-heading">{{ $t('siteEdit.ov.state') }}</h2>
      <p class="ov-explain">
        {{ explanation }}
        <a v-if="state.kind === 'running'" class="ov-url" :href="url" @click.prevent="openUrl(url)">{{ url }}</a>
      </p>
      <div class="ov-actions">
        <el-button :loading="toggling" @click="emit('toggle')">
          {{ site.enabled === false ? $t('sites.list.enable') : $t('sites.list.disable') }}
        </el-button>
      </div>
    </section>

    <SiteServices :domain="site.domain" class="ov-card" />

    <section class="ov-card" :aria-labelledby="`${uid}-settings`">
      <h2 :id="`${uid}-settings`" class="ov-heading">{{ $t('siteEdit.ov.settings') }}</h2>
      <dl class="ov-list">
        <div v-for="row in rows" :key="row.section" class="ov-row">
          <dt>{{ row.label }}</dt>
          <dd :class="{ mono: row.mono, 'is-warn': row.warn }">{{ row.value }}</dd>
          <el-button
            class="ov-change"
            size="small"
            text
            :aria-label="$t('siteEdit.ov.changeAria', { what: row.label })"
            @click="emit('change', row.section)"
          >{{ $t('siteEdit.ov.change') }}</el-button>
        </div>
      </dl>
    </section>

    <SiteActivity :domain="site.domain" class="ov-card" />
  </div>
</template>

<script setup lang="ts">
import { computed, useId } from 'vue'
import { useI18n } from 'vue-i18n'
import type { SiteInfo } from '../../api/types'
import { useSiteStateStore } from '../../stores/siteState'
import { useUiModeStore } from '../../stores/uiMode'
import { displayPath, openUrl, siteUrl } from '../../utils/siteUrl'
import SiteActivity from './SiteActivity.vue'
import SiteServices from './SiteServices.vue'

/**
 * Read-only summary of a site. Nothing is edited here: every value has a
 * "Change" link that opens the matching group on the Settings tab, so a
 * site has one place where it is edited and one way to save it.
 */
const props = defineProps<{ site: SiteInfo; toggling?: boolean }>()
const emit = defineEmits<{ (e: 'change', section: string): void; (e: 'toggle'): void }>()

const { t } = useI18n()
const siteState = useSiteStateStore()
const uiMode = useUiModeStore()
const uid = useId()

const state = computed(() => siteState.stateOf(props.site))
const url = computed(() => siteUrl(props.site))

// The web-server fix (start / install Apache) lives in the services card below.
const explanation = computed(() => {
  switch (state.value.kind) {
    case 'running': return t('siteEdit.ov.explain.running')
    case 'waiting': {
      const ws = siteState.webServer
      const busy = ws === 'starting' || ws === 'stopping' || ws === 'restarting'
      return t(ws === 'missing' ? 'siteEdit.ov.explain.waitingMissing' : busy ? 'siteEdit.ov.explain.waitingBusy' : 'siteEdit.ov.explain.waitingStopped')
    }
    case 'failed': return t('siteEdit.ov.explain.failed')
    case 'applying': return t('siteEdit.ov.explain.applying')
    default: return t('siteEdit.ov.explain.disabled')
  }
})

interface Row { section: string; label: string; value: string; mono?: boolean; warn?: boolean }
const rows = computed<Row[]>(() => {
  const s = props.site
  const rt = siteState.runtimeOf(s)
  const out: Row[] = [
    { section: 'folder', label: t('siteEdit.ov.folder'), value: displayPath(s.documentRoot), mono: true },
    { section: 'runtime', label: t('sites.list.colRuntime'), value: rt.missing ? `${rt.label} – ${t('sites.list.missing')}` : rt.label, warn: rt.missing },
    { section: 'https', label: 'HTTPS', value: s.sslEnabled ? t('siteEdit.ov.on') : t('siteEdit.ov.off') },
  ]
  if (uiMode.isAdvanced) {
    out.push({ section: 'domain', label: t('sites.aliases'), value: s.aliases?.length ? s.aliases.join(', ') : t('siteEdit.ov.none'), mono: !!s.aliases?.length })
    const binds = (s.bindAddresses?.length ? s.bindAddresses : [s.bindAddress || '*']).join(', ')
    out.push({
      section: 'network',
      label: t('siteEdit.ov.network'),
      value: `${binds === '*' ? t('siteEdit.ov.allAddresses') : binds} · HTTP ${s.httpPort || 80}${s.sslEnabled ? ` · HTTPS ${s.httpsPort || 443}` : ''}`,
    })
    if (s.framework) out.push({ section: 'framework', label: t('sites.framework'), value: s.framework })
  }
  if (siteState.cloudflareActive) {
    const cf = s.cloudflare
    out.push({
      section: 'tunnel',
      label: t('siteEdit.ov.tunnel'),
      value: cf?.enabled && cf.subdomain ? `https://${cf.subdomain}.${cf.zoneName}` : t('siteEdit.ov.off'),
      mono: !!cf?.enabled,
    })
  }
  return out
})
</script>

<style scoped>
.site-overview { display: grid; gap: var(--wdc-space-4); max-width: 880px; }
.ov-card {
  padding: var(--wdc-space-4) var(--wdc-space-5);
  border: 1px solid var(--wdc-border);
  border-radius: var(--wdc-radius-lg);
  background: var(--wdc-surface);
}
.ov-heading {
  margin-bottom: var(--wdc-space-3);
  font-size: 0.93rem;
  color: var(--wdc-text-2);
}
.ov-url {
  color: var(--wdc-accent-ink);
  font-family: var(--wdc-font-mono);
  font-size: 0.87rem;
  text-underline-offset: 0.2em;
  overflow-wrap: anywhere;
}
.ov-url:focus-visible { outline: 2px solid var(--wdc-accent); outline-offset: 2px; border-radius: var(--wdc-radius-xs); }
.ov-explain { margin: 0; color: var(--wdc-text); font-size: 0.93rem; line-height: 1.55; max-width: 70ch; }
.ov-actions { display: flex; flex-wrap: wrap; gap: var(--wdc-space-2); margin-top: var(--wdc-space-3); }
.ov-actions :deep(.el-button + .el-button) { margin-left: 0; }

.ov-list { margin: 0; }
.ov-row {
  display: grid;
  grid-template-columns: 150px minmax(0, 1fr) auto;
  align-items: center;
  gap: var(--wdc-space-3);
  padding: var(--wdc-space-2) 0;
  border-top: 1px solid var(--wdc-border);
}
.ov-row:first-child { border-top: 0; }
.ov-row dt { color: var(--wdc-text-2); font-size: 0.87rem; }
.ov-row dd { margin: 0; min-width: 0; color: var(--wdc-text); font-size: 0.9rem; overflow-wrap: anywhere; }
.ov-row dd.mono { font-family: var(--wdc-font-mono); font-size: 0.82rem; }
.ov-row dd.is-warn { color: var(--wdc-status-starting); }

@media (max-width: 700px) {
  .ov-row { grid-template-columns: minmax(0, 1fr) auto; }
  .ov-row dt { grid-column: 1 / -1; }
}
</style>
