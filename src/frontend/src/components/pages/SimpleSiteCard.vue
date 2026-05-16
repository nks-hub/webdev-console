<template>
  <el-card class="site-card" shadow="hover">
    <!-- card-body is the primary click target for navigation. Without
         role + tabindex + keyboard handlers, screen-reader and
         keyboard-only users couldn't reach the site edit page (the
         dropdown action menu is the only kbd-navigable surface today). -->
    <div
      class="card-body"
      role="button"
      tabindex="0"
      :aria-label="t('sites.card.openSiteAria', { domain: site.domain })"
      @click="emit('navigate', site.domain)"
      @keydown.enter.prevent="emit('navigate', site.domain)"
      @keydown.space.prevent="emit('navigate', site.domain)"
    >
      <div class="card-title-row">
        <div class="card-title">{{ site.domain }}</div>
        <div class="card-status">
          <HealthStatusDot :level="apacheRunning ? 'ok' : 'err'" />
          <span class="status-text">
            {{ apacheRunning ? t('sites.card.running') : t('sites.card.stopped') }}
          </span>
        </div>
      </div>

      <div class="card-path mono" :title="site.documentRoot">{{ site.documentRoot }}</div>

      <div class="card-badges">
        <el-tag
          v-if="site.phpVersion && site.phpVersion !== 'none'"
          size="small"
          effect="dark"
          class="badge-php"
        >PHP {{ site.phpVersion }}</el-tag>
        <el-tag
          v-if="site.sslEnabled"
          size="small"
          type="success"
          effect="dark"
        >HTTPS</el-tag>
        <el-tag
          v-if="site.cloudflare?.enabled"
          size="small"
          type="warning"
          effect="dark"
        >{{ t('sites.simple.cloudflareTunnel') }}</el-tag>
      </div>

      <!-- Activity only when there's traffic - removes "0 hits / zatím
           nenavštíveno" noise from every fresh card. -->
      <div v-if="activity && activity.totalHits > 0" class="card-activity">
        <MiniSparkline :values="activity.hourlyHits" :width="120" :height="24" />
        <span class="card-hits mono">{{ activity.totalHits }} hits</span>
        <span v-if="activity.errorCount > 0" class="card-errors mono">
          · {{ activity.errorCount }} err
        </span>
      </div>
      <div v-if="activity && activity.totalHits > 0" class="card-lasthit">{{ relativeLabel }}</div>
    </div>

    <div class="card-actions" @click.stop>
      <el-button
        size="large"
        type="primary"
        :icon="ExternalLinkIcon"
        @click="emit('open', site)"
      >{{ t('sites.card.open') }}</el-button>

      <el-button
        v-if="apacheRunning"
        size="large"
        circle
        :icon="StopIcon"
        :loading="toggling"
        :title="t('sites.card.stopApacheTooltip')"
        @click="emit('stop-apache')"
      />
      <el-button
        v-else
        size="large"
        circle
        type="success"
        :icon="PlayIcon"
        :loading="toggling"
        :title="t('sites.card.start')"
        @click="emit('start-apache')"
      />

      <el-tooltip
        :content="site.enabled === false
          ? t('sites.card.disabledTooltip')
          : t('sites.card.enabledTooltip')"
        placement="top"
      >
        <el-switch
          :model-value="site.enabled !== false"
          :loading="togglingEnabled === site.domain"
          size="large"
          @change="(v: boolean | string | number) => emit('toggle-enabled', site, Boolean(v))"
        />
      </el-tooltip>

      <!-- Teleported dropdown + viewport-preventOverflow so the menu
           never clips at narrow viewports / mobile (Task 01 fix). -->
      <el-dropdown
        trigger="click"
        :teleported="true"
        :popper-options="{ modifiers: [{ name: 'preventOverflow', options: { boundary: 'viewport', padding: 8 } }] }"
        @command="(cmd: string) => emit('command', cmd, site)"
      >
        <el-button
          size="large"
          circle
          :aria-label="t('sites.card.moreActions', { domain: site.domain })"
        >
          <el-icon><MoreFilled /></el-icon>
        </el-button>
        <template #dropdown>
          <el-dropdown-menu>
            <el-dropdown-item command="reveal">
              <el-icon><FolderOpened /></el-icon> {{ t('sites.card.revealFolder') }}
            </el-dropdown-item>
            <el-dropdown-item command="duplicate">
              <el-icon><CopyDocument /></el-icon> {{ t('sites.card.duplicate') }}
            </el-dropdown-item>
            <el-dropdown-item command="restart" :disabled="restarting">
              <el-icon v-if="restarting" class="is-loading"><RefreshRight /></el-icon>
              <el-icon v-else><RefreshRight /></el-icon>
              {{ t('sites.card.restart') }}
            </el-dropdown-item>
            <el-dropdown-item command="delete" divided class="danger-item">
              {{ t('sites.card.delete') }}
            </el-dropdown-item>
          </el-dropdown-menu>
        </template>
      </el-dropdown>
    </div>
  </el-card>
</template>

<script setup lang="ts">
import { computed, h } from 'vue'
import { useI18n } from 'vue-i18n'
import { MoreFilled, FolderOpened, CopyDocument, RefreshRight } from '@element-plus/icons-vue'
import MiniSparkline from '../common/MiniSparkline.vue'
import HealthStatusDot from '../shared/HealthStatusDot.vue'
import type { SiteInfo } from '../../api/types'

