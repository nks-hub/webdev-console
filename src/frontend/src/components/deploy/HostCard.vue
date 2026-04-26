<template>
  <el-card class="host-card" shadow="hover">
    <template #header>
      <div class="host-card-head">
        <div class="host-card-title">
          <el-checkbox
            v-if="selectable"
            :model-value="selected"
            :aria-label="`Include ${host} in group deploy`"
            @update:model-value="(v: any) => $emit('toggle-select', !!v)"
            @click.stop
          />
          <span class="host-card-name">{{ host }}</span>
          <el-tag v-if="isProduction" type="danger" size="small" effect="plain">PROD</el-tag>
          <!-- Phase 6.17b — live phase indicator. Renders when an active
               run exists for this host (parent passes activeRun), so the
               operator sees "deploying" / "awaiting_soak" inline next to
               the host name without opening the drawer. -->
          <el-tag
            v-if="activeRun"
            :type="livePhaseTagType(activeRun.latestPhase)"
            size="small"
            effect="dark"
            class="host-card-live-phase"
            :aria-label="`Live phase ${activeRun.latestPhase}`"
          >
            <el-icon class="live-icon" aria-hidden="true">
              <Loading v-if="!activeRun.isTerminal" class="is-spinning" />
              <CircleCheck v-else-if="activeRun.success" />
              <CircleClose v-else />
            </el-icon>
            {{ activeRun.latestPhase }}
          </el-tag>
        </div>
        <HealthBadge :state="healthState" />
      </div>
    </template>

    <div class="host-card-body">
      <div v-if="lastDeploy" class="host-card-row">
        <span class="muted">Last deploy:</span>
        <span class="mono">{{ formatRelative(lastDeploy.startedAt) }}</span>
        <el-tag :type="lastDeploy.success ? 'success' : 'danger'" size="small" effect="plain">
          {{ lastDeploy.success ? 'OK' : 'FAILED' }}
        </el-tag>
      </div>
      <div v-else class="muted">No deploys yet</div>

      <div v-if="lastDeploy?.releaseId" class="host-card-row">
        <span class="muted">Release:</span>
        <span class="mono">{{ lastDeploy.releaseId }}</span>
      </div>
    </div>

    <template #footer>
      <div class="host-card-footer">
        <el-button type="primary" @click="$emit('deploy')">Deploy</el-button>
        <el-button
          v-if="lastDeploy && lastDeploy.success"
          plain
          @click="$emit('rollback')"
        >Rollback</el-button>
      </div>
    </template>
  </el-card>
</template>

<script setup lang="ts">
import { computed } from 'vue'
import { Loading, CircleCheck, CircleClose } from '@element-plus/icons-vue'
import HealthBadge from './HealthBadge.vue'
import type { DeployHistoryEntryDto } from '../../api/deploy'
import type { DeployRunState } from '../../stores/deploy'

const props = defineProps<{
  host: string
  lastDeploy?: DeployHistoryEntryDto | null
  /** Phase 6.10 — show a checkbox for multi-select group deploy. */
  selectable?: boolean
  /** Phase 6.10 — current selection state (controlled by parent). */
  selected?: boolean
  /**
   * Phase 6.17b — live run state for this host (parent picks the most
   * recent non-terminal run from the deploy store keyed by host).
   * Null when no active deploy is in flight for this host.
   */
  activeRun?: DeployRunState | null
}>()

defineEmits<{
  deploy: []
  rollback: []
  'toggle-select': [value: boolean]
}>()

const isProduction = computed(() =>
  props.host.toLowerCase().includes('prod') || props.host.toLowerCase() === 'production')

const healthState = computed<'healthy' | 'degraded' | 'down' | 'unknown'>(() => {
  if (!props.lastDeploy) return 'unknown'
  if (props.lastDeploy.error) return 'down'
  return 'healthy'
})

/**
 * Phase 6.17b — color-code the live phase tag. In-flight phases get a
 * primary accent (blue) so they stand out as "happening now"; terminal
 * success/failure get the standard green/red.
 */
function livePhaseTagType(phase: string): 'success' | 'danger' | 'warning' | 'primary' | 'info' {
  if (phase === 'Done') return 'success'
  if (phase === 'Failed' || phase === 'Cancelled') return 'danger'
  if (phase === 'RolledBack' || phase === 'RollingBack' || phase === 'AwaitingSoak') return 'warning'
  return 'primary' // anything in-flight (Queued, Fetching, Building, Migrating, Switched, etc.)
}

function formatRelative(iso: string): string {
  const ms = Date.now() - new Date(iso).getTime()
  if (ms < 60_000) return `${Math.floor(ms / 1000)}s ago`
  if (ms < 3_600_000) return `${Math.floor(ms / 60_000)}m ago`
  if (ms < 86_400_000) return `${Math.floor(ms / 3_600_000)}h ago`
  return `${Math.floor(ms / 86_400_000)}d ago`
}
</script>

<style scoped>
.host-card { width: 100%; }
.host-card-head {
  display: flex; align-items: center; justify-content: space-between; gap: 12px;
}
.host-card-title { display: flex; align-items: center; gap: 8px; }
.host-card-name { font-weight: 600; font-size: 14px; }

.host-card-body {
  display: flex; flex-direction: column; gap: 8px;
  font-size: 13px;
}
.host-card-row { display: flex; align-items: center; gap: 8px; }
.muted { color: var(--el-text-color-secondary); }
.mono { font-family: ui-monospace, 'JetBrains Mono', Consolas, monospace; }

.host-card-footer { display: flex; gap: 8px; justify-content: flex-end; }

.host-card-live-phase {
  display: inline-flex;
  align-items: center;
  gap: 4px;
}
.live-icon {
  font-size: 12px;
}
.is-spinning {
  animation: spin 1.4s linear infinite;
}
@keyframes spin {
  from { transform: rotate(0deg); }
  to { transform: rotate(360deg); }
}
@media (prefers-reduced-motion: reduce) {
  .is-spinning { animation: none; }
}
</style>