defineOptions({ name: 'SimpleSiteCard' })

// Per-site card extracted out of SitesListSimple.vue. Owns no state —
// the parent owns `apacheRunning`, `toggling*`, `restarting` flags plus
// the activity snapshot, and listens to emits for every user action.
// Keeping it stateless makes the parent's loop tight (one v-for over
// `sites` with stable props) and lets future contexts (e.g. a "favourite
// sites" grid) reuse the same card without bringing the list's busy
// flags along.

export interface SiteActivity {
  hourlyHits: number[]
  totalHits: number
  errorCount: number
  lastHitIso: string | null
}

const props = defineProps<{
  site: SiteInfo
  apacheRunning: boolean
  toggling: boolean
  togglingEnabled: string | null
  restarting: boolean
  activity?: SiteActivity | null
  /** Pre-localized "5 min ago" string from the parent (Czech/EN aware). */
  relativeLabel: string
}>()

const emit = defineEmits<{
  navigate: [domain: string]
  open: [site: SiteInfo]
  'start-apache': []
  'stop-apache': []
  'toggle-enabled': [site: SiteInfo, value: boolean]
  command: [cmd: string, site: SiteInfo]
}>()

const { t } = useI18n()

// Inline-defined icon components keep the bundle from importing the
// full ElIcon catalogue. ExternalLink + Stop + Play are not exported
// from @element-plus/icons-vue by those names — same source file used
// by the parent originally.
const ExternalLinkIcon = {
  render: () => h('svg', {
    xmlns: 'http://www.w3.org/2000/svg', viewBox: '0 0 24 24', width: '1em', height: '1em',
    fill: 'none', stroke: 'currentColor', 'stroke-width': '2',
    'stroke-linecap': 'round', 'stroke-linejoin': 'round',
  }, [
    h('path', { d: 'M18 13v6a2 2 0 0 1-2 2H5a2 2 0 0 1-2-2V8a2 2 0 0 1 2-2h6' }),
    h('polyline', { points: '15 3 21 3 21 9' }),
    h('line', { x1: '10', y1: '14', x2: '21', y2: '3' }),
  ]),
}
const StopIcon = {
  render: () => h('svg', {
    xmlns: 'http://www.w3.org/2000/svg', viewBox: '0 0 24 24', width: '1em', height: '1em',
    fill: 'currentColor',
  }, [h('rect', { x: '6', y: '6', width: '12', height: '12' })]),
}
const PlayIcon = {
  render: () => h('svg', {
    xmlns: 'http://www.w3.org/2000/svg', viewBox: '0 0 24 24', width: '1em', height: '1em',
    fill: 'currentColor',
  }, [h('polygon', { points: '6,4 20,12 6,20' })]),
}

// Strip-unused warning silencer — Vue handles the prop type ref already.
void computed(() => props.site.domain)
</script>

<style scoped>
.site-card {
  cursor: pointer;
  transition: transform 0.12s ease, box-shadow 0.12s ease;
}
.site-card:hover {
  transform: translateY(-1px);
}
.site-card :deep(.el-card__body) {
  padding: 12px 14px;
  display: flex;
  flex-direction: column;
  gap: 6px;
}
.card-body {
  display: flex;
  flex-direction: column;
  gap: 6px;
  min-width: 0;
  cursor: pointer;
  outline-offset: 4px;
  border-radius: var(--wdc-radius-sm);
}
/* Show focus ring on keyboard navigation but not on mouse click to
   avoid the persistent halo after clicking a card (common Element
   Plus card UX expectation). */
.card-body:focus-visible {
  outline: 2px solid var(--wdc-accent);
}
.card-title-row {
  display: flex;
  align-items: center;
  justify-content: space-between;
  gap: 12px;
  min-width: 0;
}
.card-title {
  font-size: 1.125rem;
  font-weight: 700;
  color: var(--wdc-text);
  overflow: hidden;
  text-overflow: ellipsis;
  white-space: nowrap;
  min-width: 0;
  letter-spacing: -0.01em;
}
.card-status {
  display: flex;
  align-items: center;
  gap: 6px;
  font-size: 0.78rem;
  color: var(--wdc-text-2);
}
/* Status dot delegated to HealthStatusDot shared primitive (plan §6). */
.card-path {
  color: var(--wdc-text-3);
  font-size: 0.8125rem;
  overflow: hidden;
  text-overflow: ellipsis;
  white-space: nowrap;
}
.card-badges {
  display: flex;
  gap: 6px;
  flex-wrap: wrap;
  margin-top: 2px;
}
.badge-php {
  background: var(--wdc-accent) !important;
  border-color: var(--wdc-accent) !important;
}
.card-activity {
  display: flex;
  align-items: center;
  gap: 8px;
  margin-top: 6px;
  font-size: 0.74rem;
  color: var(--wdc-text-2);
}
.card-hits { color: var(--wdc-text); }
.card-errors { color: var(--el-color-danger); }
.card-lasthit {
  font-size: 0.7rem;
  color: var(--wdc-text-3);
}
.card-actions {
  display: flex;
  align-items: center;
  gap: 6px;
  margin-top: 8px;
  flex-wrap: wrap;
}
.danger-item { color: var(--el-color-danger); }
</style>
